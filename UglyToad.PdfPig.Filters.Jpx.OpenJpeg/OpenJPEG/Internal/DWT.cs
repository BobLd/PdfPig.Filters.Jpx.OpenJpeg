/*
 * Copyright (c) 2002-2007, Communications and Remote Sensing Laboratory, Universite catholique de Louvain (UCL), Belgium
 * Copyright (c) 2002-2007, Professor Benoit Macq
 * Copyright (c) 2001-2003, David Janssens
 * Copyright (c) 2002-2003, Yannick Verschueren
 * Copyright (c) 2003-2007, Francois-Olivier Devaux and Antonin Descampe
 * Copyright (c) 2005, Herve Drolon, FreeImage Team
 * All rights reserved.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions
 * are met:
 * 1. Redistributions of source code must retain the above copyright
 *    notice, this list of conditions and the following disclaimer.
 * 2. Redistributions in binary form must reproduce the above copyright
 *    notice, this list of conditions and the following disclaimer in the
 *    documentation and/or other materials provided with the distribution.
 *
 * THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS `AS IS'
 * AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
 * IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE
 * ARE DISCLAIMED.  IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE
 * LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR
 * CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF
 * SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS
 * INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN
 * CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE)
 * ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE
 * POSSIBILITY OF SUCH DAMAGE.
 */

using System.Buffers;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
#if NET8_0_OR_GREATER
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
#endif

namespace OpenJpeg.Internal;

/// <summary>
/// Discrete Wavelet Transform
/// </summary>
/// <remarks>
/// The DWT can be reversible, or irreversible.
///
/// Reversible uses integer math and a modified YUV colorspace,
/// irreversible uses floating point math and the YCbCr colorspace
///
/// Irreversible is only applicable for lossy encoding.
/// </remarks>
internal static class DWT
{
    private const float K = 1.230174105f;
    private const float invK = (float)(1.0 / 1.230174105);
    private const float c13318 = 1.625732422f; //<-- two_invK
    private const float dwt_alpha = -1.586134342f; //  12994
    private const float dwt_beta = -0.052980118f; //    434
    private const float dwt_gamma = 0.882911075f; //  -7233
    private const float dwt_delta = 0.443506852f; //  -3633

    /// <summary>
    /// Number of int32 values per SIMD register, as in upstream's AVX2 build (16 columns per 5/3 vertical pass).
    /// The vector code handles this with one or more Vector&lt;int&gt; per half.
    /// </summary>
    private const uint VREG_INT_COUNT = 8;
    private const uint PARALLEL_COLS_53 = 2 * VREG_INT_COUNT;
    private const int NB_ELTS_V8 = 8;
    /// <summary>Floats per Vector&lt;float&gt;, and vectors per NB_ELTS_V8 wavelet element.</summary>
    private static readonly int VecF = Vector<float>.Count;
    private static readonly int VecPerElt = NB_ELTS_V8 / Math.Max(1, Vector<float>.Count);
    /// <summary>SIMD path for the 9/7 lifting steps, when vectors are hardware accelerated and fit an element.</summary>
    private static readonly bool UseVectors = Vector.IsHardwareAccelerated && NB_ELTS_V8 % Vector<float>.Count == 0;

    /// <summary>
    /// This table contains the norms of the 9-7 wavelets for different bands.
    /// </summary>
    private static readonly double[][] dwt_norms_real =
    [
        [1.000, 1.965, 4.177, 8.403, 16.90, 33.84, 67.69, 135.3, 270.6, 540.9],
        [2.022, 3.989, 8.355, 17.04, 34.27, 68.63, 137.3, 274.6, 549.0],
        [2.022, 3.989, 8.355, 17.04, 34.27, 68.63, 137.3, 274.6, 549.0],
        [2.080, 3.865, 8.307, 17.18, 34.71, 69.59, 139.3, 278.6, 557.2]
    ];

    /// <summary>
    /// This table contains the norms of the 5-3 wavelets for different bands.
    /// </summary>
    private static readonly double[][] dwt_norms =
    [
        [1.000, 1.500, 2.750, 5.375, 10.68, 21.34, 42.67, 85.33, 170.7, 341.3],
        [1.038, 1.592, 2.919, 5.703, 11.33, 22.64, 45.25, 90.48, 180.9],
        [1.038, 1.592, 2.919, 5.703, 11.33, 22.64, 45.25, 90.48, 180.9],
        [.7186, .9218, 1.586, 3.043, 6.019, 12.01, 24.00, 47.97, 95.93]
    ];

    private delegate void Encodefunc(int[] a, int dn, int sn, int cas);
    private delegate void EncodeAndDeinterleaveVfunc(int[] a, int a_pt, int[] tmp, uint height, bool even, uint stride_width, uint cols);
    private delegate void EncodeAndDeinterleaveH_OneRowfunc(int[] row, int row_pt, int[] tmp, uint width, bool even);

    /// <summary>
    /// Forward 5-3 wavelet transform in 2-D
    /// </summary>
    /// <remarks>2.5 - opj_dwt_encode</remarks>
    internal static bool Encode(TileCoder tcd, TcdTilecomp tilec)
    {
        return EncodeProcedure(tilec, EncodeAndDeinterleaveV, EncodeAndDeinterleaveH_OneRow, tcd.DisableMultiThreading);
    }

    /// <summary>
    /// Forward 9-7 wavelet transform in 2-D
    /// </summary>
    /// <remarks>2.5 - opj_dwt_encode_real</remarks>
    internal static bool EncodeReal(TileCoder tcd, TcdTilecomp tilec)
    {
        return EncodeProcedure(tilec, EncodeAndDeinterleaveV_Real, EncodeAndDeinterleaveH_OneRowReal, tcd.DisableMultiThreading);
    }

    //2.5 - opj_dwt_encode_procedure
    private static bool EncodeProcedure(TcdTilecomp tilec,
        EncodeAndDeinterleaveVfunc encode_and_deinterleave_v,
        EncodeAndDeinterleaveH_OneRowfunc encode_and_deinterleave_h_one_row,
        bool disable_multi_threading)
    {
        var num_threads = Parallelism.ThreadCount(disable_multi_threading);

        int[] tiledp = tilec.data;

        uint w = (uint)(tilec.x1 - tilec.x0);
        int l = (int)tilec.numresolutions - 1;

        var tr = tilec.resolutions;
        int cur_res = l; //<-- pointer to tilec.resolutions
        int last_res = cur_res - 1; //<-- pointer to tilec.resolutions

        uint data_size = MaxResolution(tilec.resolutions, (int)tilec.numresolutions);
        if (data_size > (Constants.SIZE_MAX / (NB_ELTS_V8 * sizeof(int))))
        {
            return false;
        }

        data_size *= NB_ELTS_V8; //C# org impl is number of bytes, here it's number of ints
        int[] bj = new int[data_size];
        int i = l;

        while (i-- != 0)
        {
            //Width of the resolution level computed
            uint rw = (uint)(tr[cur_res].x1 - tr[cur_res].x0);

            //Height of the resolution level computed
            uint rh = (uint)(tr[cur_res].y1 - tr[cur_res].y0);

            //Width of the resolution level once lower than computed one
            uint rw1 = (uint)(tr[last_res].x1 - tr[last_res].x0);

            //Height of the resolution level once lower than computed one
            uint rh1 = (uint)(tr[last_res].y1 - tr[last_res].y0);

            //0 = non inversion on vertical filtering 1 = inversion between low-pass and high-pass filtering
            int cas_row = tr[cur_res].x0 & 1;

            //0 = non inversion on horizontal filtering 1 = inversion between low-pass and high-pass filtering
            int cas_col = tr[cur_res].y0 & 1;

            int sn = (int)rh1;
            int dn = (int)(rh - rh1);

            // Perform vertical pass
            if (num_threads <= 1 || rw < 2 * NB_ELTS_V8)
            {
                int j = 0;
                for (; j + NB_ELTS_V8 - 1 < rw; j += NB_ELTS_V8)
                {
                    encode_and_deinterleave_v(tiledp, j, bj, rh, cas_col == 0, w, NB_ELTS_V8);
                }

                if (j < rw)
                {
                    encode_and_deinterleave_v(tiledp, j, bj, rh, cas_col == 0, w, rw - (uint)j);
                }
            }
            else
            {
                int num_jobs = num_threads;

                if (rw < num_jobs)
                {
                    num_jobs = (int)rw;
                }

                uint step_j = ((rw / (uint)num_jobs) / NB_ELTS_V8) * NB_ELTS_V8;

                var jobs = new List<encode_v_job>(num_jobs);

                for (uint j = 0; j < num_jobs; j++)
                {
                    var job = new encode_v_job(
                        new dwt_local()
                        {
                            mem = ArrayPool<int>.Shared.Rent((int)data_size),
                            dn = dn,
                            sn = sn,
                            cas = cas_col
                        },
                        rh,
                        w,
                        tiledp,
                        0,
                        j * step_j,
                        (j + 1 == num_jobs) ? rw : (j + 1) * step_j,
                        encode_and_deinterleave_v
                    );

                    jobs.Add(job);
                }

                Parallelism.RunAll(jobs, encode_v_func);
                foreach (var job in jobs)
                {
                    ArrayPool<int>.Shared.Return(job.v.mem);
                }
            }

            sn = (int)rw1;
            dn = (int)(rw - rw1);

            // Perform horizontal pass
            if (num_threads <= 1 || rh <= 1)
            {
                for (int j = 0; j < rh; j++)
                {
                    encode_and_deinterleave_h_one_row(tiledp, j * (int)w, bj, rw, cas_row == 0);
                }
            }
            else
            {
                int num_jobs = num_threads;

                if (rh < num_jobs)
                {
                    num_jobs = (int)rh;
                }

                uint step_j = rh / (uint)num_jobs;

                var jobs = new List<encode_h_job>(num_jobs);

                for (uint j = 0; j < num_jobs; j++)
                {
                    var max_j = (j + 1) * step_j;
                    if (j == num_jobs - 1)
                        max_j = rh;

                    var job = new encode_h_job(
                        new dwt_local()
                        {
                            mem = ArrayPool<int>.Shared.Rent((int)data_size),
                            dn = dn,
                            sn = sn,
                            cas = cas_row
                        },
                        rw,
                        w,
                        tiledp,
                        0,
                        j * step_j,
                        max_j,
                        encode_and_deinterleave_h_one_row
                    );

                    jobs.Add(job);
                }

                Parallelism.RunAll(jobs, encode_h_func);
                foreach (var job in jobs)
                {
                    ArrayPool<int>.Shared.Return(job.h.mem);
                }
            }

            cur_res = last_res;

            last_res--;
        }

        return true; ;
    }

    //2.5 - opj_dwt_encode_h_func
    private static void encode_h_func(encode_h_job job)
    {
        for (uint j = job.min_j; j < job.max_j; j++)
        {
            job.fn(job.tiled, job.tiledp + (int)j * (int)job.w, job.h.mem, job.rw, job.h.cas == 0);
        }
    }

    //2.5 - opj_dwt_encode_v_func
    private static void encode_v_func(encode_v_job job)
    {
        uint j;
        for (j = job.min_j; j + NB_ELTS_V8 - 1 < job.max_j; j += NB_ELTS_V8)
        {
            job.encode_and_deinterleave_v(job.tiled, job.tiledp + (int)j, job.v.mem, job.rh, job.v.cas == 0, job.w, NB_ELTS_V8);
        }

        if (j < job.max_j)
        {
            job.encode_and_deinterleave_v(job.tiled, job.tiledp + (int)j, job.v.mem, job.rh, job.v.cas == 0, job.w, job.max_j - j);
        }
    }

    /// <summary>
    /// Determine maximum computed resolution level for inverse wavelet transform
    /// </summary>
    /// <param name="r">Resolutions</param>
    /// <param name="i">Number of resolutions that will be decoded</param>
    /// <remarks>2.5 - opj_dwt_max_resolution</remarks>
    private static uint MaxResolution(TcdResolution[] rs, int numres)
    {
        uint mr = 0;
        for (int c = 1; c < numres; c++)
        {
            var r = rs[c];
            uint w;
            if (mr < (w = (uint)(r.x1 - r.x0)))
                mr = w;
            if (mr < (w = (uint)(r.y1 - r.y0)))
                mr = w;
        }
        return mr;
    }

    //2.5 - opj_dwt_calc_explicit_stepsizes
    internal static void CalcExplicitStepsizes(TileCompParams tccp, uint prec)
    {
        uint numbands = 3 * tccp.numresolutions - 2;
        for (uint bandno = 0; bandno < numbands; bandno++)
        {
            double stepsize;

            var resno = (bandno == 0) ? 0 : ((bandno - 1) / 3 + 1);
            var orient = (bandno == 0) ? 0 : ((bandno - 1) % 3 + 1);
            var level = tccp.numresolutions - 1 - resno;
            var gain = (tccp.qmfbid == 0) ? 0U : ((orient == 0) ? 0U : (((orient == 1) ||
                                                                         (orient == 2)) ? 1U : 2U));
            if (tccp.qntsty == CCP_QNTSTY.NOQNT)
            {
                stepsize = 1.0;
            }
            else
            {
                double norm = GetNormReal(level, orient);
                stepsize = (1 << ((int)gain)) / norm;
            }
            EncodeStepsize((int)Math.Floor(stepsize * 8192.0), (int)(prec + gain), out tccp.stepsizes[bandno]);
        }
    }

    //2.5 - opj_dwt_getnorm_real
    private static double GetNormReal(uint level, uint orient)
    {
        //This is just a band-aid to avoid a buffer overflow
        if (orient == 0 && level >= 10)
        {
            level = 9;
        }
        else if (orient > 0 && level >= 9)
        {
            level = 8;
        }

        return dwt_norms_real[orient][level];
    }

    /// <remarks>2.5 - opj_dwt_encode_stepsize</remarks>
    private static void EncodeStepsize(int stepsize, int numbps, out StepSize bandno_stepsize)
    {
        var p = MyMath.int_floorlog2(stepsize) - 13;
        var n = 11 - MyMath.int_floorlog2(stepsize);
        bandno_stepsize = new StepSize(
            numbps - p,
            (n < 0 ? stepsize >> -n : stepsize << n) & 0x7ff);
    }

    //2.5 - opj_dwt_decode_real
    internal static bool DecodeReal(TileCoder tcd, TcdTilecomp tilec, uint numres)
    {
        if (tcd.WholeTileDecoding)
        {
            return decode_tile_97(tilec, numres, tcd.DisableMultiThreading);
        }

        return decode_partial_97(tilec, numres);
    }

    //2.5 - opj_dwt_decode_partial_97
    private static bool decode_partial_97(TcdTilecomp tilec, uint numres)
    {
        v4dwt_local h = new v4dwt_local();
        v4dwt_local v = new v4dwt_local();
        // This value matches the maximum left/right extension given in tables
        // F.2 and F.3 of the standard. Note: in opj_tcd_is_subband_area_of_interest()
        // we currently use 3.
        const uint filter_width = 4U;

        TcdResolution[] tr_ar = tilec.resolutions;
        int tr_pos = 0;
        TcdResolution tr = tr_ar[tr_pos], tr_max = tr_ar[numres - 1];

        //Width of the resolution level computed
        uint rw = (uint)(tr.x1 - tr.x0);

        //Height of the resolution level computed
        uint rh = (uint)(tr.y1 - tr.y0);

        // Compute the intersection of the area of interest, expressed in tile coordinates
        // with the tile coordinates
        uint win_tcx0 = tilec.win_x0;
        uint win_tcy0 = tilec.win_y0;
        uint win_tcx1 = tilec.win_x1;
        uint win_tcy1 = tilec.win_y1;

        if (tr_max.x0 == tr_max.x1 || tr_max.y0 == tr_max.y1)
        {
            return true;
        }

        var sa = SparseArrayInt32.Init(tilec, numres);
        if (sa is null)
        {
            return false;
        }

        if (numres == 1U)
        {
            bool ret = sa.read(tr_max.win_x0 - (uint)tr_max.x0,
                               tr_max.win_y0 - (uint)tr_max.y0,
                               tr_max.win_x1 - (uint)tr_max.x0,
                               tr_max.win_y1 - (uint)tr_max.y0,
                               tilec.data_win, 0,
                               1, tr_max.win_x1 - tr_max.win_x0,
                               true);
            Debug.Assert(ret);
            return true;
        }
        ulong data_size = MaxResolution(tr_ar, (int)numres);
        // overflow check
        // C#
        if (data_size > (Constants.SIZE_MAX / NB_ELTS_V8 * (sizeof(float))))
        {
            return false;
        }
        h.wavelet = new float[data_size * NB_ELTS_V8];
        v.wavelet = h.wavelet;

        for (uint resno = 1; resno < numres; resno++)
        {
            uint j;
            /* Window of interest subband-based coordinates */
            /* Window of interest tile-resolution-based coordinates */
            uint win_tr_x0, win_tr_x1, win_tr_y0, win_tr_y1;
            /* Tile-resolution subband-based coordinates */

            tr = tr_ar[++tr_pos];

            h.sn = (int)rw;
            v.sn = (int)rh;

            rw = (uint)(tr.x1 - tr.x0);
            rh = (uint)(tr.y1 - tr.y0);

            h.dn = (int)(rw - (uint)h.sn);
            h.cas = tr.x0 % 2;

            v.dn = (int)(rh - (uint)v.sn);
            v.cas = tr.y0 % 2;

            // Get the subband coordinates for the window of interest
            // LL band
            GetBandCoordinates(tilec, resno, 0,
                               win_tcx0, win_tcy0, win_tcx1, win_tcy1,
                               out var win_ll_x0, out var win_ll_y0,
                               out var win_ll_x1, out var win_ll_y1);

            // HL band
            GetBandCoordinates(tilec, resno, 1,
                               win_tcx0, win_tcy0, win_tcx1, win_tcy1,
                               out var win_hl_x0, out _, out var win_hl_x1, out _);

            /* LH band */
            GetBandCoordinates(tilec, resno, 2,
                               win_tcx0, win_tcy0, win_tcx1, win_tcy1,
                               out _, out var win_lh_y0, out _, out var win_lh_y1);

            /* Beware: band index for non-LL0 resolution are 0=HL, 1=LH and 2=HH */
            uint tr_ll_x0 = (uint)tr.bands[1].x0;
            uint tr_ll_y0 = (uint)tr.bands[0].y0;
            uint tr_hl_x0 = (uint)tr.bands[0].x0;
            uint tr_lh_y0 = (uint)tr.bands[1].y0;

            /* Subtract the origin of the bands for this tile, to the subwindow */
            /* of interest band coordinates, so as to get them relative to the */
            /* tile */
            win_ll_x0 = MyMath.uint_subs(win_ll_x0, tr_ll_x0);
            win_ll_y0 = MyMath.uint_subs(win_ll_y0, tr_ll_y0);
            win_ll_x1 = MyMath.uint_subs(win_ll_x1, tr_ll_x0);
            win_ll_y1 = MyMath.uint_subs(win_ll_y1, tr_ll_y0);
            win_hl_x0 = MyMath.uint_subs(win_hl_x0, tr_hl_x0);
            win_hl_x1 = MyMath.uint_subs(win_hl_x1, tr_hl_x0);
            win_lh_y0 = MyMath.uint_subs(win_lh_y0, tr_lh_y0);
            win_lh_y1 = MyMath.uint_subs(win_lh_y1, tr_lh_y0);

            SegmentGrow(filter_width, (uint)h.sn, ref win_ll_x0, ref win_ll_x1);
            SegmentGrow(filter_width, (uint)h.dn, ref win_hl_x0, ref win_hl_x1);

            SegmentGrow(filter_width, (uint)v.sn, ref win_ll_y0, ref win_ll_y1);
            SegmentGrow(filter_width, (uint)v.dn, ref win_lh_y0, ref win_lh_y1);

            /* Compute the tile-resolution-based coordinates for the window of interest */
            if (h.cas == 0)
            {
                win_tr_x0 = Math.Min(2 * win_ll_x0, 2 * win_hl_x0 + 1);
                win_tr_x1 = Math.Min(Math.Max(2 * win_ll_x1, 2 * win_hl_x1 + 1), rw);
            }
            else
            {
                win_tr_x0 = Math.Min(2 * win_hl_x0, 2 * win_ll_x0 + 1);
                win_tr_x1 = Math.Min(Math.Max(2 * win_hl_x1, 2 * win_ll_x1 + 1), rw);
            }

            if (v.cas == 0)
            {
                win_tr_y0 = Math.Min(2 * win_ll_y0, 2 * win_lh_y0 + 1);
                win_tr_y1 = Math.Min(Math.Max(2 * win_ll_y1, 2 * win_lh_y1 + 1), rh);
            }
            else
            {
                win_tr_y0 = Math.Min(2 * win_lh_y0, 2 * win_ll_y0 + 1);
                win_tr_y1 = Math.Min(Math.Max(2 * win_lh_y1, 2 * win_ll_y1 + 1), rh);
            }

            h.win_l_x0 = win_ll_x0;
            h.win_l_x1 = win_ll_x1;
            h.win_h_x0 = win_hl_x0;
            h.win_h_x1 = win_hl_x1;
            for (j = 0; j + (NB_ELTS_V8 - 1) < rh; j += NB_ELTS_V8)
            {
                if ((j + (NB_ELTS_V8 - 1) >= win_ll_y0 && j < win_ll_y1) ||
                        (j + (NB_ELTS_V8 - 1) >= win_lh_y0 + (uint)v.sn &&
                        j < win_lh_y1 + (uint)v.sn))
                {
                    v8dwt_interleave_partial_h(h, sa, j, Math.Min(NB_ELTS_V8, rh - j));
                    v8dwt_decode(h);
                    if (!sa.write(win_tr_x0, j,
                                  win_tr_x1, j + NB_ELTS_V8,
                                  h.wavelet, (int)win_tr_x0 * NB_ELTS_V8, //C# Wavlet indexing, therefore mul with NB_ELTS_V8
                                  NB_ELTS_V8, 1, true))
                    {
                        return false;
                    }
                }
            }

            if (j < rh &&
                ((j + (NB_ELTS_V8 - 1) >= win_ll_y0 && j < win_ll_y1) ||
                 (j + (NB_ELTS_V8 - 1) >= win_lh_y0 + (uint)v.sn &&
                  j < win_lh_y1 + (uint)v.sn)))
            {
                v8dwt_interleave_partial_h(h, sa, j, rh - j);
                v8dwt_decode(h);
                if (!sa.write(win_tr_x0, j,
                              win_tr_x1, rh,
                              h.wavelet, (int)win_tr_x0 * NB_ELTS_V8,
                              NB_ELTS_V8, 1, true))
                {
                    return false;
                }
            }

            v.win_l_x0 = win_ll_y0;
            v.win_l_x1 = win_ll_y1;
            v.win_h_x0 = win_lh_y0;
            v.win_h_x1 = win_lh_y1;
            for (j = win_tr_x0; j < win_tr_x1; j += NB_ELTS_V8)
            {
                uint nb_elts = Math.Min(NB_ELTS_V8, win_tr_x1 - j);

                v8dwt_interleave_partial_v(v, sa, j, nb_elts);
                v8dwt_decode(v);
                if (!sa.write(j, win_tr_y0,
                              j + nb_elts, win_tr_y1,
                              v.wavelet, (int)win_tr_y0 * NB_ELTS_V8,
                              1, NB_ELTS_V8, true))
                {
                    return false;
                }
            }
        }

        {
            bool ret = sa.read(
                           tr_max.win_x0 - (uint)tr_max.x0,
                           tr_max.win_y0 - (uint)tr_max.y0,
                           tr_max.win_x1 - (uint)tr_max.x0,
                           tr_max.win_y1 - (uint)tr_max.y0,
                           tilec.data_win, 0,
                           1, tr_max.win_x1 - tr_max.win_x0,
                           true);
            Debug.Assert(ret);
        }

        return true;
    }

    /// <summary>
    /// Inverse 9-7 wavelet transform in 2-D.
    /// </summary>
    /// <remarks>2.5.1 - opj_dwt_decode_tile</remarks>
    // The 9/7 DWT methods marked "see decode_tile_97" are fully optimised from the first call. Called once per
    // tile or per 8 rows, they otherwise run as tier-0 or OSR code for most of a first decode: four 9/7 samples,
    // one decode per process, DWT 280 -> 83 ms; warm unchanged. Plain vector loops, nothing for dynamic PGO.
#if NETCOREAPP3_0_OR_GREATER
    [MethodImpl(MethodImplOptions.AggressiveOptimization)] // 9/7 DWT: see decode_tile_97
#endif
    private static bool decode_tile_97(TcdTilecomp tilec, uint numres, bool disable_multi_threading)
    {
        v4dwt_local h = new v4dwt_local();
        v4dwt_local v = new v4dwt_local();

        TcdResolution[] res_ar = tilec.resolutions;
        int res_ar_pos = 0;
        TcdResolution res = res_ar[res_ar_pos];

        //Width of the resolution level computed
        uint rw = (uint)(res.x1 - res.x0);

        //Height of the resolution level computed
        uint rh = (uint)(res.y1 - res.y0);

        int w = tilec.resolutions[tilec.minimum_num_resolutions - 1].x1
              - tilec.resolutions[tilec.minimum_num_resolutions - 1].x0;

        // Not entirely sure for the return code of w == 0 which is triggered per
        // https://github.com/uclouvain/openjpeg/issues/1505
        if (numres == 1U || w == 0)
        {
            return true;
        }
        var num_threads = disable_multi_threading ? 0 : Parallelism.ThreadCount(false);

        ulong data_size = MaxResolution(res_ar, (int)numres);
        /* overflow check */
        if (data_size > (Constants.SIZE_MAX / NB_ELTS_V8 * sizeof(float)))
        {
            return false;
        }

        h.wavelet = new float[data_size * NB_ELTS_V8];
        v.wavelet = h.wavelet;

        while (--numres != 0)
        {
            int[] aj_ar = tilec.data;
            var ajf = MemoryMarshal.Cast<int, float>(aj_ar.AsSpan()); // tile data holds float bits here
            int aj = 0, j;

            h.sn = (int)rw;
            v.sn = (int)rh;

            res = res_ar[++res_ar_pos];

            rw = (uint)(res.x1 - res.x0);
            rh = (uint)(res.y1 - res.y0);

            h.dn = (int)(rw - (uint)h.sn);
            h.cas = res.x0 % 2;

            h.win_l_x0 = 0;
            h.win_l_x1 = (uint)h.sn;
            h.win_h_x0 = 0;
            h.win_h_x1 = (uint)h.dn;

            int num_jobs = DwtJobCount(num_threads, rh / NB_ELTS_V8, (ulong)rw * rh);
            if (num_jobs <= 1)
            {
                for (j = 0; j + (NB_ELTS_V8 - 1) < rh; j += NB_ELTS_V8)
                {
                    v8dwt_interleave_h(h, aj_ar, aj, w, NB_ELTS_V8);
                    v8dwt_decode(h);

                    WriteBackRows8(h.wavelet, ajf, aj, w, (int)rw);

                    aj += w * NB_ELTS_V8;
                }
            }
            else
            {
                uint step_j = ((rh / (uint)num_jobs) / NB_ELTS_V8) * NB_ELTS_V8;

                var jobs = new List<dwt97_decode_h_job>(num_jobs);

                for (j = 0; j < num_jobs; j++)
                {
                    var job = new dwt97_decode_h_job(
                        h.Clone(), rw, (uint)w, aj_ar, aj,
                        (j + 1 == num_jobs) ? (rh & unchecked((uint)~(NB_ELTS_V8 - 1))) - (uint)j * step_j : step_j
                    );
                    job.h.wavelet = ArrayPool<float>.Shared.Rent(h.wavelet.Length);

                    aj += (int)(w * job.nb_rows);

                    jobs.Add(job);
                }
                Parallelism.RunAll(jobs, dwt97_decode_h_func);
                foreach (var job in jobs)
                {
                    ArrayPool<float>.Shared.Return(job.h.wavelet);
                }
                j = (int)(rh & unchecked((uint)~(NB_ELTS_V8 - 1)));
            }

            if (j < rh)
            {

                v8dwt_interleave_h(h, aj_ar, aj, w, rh - (uint)j);
                v8dwt_decode(h);

                for (int k = 0; k < rw; k++)
                {
                    int k_wavelet = k * NB_ELTS_V8, ajk = aj + k;
                    for (uint l = 0; l < rh - j; l++)
                    {
                        ajf[(int)(ajk + w * l)] = h.wavelet[k_wavelet + l];
                    }
                }
            }

            v.dn = (int)(rh - (uint)v.sn);
            v.cas = res.y0 % 2;
            v.win_l_x0 = 0;
            v.win_l_x1 = (uint)v.sn;
            v.win_h_x0 = 0;
            v.win_h_x1 = (uint)v.dn;

            aj = 0;
            // Scaling is poor (see below): at most half the threads.
            num_jobs = DwtJobCount(Math.Max(num_threads / 2, num_threads > 1 ? 2 : 0), rw / NB_ELTS_V8, (ulong)rw * rh);
            if (num_jobs <= 1)
            {
                aj += VerticalStrips97(v, aj_ar, aj, w, (int)rh, (int)(rw & ~(uint)(NB_ELTS_V8 - 1)));
            }
            else
            {
                /* "bench_dwt -I" shows that scaling is poor, likely due to RAM
                    transfer being the limiting factor. So limit the number of
                    threads.
                    C# note: I've not run this benchmark
                 */
                uint step_j = ((rw / (uint)num_jobs) / NB_ELTS_V8) * NB_ELTS_V8;

                var jobs = new List<dwt97_decode_v_job>(num_jobs);

                for (j = 0; j < num_jobs; j++)
                {
                    var job = new dwt97_decode_v_job(
                        v.Clone(), rh, (uint)w, aj_ar, aj,
                        (j + 1 == num_jobs) ? (rw & unchecked((uint)~(NB_ELTS_V8 - 1))) - (uint)j * step_j : step_j
                    );
                    job.v.wavelet = ArrayPool<float>.Shared.Rent(v.wavelet.Length);

                    aj += (int)(job.nb_columns);

                    jobs.Add(job);
                }
                Parallelism.RunAll(jobs, dwt97_decode_v_func);
                foreach (var job in jobs)
                {
                    ArrayPool<float>.Shared.Return(job.v.wavelet);
                }
            }

            //Makes sure not not overflow the array by copying "less than 4 floats".
            if ((rw & (NB_ELTS_V8 - 1)) != 0)
            {
                j = (int)(rw & (NB_ELTS_V8 - 1));
                v8dwt_interleave_v(v, aj_ar, aj, w, j);
                v8dwt_decode(v);

                for (int k = 0; k < rh; ++k)
                {
                    Buffer.BlockCopy(v.wavelet, k * NB_ELTS_V8 * sizeof(float), aj_ar, (aj + (k * w)) * sizeof(float), j * sizeof(float));
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Inverse 9-7 wavelet transform in 1-D.
    /// </summary>
    /// <remarks>
    /// 2.5
    /// </remarks>
#if NETCOREAPP3_0_OR_GREATER
    [MethodImpl(MethodImplOptions.AggressiveOptimization)] // 9/7 DWT: see decode_tile_97
#endif
    private static void v8dwt_decode(v4dwt_local dwt)
    {
        /* BUG_WEIRD_TWO_INVK (look for this identifier in tcd.c) */
        /* Historic value for 2 / opj_invK */
        /* Normally, we should use invK, but if we do so, we have failures in the */
        /* conformance test, due to MSE and peak errors significantly higher than */
        /* accepted value */
        /* Due to using two_invK instead of invK, we have to compensate in tcd.c */
        /* the computation of the stepsize for the non LL subbands */
        const float two_invK = 1.625732422f;

        int a, b;
        if (dwt.cas == 0)
        {
            if (!((dwt.dn > 0) || (dwt.sn > 1)))
                return;
            a = 0;
            b = 1;
        }
        else
        {
            if (!((dwt.sn > 0) || (dwt.dn > 1)))
                return;
            a = 1;
            b = 0;
        }

        // SIMD (upstream opj_v8dwt_decode_step1/2_sse/avx) is in v8dwt_decode_step1/2, via Vector<float>.

        //C# Both a and b index dwt.wavelet, hench why we have to mul with NB_ELTS_V8
        v8dwt_decode_step1(dwt.wavelet, a * NB_ELTS_V8, (int)dwt.win_l_x0, (int)dwt.win_l_x1, K);
        v8dwt_decode_step1(dwt.wavelet, b * NB_ELTS_V8, (int)dwt.win_h_x0, (int)dwt.win_h_x1, two_invK);
        v8dwt_decode_step2(dwt.wavelet, b * NB_ELTS_V8, (a + 1) * NB_ELTS_V8, (int)dwt.win_l_x0, (int)dwt.win_l_x1,
            Math.Min(dwt.sn, dwt.dn - a), -dwt_delta);
        v8dwt_decode_step2(dwt.wavelet, a * NB_ELTS_V8, (b + 1) * NB_ELTS_V8, (int)dwt.win_h_x0, (int)dwt.win_h_x1,
            Math.Min(dwt.dn, dwt.sn - b), -dwt_gamma);
        v8dwt_decode_step2(dwt.wavelet, b * NB_ELTS_V8, (a + 1) * NB_ELTS_V8, (int)dwt.win_l_x0, (int)dwt.win_l_x1,
            Math.Min(dwt.sn, dwt.dn - a), -dwt_beta);
        v8dwt_decode_step2(dwt.wavelet, a * NB_ELTS_V8, (b + 1) * NB_ELTS_V8, (int)dwt.win_h_x0, (int)dwt.win_h_x1,
            Math.Min(dwt.dn, dwt.sn - b), -dwt_alpha);
    }

    /// <summary>
    /// Wavelet decode step 1
    /// </summary>
    /// <remarks>
    /// 2.5 - opj_v8dwt_decode_step1
    /// </remarks>
    /// <param name="wavelet">The array with wavelet</param>
    /// <param name="start">Position of the wavelet in the array</param>
    /// <param name="end">Wavelet count</param>
    /// <param name="c">Some constant</param>
#if NETCOREAPP3_0_OR_GREATER
    [MethodImpl(MethodImplOptions.AggressiveOptimization)] // 9/7 DWT: see decode_tile_97
#endif
    private static void v8dwt_decode_step1(float[] f, int fw, int start, int end, float c)
    {
        if (UseVectors)
        {
            // Each wavelet element is NB_ELTS_V8 floats: one or two Vector<float>. Same single multiply
            // per lane as the scalar code, so the result is bit-identical (like upstream's SSE/AVX path).
            var v = MemoryMarshal.Cast<float, Vector<float>>(f.AsSpan());
            var vc = new Vector<float>(c);
            // Vector indices, stepped directly: offsets are non-negative multiples of NB_ELTS_V8.
            int e = (int)((uint)fw / (uint)VecF) + start * 2 * VecPerElt;
            int n = end - start;
            if (n <= 0)
                return;
#if !NETSTANDARD2_1 // no Unsafe class there (the other targets get it in-box or through System.Memory)
            if ((uint)(e + n * 2 * VecPerElt - VecPerElt) <= (uint)v.Length)
            {
                // Range checked once above: unchecked accesses, 4x unrolled like upstream's _sse version.
                ref Vector<float> r = ref Unsafe.Add(ref MemoryMarshal.GetReference(v), e);
                int i = 0;
                if (VecPerElt == 1)
                {
                    for (; i + 3 < n; i += 4, r = ref Unsafe.Add(ref r, 8))
                    {
                        r *= vc;
                        Unsafe.Add(ref r, 2) *= vc;
                        Unsafe.Add(ref r, 4) *= vc;
                        Unsafe.Add(ref r, 6) *= vc;
                    }
                }
                for (; i < n; i++, r = ref Unsafe.Add(ref r, 2 * VecPerElt))
                {
                    for (int k = 0; k < VecPerElt; k++)
                    {
                        Unsafe.Add(ref r, k) *= vc;
                    }
                }
                return;
            }
#endif
            for (int i = start; i < end; ++i, e += 2 * VecPerElt)
            {
                for (int k = 0; k < VecPerElt; k++)
                {
                    v[e + k] *= vc;
                }
            }
            return;
        }

        // To be adapted if NB_ELTS_V8 changes
        for (int i = start; i < end; ++i)
        {
            f[fw + i * 2 * 8 + 0] = f[fw + i * 2 * 8 + 0] * c;
            f[fw + i * 2 * 8 + 1] = f[fw + i * 2 * 8 + 1] * c;
            f[fw + i * 2 * 8 + 2] = f[fw + i * 2 * 8 + 2] * c;
            f[fw + i * 2 * 8 + 3] = f[fw + i * 2 * 8 + 3] * c;
            f[fw + i * 2 * 8 + 4] = f[fw + i * 2 * 8 + 4] * c;
            f[fw + i * 2 * 8 + 5] = f[fw + i * 2 * 8 + 5] * c;
            f[fw + i * 2 * 8 + 6] = f[fw + i * 2 * 8 + 6] * c;
            f[fw + i * 2 * 8 + 7] = f[fw + i * 2 * 8 + 7] * c;
        }
    }

    //2.5 - opj_v8dwt_encode_step1
    private static void v8dwt_encode_step1(int[] f, int fw, uint end, float cst)
    {
        var fwi = new IntOrFloat();
        for (int i = 0; i < end; ++i)
        {
            for (int c = 0; c < NB_ELTS_V8; c++)
            {
                fwi.I = f[fw + i * 2 * NB_ELTS_V8 + c];
                fwi.F *= cst;
                f[fw + i * 2 * NB_ELTS_V8 + c] = fwi.I;
            }
        }
    }

    //2.5 - opj_v8dwt_encode_step2
    private static void v8dwt_encode_step2(int[] f, int fl, int fw, uint end, uint m, float cst)
    {
        uint imax = Math.Min(end, m);
        var fli = new IntOrFloat();
        var fwi = new IntOrFloat();
        //Snip SSE code

        if (imax > 0)
        {
            for (int c = 0; c < NB_ELTS_V8; c++)
            {
                fli.I = f[fl + 0 * NB_ELTS_V8 + c];
                fwi.I = f[fw + 0 * NB_ELTS_V8 + c];
                fli.F = (fli.F + fwi.F) * cst;
                fwi.I = f[fw + -1 * NB_ELTS_V8 + c];
                fwi.F += fli.F;
                f[fw + -1 * NB_ELTS_V8 + c] = fwi.I;
            }
            fw += 2 * NB_ELTS_V8;
            for (int i = 1; i < imax; i++)
            {
                for (int c = 0; c < NB_ELTS_V8; c++)
                {
                    fli.I = f[fw + -2 * NB_ELTS_V8 + c];
                    fwi.I = f[fw + 0 * NB_ELTS_V8 + c];
                    fli.F = (fli.F + fwi.F) * cst;
                    fwi.I = f[fw + -1 * NB_ELTS_V8 + c];
                    fwi.F += fli.F;
                    f[fw + -1 * NB_ELTS_V8 + c] = fwi.I;
                }
                fw += 2 * NB_ELTS_V8;
            }
        }
        if (m < end)
        {
            Debug.Assert(m + 1 == end);
            for (int c = 0; c < NB_ELTS_V8; c++)
            {
                fwi.I = f[fw + -2 * NB_ELTS_V8 + c];
                fli.F = (2 * fwi.F) * cst;
                fwi.I = f[fw + -1 * NB_ELTS_V8 + c];
                fwi.F += fli.F;
                f[fw + -1 * NB_ELTS_V8 + c] = fwi.I;
            }
        }
    }

    //2.5 - opj_v8dwt_decode_step2
#if NETCOREAPP3_0_OR_GREATER
    [MethodImpl(MethodImplOptions.AggressiveOptimization)] // 9/7 DWT: see decode_tile_97
#endif
    private static void v8dwt_decode_step2(float[] f, int fl, int fw, int start, int end, int m, float c)
    {
        int imax = Math.Min(end, m);
        if (start > 0)
        {
            fw += 2 * NB_ELTS_V8 * start;
            fl = fw - 2 * NB_ELTS_V8;
        }
        if (UseVectors)
        {
            // f[w - 1] += (f[l] + f[w]) * c, lane-wise; same operation order as the scalar code.
            var v = MemoryMarshal.Cast<float, Vector<float>>(f.AsSpan());
            var vc = new Vector<float>(c);
            // Vector indices, stepped directly: offsets are non-negative multiples of NB_ELTS_V8.
            int vw = (int)((uint)fw / (uint)VecF), vl = (int)((uint)fl / (uint)VecF);
            int n = Math.Max(imax - start, 0);
#if !NETSTANDARD2_1
            // Highest index touched: vw + 2n - 1 with the m < end tail, vw + 2n - 2 without.
            if (VecPerElt == 1 && vw >= 1 && vl >= 0 && vw + 2 * n - (m < end ? 0 : 1) <= v.Length && vl < v.Length)
            {
                // Range checked once above. Like upstream's _sse version, the right neighbour of each
                // update is carried to the next one in a register: two loads per element instead of three.
                ref Vector<float> r0 = ref MemoryMarshal.GetReference(v);
                ref Vector<float> w = ref Unsafe.Add(ref r0, vw);
                var left = Unsafe.Add(ref r0, vl);
                for (int i = 0; i < n; i++, w = ref Unsafe.Add(ref w, 2))
                {
                    var right = w;
                    ref Vector<float> o = ref Unsafe.Subtract(ref w, 1);
                    o = o + ((left + right) * vc);
                    left = right;
                }
                if (m < end)
                {
                    Debug.Assert(m + 1 == end);
                    vc = new Vector<float>(c + c);
                    ref Vector<float> o = ref Unsafe.Subtract(ref w, 1);
                    o = o + (left * vc);
                }
                return;
            }
#endif
            for (int i = start; i < imax; ++i)
            {
                int vo = vw - VecPerElt;
                for (int k = 0; k < VecPerElt; k++)
                {
                    v[vo + k] = v[vo + k] + ((v[vl + k] + v[vw + k]) * vc);
                }
                vl = vw;
                vw += 2 * VecPerElt;
            }
            if (m < end)
            {
                Debug.Assert(m + 1 == end);
                vc = new Vector<float>(c + c);
                int vo = vw - VecPerElt;
                for (int k = 0; k < VecPerElt; k++)
                {
                    v[vo + k] = v[vo + k] + (v[vl + k] * vc);
                }
            }
            return;
        }
        /* To be adapted if NB_ELTS_V8 changes */
        for (int i = start; i < imax; ++i)
        {
            //It's not a bug to have greater precision, it's a problem for verifying
            //the result against original library.
            f[fw - 8] = f[fw - 8] + ((f[fl + 0] + f[fw + 0]) * c);
            f[fw - 7] = f[fw - 7] + ((f[fl + 1] + f[fw + 1]) * c);
            f[fw - 6] = f[fw - 6] + ((f[fl + 2] + f[fw + 2]) * c);
            f[fw - 5] = f[fw - 5] + ((f[fl + 3] + f[fw + 3]) * c);
            f[fw - 4] = f[fw - 4] + ((f[fl + 4] + f[fw + 4]) * c);
            f[fw - 3] = f[fw - 3] + ((f[fl + 5] + f[fw + 5]) * c);
            f[fw - 2] = f[fw - 2] + ((f[fl + 6] + f[fw + 6]) * c);
            f[fw - 1] = f[fw - 1] + ((f[fl + 7] + f[fw + 7]) * c);
            fl = fw;
            fw += 2 * NB_ELTS_V8;
        }
        if (m < end)
        {
            Debug.Assert(m + 1 == end);
            c += c;
            f[fw - 8] = f[fw - 8] + (f[fl + 0] * c);
            f[fw - 7] = f[fw - 7] + (f[fl + 1] * c);
            f[fw - 6] = f[fw - 6] + (f[fl + 2] * c);
            f[fw - 5] = f[fw - 5] + (f[fl + 3] * c);
            f[fw - 4] = f[fw - 4] + (f[fl + 4] * c);
            f[fw - 3] = f[fw - 3] + (f[fl + 5] * c);
            f[fw - 2] = f[fw - 2] + (f[fl + 6] * c);
            f[fw - 1] = f[fw - 1] + (f[fl + 7] * c);
        }
    }

    //2.5
    private static void EncodeStep1Combined(int[] f, int fw, uint iters_c1, uint iters_c2, float c1, float c2)
    {
        IntOrFloat fw1 = new IntOrFloat();
        uint i = 0;
        uint iters_common = Math.Min(iters_c1, iters_c2);
        Debug.Assert(Math.Abs((int)iters_c1 - (int)iters_c2) <= 1);
        for (; i + 3 < iters_common; i += 4)
        {
            fw1.I = f[fw + 0];
            fw1.F *= c1;
            f[fw + 0] = fw1.I;

            fw1.I = f[fw + 1];
            fw1.F *= c2;
            f[fw + 1] = fw1.I;

            fw1.I = f[fw + 2];
            fw1.F *= c1;
            f[fw + 2] = fw1.I;

            fw1.I = f[fw + 3];
            fw1.F *= c2;
            f[fw + 3] = fw1.I;

            fw1.I = f[fw + 4];
            fw1.F *= c1;
            f[fw + 4] = fw1.I;

            fw1.I = f[fw + 5];
            fw1.F *= c2;
            f[fw + 5] = fw1.I;

            fw1.I = f[fw + 6];
            fw1.F *= c1;
            f[fw + 6] = fw1.I;

            fw1.I = f[fw + 7];
            fw1.F *= c2;
            f[fw + 7] = fw1.I;

            fw += 8;
        }

        for (; i < iters_common; i++)
        {
            fw1.I = f[fw + 0];
            fw1.F *= c1;
            f[fw + 0] = fw1.I;

            fw1.I = f[fw + 1];
            fw1.F *= c2;
            f[fw + 1] = fw1.I;

            fw += 2;
        }

        if (i < iters_c1)
        {
            fw1.I = f[fw + 0];
            fw1.F *= c1;
            f[fw + 0] = fw1.I;
        }
        else if (i < iters_c2)
        {
            fw1.I = f[fw + 1];
            fw1.F *= c2;
            f[fw + 1] = fw1.I;
        }
    }

    //2.5
    private static void EncodeStep2(int[] f, int fl, int fw, uint end, uint m, float c)
    {
        IntOrFloat fw1 = new IntOrFloat(), fw2 = new IntOrFloat();
        uint imax = Math.Min(end, m);
        if (imax > 0)
        {
            //fw[-1] += (fl[0] + fw[0]) * c;
            fw1.I = f[fl + 0];
            fw2.I = f[fw + 0];
            fw2.F = (fw1.F + fw2.F) * c;
            fw1.I = f[fw - 1];
            fw1.F += fw2.F;
            f[fw - 1] = fw1.I;
            fw += 2;
            int i = 1;
            for (; i + 3 < imax; i += 4)
            {
                //fw[-1] += (fw[-2] + fw[0]) * c;
                fw1.I = f[fw - 2];
                fw2.I = f[fw + 0];
                fw2.F = (fw1.F + fw2.F) * c;
                fw1.I = f[fw - 1];
                fw1.F += fw2.F;
                f[fw - 1] = fw1.I;

                //fw[1] += (fw[0] + fw[2]) * c;
                fw1.I = f[fw + 0];
                fw2.I = f[fw + 2];
                fw2.F = (fw1.F + fw2.F) * c;
                fw1.I = f[fw + 1];
                fw1.F += fw2.F;
                f[fw + 1] = fw1.I;

                //fw[3] += (fw[2] + fw[4]) * c;
                fw1.I = f[fw + 2];
                fw2.I = f[fw + 4];
                fw2.F = (fw1.F + fw2.F) * c;
                fw1.I = f[fw + 3];
                fw1.F += fw2.F;
                f[fw + 3] = fw1.I;

                //fw[5] += (fw[4] + fw[6]) * c;
                fw1.I = f[fw + 4];
                fw2.I = f[fw + 6];
                fw2.F = (fw1.F + fw2.F) * c;
                fw1.I = f[fw + 5];
                fw1.F += fw2.F;
                f[fw + 5] = fw1.I;

                fw += 8;
            }

            for (; i < imax; i++)
            {
                //fw[-1] += (fw[-2] + fw[0]) * c
                fw1.I = f[fw - 2];
                fw2.I = f[fw + 0];
                fw2.F = (fw1.F + fw2.F) * c;
                fw1.I = f[fw - 1];
                fw1.F += fw2.F;
                f[fw - 1] = fw1.I;

                fw += 2;
            }
        }

        if (m < end)
        {
            Debug.Assert(m + 1 == end);
            //fw[-1] += (2 * fw[-2]) * c;
            fw2.I = f[fw - 2];
            fw2.F = (2 * fw2.F) * c;
            fw1.I = f[fw - 1];
            fw1.F += fw2.F;
            f[fw - 1] = fw1.I;
        }
    }

    //2.5
    private static bool decode_tile(TcdTilecomp tilec, uint numres, bool disable_multi_threading)
    {
        dwt_local h = new dwt_local();
        dwt_local v = new dwt_local();

        TcdResolution[] tr_ar = tilec.resolutions;
        int tr_pos = 0;
        TcdResolution tr = tr_ar[tr_pos], tr_max = tr_ar[numres - 1];

        //Width of the resolution level computed
        uint rw = (uint)(tr.x1 - tr.x0);

        //Height of the resolution level computed
        uint rh = (uint)(tr.y1 - tr.y0);

        uint w = (uint)(tilec.resolutions[tilec.minimum_num_resolutions -
                                                           1].x1 -
                        tilec.resolutions[tilec.minimum_num_resolutions - 1].x0);

        if (numres == 1U)
        {
            return true;
        }

        var num_threads = Parallelism.ThreadCount(disable_multi_threading);
        ulong h_mem_size = MaxResolution(tr_ar, (int)numres);

        /* overflow check */
        if (h_mem_size > (Constants.SIZE_MAX / (2 * PARALLEL_COLS_53) / sizeof(int)))
        {
            return false;
        }
        // We need 2 * PARALLEL_COLS_53 times the height of the array, since the vertical pass processes up to
        // two strips of PARALLEL_COLS_53 columns at a time (upstream: one). No spare ints for the 64-byte offset
        // of idwt53_v_cols: the pair kernels leave the last row of tmp free, and a rent above a power of two
        // would double the pooled buffer.
        h_mem_size *= 2 * PARALLEL_COLS_53;
        h.mem = ArrayPool<int>.Shared.Rent((int)h_mem_size); // returned below; dirty is fine, as in upstream
        v.mem = h.mem;

        while (--numres != 0)
        {
            int tiledp = 0;

            tr = tr_ar[++tr_pos];
            h.sn = (int)rw;
            v.sn = (int)rh;

            rw = (uint)(tr.x1 - tr.x0);
            rh = (uint)(tr.y1 - tr.y0);

            h.dn = (int)(rw - (uint)h.sn);
            h.cas = tr.x0 % 2;

            int num_jobs = DwtJobCount(num_threads, rh, (ulong)rw * rh);
            if (num_jobs <= 1)
            {
                idwt53_h_rows(h, tilec.data, tiledp, (int)w, 0, (int)rh);
            }
            else
            {
                uint step_j = (rh / (uint)num_jobs);

                var jobs = new List<decode_h_job>(num_jobs);

                for (uint j = 0; j < num_jobs; j++)
                {
                    var max_j = (j + 1U) * step_j; // this will overflow
                    if (j == (num_jobs - 1)) //So we clamp max_j
                        max_j = rh;
                    var job = new decode_h_job(
                        h.Clone(), rw, w, tilec.data, tiledp,
                        j * step_j, max_j
                    );

                    job.h.mem = ArrayPool<int>.Shared.Rent((int)h_mem_size);

                    jobs.Add(job);
                }
                Parallelism.RunAll(jobs, decode_h_func);
                foreach (var job in jobs)
                {
                    ArrayPool<int>.Shared.Return(job.h.mem);
                }
            }

            v.dn = (int)(rh - (uint)v.sn);
            v.cas = tr.y0 % 2;

            num_jobs = DwtJobCount(num_threads, rw, (ulong)rw * rh);
            if (num_jobs <= 1)
            {
                idwt53_v_cols(v, tilec.data, tiledp, (int)w, 0, (int)rw);
            }
            else
            {
                // Not in upstream: whole cache lines (16 ints) per job. A job that ends inside a line shares it with
                // the next job, and both write it on every row: 32 jobs of 78 columns took 2.3 ms on 10026 (one
                // thread: 3.0 ms), 80 columns 0.8 ms.
                uint step_j = (rw / (uint)num_jobs + 15u) & ~15u;
                num_jobs = (int)((rw + step_j - 1) / step_j);

                var jobs = new List<decode_v_job>(num_jobs);

                for (uint j = 0; j < num_jobs; j++)
                {
                    var max_j = (j + 1U) * step_j; // this can overflow
                    if (j == (num_jobs - 1))
                        max_j = rw;
                    var job = new decode_v_job(
                        v.Clone(), rh, w, tilec.data, tiledp,
                        j * step_j, max_j
                    );
                    job.v.mem = ArrayPool<int>.Shared.Rent((int)h_mem_size);

                    jobs.Add(job);
                }
                Parallelism.RunAll(jobs, decode_v_func);
                foreach (var job in jobs)
                {
                    ArrayPool<int>.Shared.Return(job.v.mem);
                }
            }
        }

        ArrayPool<int>.Shared.Return(h.mem);
        return true;
    }

    /// <summary>Samples one DWT job should have at least: a fork/join costs more than a smaller job saves.</summary>
    private const ulong DwtMinSamplesPerJob = 65536;

    /// <summary>
    /// Jobs for one DWT pass over <paramref name="samples"/> samples that can be split into <paramref name="units"/>
    /// independent rows, columns or strips. Rows and columns are independent, so the split does not change the output.
    /// 1 or less means run inline.
    /// </summary>
    private static int DwtJobCount(int num_threads, uint units, ulong samples) =>
        (int)Math.Min((ulong)Math.Max(num_threads, 0), Math.Min(units, samples / DwtMinSamplesPerJob));

    //2.5
    private static bool DecodePartialTile(TcdTilecomp tilec, uint numres)
    {
        dwt_local h = new dwt_local();
        dwt_local v = new dwt_local();
        // This value matches the maximum left/right extension given in tables
        // F.2 and F.3 of the standard.
        const uint filter_width = 2U;

        TcdResolution[] tr_ar = tilec.resolutions;
        int tr_pos = 0;
        TcdResolution tr = tr_ar[tr_pos], tr_max = tr_ar[numres - 1];

        //Width of the resolution level computed
        uint rw = (uint)(tr.x1 - tr.x0);

        //Height of the resolution level computed
        uint rh = (uint)(tr.y1 - tr.y0);

        // Compute the intersection of the area of interest, expressed in tile coordinates
        // with the tile coordinates
        uint win_tcx0 = tilec.win_x0;
        uint win_tcy0 = tilec.win_y0;
        uint win_tcx1 = tilec.win_x1;
        uint win_tcy1 = tilec.win_y1;

        if (tr_max.x0 == tr_max.x1 || tr_max.y0 == tr_max.y1)
        {
            return true;
        }

        SparseArrayInt32 sa = SparseArrayInt32.Init(tilec, numres);
        if (sa is null)
            return false;

        if (numres == 1U)
        {
            bool ret = sa.read(tr_max.win_x0 - (uint)tr_max.x0,
                               tr_max.win_y0 - (uint)tr_max.y0,
                               tr_max.win_x1 - (uint)tr_max.x0,
                               tr_max.win_y1 - (uint)tr_max.y0,
                               tilec.data_win, 0,
                               1, tr_max.win_x1 - tr_max.win_x0,
                               true);
            Debug.Assert(ret);
            return true;
        }
        ulong h_mem_size = MaxResolution(tr_ar, (int)numres);
        // overflow check
        // in vertical pass, we process 4 columns at a time
        if (h_mem_size > (Constants.SIZE_MAX / (4 * sizeof(int))))
        {
            return false;
        }
        h_mem_size *= 4;
        h.mem = new int[h_mem_size];
        v.mem = h.mem;

        for (uint resno = 1; resno < numres; resno++)
        {
            /* Window of interest subband-based coordinates */
            /* Window of interest tile-resolution-based coordinates */
            uint win_tr_x0, win_tr_x1, win_tr_y0, win_tr_y1;
            /* Tile-resolution subband-based coordinates */

            tr = tr_ar[++tr_pos];

            h.sn = (int)rw;
            v.sn = (int)rh;

            rw = (uint)(tr.x1 - tr.x0);
            rh = (uint)(tr.y1 - tr.y0);

            h.dn = (int)(rw - (uint)h.sn);
            h.cas = tr.x0 % 2;

            v.dn = (int)(rh - (uint)v.sn);
            v.cas = tr.y0 % 2;

            // Get the subband coordinates for the window of interest
            // LL band
            GetBandCoordinates(tilec, resno, 0,
                               win_tcx0, win_tcy0, win_tcx1, win_tcy1,
                               out uint win_ll_x0, out uint win_ll_y0,
                               out uint win_ll_x1, out uint win_ll_y1);

            // HL band
            GetBandCoordinates(tilec, resno, 1,
                               win_tcx0, win_tcy0, win_tcx1, win_tcy1,
                               out uint win_hl_x0, out _, out uint win_hl_x1, out _);

            /* LH band */
            GetBandCoordinates(tilec, resno, 2,
                               win_tcx0, win_tcy0, win_tcx1, win_tcy1,
                               out _, out uint win_lh_y0, out _, out uint win_lh_y1);

            /* Beware: band index for non-LL0 resolution are 0=HL, 1=LH and 2=HH */
            uint tr_ll_x0 = (uint)tr.bands[1].x0;
            uint tr_ll_y0 = (uint)tr.bands[0].y0;
            uint tr_hl_x0 = (uint)tr.bands[0].x0;
            uint tr_lh_y0 = (uint)tr.bands[1].y0;

            /* Subtract the origin of the bands for this tile, to the subwindow */
            /* of interest band coordinates, so as to get them relative to the */
            /* tile */
            win_ll_x0 = MyMath.uint_subs(win_ll_x0, tr_ll_x0);
            win_ll_y0 = MyMath.uint_subs(win_ll_y0, tr_ll_y0);
            win_ll_x1 = MyMath.uint_subs(win_ll_x1, tr_ll_x0);
            win_ll_y1 = MyMath.uint_subs(win_ll_y1, tr_ll_y0);
            win_hl_x0 = MyMath.uint_subs(win_hl_x0, tr_hl_x0);
            win_hl_x1 = MyMath.uint_subs(win_hl_x1, tr_hl_x0);
            win_lh_y0 = MyMath.uint_subs(win_lh_y0, tr_lh_y0);
            win_lh_y1 = MyMath.uint_subs(win_lh_y1, tr_lh_y0);

            SegmentGrow(filter_width, (uint)h.sn, ref win_ll_x0, ref win_ll_x1);
            SegmentGrow(filter_width, (uint)h.dn, ref win_hl_x0, ref win_hl_x1);

            SegmentGrow(filter_width, (uint)v.sn, ref win_ll_y0, ref win_ll_y1);
            SegmentGrow(filter_width, (uint)v.dn, ref win_lh_y0, ref win_lh_y1);

            /* Compute the tile-resolution-based coordinates for the window of interest */
            if (h.cas == 0)
            {
                win_tr_x0 = Math.Min(2 * win_ll_x0, 2 * win_hl_x0 + 1);
                win_tr_x1 = Math.Min(Math.Max(2 * win_ll_x1, 2 * win_hl_x1 + 1), rw);
            }
            else
            {
                win_tr_x0 = Math.Min(2 * win_hl_x0, 2 * win_ll_x0 + 1);
                win_tr_x1 = Math.Min(Math.Max(2 * win_hl_x1, 2 * win_ll_x1 + 1), rw);
            }

            if (v.cas == 0)
            {
                win_tr_y0 = Math.Min(2 * win_ll_y0, 2 * win_lh_y0 + 1);
                win_tr_y1 = Math.Min(Math.Max(2 * win_ll_y1, 2 * win_lh_y1 + 1), rh);
            }
            else
            {
                win_tr_y0 = Math.Min(2 * win_lh_y0, 2 * win_ll_y0 + 1);
                win_tr_y1 = Math.Min(Math.Max(2 * win_lh_y1, 2 * win_ll_y1 + 1), rh);
            }

            for (uint j = 0; j < rh; ++j)
            {
                if ((j >= win_ll_y0 && j < win_ll_y1) ||
                        (j >= win_lh_y0 + (uint)v.sn && j < win_lh_y1 + (uint)v.sn))
                {

                    // Avoids dwt.c:1584:44 (in opj_dwt_decode_partial_1): runtime error:
                    // signed integer overflow: -1094795586 + -1094795586 cannot be represented in type 'int'
                    // on opj_decompress -i  ../../openjpeg/MAPA.jp2 -o out.tif -d 0,0,256,256
                    // This is less extreme than memsetting the whole buffer to 0
                    // although we could potentially do better with better handling of edge conditions
                    if (win_tr_x1 >= 1 && win_tr_x1 < rw)
                    {
                        h.mem[win_tr_x1 - 1] = 0;
                    }
                    if (win_tr_x1 < rw)
                    {
                        h.mem[win_tr_x1] = 0;
                    }

                    interleave_partial_h(h.mem,
                                       h.cas,
                                       sa,
                                       j,
                                       (uint)h.sn,
                                       win_ll_x0,
                                       win_ll_x1,
                                       win_hl_x0,
                                       win_hl_x1);
                    decode_partial_1(h.mem, h.dn, h.sn, h.cas,
                                     (int)win_ll_x0,
                                     (int)win_ll_x1,
                                     (int)win_hl_x0,
                                     (int)win_hl_x1);
                    if (!sa.write(win_tr_x0, j,
                                  win_tr_x1, j + 1,
                                  h.mem, (int)win_tr_x0,
                                  1, 0, true))
                    {
                        return false;
                    }
                }
            }

            for (uint i = win_tr_x0; i < win_tr_x1;)
            {
                uint nb_cols = Math.Min(4U, win_tr_x1 - i);
                interleave_partial_v(v.mem,
                                     v.cas,
                                     sa,
                                     i,
                                     nb_cols,
                                     (uint)v.sn,
                                     win_ll_y0,
                                     win_ll_y1,
                                     win_lh_y0,
                                     win_lh_y1);
                decode_partial_1_parallel(v.mem, nb_cols, v.dn, v.sn, v.cas,
                                          (int)win_ll_y0,
                                          (int)win_ll_y1,
                                          (int)win_lh_y0,
                                          (int)win_lh_y1);
                if (!sa.write(i, win_tr_y0,
                              i + nb_cols, win_tr_y1,
                              v.mem, 4 * (int)win_tr_y0,
                              1, 4, true))
                {
                    return false;
                }

                i += nb_cols;
            }
        }

        {
            bool ret = sa.read(
                           tr_max.win_x0 - (uint)tr_max.x0,
                           tr_max.win_y0 - (uint)tr_max.y0,
                           tr_max.win_x1 - (uint)tr_max.x0,
                           tr_max.win_y1 - (uint)tr_max.y0,
                           tilec.data_win, 0,
                           1, tr_max.win_x1 - tr_max.win_x0,
                           true);
            Debug.Assert(ret);
        }

        return true;
    }

    //2.5 - opj_dwt_interleave_partial_h
    private static void interleave_partial_h(
        int[] dest,
        int cas,
        SparseArrayInt32 sa,
        uint sa_line,
        uint sn,
        uint win_l_x0,
        uint win_l_x1,
        uint win_h_x0,
        uint win_h_x1)
    {
        var ret = sa.read(win_l_x0, sa_line,
            win_l_x1, sa_line + 1,
            dest, cas + 2 * (int)win_l_x0,
            2, 0, true);
        Debug.Assert(ret);

        ret = sa.read(sn + win_h_x0, sa_line,
                      sn + win_h_x1, sa_line + 1,
                      dest, 1 - cas + 2 * (int)win_h_x0,
                      2, 0, true);
        Debug.Assert(ret);
    }

    //2.5 - opj_dwt_interleave_partial_v
    private static void interleave_partial_v(
        int[] dest,
        int cas,
        SparseArrayInt32 sa,
        uint sa_col,
        uint nb_cols,
        uint sn,
        uint win_l_y0,
        uint win_l_y1,
        uint win_h_y0,
        uint win_h_y1)
    {
        var ret = sa.read(sa_col, win_l_y0,
            sa_col + nb_cols, win_l_y1,
            dest, cas * 4 + 2 * 4 * (int)win_l_y0,
            1, 2 * 4, true);
        Debug.Assert(ret);

        ret = sa.read(sa_col, sn + win_h_y0,
                      sa_col + nb_cols, sn + win_h_y1,
                      dest, (1 - cas) * 4 + 2 * 4 * (int)win_h_y0,
                      1, 2 * 4, true);
        Debug.Assert(ret);
    }

    /// <remarks>
    /// 2.5 - opj_dwt_decode_partial_1
    ///
    /// OPJ_S(i) => a[(i)*2]
    /// OPJ_D(i) => a[(1+(i)*2)]
    /// OPJ_S_(i) => ((i)<0?OPJ_S(0):((i)>=sn?OPJ_S(sn-1):OPJ_S(i)))
    /// OPJ_D_(i) => ((i)<0?OPJ_D(0):((i)>=dn?OPJ_D(dn-1):OPJ_D(i)))
    /// OPJ_SS_(i) => ((i)<0?OPJ_S(0):((i)>=dn?OPJ_S(dn-1):OPJ_S(i)))
    /// OPJ_DD_(i) => ((i)<0?OPJ_D(0):((i)>=sn?OPJ_D(sn-1):OPJ_D(i)))
    ///
    /// Substituted:
    /// OPJ_D_(i) => ((i) < 0 ? a[1] : ((i) >= dn ? a[(1 + (dn - 1) * 2)] : a[(1 + (i) * 2)]))
    /// OPJ_S_(i) => ((i)<0?a[0]:((i)>=sn?a[(sn-1)*2]:a[(i)*2]))
    /// OPJ_SS_(i) => ((i)<0?a[0]:((i)>=dn?a[(dn-1)*2]:a[(i)*2]))
    /// OPJ_DD_(i) => ((i)<0?a[1]:((i)>=sn?a[(1+(sn-1)*2)]:a[(1+(i)*2)]))
    /// </remarks>
    private static void decode_partial_1(int[] a, int dn, int sn,
                                         int cas,
                                         int win_l_x0,
                                         int win_l_x1,
                                         int win_h_x0,
                                         int win_h_x1)
    {
        int i;

        if (cas == 0)
        {
            if ((dn > 0) || (sn > 1))
            {
                i = win_l_x0;
                if (i < win_l_x1)
                {
                    /* Left-most case */
                    a[i * 2] -= (((i - 1) < 0 ? a[(1 + (0) * 2)] : ((i - 1) >= dn ? a[(1 + (dn - 1) * 2)] : a[(1 + (i - 1) * 2)]))
                               + ((i) < 0 ? a[(1 + (0) * 2)] : ((i) >= dn ? a[(1 + (dn - 1) * 2)] : a[(1 + (i) * 2)]))
                               + 2) >> 2;
                    i++;

                    int i_max = win_l_x1;
                    if (i_max > dn)
                    {
                        i_max = dn;
                    }
                    for (; i < i_max; i++)
                    {
                        /* No bound checking */
                        a[i * 2] -= (a[1 + (i - 1) * 2] + a[1 + i * 2] + 2) >> 2;
                    }
                    for (; i < win_l_x1; i++)
                    {
                        /* Right-most case */
                        a[i * 2] -= (((i - 1) < 0 ? a[(1 + (0) * 2)] : ((i - 1) >= dn ? a[(1 + (dn - 1) * 2)] : a[(1 + (i - 1) * 2)]))
                                   + ((i) < 0 ? a[(1 + (0) * 2)] : ((i) >= dn ? a[(1 + (dn - 1) * 2)] : a[(1 + (i) * 2)]))
                                   + 2) >> 2;
                    }
                }

                i = win_h_x0;
                if (i < win_h_x1)
                {
                    int i_max = win_h_x1;
                    if (i_max >= sn)
                    {
                        i_max = sn - 1;
                    }
                    for (; i < i_max; i++)
                    {
                        /* No bound checking */
                        a[1 + i * 2] += (a[(i) * 2] + a[(i + 1) * 2]) >> 1;
                    }
                    for (; i < win_h_x1; i++)
                    {
                        /* Right-most case */
                        a[1 + i * 2] += (((i) < 0 ? a[0] : ((i) >= sn ? a[(sn - 1) * 2] : a[(i) * 2]))
                                       + ((i + 1) < 0 ? a[0] : ((i + 1) >= sn ? a[(sn - 1) * 2] : a[(i + 1) * 2]))
                                       ) >> 1;
                    }
                }
            }
        }
        else
        {
            if (sn == 0 && dn == 1)
            {        /* NEW :  CASE ONE ELEMENT */
                a[0] /= 2;
            }
            else
            {
                for (i = win_l_x0; i < win_l_x1; i++)
                {
                    a[1 + i * 2] = MyMath.int_sub_no_overflow(
                                          a[1 + i * 2],
                                          MyMath.int_add_no_overflow(
                                              MyMath.int_add_no_overflow(
                                                  ((i) < 0 ? a[0] : ((i) >= dn ? a[(dn - 1) * 2] : a[(i) * 2])),
                                                  ((i + 1) < 0 ? a[0] : ((i + 1) >= dn ? a[(dn - 1) * 2] : a[(i + 1) * 2]))),
                                              2) >> 2);
                }
                for (i = win_h_x0; i < win_h_x1; i++)
                {
                    a[i * 2] = MyMath.int_add_no_overflow(
                                      a[i * 2],
                                      MyMath.int_add_no_overflow(
                                          ((i) < 0 ? a[1] : ((i) >= sn ? a[(1 + (sn - 1) * 2)] : a[(1 + (i) * 2)])),
                                          ((i - 1) < 0 ? a[1] : ((i - 1) >= sn ? a[(1 + (sn - 1) * 2)] : a[(1 + (i - 1) * 2)])))
                                      >> 1);
                }
            }
        }
    }

    /// <remarks>
    /// 2.5
    ///
    /// Todo: Make this more readable. Look at the OpenJpeg 2.1 C# impl.
    ///       where the code is much more readable (Decode_1)
    ///
    /// OPJ_S_off(i,off) => a[(uint)(i)*2*4+off]
    /// OPJ_D_off(i,off) => a[(1+(uint)(i)*2)*4+off]
    /// OPJ_S__off(i,off) => ((i)<0?OPJ_S_off(0,off):((i)>=sn?OPJ_S_off(sn-1,off):OPJ_S_off(i,off)))
    /// OPJ_D__off(i,off) => ((i)<0?OPJ_D_off(0,off):((i)>=dn?OPJ_D_off(dn-1,off):OPJ_D_off(i,off)))
    /// OPJ_SS__off(i,off) => ((i)<0?OPJ_S_off(0,off):((i)>=dn?OPJ_S_off(dn-1,off):OPJ_S_off(i,off)))
    /// OPJ_DD__off(i,off) => ((i)<0?OPJ_D_off(0,off):((i)>=sn?OPJ_D_off(sn-1,off):OPJ_D_off(i,off)))
    ///
    /// Substituted:
    /// OPJ_S__off(i,off) => ((i)<0?a[off]:((i)>=sn?a[(uint)(sn-1)*2*4+off]:a[(uint)(i)*2*4+off]))
    /// OPJ_D__off(i,off) => ((i)<0?a[1+off]:((i)>=dn?a[(1+(uint)(dn-1)*2)*4+off]:a[(1+(uint)(i)*2)*4+off]))
    /// OPJ_SS__off(i,off) => ((i)<0?a[off]:((i)>=dn?a[(uint)(dn-1)*2*4+off]:a[(uint)(i)*2*4+off]))
    /// OPJ_DD__off(i,off) => ((i)<0?a[1+off]:((i)>=sn?a[(1+(uint)(sn-1)*2)*4+off]:a[(1+(uint)(i)*2)*4+off])
    /// </remarks>
    private static void decode_partial_1_parallel(
                                                int[] a,
                                                uint nb_cols,
                                                int dn, int sn,
                                                int cas,
                                                int win_l_x0,
                                                int win_l_x1,
                                                int win_h_x0,
                                                int win_h_x1)
    {
        int i;
        uint off;

        if (cas == 0)
        {
            if ((dn > 0) || (sn > 1))
            {
                i = win_l_x0;
                if (i < win_l_x1)
                {
                    /* Left-most case */
                    for (off = 0; off < 4; off++)
                    {
                        a[(uint)(i) * 2 * 4 + off] -= (
                            ((i - 1) < 0 ? a[(1) * 4 + off] : ((i - 1) >= dn ? a[(1 + (uint)(dn - 1) * 2) * 4 + off] : a[(1 + (uint)(i - 1) * 2) * 4 + off]))
                          + ((i) < 0 ? a[(1) * 4 + off] : ((i) >= dn ? a[(1 + (uint)(dn - 1) * 2) * 4 + off] : a[(1 + (uint)(i) * 2) * 4 + off]))
                          + 2
                         ) >> 2;
                    }
                    i++;

                    int i_max = win_l_x1;
                    if (i_max > dn)
                    {
                        i_max = dn;
                    }

#if NET7_0_OR_GREATER
                    if (Vector128.IsHardwareAccelerated)
                    {
                        // Upstream SSE2 block: the 4 interleaved columns are one Vector128 per element.
                        var va = MemoryMarshal.Cast<int, Vector128<int>>(a.AsSpan());
                        var two = Vector128.Create(2);
                        for (; i < i_max; i++)
                        {
                            va[2 * i] -= (va[2 * i - 1] + va[2 * i + 1] + two) >> 2;
                        }
                    }
#endif
                    for (; i < i_max; i++)
                    {
                        /* No bound checking */
                        for (off = 0; off < 4; off++)
                        {
                            a[(uint)(i) * 2 * 4 + off] -= (
                                a[(1 + (uint)(i - 1) * 2) * 4 + off]
                              + a[(1 + (uint)(i) * 2) * 4 + off]
                              + 2
                            ) >> 2;
                        }
                    }
                    for (; i < win_l_x1; i++)
                    {
                        /* Right-most case */
                        for (off = 0; off < 4; off++)
                        {
                            a[(uint)(i) * 2 * 4 + off] -= (
                                ((i - 1) < 0 ? a[(1) * 4 + off] : ((i - 1) >= dn ? a[(1 + (uint)(dn - 1) * 2) * 4 + off] : a[(1 + (uint)(i - 1) * 2) * 4 + off]))
                              + ((i) < 0 ? a[(1) * 4 + off] : ((i) >= dn ? a[(1 + (uint)(dn - 1) * 2) * 4 + off] : a[(1 + (uint)(i) * 2) * 4 + off]))
                              + 2
                            ) >> 2;
                        }
                    }
                }

                i = win_h_x0;
                if (i < win_h_x1)
                {
                    int i_max = win_h_x1;
                    if (i_max >= sn)
                    {
                        i_max = sn - 1;
                    }

#if NET7_0_OR_GREATER
                    if (Vector128.IsHardwareAccelerated)
                    {
                        // Upstream SSE2 block, as above.
                        var va = MemoryMarshal.Cast<int, Vector128<int>>(a.AsSpan());
                        for (; i < i_max; i++)
                        {
                            va[2 * i + 1] += (va[2 * i] + va[2 * i + 2]) >> 1;
                        }
                    }
#endif
                    for (; i < i_max; i++)
                    {
                        /* No bound checking */
                        for (off = 0; off < 4; off++)
                        {
                            a[(1 + (uint)(i) * 2) * 4 + off] += (
                                a[(uint)(i) * 2 * 4 + off]
                              + a[(uint)(i + 1) * 2 * 4 + off]
                            ) >> 1;
                        }
                    }
                    for (; i < win_h_x1; i++)
                    {
                        /* Right-most case */
                        for (off = 0; off < 4; off++)
                        {
                            a[(1 + (uint)(i) * 2) * 4 + off] += (
                                ((i) < 0 ? a[off] : ((i) >= sn ? a[(uint)(sn - 1) * 2 * 4 + off] : a[(uint)(i) * 2 * 4 + off]))
                              + ((i + 1) < 0 ? a[off] : ((i + 1) >= sn ? a[(uint)(sn - 1) * 2 * 4 + off] : a[(uint)(i + 1) * 2 * 4 + off]))
                            ) >> 1;
                        }
                    }
                }
            }
        }
        else
        {
            if (sn == 0 && dn == 1)
            {        /* NEW :  CASE ONE ELEMENT */
                for (off = 0; off < 4; off++)
                {
                    a[off] /= 2;
                }
            }
            else
            {
                for (i = win_l_x0; i < win_l_x1; i++)
                {
                    for (off = 0; off < 4; off++)
                    {
                        a[(1 + (uint)(i) * 2) * 4 + off] =
                             MyMath.int_sub_no_overflow(
                                         a[(1 + (uint)(i) * 2) * 4 + off],
                                         MyMath.int_add_no_overflow(
                                             MyMath.int_add_no_overflow(
                                                 ((i) < 0 ? a[(uint)(0) * 2 * 4 + off] : ((i) >= dn ? a[(uint)(dn - 1) * 2 * 4 + off] : a[(uint)(i) * 2 * 4 + off])),
                                                 ((i + 1) < 0 ? a[(uint)(0) * 2 * 4 + off] : ((i + 1) >= dn ? a[(uint)(dn - 1) * 2 * 4 + off] : a[(uint)(i + 1) * 2 * 4 + off]))),
                                             2)
                                         >> 2);
                    }
                }
                for (i = win_h_x0; i < win_h_x1; i++)
                {
                    for (off = 0; off < 4; off++)
                    {
                        a[(uint)(i) * 2 * 4 + off] =
                            MyMath.int_add_no_overflow(
                                      a[(uint)(i) * 2 * 4 + off],
                                      MyMath.int_add_no_overflow(
                                          ((i) < 0 ? a[(1 + (uint)(0) * 2) * 4 + off] : ((i) >= sn ? a[(1 + (uint)(sn - 1) * 2) * 4 + off] : a[(1 + (uint)(i) * 2) * 4 + off])),
                                          ((i - 1) < 0 ? a[(1 + (uint)(0) * 2) * 4 + off] : ((i - 1) >= sn ? a[(1 + (uint)(sn - 1) * 2) * 4 + off] : a[(1 + (uint)(i - 1) * 2) * 4 + off])))
                                      >> 1);
                    }
                }
            }
        }
    }

#if NETCOREAPP3_0_OR_GREATER
    [MethodImpl(MethodImplOptions.AggressiveOptimization)] // 9/7 DWT: see decode_tile_97
#endif
    private static void v8dwt_interleave_h(v4dwt_local dwt, int[] a_ar, int a, int width, uint remaining_height)
    {
        float[] bi_ar = dwt.wavelet;
        // The tile data holds float bits: copy them as ints (no IntOrFloat round trip through memory).
        Span<int> wi = MemoryMarshal.Cast<float, int>(bi_ar.AsSpan());
        int bi = dwt.cas * NB_ELTS_V8; //C# NB_ELTS_V8 is because we're indexing into dwt.wavelet
        int x0 = (int)dwt.win_l_x0;
        int x1 = (int)dwt.win_l_x1;

        for (int k = 0; k < 2; ++k)
        {
            // Upstream also requires 16-byte aligned pointers here. That check does not carry over to array
            // indices (it ruled out every odd element), and both paths give the same result.
            if (remaining_height >= NB_ELTS_V8)
            {
                // Fast code path
                int i = x0;
#if NET8_0_OR_GREATER
                if (Avx.IsSupported)
                {
                    // 8 columns at a time: load 8 contiguous row segments, transpose, store 8 elements.
                    ReadOnlySpan<float> af = MemoryMarshal.Cast<int, float>(a_ar.AsSpan());
                    Span<float> bf = bi_ar;
                    for (; i + 8 <= x1; i += 8)
                    {
                        int j = a + i;
                        var r0 = Vector256.Create(af.Slice(j, 8));
                        var r1 = Vector256.Create(af.Slice(j + width, 8));
                        var r2 = Vector256.Create(af.Slice(j + width * 2, 8));
                        var r3 = Vector256.Create(af.Slice(j + width * 3, 8));
                        var r4 = Vector256.Create(af.Slice(j + width * 4, 8));
                        var r5 = Vector256.Create(af.Slice(j + width * 5, 8));
                        var r6 = Vector256.Create(af.Slice(j + width * 6, 8));
                        var r7 = Vector256.Create(af.Slice(j + width * 7, 8));
                        Transpose8x8(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7);
                        int dst = bi + i * 2 * NB_ELTS_V8;
                        r0.CopyTo(bf.Slice(dst));
                        r1.CopyTo(bf.Slice(dst + 2 * NB_ELTS_V8));
                        r2.CopyTo(bf.Slice(dst + 4 * NB_ELTS_V8));
                        r3.CopyTo(bf.Slice(dst + 6 * NB_ELTS_V8));
                        r4.CopyTo(bf.Slice(dst + 8 * NB_ELTS_V8));
                        r5.CopyTo(bf.Slice(dst + 10 * NB_ELTS_V8));
                        r6.CopyTo(bf.Slice(dst + 12 * NB_ELTS_V8));
                        r7.CopyTo(bf.Slice(dst + 14 * NB_ELTS_V8));
                    }
                }
#endif
                for (; i < x1; ++i)
                {
                    int j = a + i, dst = bi + i * 2 * NB_ELTS_V8;
                    wi[dst + 0] = a_ar[j];
                    j += width; wi[dst + 1] = a_ar[j];
                    j += width; wi[dst + 2] = a_ar[j];
                    j += width; wi[dst + 3] = a_ar[j];
                    j += width; wi[dst + 4] = a_ar[j];
                    j += width; wi[dst + 5] = a_ar[j];
                    j += width; wi[dst + 6] = a_ar[j];
                    j += width; wi[dst + 7] = a_ar[j];
                }
            }
            else
            {
                // Slow code path
                for (int i = x0; i < x1; ++i)
                {
                    int j = a + i, dst = bi + i * 2 * NB_ELTS_V8;
                    wi[dst + 0] = a_ar[j];
                    j += width;
                    if (remaining_height == 1) continue;
                    wi[dst + 1] = a_ar[j];
                    j += width;
                    if (remaining_height == 2) continue;
                    wi[dst + 2] = a_ar[j];
                    j += width;
                    if (remaining_height == 3) continue;
                    wi[dst + 3] = a_ar[j];
                    j += width;
                    if (remaining_height == 4) continue;
                    wi[dst + 4] = a_ar[j];
                    j += width;
                    if (remaining_height == 5) continue;
                    wi[dst + 5] = a_ar[j];
                    j += width;
                    if (remaining_height == 6) continue;
                    wi[dst + 6] = a_ar[j];
                    j += width;
                    if (remaining_height == 7) continue;
                    wi[dst + 7] = a_ar[j];
                }
            }

            bi = (1 - dwt.cas) * NB_ELTS_V8;
            a += dwt.sn;
            x0 = (int)dwt.win_h_x0;
            x1 = (int)dwt.win_h_x1;
        }
    }

    /// <summary>
    /// Writes back 8 rows after a horizontal 9/7 pass: wavelet element k holds column k of the 8 rows,
    /// so row n gets element k's value n (upstream: the two scalar loops in opj_dwt_decode_tile_97).
    /// </summary>
#if NETCOREAPP3_0_OR_GREATER
    [MethodImpl(MethodImplOptions.AggressiveOptimization)] // 9/7 DWT: see decode_tile_97
#endif
    private static void WriteBackRows8(float[] wavelet, Span<float> ajf, int aj, int w, int rw)
    {
        int k = 0;
#if NET8_0_OR_GREATER
        if (Avx.IsSupported)
        {
            // 8 elements at a time: an 8x8 transpose in registers turns 64 scattered stores into
            // 8 contiguous ones. Pure data movement, so the output is unchanged.
            ReadOnlySpan<float> wav = wavelet;
            for (; k + 8 <= rw; k += 8)
            {
                var r0 = Vector256.Create(wav.Slice((k + 0) * NB_ELTS_V8, 8));
                var r1 = Vector256.Create(wav.Slice((k + 1) * NB_ELTS_V8, 8));
                var r2 = Vector256.Create(wav.Slice((k + 2) * NB_ELTS_V8, 8));
                var r3 = Vector256.Create(wav.Slice((k + 3) * NB_ELTS_V8, 8));
                var r4 = Vector256.Create(wav.Slice((k + 4) * NB_ELTS_V8, 8));
                var r5 = Vector256.Create(wav.Slice((k + 5) * NB_ELTS_V8, 8));
                var r6 = Vector256.Create(wav.Slice((k + 6) * NB_ELTS_V8, 8));
                var r7 = Vector256.Create(wav.Slice((k + 7) * NB_ELTS_V8, 8));
                Transpose8x8(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7);
                int o = aj + k;
                r0.CopyTo(ajf.Slice(o));
                r1.CopyTo(ajf.Slice(o + w));
                r2.CopyTo(ajf.Slice(o + w * 2));
                r3.CopyTo(ajf.Slice(o + w * 3));
                r4.CopyTo(ajf.Slice(o + w * 4));
                r5.CopyTo(ajf.Slice(o + w * 5));
                r6.CopyTo(ajf.Slice(o + w * 6));
                r7.CopyTo(ajf.Slice(o + w * 7));
            }
        }
#endif
        for (; k < rw; k++)
        {
            int k_wavelet = k * NB_ELTS_V8;
            ajf[aj + k] = wavelet[k_wavelet + 0];
            ajf[aj + k + w] = wavelet[k_wavelet + 1];
            ajf[aj + k + w * 2] = wavelet[k_wavelet + 2];
            ajf[aj + k + w * 3] = wavelet[k_wavelet + 3];
            ajf[aj + k + w * 4] = wavelet[k_wavelet + 4];
            ajf[aj + k + w * 5] = wavelet[k_wavelet + 5];
            ajf[aj + k + w * 6] = wavelet[k_wavelet + 6];
            ajf[aj + k + w * 7] = wavelet[k_wavelet + 7];
        }
    }

#if NET8_0_OR_GREATER
    /// <summary>In-register transpose of an 8x8 float matrix (rows r0..r7 become columns).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Transpose8x8(ref Vector256<float> r0, ref Vector256<float> r1, ref Vector256<float> r2, ref Vector256<float> r3,
                             ref Vector256<float> r4, ref Vector256<float> r5, ref Vector256<float> r6, ref Vector256<float> r7)
    {
        var t0 = Avx.UnpackLow(r0, r1);
        var t1 = Avx.UnpackHigh(r0, r1);
        var t2 = Avx.UnpackLow(r2, r3);
        var t3 = Avx.UnpackHigh(r2, r3);
        var t4 = Avx.UnpackLow(r4, r5);
        var t5 = Avx.UnpackHigh(r4, r5);
        var t6 = Avx.UnpackLow(r6, r7);
        var t7 = Avx.UnpackHigh(r6, r7);
        var u0 = Avx.Shuffle(t0, t2, 0x44);
        var u1 = Avx.Shuffle(t0, t2, 0xEE);
        var u2 = Avx.Shuffle(t1, t3, 0x44);
        var u3 = Avx.Shuffle(t1, t3, 0xEE);
        var u4 = Avx.Shuffle(t4, t6, 0x44);
        var u5 = Avx.Shuffle(t4, t6, 0xEE);
        var u6 = Avx.Shuffle(t5, t7, 0x44);
        var u7 = Avx.Shuffle(t5, t7, 0xEE);
        r0 = Avx.Permute2x128(u0, u4, 0x20);
        r1 = Avx.Permute2x128(u1, u5, 0x20);
        r2 = Avx.Permute2x128(u2, u6, 0x20);
        r3 = Avx.Permute2x128(u3, u7, 0x20);
        r4 = Avx.Permute2x128(u0, u4, 0x31);
        r5 = Avx.Permute2x128(u1, u5, 0x31);
        r6 = Avx.Permute2x128(u2, u6, 0x31);
        r7 = Avx.Permute2x128(u3, u7, 0x31);
    }
#endif

    /// <summary>
    /// Vertical 9/7 pass over ncols columns (a multiple of NB_ELTS_V8) starting at aj; returns ncols.
    /// With vectors, two 8-column strips share each sweep over the rows: every row read then covers 16
    /// floats (a whole cache line) and each memory page is visited once instead of twice. Each strip still
    /// goes through the same interleave / lifting / write-back, so the result is unchanged.
    /// </summary>
#if NETCOREAPP3_0_OR_GREATER
    [MethodImpl(MethodImplOptions.AggressiveOptimization)] // 9/7 DWT: see decode_tile_97
#endif
    private static int VerticalStrips97(v4dwt_local v, int[] aj_ar, int aj, int w, int rh, int ncols)
    {
        int done = 0;
        if (UseVectors && ncols >= 2 * NB_ELTS_V8)
        {
            var v2 = v.Clone();
            v2.wavelet = ArrayPool<float>.Shared.Rent(v.wavelet.Length);
            for (; done + 2 * NB_ELTS_V8 <= ncols; done += 2 * NB_ELTS_V8)
            {
                v8dwt_interleave_v_pair(v, v2, aj_ar, aj + done, w);
                v8dwt_decode(v);
                v8dwt_decode(v2);
                CopyWaveletRowsBackPair(v.wavelet, v2.wavelet, rh, aj_ar, aj + done, w);
            }
            ArrayPool<float>.Shared.Return(v2.wavelet);
        }
        for (; done < ncols; done += NB_ELTS_V8)
        {
            v8dwt_interleave_v(v, aj_ar, aj + done, w, NB_ELTS_V8);
            v8dwt_decode(v);
            CopyWaveletRowsBack(v.wavelet, rh, aj_ar, aj + done, w);
        }
        return ncols;
    }

    /// <summary>
    /// <see cref="v8dwt_interleave_v"/> for two adjacent 8-column strips in one sweep: columns 0-7 go to
    /// <paramref name="v"/>, columns 8-15 to <paramref name="v2"/> (same windows and parity).
    /// </summary>
#if NETCOREAPP3_0_OR_GREATER
    [MethodImpl(MethodImplOptions.AggressiveOptimization)] // 9/7 DWT: see decode_tile_97
#endif
    private static void v8dwt_interleave_v_pair(v4dwt_local v, v4dwt_local v2, int[] a_ar, int a, int width)
    {
        var w1 = MemoryMarshal.Cast<float, Vector<int>>(v.wavelet.AsSpan());
        var w2 = MemoryMarshal.Cast<float, Vector<int>>(v2.wavelet.AsSpan());
#if !NETSTANDARD2_1
        // Range checked once (rows a .. a + (sn + dn - 1) * width, 16 values each; highest wavelet element
        // written is cas + 2 * (win_x1 - 1) of either half), then unchecked unaligned loads and stores.
        long maxElt = Math.Max(v.win_l_x1 > 0 ? v.cas + 2L * (v.win_l_x1 - 1) : -1,
                               v.win_h_x1 > 0 ? 1 - v.cas + 2L * (v.win_h_x1 - 1) : -1);
        if (VecPerElt == 1 && v.sn + v.dn > 0 && a >= 0
            && (long)a + (long)(v.sn + v.dn - 1) * width + 2 * NB_ELTS_V8 <= a_ar.Length
            && v.win_l_x1 <= (uint)v.sn && v.win_h_x1 <= (uint)v.dn
            && maxElt < Math.Min(w1.Length, w2.Length))
        {
            ref byte src0 = ref Unsafe.As<int, byte>(ref MemoryMarshal.GetReference(a_ar.AsSpan()));
            ref Vector<int> d1 = ref MemoryMarshal.GetReference(w1);
            ref Vector<int> d2 = ref MemoryMarshal.GetReference(w2);
            for (int pass = 0; pass < 2; pass++)
            {
                int x0 = (int)(pass == 0 ? v.win_l_x0 : v.win_h_x0), x1 = (int)(pass == 0 ? v.win_l_x1 : v.win_h_x1);
                int b = pass == 0 ? v.cas : 1 - v.cas, row0 = pass == 0 ? a : a + v.sn * width;
                for (int i = x0; i < x1; ++i)
                {
                    ref byte r = ref Unsafe.Add(ref src0, (nint)(row0 + (long)i * width) * sizeof(int));
                    Unsafe.Add(ref d1, b + i * 2) = Unsafe.ReadUnaligned<Vector<int>>(ref r);
                    Unsafe.Add(ref d2, b + i * 2) = Unsafe.ReadUnaligned<Vector<int>>(ref Unsafe.Add(ref r, NB_ELTS_V8 * sizeof(int)));
                }
            }
            return;
        }
#endif
        int bi = v.cas;
        for (int i = (int)v.win_l_x0; i < v.win_l_x1; ++i)
        {
            int src = a + i * width, dst = (bi + i * 2) * VecPerElt;
            for (int k = 0; k < VecPerElt; k++)
            {
                w1[dst + k] = new Vector<int>(a_ar, src + k * VecF);
                w2[dst + k] = new Vector<int>(a_ar, src + NB_ELTS_V8 + k * VecF);
            }
        }
        a += v.sn * width;
        bi = 1 - v.cas;
        for (int i = (int)v.win_h_x0; i < v.win_h_x1; ++i)
        {
            int src = a + i * width, dst = (bi + i * 2) * VecPerElt;
            for (int k = 0; k < VecPerElt; k++)
            {
                w1[dst + k] = new Vector<int>(a_ar, src + k * VecF);
                w2[dst + k] = new Vector<int>(a_ar, src + NB_ELTS_V8 + k * VecF);
            }
        }
    }

    /// <summary><see cref="CopyWaveletRowsBack"/> for two strips in one sweep (columns 0-7 from wavelet1, 8-15 from wavelet2).</summary>
#if NETCOREAPP3_0_OR_GREATER
    [MethodImpl(MethodImplOptions.AggressiveOptimization)] // 9/7 DWT: see decode_tile_97
#endif
    private static void CopyWaveletRowsBackPair(float[] wavelet1, float[] wavelet2, int rows, int[] aj_ar, int aj, int w)
    {
        var w1 = MemoryMarshal.Cast<float, Vector<int>>(wavelet1.AsSpan());
        var w2 = MemoryMarshal.Cast<float, Vector<int>>(wavelet2.AsSpan());
#if !NETSTANDARD2_1
        if (VecPerElt == 1 && rows > 0 && aj >= 0 && rows <= Math.Min(w1.Length, w2.Length)
            && (long)aj + (long)(rows - 1) * w + 2 * NB_ELTS_V8 <= aj_ar.Length)
        {
            // Range checked once above, then unchecked unaligned stores.
            ref byte dst0 = ref Unsafe.As<int, byte>(ref aj_ar[aj]);
            ref Vector<int> s1 = ref MemoryMarshal.GetReference(w1);
            ref Vector<int> s2 = ref MemoryMarshal.GetReference(w2);
            for (int k = 0; k < rows; ++k)
            {
                ref byte d = ref Unsafe.Add(ref dst0, (nint)((long)k * w) * sizeof(int));
                Unsafe.WriteUnaligned(ref d, Unsafe.Add(ref s1, k));
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref d, NB_ELTS_V8 * sizeof(int)), Unsafe.Add(ref s2, k));
            }
            return;
        }
#endif
        for (int k = 0; k < rows; ++k)
        {
            int src = k * VecPerElt, dst = aj + k * w;
            for (int e = 0; e < VecPerElt; e++)
            {
                w1[src + e].CopyTo(aj_ar, dst + e * VecF);
                w2[src + e].CopyTo(aj_ar, dst + NB_ELTS_V8 + e * VecF);
            }
        }
    }

    /// <summary>
    /// Writes back the 8 decoded columns of a vertical 9/7 pass: row k of the wavelet buffer goes to
    /// aj_ar[aj + k * w], 8 values each. Same as the Buffer.BlockCopy per row it replaces.
    /// </summary>
#if NETCOREAPP3_0_OR_GREATER
    [MethodImpl(MethodImplOptions.AggressiveOptimization)] // 9/7 DWT: see decode_tile_97
#endif
    private static void CopyWaveletRowsBack(float[] wavelet, int rows, int[] aj_ar, int aj, int w)
    {
        if (UseVectors)
        {
            var wv = MemoryMarshal.Cast<float, Vector<int>>(wavelet.AsSpan());
#if !NETSTANDARD2_1
            if (VecPerElt == 1 && rows > 0 && aj >= 0 && rows <= wv.Length
                && (long)aj + (long)(rows - 1) * w + NB_ELTS_V8 <= aj_ar.Length)
            {
                // Range checked once above, then unchecked unaligned stores.
                ref byte dst0 = ref Unsafe.As<int, byte>(ref aj_ar[aj]);
                ref Vector<int> s1 = ref MemoryMarshal.GetReference(wv);
                for (int k = 0; k < rows; ++k)
                {
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst0, (nint)((long)k * w) * sizeof(int)), Unsafe.Add(ref s1, k));
                }
                return;
            }
#endif
            for (int k = 0; k < rows; ++k)
            {
                int src = k * VecPerElt, dst = aj + k * w;
                for (int e = 0; e < VecPerElt; e++)
                {
                    wv[src + e].CopyTo(aj_ar, dst + e * VecF);
                }
            }
            return;
        }
        for (int k = 0; k < rows; ++k)
        {
            Buffer.BlockCopy(wavelet, k * NB_ELTS_V8 * sizeof(float), aj_ar, (aj + (k * w)) * sizeof(float), NB_ELTS_V8 * sizeof(float));
        }
    }

    //2.5
#if NETCOREAPP3_0_OR_GREATER
    [MethodImpl(MethodImplOptions.AggressiveOptimization)] // 9/7 DWT: see decode_tile_97
#endif
    private static void v8dwt_interleave_v(v4dwt_local dwt, int[] a_ar, int a, int width, int n_elts_read)
    {
        //C# Impl. Note that bi_ar is not a float[], but a wavelet array where each entery has 8 floating points.
        //         This while the a array is a plain float[]. I.e how they are to be indexed differers
        float[] bi_ar = dwt.wavelet;
        int bi = dwt.cas;

        if (UseVectors && n_elts_read == NB_ELTS_V8)
        {
            // Wavelet elements start at multiples of NB_ELTS_V8 floats, so they index whole vectors.
            var wv = MemoryMarshal.Cast<float, Vector<int>>(bi_ar.AsSpan());
            for (int i = (int)dwt.win_l_x0; i < dwt.win_l_x1; ++i)
            {
                int src = a + i * width, dst = (bi + i * 2) * VecPerElt;
                for (int k = 0; k < VecPerElt; k++)
                {
                    wv[dst + k] = new Vector<int>(a_ar, src + k * VecF);
                }
            }
            a += dwt.sn * width;
            bi = 1 - dwt.cas;
            for (int i = (int)dwt.win_h_x0; i < dwt.win_h_x1; ++i)
            {
                int src = a + i * width, dst = (bi + i * 2) * VecPerElt;
                for (int k = 0; k < VecPerElt; k++)
                {
                    wv[dst + k] = new Vector<int>(a_ar, src + k * VecF);
                }
            }
            return;
        }

        for (int i = (int)dwt.win_l_x0; i < dwt.win_l_x1; ++i)
        {
            Buffer.BlockCopy(a_ar, (a + i * width) * sizeof(float), bi_ar, (bi + i * 2) * NB_ELTS_V8 * sizeof(float), n_elts_read * sizeof(float));
        }
        a += dwt.sn * width;
        bi = (1 - dwt.cas);
        for (int i = (int)dwt.win_h_x0; i < dwt.win_h_x1; ++i)
        {
            Buffer.BlockCopy(a_ar, (a + i * width) * sizeof(float), bi_ar, (bi + i * 2) * NB_ELTS_V8 * sizeof(float), n_elts_read * sizeof(float));
        }
    }

    //2.5
    private static void v8dwt_interleave_partial_h(v4dwt_local dwt, SparseArrayInt32 sa, uint sa_line, uint remaining_height)
    {
        for (uint i = 0; i < remaining_height; i++)
        {
            bool ret = sa.read(dwt.win_l_x0, sa_line + i,
                          dwt.win_l_x1, sa_line + i + 1,
                          /* Nasty cast from float* to int32* */
                          dwt.wavelet, (dwt.cas + 2 * (int)dwt.win_l_x0) * NB_ELTS_V8 + (int)i, //C# dwt.wavelet index must be multiplied with NB_ELTS_V8
                          2 * NB_ELTS_V8, 0, true);
            Debug.Assert(ret);
            ret = sa.read((uint)dwt.sn + dwt.win_h_x0, sa_line + i,
                          (uint)dwt.sn + dwt.win_h_x1, sa_line + i + 1,
                          /* Nasty cast from float* to int32* */
                          dwt.wavelet, (1 - dwt.cas + 2 * (int)dwt.win_h_x0) * NB_ELTS_V8 + (int)i,
                          2 * NB_ELTS_V8, 0, true);
            Debug.Assert(ret);
        }
    }

    //2.5
    private static void v8dwt_interleave_partial_v(v4dwt_local dwt, SparseArrayInt32 sa, uint sa_col, uint nb_elts_read)
    {
        var ret = sa.read(sa_col, dwt.win_l_x0,
            sa_col + nb_elts_read, dwt.win_l_x1,
            /* Nasty cast from float* to int32* */
            dwt.wavelet, (dwt.cas + 2 * (int)dwt.win_l_x0) * NB_ELTS_V8,
            1, 2 * NB_ELTS_V8, true);
        Debug.Assert(ret);

        ret = sa.read(sa_col, (uint)dwt.sn + dwt.win_h_x0,
                      sa_col + nb_elts_read, (uint)dwt.sn + dwt.win_h_x1,
                      /* Nasty cast from float* to int32* */
                      dwt.wavelet, (1 - dwt.cas + 2 * (int)dwt.win_h_x0) * NB_ELTS_V8,
                      1, 2 * NB_ELTS_V8, true);
        Debug.Assert(ret);
    }

    /// <summary>
    /// Forward lazy transform (horizontal).
    /// </summary>
    /// <remarks>2.5 - opj_dwt_deinterleave_h</remarks>
    private static void Deinterleave_h(int[] a, int[] b, int b_pt, int dn, int sn, int cas)
    {
        int dest = b_pt;
        int src = 0 + cas;

        for (int i = 0; i < sn; i++)
        {
            b[dest++] = a[src];
            src += 2;
        }

        dest = b_pt + sn;
        src = 0 + 1 - cas;

        for (int i = 0; i < dn; i++)
        {
            b[dest++] = a[src];
            src += 2;
        }
    }

    /// <summary>
    /// Inverse 5-3 wavelet transform in 2-D
    /// </summary>
    /// <param name="tilec">Tile component information (current tile)</param>
    /// <param name="numres">Number of resolution levels to decode</param>
    /// <remarks>
    /// 2.5 - opj_dwt_decode
    /// </remarks>
    internal static bool Decode(TileCoder tcd, TcdTilecomp tilec, uint numres)
    {
        if (tcd.WholeTileDecoding)
        {
            return decode_tile(tilec, numres, tcd.DisableMultiThreading);
        }

        return DecodePartialTile(tilec, numres);
    }

    /// <summary>
    /// Forward 9-7 wavelet transform in 1-D.
    /// </summary>
    /// <remarks>2.5 - opj_dwt_encode_1_real</remarks>
    private static void Encode_1_real(int[] w, int dn, int sn, int cas)
    {
        int a, b;
        if (cas == 0)
        {
            a = 0;
            b = 1;
        }
        else
        {
            a = 1;
            b = 0;
        }
        EncodeStep2(w, a, b + 1, (uint)dn, (uint)Math.Min(dn, sn - b), dwt_alpha);
        EncodeStep2(w, b, a + 1, (uint)sn, (uint)Math.Min(sn, dn - a), dwt_beta);
        EncodeStep2(w, a, b + 1, (uint)dn, (uint)Math.Min(dn, sn - b), dwt_gamma);
        EncodeStep2(w, b, a + 1, (uint)sn, (uint)Math.Min(sn, dn - a), dwt_delta);

        if (a == 0)
        {
            EncodeStep1Combined(w, 0, (uint)sn, (uint)dn, invK, K);
        }
        else
        {
            EncodeStep1Combined(w, 0, (uint)dn, (uint)sn, K, invK);
        }
    }

    //2.5 - opj_dwt_encode_and_deinterleave_v_real
    private static void EncodeAndDeinterleaveV_Real(int[] arr, int a_pt, int[] tmp, uint height, bool even, uint stride_width, uint cols)
    {
        if (height == 1)
            return;

        int sn = (int)((height + (even ? 1u : 0u)) >> 1);
        int dn = (int)(height - sn);
        int a, b;

        FetchColsVerticalPass(arr, a_pt, tmp, height, (int)stride_width, cols);

        if (even)
        {
            a = 0;
            b = 1;
        }
        else
        {
            a = 1;
            b = 0;
        }
        v8dwt_encode_step2(tmp, a * NB_ELTS_V8, (b + 1) * NB_ELTS_V8, (uint)dn, (uint)Math.Min(dn, sn - b), dwt_alpha);
        v8dwt_encode_step2(tmp, b * NB_ELTS_V8, (a + 1) * NB_ELTS_V8, (uint)sn, (uint)Math.Min(sn, dn - a), dwt_beta);
        v8dwt_encode_step2(tmp, a * NB_ELTS_V8, (b + 1) * NB_ELTS_V8, (uint)dn, (uint)Math.Min(dn, sn - b), dwt_gamma);
        v8dwt_encode_step2(tmp, b * NB_ELTS_V8, (a + 1) * NB_ELTS_V8, (uint)sn, (uint)Math.Min(sn, dn - a), dwt_delta);
        v8dwt_encode_step1(tmp, b * NB_ELTS_V8, (uint)dn, K);
        v8dwt_encode_step1(tmp, a * NB_ELTS_V8, (uint)sn, invK);

        if (cols == NB_ELTS_V8)
        {
            DeinterleaveV_Cols(tmp, arr, a_pt, dn, sn, (int)stride_width, even ? 0 : 1, NB_ELTS_V8);
        }
        else
        {
            DeinterleaveV_Cols(tmp, arr, a_pt, dn, sn, (int)stride_width, even ? 0 : 1, cols);
        }
    }

    /// <summary>
    /// Forward 5-3 transform, for the vertical pass, processing cols columns
    /// where cols <= NB_ELTS_V8
    /// </summary>
    /// <remarks>2.5 - opj_dwt_encode_and_deinterleave_v</remarks>
    private static void EncodeAndDeinterleaveV(int[] a, int a_pt, int[] tmp, uint height, bool even, uint stride_width, uint cols)
    {
        uint sn = (height + (even ? 1u : 0u)) >> 1;
        uint dn = height - sn;

        FetchColsVerticalPass(a, a_pt, tmp, height, (int)stride_width, cols);

        //C# Snip SSE2 code

        if (even)
        {
            if (height > 1)
            {
                uint i;
                for (i = 0; i + 1 < sn; i++)
                {
                    for (uint c = 0; c < 8; c++)
                    {
                        tmp[((1 + (i) * 2)) * 8 + c] -= (tmp[(i) * 2 * 8 + c] + tmp[(i + 1) * 2 * 8 + c]) >> 1;
                    }
                }
                if (((height) % 2) == 0)
                {
                    for (uint c = 0; c < 8; c++)
                    {
                        tmp[((1 + (i) * 2)) * 8 + c] -= tmp[(i) * 2 * 8 + c];
                    }
                }
                for (uint c = 0; c < 8; c++)
                {
                    tmp[(0) * 2 * 8 + c] += (tmp[((1 + (0) * 2)) * 8 + c] + tmp[((1 + (0) * 2)) * 8 + c] + 2) >> 2;
                }
                for (i = 1; i < dn; i++)
                {
                    for (uint c = 0; c < 8; c++)
                    {
                        tmp[(i) * 2 * 8 + c] += (tmp[((1 + (i - 1) * 2)) * 8 + c] + tmp[((1 + (i) * 2)) * 8 + c] + 2) >> 2;
                    }
                }
                if (((height) % 2) == 1)
                {
                    for (uint c = 0; c < 8; c++)
                    {
                        tmp[(i) * 2 * 8 + c] += (tmp[((1 + (i - 1) * 2)) * 8 + c] + tmp[((1 + (i - 1) * 2)) * 8 + c] + 2) >> 2;
                    }
                }
            }
        }
        else
        {
            uint c;
            if (height == 1)
            {
                for (c = 0; c < 8; c++)
                {
                    tmp[(0) * 2 * 8 + c] *= 2;
                }
            }
            else
            {
                uint i;
                for (c = 0; c < 8; c++)
                {
                    tmp[(0) * 2 * 8 + c] -= tmp[((1 + (0) * 2)) * 8 + c];
                }
                for (i = 1; i < sn; i++)
                {
                    for (c = 0; c < 8; c++)
                    {
                        tmp[(i) * 2 * 8 + c] -= (tmp[((1 + (i) * 2)) * 8 + c] + tmp[((1 + (i - 1) * 2)) * 8 + c]) >> 1;
                    }
                }
                if (((height) % 2) == 1)
                {
                    for (c = 0; c < 8; c++)
                    {
                        tmp[(i) * 2 * 8 + c] -= tmp[((1 + (i - 1) * 2)) * 8 + c];
                    }
                }
                for (i = 0; i + 1 < dn; i++)
                {
                    for (c = 0; c < 8; c++)
                    {
                        tmp[((1 + (i) * 2)) * 8 + c] += (tmp[(i) * 2 * 8 + c] + tmp[(i + 1) * 2 * 8 + c] + 2) >> 2;
                    }
                }
                if (((height) % 2) == 0)
                {
                    for (c = 0; c < 8; c++)
                    {
                        tmp[((1 + (i) * 2)) * 8 + c] += (tmp[(i) * 2 * 8 + c] + tmp[(i) * 2 * 8 + c] + 2) >> 2;
                    }
                }
            }
        }

        if (cols == NB_ELTS_V8)
        {
            DeinterleaveV_Cols(tmp, a, a_pt, (int)dn, (int)sn,
                               (int)stride_width, even ? 0 : 1, NB_ELTS_V8);
        }
        else
        {
            DeinterleaveV_Cols(tmp, a, a_pt, (int)dn, (int)sn,
                               (int)stride_width, even ? 0 : 1, cols);
        }
    }

    /// <summary>
    /// Deinterleave result of forward transform, where cols <= NB_ELTS_V8
    /// and src contains NB_ELTS_V8 consecutive values for up to NB_ELTS_V8
    /// columns
    /// </summary>
    /// <remarks>2.5 - opj_dwt_deinterleave_v_cols</remarks>
    private static void DeinterleaveV_Cols(int[] src, int[] dst, int dst_pt, int dn, int sn, int stride_width, int cas, uint cols)
    {
        int org_dest = dst_pt;
        int src_pt = cas * NB_ELTS_V8;
        int i = sn;
        for (int k = 0; k < 2; k++)
        {
            while (i-- != 0)
            {
                if (cols == NB_ELTS_V8)
                {
                    Buffer.BlockCopy(src, src_pt * sizeof(int), dst, dst_pt * sizeof(int), NB_ELTS_V8 * sizeof(int));
                }
                else
                {
                    int c = 0;
                    switch (cols)
                    {
                        case 7:
                            dst[dst_pt + c] = src[src_pt + c];
                            c++;
                            goto case 6;
                        case 6:
                            dst[dst_pt + c] = src[src_pt + c];
                            c++;
                            goto case 5;
                        case 5:
                            dst[dst_pt + c] = src[src_pt + c];
                            c++;
                            goto case 4;
                        case 4:
                            dst[dst_pt + c] = src[src_pt + c];
                            c++;
                            goto case 3;
                        case 3:
                            dst[dst_pt + c] = src[src_pt + c];
                            c++;
                            goto case 2;
                        case 2:
                            dst[dst_pt + c] = src[src_pt + c];
                            c++;
                            goto default;
                        default:
                            dst[dst_pt + c] = src[src_pt + c];
                            break;
                    }
                }
                dst_pt += stride_width;
                src_pt += 2 * NB_ELTS_V8;
            }

            dst_pt = org_dest + sn * stride_width;
            src_pt = (1 - cas) * NB_ELTS_V8;
            i = dn;
        }
    }

    /// <summary>
    /// Process one line for the horizontal pass of the 9x7 forward transform
    /// </summary>
    /// <remarks>2.5 - opj_dwt_encode_and_deinterleave_h_one_row_real</remarks>
    private static void EncodeAndDeinterleaveH_OneRowReal(int[] row, int row_pt, int[] tmp, uint width, bool even)
    {
        if (width == 1)
            return;
        int sn = (int)((width + (even ? 1 : 0)) >> 1);
        int dn = (int)(width - (uint)sn);
        Buffer.BlockCopy(row, row_pt * sizeof(int), tmp, 0, (int)(width * sizeof(int)));
        Encode_1_real(tmp, dn, sn, even ? 0 : 1);
        Deinterleave_h(tmp, row, row_pt, dn, sn, even ? 0 : 1);
    }

    /// <summary>
    /// Process one line for the horizontal pass of the 5x3 forward transform
    /// </summary>
    /// <remarks>2.5 - opj_dwt_encode_and_deinterleave_h_one_row</remarks>
    private static void EncodeAndDeinterleaveH_OneRow(int[] row, int row_pt, int[] tmp, uint width, bool even)
    {
        int sn = (int)((width + (even ? 1 : 0)) >> 1);
        int dn = (int)(width - (uint)sn);

        if (even)
        {
            if (width > 1)
            {
                int i;
                for (i = 0; i < sn - 1; i++)
                {
                    tmp[sn + i] = row[row_pt + 2 * i + 1] - ((row[row_pt + (i) * 2] + row[row_pt + (i + 1) * 2]) >> 1);
                }
                if ((width % 2) == 0)
                {
                    tmp[sn + i] = row[row_pt + 2 * i + 1] - row[row_pt + (i) * 2];
                }
                row[row_pt + 0] += (tmp[sn] + tmp[sn] + 2) >> 2;
                for (i = 1; i < dn; i++)
                {
                    row[row_pt + i] = row[row_pt + 2 * i] + ((tmp[sn + (i - 1)] + tmp[sn + i] + 2) >> 2);
                }
                if ((width % 2) == 1)
                {
                    row[row_pt + i] = row[row_pt + 2 * i] + ((tmp[sn + (i - 1)] + tmp[sn + (i - 1)] + 2) >> 2);
                }
                Buffer.BlockCopy(tmp, sn * sizeof(int), row, (row_pt + sn) * sizeof(int), dn * sizeof(int));
            }
        }
        else
        {
            if (width == 1)
            {
                row[row_pt] *= 2;
            }
            else
            {
                int i;
                tmp[sn + 0] = row[row_pt + 0] - row[row_pt + 1];
                for (i = 1; i < sn; i++)
                {
                    tmp[sn + i] = row[row_pt + 2 * i] - ((row[row_pt + 2 * i + 1] + row[row_pt + 2 * (i - 1) + 1]) >> 1);
                }
                if ((width % 2) == 1)
                {
                    tmp[sn + i] = row[row_pt + 2 * i] - row[row_pt + 2 * (i - 1) + 1];
                }

                for (i = 0; i < dn - 1; i++)
                {
                    row[row_pt + i] = row[row_pt + 2 * i + 1] + ((tmp[sn + i] + tmp[sn + i + 1] + 2) >> 2);
                }
                if ((width % 2) == 0)
                {
                    row[row_pt + i] = row[row_pt + 2 * i + 1] + ((tmp[sn + i] + tmp[sn + i] + 2) >> 2);
                }
                Buffer.BlockCopy(tmp, sn * sizeof(int), row, (row_pt + sn) * sizeof(int), dn * sizeof(int));
            }
        }
    }

    /** Fetch up to cols <= NB_ELTS_V8 for each line, and put them in tmpOut */
    /* that has a NB_ELTS_V8 interleave factor. */
    //2.5
    private static void FetchColsVerticalPass(int[] a, int array, int[] tmp, uint height, int stride_width, uint cols)
    {
        if (cols == NB_ELTS_V8)
        {
            for (int k = 0; k < height; ++k)
            {
                Buffer.BlockCopy(a, (array + k * stride_width) * sizeof(int), tmp, (NB_ELTS_V8 * k) * sizeof(int), NB_ELTS_V8 * sizeof(int));
            }
        }
        else
        {
            for (int k = 0; k < height; ++k)
            {
                int c = 0;
                for (; c < cols; c++)
                {
                    tmp[NB_ELTS_V8 * k + c] = a[array + c + k * stride_width];
                }
                for (; c < NB_ELTS_V8; c++)
                {
                    tmp[NB_ELTS_V8 * k + c] = 0;
                }
            }
        }
    }

    //2.5
    private static void decode_h_func(decode_h_job job)
    {
        idwt53_h_rows(job.h, job.tiled, job.tiledp, (int)job.w, (int)job.min_j, (int)job.max_j);
    }

    //2.5
    private static void dwt97_decode_h_func(dwt97_decode_h_job job)
    {
        var w = (int)job.w;
        var aj_ar = job.aj;
        var aj = job.ajp;
        var ajf = MemoryMarshal.Cast<int, float>(aj_ar.AsSpan()); // tile data holds float bits here

        for (int j = 0; j + NB_ELTS_V8 <= job.nb_rows; j += NB_ELTS_V8)
        {
            v8dwt_interleave_h(job.h, aj_ar, aj, w, NB_ELTS_V8);
            v8dwt_decode(job.h);

            WriteBackRows8(job.h.wavelet, ajf, aj, w, (int)job.rw);

            aj += w * NB_ELTS_V8;
        }
    }

    //2.5
    private static void dwt97_decode_v_func(dwt97_decode_v_job job)
    {
        var aj_ar = job.aj;
        var aj = job.ajp;

        VerticalStrips97(job.v, aj_ar, aj, (int)job.w, (int)job.rh, (int)(job.nb_columns & ~(uint)(NB_ELTS_V8 - 1)));
    }

    /// <summary>
    /// Horizontal 5/3 pass over rows j0 .. j1 - 1 (row j starts at tiledp + j * w). Upstream calls opj_idwt53_h
    /// per row; here the dispatch and the range check are done once, and on AVX2 the row kernel (even-start
    /// rows, or odd-start rows with at least 9 high-pass samples) is inlined into the loop. Anything else goes
    /// through <see cref="idwt53_h"/> row by row.
    /// </summary>
#if NETCOREAPP3_0_OR_GREATER
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
#endif
    private static void idwt53_h_rows(dwt_local dwt, int[] tiled, int tiledp, int w, int j0, int j1)
    {
#if NET8_0_OR_GREATER
        int sn = dwt.sn, len = sn + dwt.dn;
        if (Avx2.IsSupported && j1 > j0 && w >= 0
            && (long)tiledp + (long)j0 * w >= 0 && (long)tiledp + (long)(j1 - 1) * w + len <= tiled.Length
            && len <= dwt.mem.Length)
        {
            ref int t = ref MemoryMarshal.GetArrayDataReference(tiled);
            ref int outs = ref MemoryMarshal.GetArrayDataReference(dwt.mem);
            if (dwt.cas == 0 && len > 1 && !Simd.Use512)
            {
                for (int j = j0, row = tiledp + j0 * w; j < j1; j++, row += w)
                {
                    idwt53_h_cas0_avx2_row(ref t, ref outs, row, sn, len);
                }
                return;
            }
            if (dwt.cas != 0 && len - sn >= 9)
            {
                for (int j = j0, row = tiledp + j0 * w; j < j1; j++, row += w)
                {
                    idwt53_h_cas1_avx2_row(ref Unsafe.Add(ref t, row), ref outs, sn, len);
                }
                return;
            }
        }
#endif
        for (int j = j0; j < j1; j++)
        {
            idwt53_h(dwt, tiled, tiledp + j * w);
        }
    }

    /// <summary>
    /// Inverse 5-3 wavelet transform in 1-D for one row.
    /// </summary>
    /// <remarks>
    /// 2.5
    /// Performs interleave, inverse wavelet transform and copy back to buffer
    /// </remarks>
#if NETCOREAPP3_0_OR_GREATER
    // Fully optimised from the first call (with the AVX2 row kernel): 10026 horizontal pass 5.5 ms -> 3.4 ms.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
#endif
    private static void idwt53_h(dwt_local dwt, int[] tiled, int tiledp)
    {
        int sn = dwt.sn;
        int len = sn + dwt.dn;
        if (dwt.cas == 0)
        { /* Left-most sample is on even coordinate */
            if (len > 1)
            {
                idwt53_h_cas0(dwt.mem, sn, len, tiled, tiledp);
            }
            else
            {
                /* Unmodified value */
            }
        }
        else
        { /* Left-most sample is on odd coordinate */
            if (len == 1)
            {
                tiled[tiledp] /= 2;
            }
            else if (len == 2)
            {
                var o = dwt.mem;
                int in_even = tiledp + sn;
                int in_odd = tiledp;
                o[1] = tiled[in_odd] - ((tiled[in_even] + 1) >> 1);
                o[0] = tiled[in_even] + o[1];
                dwt.mem.AsSpan(0, len).CopyTo(tiled.AsSpan(tiledp, len));
            }
            else if (len > 2)
            {
                opj_idwt53_h_cas1(dwt.mem, sn, len, tiled, tiledp);
            }
        }
    }

    //2.5
#if NET8_0_OR_GREATER
    /// <summary>
    /// AVX2 version of <see cref="idwt53_h_cas0"/>: 8 even and 8 odd outputs per iteration.
    /// </summary>
    /// <remarks>2.5.3 - the __AVX2__ branch of opj_idwt53_h_cas0 (upstream e0e0c802). Integer only, same results.</remarks>
    // Fully optimised from the first call; see idwt53_h.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void idwt53_h_cas0_avx2(int[] tmp, int sn, int len, int[] tiled, int tiledp)
    {
        // The row reads tiled[tiledp .. tiledp + len) and writes tmp[0 .. len). Checked once here so the row
        // kernel can use unchecked accesses; otherwise take the checked code.
        if (tiledp < 0 || (long)tiledp + len > tiled.Length || len > tmp.Length)
        {
            idwt53_h_cas0_checked(tmp, sn, len, tiled, tiledp);
            return;
        }
        idwt53_h_cas0_avx2_row(ref MemoryMarshal.GetArrayDataReference(tiled), ref MemoryMarshal.GetArrayDataReference(tmp), tiledp, sn, len);
    }

    /// <summary>
    /// One row of <see cref="idwt53_h_cas0_avx2"/>, unchecked: the caller guarantees t[tiledp .. tiledp + len)
    /// and outs[0 .. len) are inside their arrays. Inlined into the per-row-range loop of
    /// <see cref="idwt53_h_rows"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void idwt53_h_cas0_avx2_row(ref int t, ref int outs, int tiledp, int sn, int len)
    {
        if (len >= 18)
        {
            // Not upstream's loop (same values), see idwt53_h_cas0_avx2_step:
            // - only the sn even inputs are copied out to outs; the outputs go straight into the row. Step b
            //   writes row[2b .. 2b + 15] and the next one reads odd inputs from row[sn + b + 7 ..], so writes
            //   stay behind the reads (b < sn). Upstream writes everything to tmp and copies all len values back.
            // - steps at b = 0, 8, .. and a last one at dn - 9, the last that stays inside the row, instead of up
            //   to 7 scalar iterations. It overlaps the previous step and rewrites the same values; its odd inputs
            //   can lie under earlier outputs, so they are loaded first, with the odd values the end needs.
            ref int row = ref Unsafe.Add(ref t, tiledp);
            ref int odm1 = ref Unsafe.Add(ref row, sn - 1);
            int dn = len - sn;
            nuint last = (nuint)(dn - 9);
            var ldm1 = Vector256.LoadUnsafe(ref odm1, last);
            var ld0 = Vector256.LoadUnsafe(ref odm1, last + 1);
            var ld1 = Vector256.LoadUnsafe(ref odm1, last + 2);
            int d0 = Unsafe.Add(ref odm1, 1), dl2 = Unsafe.Add(ref odm1, dn - 1), dl = Unsafe.Add(ref odm1, dn);
            CopyRowUnchecked(ref row, ref outs, sn);
            ref int ev = ref outs;

            var two = Vector256.Create(2);
            for (nuint b = 0; b < last; b += 8)
            {
                idwt53_h_cas0_avx2_step(ref row, b, Vector256.LoadUnsafe(ref ev, b), Vector256.LoadUnsafe(ref ev, b + 1),
                    Vector256.LoadUnsafe(ref odm1, b), Vector256.LoadUnsafe(ref odm1, b + 1), Vector256.LoadUnsafe(ref odm1, b + 2), two);
            }
            idwt53_h_cas0_avx2_step(ref row, last, Vector256.LoadUnsafe(ref ev, last), Vector256.LoadUnsafe(ref ev, last + 1), ldm1, ld0, ld1, two);

            // Left boundary (the steps used the general formula for out[0]), then out[2dn - 2 ..], as upstream.
            int e0 = ev - ((d0 + 1) >> 1);
            row = e0;
            Unsafe.Add(ref row, 1) = d0 + ((e0 + Unsafe.Add(ref row, 2)) >> 1);
            int el = Unsafe.Add(ref ev, dn - 1) - ((dl2 + dl + 2) >> 2);
            Unsafe.Add(ref row, 2 * dn - 2) = el;
            if ((len & 1) != 0)
            {
                int en = Unsafe.Add(ref ev, dn) - ((dl + 1) >> 1);
                Unsafe.Add(ref row, 2 * dn) = en;
                Unsafe.Add(ref row, 2 * dn - 1) = dl + ((el + en) >> 1);
            }
            else
            {
                Unsafe.Add(ref row, 2 * dn - 1) = dl + el;
            }
            return;
        }

        // Short rows: scalar, carrying out[o] in a register (same values as upstream's tail loop).
        int in_even = tiledp, in_odd = tiledp + sn, o = 0;
        int cur = Unsafe.Add(ref t, in_even) - ((Unsafe.Add(ref t, in_odd) + 1) >> 1);
        Unsafe.Add(ref outs, o) = cur;
        for (int j = 1; j < len - 2; j += 2)
        {
            int d = Unsafe.Add(ref t, in_odd);
            int next = Unsafe.Add(ref t, in_even + 1) - ((d + Unsafe.Add(ref t, in_odd + 1) + 2) >> 2);
            Unsafe.Add(ref outs, o + 2) = next;
            Unsafe.Add(ref outs, o + 1) = d + ((cur + next) >> 1);
            cur = next;
            in_even++;
            in_odd++;
            o += 2;
        }

        if ((len & 1) != 0)
        {
            int next = Unsafe.Add(ref t, in_even + 1) - ((Unsafe.Add(ref t, in_odd) + 1) >> 1);
            Unsafe.Add(ref outs, o + 2) = next;
            Unsafe.Add(ref outs, o + 1) = Unsafe.Add(ref t, in_odd) + ((cur + next) >> 1);
        }
        else
        {
            Unsafe.Add(ref outs, o + 1) = Unsafe.Add(ref t, in_odd) + cur;
        }

        CopyRowUnchecked(ref outs, ref Unsafe.Add(ref t, tiledp), len);
    }

    /// <summary>
    /// Outputs 2b .. 2b + 15 of <see cref="idwt53_h_cas0_avx2_row"/> from even inputs e0 = ev[b .. b + 7],
    /// e1 = ev[b + 1 .. b + 8] and odd inputs dm1 = od[b - 1 .. b + 6], d0 = od[b ..], d1 = od[b + 1 ..]. Both even
    /// vectors (samples b .. b + 7 and b + 1 .. b + 8) are computed from loads; upstream shifts one into the other
    /// with a lane permute, a blend and an extract, which all compete for the one shuffle port. out[0] (b = 0) comes
    /// out wrong and is fixed by the caller.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void idwt53_h_cas0_avx2_step(ref int outs, nuint b, Vector256<int> e0, Vector256<int> e1,
        Vector256<int> dm1, Vector256<int> d0, Vector256<int> d1, Vector256<int> two)
    {
        var even_m1 = Avx2.Subtract(e0, Avx2.ShiftRightArithmetic(Avx2.Add(Avx2.Add(dm1, d0), two), 2));
        var even = Avx2.Subtract(e1, Avx2.ShiftRightArithmetic(Avx2.Add(Avx2.Add(d0, d1), two), 2));
        var odd = Avx2.Add(Avx2.ShiftRightArithmetic(Avx2.Add(even_m1, even), 1), d0);

        var unpack1 = Avx2.UnpackLow(even_m1, odd);
        var unpack2 = Avx2.UnpackHigh(even_m1, odd);
        nuint o = 2 * b;
        unpack1.GetLower().StoreUnsafe(ref outs, o);
        unpack2.GetLower().StoreUnsafe(ref outs, o + 4);
        unpack1.GetUpper().StoreUnsafe(ref outs, o + 8);
        unpack2.GetUpper().StoreUnsafe(ref outs, o + 12);
    }

    /// <summary>
    /// AVX2 version of <see cref="opj_idwt53_h_cas1"/> (rows starting on an odd coordinate) for dn = len - sn &gt;= 9,
    /// unchecked: the caller guarantees row[0 .. len) and outs[0 .. len) are inside their arrays. Not in upstream,
    /// which has no SIMD for this case; integer only, same values. Built like
    /// <see cref="idwt53_h_cas0_avx2_row"/>: the low-pass inputs s are copied to outs[1 .. sn], the outputs go
    /// straight into the row, a last overlapping step at dn - 9 instead of a scalar tail.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void idwt53_h_cas1_avx2_row(ref int row, ref int outs, int sn, int len)
    {
        // Output 2k is d[k] + ((s'[k - 1] + s'[k]) >> 1), output 2k + 1 is s'[k] = s[k] - ((d[k] + d[k + 1] + 2) >> 2).
        // Step b writes row[2b .. 2b + 15] and reads d from row[sn + b - 1 ..]: earlier steps wrote up to row[2b - 1],
        // so writes stay behind the reads (b < sn). The last step's d inputs and the boundary d values can lie under
        // earlier outputs, so they are loaded first.
        int dn = len - sn;
        nuint last = (nuint)(dn - 9);
        ref int dm1 = ref Unsafe.Add(ref row, sn - 1);
        var ldm1 = Vector256.LoadUnsafe(ref dm1, last);
        var ld0 = Vector256.LoadUnsafe(ref dm1, last + 1);
        var ld1 = Vector256.LoadUnsafe(ref dm1, last + 2);
        int d0 = Unsafe.Add(ref dm1, 1), dl = Unsafe.Add(ref dm1, dn);
        CopyRowUnchecked(ref row, ref Unsafe.Add(ref outs, 1), sn);

        var two = Vector256.Create(2);
        for (nuint b = 0; b < last; b += 8)
        {
            idwt53_h_cas1_avx2_step(ref row, b, Vector256.LoadUnsafe(ref outs, b), Vector256.LoadUnsafe(ref outs, b + 1),
                Vector256.LoadUnsafe(ref dm1, b), Vector256.LoadUnsafe(ref dm1, b + 1), Vector256.LoadUnsafe(ref dm1, b + 2), two);
        }
        idwt53_h_cas1_avx2_step(ref row, last, Vector256.LoadUnsafe(ref outs, last), Vector256.LoadUnsafe(ref outs, last + 1), ldm1, ld0, ld1, two);

        // Output 0 (the steps used s[-1], which does not exist), then the right boundary, as upstream.
        row = d0 + Unsafe.Add(ref row, 1);
        if ((len & 1) == 0)
        {
            int sl = Unsafe.Add(ref outs, sn) - ((dl + 1) >> 1);
            Unsafe.Add(ref row, len - 2) = dl + ((sl + Unsafe.Add(ref row, len - 3)) >> 1);
            Unsafe.Add(ref row, len - 1) = sl;
        }
        else
        {
            Unsafe.Add(ref row, len - 1) = dl + Unsafe.Add(ref row, len - 2);
        }
    }

    /// <summary>
    /// Outputs 2b .. 2b + 15 of <see cref="idwt53_h_cas1_avx2_row"/> from low-pass inputs sm1 = s[b - 1 .. b + 6],
    /// s0 = s[b .. b + 7] and high-pass inputs dm1 = d[b - 1 ..], d0 = d[b ..], d1 = d[b + 1 ..]. s'[k - 1] is
    /// computed from loads rather than shifted from s'[k] (see <see cref="idwt53_h_cas0_avx2_step"/>).
    /// Output 0 (b = 0) comes out wrong and is fixed by the caller.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void idwt53_h_cas1_avx2_step(ref int outs, nuint b, Vector256<int> sm1, Vector256<int> s0,
        Vector256<int> dm1, Vector256<int> d0, Vector256<int> d1, Vector256<int> two)
    {
        var low_m1 = Avx2.Subtract(sm1, Avx2.ShiftRightArithmetic(Avx2.Add(Avx2.Add(dm1, d0), two), 2));
        var low = Avx2.Subtract(s0, Avx2.ShiftRightArithmetic(Avx2.Add(Avx2.Add(d0, d1), two), 2));
        var high = Avx2.Add(Avx2.ShiftRightArithmetic(Avx2.Add(low_m1, low), 1), d0);

        var unpack1 = Avx2.UnpackLow(high, low);
        var unpack2 = Avx2.UnpackHigh(high, low);
        nuint o = 2 * b;
        unpack1.GetLower().StoreUnsafe(ref outs, o);
        unpack2.GetLower().StoreUnsafe(ref outs, o + 4);
        unpack1.GetUpper().StoreUnsafe(ref outs, o + 8);
        unpack2.GetUpper().StoreUnsafe(ref outs, o + 12);
    }

    /// <summary>Bounds-checked fallback for <see cref="idwt53_h_cas0_avx2"/>: the same lifting, scalar.</summary>
    private static void idwt53_h_cas0_checked(int[] tmp, int sn, int len, int[] tiled, int tiledp)
    {
        int in_even = tiledp, in_odd = tiledp + sn, o = 0;
        tmp[o] = tiled[in_even] - ((tiled[in_odd] + 1) >> 1);
        for (int j = 1; j < len - 2; j += 2)
        {
            tmp[o + 2] = tiled[in_even + 1] - ((tiled[in_odd] + tiled[in_odd + 1] + 2) >> 2);
            tmp[o + 1] = tiled[in_odd] + ((tmp[o] + tmp[o + 2]) >> 1);
            in_even++;
            in_odd++;
            o += 2;
        }
        if ((len & 1) != 0)
        {
            tmp[o + 2] = tiled[in_even + 1] - ((tiled[in_odd] + 1) >> 1);
            tmp[o + 1] = tiled[in_odd] + ((tmp[o] + tmp[o + 2]) >> 1);
        }
        else
        {
            tmp[o + 1] = tiled[in_odd] + tmp[o];
        }
        CopyRowBack(tmp, len, tiled, tiledp);
    }
#endif

#if NET8_0_OR_GREATER
    /// <summary>
    /// Vector512 version of <see cref="idwt53_h_cas0"/>: 16 even and 16 odd outputs per iteration.
    /// </summary>
    /// <remarks>
    /// 2.5.3 - the __AVX512F__ branch of opj_idwt53_h_cas0, written with cross-platform Vector512 operations
    /// (the even/odd interleave uses widening instead of permutex2var). Integer only, same results.
    /// </remarks>
    private static void idwt53_h_cas0_512(int[] tmp, int sn, int len, int[] tiled, int tiledp)
    {
        ReadOnlySpan<int> t = tiled;
        Span<int> outs = tmp;
        int in_even = tiledp, in_odd = tiledp + sn, o = 0;
        int prev_even = tiled[in_even] - ((tiled[in_odd] + 1) >> 1);
        var two = Vector512.Create(2);
        var move_right = Vector512.Create(0, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14);
        var lane0 = Vector512.Create(-1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        var low32 = Vector512.Create(0xFFFFFFFFL);
        int simd_batch = (len - 2) / 32;

        for (int i = 0; i < simd_batch; i++)
        {
            var lf = Vector512.Create(t.Slice(in_even + 1, 16));
            var hf1 = Vector512.Create(t.Slice(in_odd, 16));
            var hf2 = Vector512.Create(t.Slice(in_odd + 1, 16));

            var even = lf - ((hf1 + hf2 + two) >> 2);
            int next_even = even.GetElement(15);
            var even_m1 = Vector512.ConditionalSelect(lane0, Vector512.Create(prev_even), Vector512.Shuffle(even, move_right));

            // out[0] + out[2]
            var odd = ((even_m1 + even) >> 1) + hf1;

            // Interleave even_m1 and odd: each 64-bit lane is (even_m1[k], odd[k]) in memory order.
            var (eLo, eHi) = Vector512.Widen(even_m1);
            var (oLo, oHi) = Vector512.Widen(odd);
            ((eLo & low32) | (oLo << 32)).AsInt32().CopyTo(outs.Slice(o));
            ((eHi & low32) | (oHi << 32)).AsInt32().CopyTo(outs.Slice(o + 16));

            prev_even = next_even;
            o += 32;
            in_even += 16;
            in_odd += 16;
        }

        tmp[o] = prev_even;
        for (int j = simd_batch * 32 + 1; j < len - 2; j += 2)
        {
            tmp[o + 2] = tiled[in_even + 1] - ((tiled[in_odd] + tiled[in_odd + 1] + 2) >> 2);
            tmp[o + 1] = tiled[in_odd] + ((tmp[o] + tmp[o + 2]) >> 1);
            in_even++;
            in_odd++;
            o += 2;
        }

        if ((len & 1) != 0)
        {
            tmp[o + 2] = tiled[in_even + 1] - ((tiled[in_odd] + 1) >> 1);
            tmp[o + 1] = tiled[in_odd] + ((tmp[o] + tmp[o + 2]) >> 1);
        }
        else
        {
            tmp[o + 1] = tiled[in_odd] + tmp[o];
        }

        CopyRowBack(tmp, len, tiled, tiledp);
    }
#endif

    /// <summary>
    /// Copies tmp[0 .. len) to tiled[tiledp ..]: the end of each horizontal 5/3 row. An own unchecked vector loop
    /// instead of Span.CopyTo, whose precompiled (ReadyToRun) Buffer.Memmove runs until tier-up, or always with
    /// tiering off, and measured 3x slower on these rows (10719 horizontal pass, tiering off: 31.6 ms vs 9.5 ms).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CopyRowBack(int[] tmp, int len, int[] tiled, int tiledp)
    {
#if NET7_0_OR_GREATER
        if (Vector256.IsHardwareAccelerated && (uint)len <= (uint)tmp.Length && tiledp >= 0 && (long)tiledp + len <= tiled.Length)
        {
            // Range checked above; tmp and tiled are different arrays, so no overlap.
            CopyRowUnchecked(ref MemoryMarshal.GetArrayDataReference(tmp),
                             ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(tiled), tiledp), len);
            return;
        }
#endif
        tmp.AsSpan(0, len).CopyTo(tiled.AsSpan(tiledp, len));
    }

#if NET7_0_OR_GREATER
    /// <summary>Copies len ints from src to dst (not overlapping), unchecked, 256-bit vectors.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CopyRowUnchecked(ref int src, ref int dst, int len)
    {
        // nuint index: no sign extension per access
        nuint i = 0, n = (nuint)len;
        for (; i + 32 <= n; i += 32)
        {
            var a = Vector256.LoadUnsafe(ref src, i);
            var b = Vector256.LoadUnsafe(ref src, i + 8);
            var c = Vector256.LoadUnsafe(ref src, i + 16);
            var d = Vector256.LoadUnsafe(ref src, i + 24);
            a.StoreUnsafe(ref dst, i);
            b.StoreUnsafe(ref dst, i + 8);
            c.StoreUnsafe(ref dst, i + 16);
            d.StoreUnsafe(ref dst, i + 24);
        }
        for (; i + 8 <= n; i += 8)
        {
            Vector256.LoadUnsafe(ref src, i).StoreUnsafe(ref dst, i);
        }
        for (; i < n; i++)
        {
            Unsafe.Add(ref dst, i) = Unsafe.Add(ref src, i);
        }
    }
#endif

    private static void idwt53_h_cas0(int[] tmp, int sn, int len, int[] tiled, int tiledp)
    {
        Debug.Assert(len > 1);
#if NET8_0_OR_GREATER
        if (Simd.Use512)
        {
            idwt53_h_cas0_512(tmp, sn, len, tiled, tiledp);
            return;
        }
        if (Avx2.IsSupported)
        {
            idwt53_h_cas0_avx2(tmp, sn, len, tiled, tiledp);
            return;
        }
#endif

        int i, j;
        int in_even = tiledp;
        int in_odd = tiledp + sn;

        // Improved version of the TWO_PASS_VERSION:
        // Performs lifting in one single iteration. Saves memory
        // accesses and explicit interleaving.
        int d1c, d1n, s1n, s0c, s0n;

        s1n = tiled[in_even];
        d1n = tiled[in_odd];
        s0n = s1n - ((d1n + 1) >> 1);

        for (i = 0, j = 1; i < (len - 3); i += 2, j++)
        {
            d1c = d1n;
            s0c = s0n;

            s1n = tiled[in_even + j];
            d1n = tiled[in_odd + j];

            s0n = s1n - ((d1c + d1n + 2) >> 2);

            tmp[i] = s0c;
            tmp[i + 1] = MyMath.int_add_no_overflow(d1c, MyMath.int_add_no_overflow(s0c,
                                                 s0n) >> 1);
        }

        tmp[i] = s0n;

        if ((len & 1) != 0)
        {
            tmp[len - 1] = tiled[in_even + (len - 1) / 2] - ((d1n + 1) >> 1);
            tmp[len - 2] = d1n + ((s0n + tmp[len - 1]) >> 1);
        }
        else
        {
            tmp[len - 1] = d1n + s0n;
        }
        CopyRowBack(tmp, len, tiled, tiledp);
    }

    //2.5
    private static void opj_idwt53_h_cas1(int[] tmp, int sn, int len, int[] tiled, int tiledp)
    {
        Debug.Assert(len > 2);

        int i, j;
        int in_even = tiledp + sn;
        int in_odd = tiledp;

        /* Improved version of the TWO_PASS_VERSION: */
        /* Performs lifting in one single iteration. Saves memory */
        /* accesses and explicit interleaving. */
        var s1 = tiled[in_even + 1];
        var dc = tiled[in_odd] - ((tiled[in_even] + s1 + 2) >> 2);
        tmp[0] = tiled[in_even] + dc;

        int end = len - 2 - ((len & 1) == 0 ? 1 : 0);
        for (i = 1, j = 1; i < end; i += 2, j++)
        {

            int s2 = tiled[in_even + j + 1];

            int dn = tiled[in_odd + j] - ((s1 + s2 + 2) >> 2);
            tmp[i] = dc;
            tmp[i + 1] = MyMath.int_add_no_overflow(s1, MyMath.int_add_no_overflow(dn, dc) >> 1);

            dc = dn;
            s1 = s2;
        }

        tmp[i] = dc;

        if ((len & 1) == 0)
        {
            int dn = tiled[in_odd + len / 2 - 1] - ((s1 + 1) >> 1);
            tmp[len - 2] = s1 + ((dn + dc) >> 1);
            tmp[len - 1] = dn;
        }
        else
        {
            tmp[len - 1] = s1 + dc;
        }
        CopyRowBack(tmp, len, tiled, tiledp);
    }

    //2.5
    private static void idwt3_v_cas0(int[] tmp, int sn, int len, int[] tiled, int tiledp_col, int stride)
    {
        int i, j;

        Debug.Assert(len > 1);

        /* Performs lifting in one single iteration. Saves memory */
        /* accesses and explicit interleaving. */

        int s1n = tiled[tiledp_col];
        int d1n = tiled[tiledp_col + sn * stride];
        int s0n = s1n - ((d1n + 1) >> 1);

        for (i = 0, j = 0; i < (len - 3); i += 2, j++)
        {
            int d1c = d1n;
            int s0c = s0n;

            s1n = tiled[tiledp_col + (j + 1) * stride];
            d1n = tiled[tiledp_col + (sn + j + 1) * stride];

            s0n = MyMath.int_sub_no_overflow(s1n,
                              MyMath.int_add_no_overflow(MyMath.int_add_no_overflow(d1c, d1n), 2) >> 2);

            tmp[i] = s0c;
            tmp[i + 1] = MyMath.int_add_no_overflow(d1c, MyMath.int_add_no_overflow(s0c,
                                                 s0n) >> 1);
        }

        tmp[i] = s0n;

        if ((len & 1) != 0)
        {
            tmp[len - 1] =
                tiled[tiledp_col + ((len - 1) / 2) * stride] -
                ((d1n + 1) >> 1);
            tmp[len - 2] = d1n + ((s0n + tmp[len - 1]) >> 1);
        }
        else
        {
            tmp[len - 1] = d1n + s0n;
        }

        for (i = 0; i < len; ++i)
        {
            tiled[tiledp_col + i * stride] = tmp[i];
        }
    }

    //2.5
    private static void idwt3_v_cas1(int[] tmp, int sn, int len, int[] tiled, int tiledp_col, int stride)
    {
        int i, j;
        int in_even = tiledp_col + sn * stride;
        int in_odd = tiledp_col;

        Debug.Assert(len > 2);

        // Performs lifting in one single iteration. Saves memory
        // accesses and explicit interleaving.
        var s1 = tiled[in_even + stride];
        var dc = tiled[in_odd] - ((tiled[in_even] + s1 + 2) >> 2);
        tmp[0] = tiled[in_even] + dc;
        int end = len - 2 - ((len & 1) == 0 ? 1 : 0);
        for (i = 1, j = 1; i < end; i += 2, j++)
        {

            int s2 = tiled[in_even + (j + 1) * stride];

            int dn = tiled[in_odd + j * stride] - ((s1 + s2 + 2) >> 2);
            tmp[i] = dc;
            tmp[i + 1] = s1 + ((dn + dc) >> 1);

            dc = dn;
            s1 = s2;
        }
        tmp[i] = dc;
        if ((len & 1) == 0)
        {
            int dn = tiled[in_odd + (len / 2 - 1) * stride] - ((s1 + 1) >> 1);
            tmp[len - 2] = s1 + ((dn + dc) >> 1);
            tmp[len - 1] = dn;
        }
        else
        {
            tmp[len - 1] = s1 + dc;
        }

        for (i = 0; i < len; ++i)
        {
            tiled[tiledp_col + i * stride] = tmp[i];
        }
    }

    //2.5
    private static void decode_v_func(decode_v_job job)
    {
        idwt53_v_cols(job.v, job.tiled, job.tiledp, (int)job.w, (int)job.min_j, (int)job.max_j);
    }

    /// <summary>
    /// Vertical 5/3 pass over columns j0 .. j1 - 1 (the single-thread loop and each job). Upstream calls
    /// opj_idwt53_v per PARALLEL_COLS_53 columns; on AVX2 the columns go two strips per sweep instead
    /// (<see cref="idwt53_v_cas0_mcols2"/>, <see cref="idwt53_v_cas1_mcols2"/>), the rest as upstream.
    /// </summary>
    private static void idwt53_v_cols(dwt_local v, int[] tiled, int tiledp, int stride, int j0, int j1)
    {
        const int P = (int)PARALLEL_COLS_53;
        int j = j0;
#if NET8_0_OR_GREATER
        int len = v.sn + v.dn, pairs = (j1 - j0) / (2 * P);
        // Same length conditions as the vector paths of idwt53_v (cas1 handles len 1 and 2 separately)
        if (pairs > 0 && len > (v.cas == 0 ? 1 : 2) && UseVectors53 && !Simd.Use512 && 2 * Vector<int>.Count == P
            && tiledp + j0 >= 0 && stride >= 0 && 2L * P * len <= v.mem.Length
            && (long)tiledp + j0 + (long)(len - 1) * stride + 2L * P * pairs <= tiled.Length)
        {
            // tmp starts on a 64-byte boundary (as upstream's, which is 32-byte aligned), pinned meanwhile so that
            // it stays there: vertical pass -7% to -9% on 256x256 tiles. The pair kernels use rows 0 .. len - 2
            // of tmp (the last row goes straight into the tile), so the offset (at most 14 ints) always fits.
            var pin = GCHandle.Alloc(v.mem, GCHandleType.Pinned);
            try
            {
                int off = (int)((-(long)pin.AddrOfPinnedObject() & 63) / sizeof(int));
                if (off + 2L * P * (len - 1) > v.mem.Length)
                    off = 0;
                for (; j + 2 * P <= j1; j += 2 * P)
                {
                    if (v.cas == 0)
                    {
                        idwt53_v_cas0_mcols2(v.mem, off, v.sn, len, tiled, tiledp + j, stride);
                    }
                    else
                    {
                        idwt53_v_cas1_mcols2(v.mem, off, v.sn, len, tiled, tiledp + j, stride);
                    }
                }
            }
            finally
            {
                pin.Free();
            }
        }
#endif
        for (; j + P <= j1; j += P)
        {
            idwt53_v(v, tiled, tiledp + j, stride, P);
        }
        if (j < j1)
            idwt53_v(v, tiled, tiledp + j, stride, j1 - j);
    }

#if NET7_0_OR_GREATER
    /// <summary>5/3 vertical SIMD path: Vector&lt;int&gt; shifts need .NET 7.</summary>
    private static readonly bool UseVectors53 = Vector.IsHardwareAccelerated && PARALLEL_COLS_53 % Vector<int>.Count == 0;

    /// <summary>
    /// Copies len rows of PARALLEL_COLS_53 values from tmp back into the tile (upstream opj_idwt53_v_final_memcpy):
    /// vector stores instead of one Array.Copy call per 16-value row.
    /// </summary>
    /// <summary>
    /// True when rows 0 .. len - 1 of the PARALLEL_COLS_53 columns at tiledp_col, and tmp[0 .. P * len), are
    /// inside their arrays: the only elements the mcols passes touch.
    /// </summary>
    private static bool ColumnsInRange(int[] tmp, int len, int[] tiled, int tiledp_col, int stride)
        => len > 0 && tiledp_col >= 0 && stride >= 0 && (long)PARALLEL_COLS_53 * len <= tmp.Length
           && (long)tiledp_col + (long)(len - 1) * stride + PARALLEL_COLS_53 <= tiled.Length;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector<int> Ld(ref int b, int i) => Unsafe.ReadUnaligned<Vector<int>>(ref Unsafe.As<int, byte>(ref Unsafe.Add(ref b, i)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void St(ref int b, int i, Vector<int> v) => Unsafe.WriteUnaligned(ref Unsafe.As<int, byte>(ref Unsafe.Add(ref b, i)), v);

    /// <summary><see cref="CopyColumnsBack"/> without bounds checks; callers check <see cref="ColumnsInRange"/>.</summary>
    // Fully optimised from the first call: under tiering this loop otherwise ran as OSR code for whole decodes.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void CopyColumnsBackUnchecked(int[] tmp, int first, int len, int[] tiled, int tiledp_col, int stride)
    {
        const int P = (int)PARALLEL_COLS_53;
        int V = Vector<int>.Count;
        ref int t = ref tiled[tiledp_col];
        ref int o = ref tmp[0];
        for (int i = first, dst = first * stride; i < len; ++i, dst += stride)
        {
            for (int c = 0; c < P; c += V)
            {
                St(ref t, dst + c, Ld(ref o, P * i + c));
            }
        }
    }

    private static void CopyColumnsBack(int[] tmp, int len, int[] tiled, int tiledp_col, int stride)
    {
        const int P = (int)PARALLEL_COLS_53;
        int V = Vector<int>.Count;
        for (int i = 0; i < len; ++i)
        {
            int src = P * i, dst = tiledp_col + i * stride;
            for (int c = 0; c < P; c += V)
            {
                new Vector<int>(tmp, src + c).CopyTo(tiled, dst + c);
            }
        }
    }

    /// <summary>
    /// Two adjacent strips of <see cref="idwt53_v_cas0_mcols"/> (2 * PARALLEL_COLS_53 columns, four vectors per
    /// row) in one sweep over the rows. When a strip does not start on a 64-byte boundary its rows span two cache
    /// lines and the next strip shares one of them; strip by strip that line is fetched twice. Same arithmetic
    /// per column. Callers check the ranges (rows 0 .. len - 1 of the 2P columns, tmp[off .. off + 2P * (len - 1))).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void idwt53_v_cas0_mcols2(int[] tmp, int off, int sn, int len, int[] tiled, int tiledp_col, int stride)
    {
        const int Q = 2 * (int)PARALLEL_COLS_53;
        int V = Vector<int>.Count;
        var two = new Vector<int>(2);
        ref int t = ref tiled[tiledp_col];
        ref int o = ref tmp[off];
        int od = sn * stride;
        var d0 = Ld(ref t, od); var d1 = Ld(ref t, od + V); var d2 = Ld(ref t, od + 2 * V); var d3 = Ld(ref t, od + 3 * V);
        var s0 = Ld(ref t, 0) - ((d0 + d0 + two) >> 2);
        var s1 = Ld(ref t, V) - ((d1 + d1 + two) >> 2);
        var s2 = Ld(ref t, 2 * V) - ((d2 + d2 + two) >> 2);
        var s3 = Ld(ref t, 3 * V) - ((d3 + d3 + two) >> 2);

        // In-loop write-back as in idwt53_v_cas0_mcols, under the same stride condition.
        bool inLoop = (stride & 127) != 0;
        if (inLoop)
        {
            St(ref t, 0, s0); St(ref t, V, s1); St(ref t, 2 * V, s2); St(ref t, 3 * V, s3);
        }
        // Output rows i, i + 1 go to dst[oe], dst[oe + oq]: tmp first, then from the first even row at or after sn
        // (iT) straight into the tile. Step i reads tile rows i / 2 + 1 and sn + i / 2 + 1, so rows sn .. i + 1 have
        // been read and are not needed again; they skip tmp and the copy-back. Not in upstream. Only with the
        // in-loop write-back: on strides of a multiple of 128 ints these rows have mostly left L1 by then (+8% on
        // 256x256 tiles), and the copy-back afterwards is faster.
        ref int dst = ref o;
        int oe = 0, oq = Q, iT = inLoop ? (sn + 1) & ~1 : int.MaxValue;
        int i = 0, re = stride, ro = od + stride;
        for (; i < len - 3; i += 2, re += stride, ro += stride, oe += 2 * oq)
        {
            if (i == iT)
            {
                dst = ref t; oe = i * stride; oq = stride;
            }
            int oo = oe + oq;
            var dn = Ld(ref t, ro); var sx = Ld(ref t, re) - ((d0 + dn + two) >> 2);
            St(ref dst, oe, s0); St(ref dst, oo, d0 + ((s0 + sx) >> 1)); d0 = dn; s0 = sx;
            dn = Ld(ref t, ro + V); sx = Ld(ref t, re + V) - ((d1 + dn + two) >> 2);
            St(ref dst, oe + V, s1); St(ref dst, oo + V, d1 + ((s1 + sx) >> 1)); d1 = dn; s1 = sx;
            dn = Ld(ref t, ro + 2 * V); sx = Ld(ref t, re + 2 * V) - ((d2 + dn + two) >> 2);
            St(ref dst, oe + 2 * V, s2); St(ref dst, oo + 2 * V, d2 + ((s2 + sx) >> 1)); d2 = dn; s2 = sx;
            dn = Ld(ref t, ro + 3 * V); sx = Ld(ref t, re + 3 * V) - ((d3 + dn + two) >> 2);
            St(ref dst, oe + 3 * V, s3); St(ref dst, oo + 3 * V, d3 + ((s3 + sx) >> 1)); d3 = dn; s3 = sx;
            if (inLoop)
            {
                // Row i / 2 + 1 < sn <= iT: still in tmp
                int ow = Q * (i / 2 + 1);
                St(ref t, re, Ld(ref o, ow)); St(ref t, re + V, Ld(ref o, ow + V));
                St(ref t, re + 2 * V, Ld(ref o, ow + 2 * V)); St(ref t, re + 3 * V, Ld(ref o, ow + 3 * V));
            }
        }
        int copied = inLoop ? i / 2 + 1 : 0;
        int oi = Q * i;
        St(ref o, oi, s0); St(ref o, oi + V, s1); St(ref o, oi + 2 * V, s2); St(ref o, oi + 3 * V, s3);

        // The last output row goes straight into the tile: its input (high-pass row dn - 1) is in d0..d3 by now
        // and the odd-length tail only reads row (len - 1) / 2 < len - 1. tmp then needs rows 0 .. len - 2 only.
        int ol = (len - 1) * stride, ol2 = Q * (len - 2);
        if ((len & 1) != 0)
        {
            int rl = ((len - 1) / 2) * stride;
            var l = Ld(ref t, rl) - ((d0 + d0 + two) >> 2);
            St(ref t, ol, l); St(ref o, ol2, d0 + ((s0 + l) >> 1));
            l = Ld(ref t, rl + V) - ((d1 + d1 + two) >> 2);
            St(ref t, ol + V, l); St(ref o, ol2 + V, d1 + ((s1 + l) >> 1));
            l = Ld(ref t, rl + 2 * V) - ((d2 + d2 + two) >> 2);
            St(ref t, ol + 2 * V, l); St(ref o, ol2 + 2 * V, d2 + ((s2 + l) >> 1));
            l = Ld(ref t, rl + 3 * V) - ((d3 + d3 + two) >> 2);
            St(ref t, ol + 3 * V, l); St(ref o, ol2 + 3 * V, d3 + ((s3 + l) >> 1));
        }
        else
        {
            St(ref t, ol, d0 + s0); St(ref t, ol + V, d1 + s1); St(ref t, ol + 2 * V, d2 + s2); St(ref t, ol + 3 * V, d3 + s3);
        }

        // Rows min(iT, i) .. i - 1 and len - 1 are in the tile already
        CopyRows2Back(ref o, ref t, copied, Math.Min(iT, i), stride);
        CopyRows2Back(ref o, ref t, i, len - 1, stride);
    }

    /// <summary>Copies rows r0 .. r1 - 1 of 2 * PARALLEL_COLS_53 columns from tmp (o) back into the tile (t).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CopyRows2Back(ref int o, ref int t, int r0, int r1, int stride)
    {
        const int Q = 2 * (int)PARALLEL_COLS_53;
        int V = Vector<int>.Count;
        for (int r = r0, dst = r0 * stride, src = r0 * Q; r < r1; r++, dst += stride, src += Q)
        {
            St(ref t, dst, Ld(ref o, src)); St(ref t, dst + V, Ld(ref o, src + V));
            St(ref t, dst + 2 * V, Ld(ref o, src + 2 * V)); St(ref t, dst + 3 * V, Ld(ref o, src + 3 * V));
        }
    }

    /// <summary>
    /// Two adjacent strips of <see cref="idwt53_v_cas1_mcols"/> in one sweep over the rows, as
    /// <see cref="idwt53_v_cas0_mcols2"/> does for even-start columns. Callers check the same ranges.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void idwt53_v_cas1_mcols2(int[] tmp, int off, int sn, int len, int[] tiled, int tiledp_col, int stride)
    {
        const int Q = 2 * (int)PARALLEL_COLS_53;
        int V = Vector<int>.Count;
        var two = new Vector<int>(2);
        ref int t = ref tiled[tiledp_col];
        ref int o = ref tmp[off];
        int ev = sn * stride;
        var s0 = Ld(ref t, ev + stride); var s1 = Ld(ref t, ev + stride + V);
        var s2 = Ld(ref t, ev + stride + 2 * V); var s3 = Ld(ref t, ev + stride + 3 * V);
        var e = Ld(ref t, ev); var c0 = Ld(ref t, 0) - ((e + s0 + two) >> 2); var r0 = e + c0;
        e = Ld(ref t, ev + V); var c1 = Ld(ref t, V) - ((e + s1 + two) >> 2); var r1 = e + c1;
        e = Ld(ref t, ev + 2 * V); var c2 = Ld(ref t, 2 * V) - ((e + s2 + two) >> 2); var r2 = e + c2;
        e = Ld(ref t, ev + 3 * V); var c3 = Ld(ref t, 3 * V) - ((e + s3 + two) >> 2); var r3 = e + c3;
        St(ref o, 0, r0); St(ref o, V, r1); St(ref o, 2 * V, r2); St(ref o, 3 * V, r3);

        // In-loop write-back as in idwt53_v_cas1_mcols, under the same stride condition.
        bool inLoop = (stride & 127) != 0;
        if (inLoop)
        {
            St(ref t, 0, r0); St(ref t, V, r1); St(ref t, 2 * V, r2); St(ref t, 3 * V, r3);
        }
        int i = 1, rs2 = ev + 2 * stride, rd = stride, wo = Q;
        int end = len - 2 - ((len & 1) == 0 ? 1 : 0);
        for (; i < end; i += 2, rs2 += stride, rd += stride, wo += Q)
        {
            int oa = Q * i, ob = oa + Q;
            var sx = Ld(ref t, rs2); var dn = Ld(ref t, rd) - ((s0 + sx + two) >> 2);
            St(ref o, oa, c0); St(ref o, ob, s0 + ((dn + c0) >> 1)); c0 = dn; s0 = sx;
            sx = Ld(ref t, rs2 + V); dn = Ld(ref t, rd + V) - ((s1 + sx + two) >> 2);
            St(ref o, oa + V, c1); St(ref o, ob + V, s1 + ((dn + c1) >> 1)); c1 = dn; s1 = sx;
            sx = Ld(ref t, rs2 + 2 * V); dn = Ld(ref t, rd + 2 * V) - ((s2 + sx + two) >> 2);
            St(ref o, oa + 2 * V, c2); St(ref o, ob + 2 * V, s2 + ((dn + c2) >> 1)); c2 = dn; s2 = sx;
            sx = Ld(ref t, rs2 + 3 * V); dn = Ld(ref t, rd + 3 * V) - ((s3 + sx + two) >> 2);
            St(ref o, oa + 3 * V, c3); St(ref o, ob + 3 * V, s3 + ((dn + c3) >> 1)); c3 = dn; s3 = sx;
            if (inLoop)
            {
                St(ref t, rd, Ld(ref o, wo)); St(ref t, rd + V, Ld(ref o, wo + V));
                St(ref t, rd + 2 * V, Ld(ref o, wo + 2 * V)); St(ref t, rd + 3 * V, Ld(ref o, wo + 3 * V));
            }
        }
        int copied = inLoop ? (i - 1) / 2 + 1 : 0;
        int oi = Q * i;
        St(ref o, oi, c0); St(ref o, oi + V, c1); St(ref o, oi + 2 * V, c2); St(ref o, oi + 3 * V, c3);

        // Last output row straight into the tile, as in idwt53_v_cas0_mcols2: its input (row sn + dn - 1) is in
        // s0..s3 by now and the even-length tail only reads row len / 2 - 1 < len - 1.
        int ol = (len - 1) * stride, ol2 = Q * (len - 2);
        if ((len & 1) == 0)
        {
            int rl = (len / 2 - 1) * stride;
            var dn = Ld(ref t, rl) - ((s0 + s0 + two) >> 2);
            St(ref o, ol2, s0 + ((dn + c0) >> 1)); St(ref t, ol, dn);
            dn = Ld(ref t, rl + V) - ((s1 + s1 + two) >> 2);
            St(ref o, ol2 + V, s1 + ((dn + c1) >> 1)); St(ref t, ol + V, dn);
            dn = Ld(ref t, rl + 2 * V) - ((s2 + s2 + two) >> 2);
            St(ref o, ol2 + 2 * V, s2 + ((dn + c2) >> 1)); St(ref t, ol + 2 * V, dn);
            dn = Ld(ref t, rl + 3 * V) - ((s3 + s3 + two) >> 2);
            St(ref o, ol2 + 3 * V, s3 + ((dn + c3) >> 1)); St(ref t, ol + 3 * V, dn);
        }
        else
        {
            St(ref t, ol, s0 + c0); St(ref t, ol + V, s1 + c1); St(ref t, ol + 2 * V, s2 + c2); St(ref t, ol + 3 * V, s3 + c3);
        }

        for (int r = copied, dst = copied * stride, src = copied * Q; r < len - 1; r++, dst += stride, src += Q)
        {
            St(ref t, dst, Ld(ref o, src)); St(ref t, dst + V, Ld(ref o, src + V));
            St(ref t, dst + 2 * V, Ld(ref o, src + 2 * V)); St(ref t, dst + 3 * V, Ld(ref o, src + 3 * V));
        }
    }

    /// <summary>
    /// Vertical inverse 5x3 transform of PARALLEL_COLS_53 columns, top-most pixel on an even coordinate.
    /// </summary>
    /// <remarks>
    /// 2.5.4 - opj_idwt53_v_cas0_mcols_SIMD. Integer only, so identical to idwt3_v_cas0 per column.
    /// With two vectors per 16 columns (AVX2) both halves share one sweep over the rows; otherwise each
    /// Vector&lt;int&gt;-wide group of columns is processed top to bottom in turn.
    /// </remarks>
    // Fully optimised from the first call: under tiering this loop otherwise ran as OSR code for whole decodes.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void idwt53_v_cas0_mcols(int[] tmp, int sn, int len, int[] tiled, int tiledp_col, int stride)
    {
        const int P = (int)PARALLEL_COLS_53;
        int V = Vector<int>.Count;
        var two = new Vector<int>(2);
        Debug.Assert(len > 1);

        if (2 * V == P && ColumnsInRange(tmp, len, tiled, tiledp_col, stride))
        {
            // Both 8-column halves in one sweep over the rows, as upstream's AVX2 code does (full cache lines,
            // one page walk per row); unchecked accesses, rows 0 .. len - 1 checked above.
            ref int t = ref tiled[tiledp_col];
            ref int o = ref tmp[0];
            int od = sn * stride;
            var s1na = Ld(ref t, 0); var s1nb = Ld(ref t, V);
            var d1na = Ld(ref t, od); var d1nb = Ld(ref t, od + V);
            var s0na = s1na - ((d1na + d1na + two) >> 2);
            var s0nb = s1nb - ((d1nb + d1nb + two) >> 2);

            int i = 0, re = stride, ro = od + stride, copied = 0;
            if ((stride & 127) != 0)
            {
                // Step k (i = 2k) reads tile rows k + 1 and sn + k + 1; each row is read once. Output row k + 1 is
                // in tmp by then, so it is written into the tile row just read, while the line is in L1; a separate
                // copy-back pass afterwards misses on most rows of a tall strip. Row 0 (output s0n) goes in first;
                // the rows left are copied back after the loop. Not in upstream.
                // Only when rows with the same address bits 0-11 are 16 or more rows apart: with a stride of a
                // multiple of 128 ints, a store to row r falsely delays the loads of rows r + 4 (4K aliasing), and
                // the separate copy-back was faster (256x256 tiles: +7%; 2496-wide tile: -11%).
                St(ref t, 0, s0na); St(ref t, V, s0nb);
                for (int wo = P; i < len - 3; i += 2, re += stride, ro += stride, wo += P)
                {
                    var d1ca = d1na; var d1cb = d1nb;
                    var s0ca = s0na; var s0cb = s0nb;
                    s1na = Ld(ref t, re); s1nb = Ld(ref t, re + V);
                    d1na = Ld(ref t, ro); d1nb = Ld(ref t, ro + V);
                    s0na = s1na - ((d1ca + d1na + two) >> 2);
                    s0nb = s1nb - ((d1cb + d1nb + two) >> 2);
                    St(ref o, P * i, s0ca); St(ref o, P * i + V, s0cb);
                    St(ref o, P * (i + 1), d1ca + ((s0ca + s0na) >> 1));
                    St(ref o, P * (i + 1) + V, d1cb + ((s0cb + s0nb) >> 1));
                    St(ref t, re, Ld(ref o, wo)); St(ref t, re + V, Ld(ref o, wo + V));
                }
                copied = i / 2 + 1;
            }
            else
            {
                for (; i < len - 3; i += 2, re += stride, ro += stride)
                {
                    var d1ca = d1na; var d1cb = d1nb;
                    var s0ca = s0na; var s0cb = s0nb;
                    s1na = Ld(ref t, re); s1nb = Ld(ref t, re + V);
                    d1na = Ld(ref t, ro); d1nb = Ld(ref t, ro + V);
                    s0na = s1na - ((d1ca + d1na + two) >> 2);
                    s0nb = s1nb - ((d1cb + d1nb + two) >> 2);
                    St(ref o, P * i, s0ca); St(ref o, P * i + V, s0cb);
                    St(ref o, P * (i + 1), d1ca + ((s0ca + s0na) >> 1));
                    St(ref o, P * (i + 1) + V, d1cb + ((s0cb + s0nb) >> 1));
                }
            }
            St(ref o, P * i, s0na); St(ref o, P * i + V, s0nb);

            if ((len & 1) != 0)
            {
                int rl = ((len - 1) / 2) * stride;
                var lasta = Ld(ref t, rl) - ((d1na + d1na + two) >> 2);
                var lastb = Ld(ref t, rl + V) - ((d1nb + d1nb + two) >> 2);
                St(ref o, P * (len - 1), lasta); St(ref o, P * (len - 1) + V, lastb);
                St(ref o, P * (len - 2), d1na + ((s0na + lasta) >> 1));
                St(ref o, P * (len - 2) + V, d1nb + ((s0nb + lastb) >> 1));
            }
            else
            {
                St(ref o, P * (len - 1), d1na + s0na); St(ref o, P * (len - 1) + V, d1nb + s0nb);
            }
            CopyColumnsBackUnchecked(tmp, copied, len, tiled, tiledp_col, stride);
            return;
        }

        for (int c = 0; c < P; c += V)
        {
            int in_even = tiledp_col + c;
            int in_odd = tiledp_col + c + sn * stride;

            var s1n = new Vector<int>(tiled, in_even);
            var d1n = new Vector<int>(tiled, in_odd);
            // s0n = s1n - ((d1n + 1) >> 1) <==> s0n = s1n - ((d1n + d1n + 2) >> 2)
            var s0n = s1n - ((d1n + d1n + two) >> 2);

            int i = 0, j = 1;
            for (; i < len - 3; i += 2, j++)
            {
                var d1c = d1n;
                var s0c = s0n;
                s1n = new Vector<int>(tiled, in_even + j * stride);
                d1n = new Vector<int>(tiled, in_odd + j * stride);
                s0n = s1n - ((d1c + d1n + two) >> 2);
                s0c.CopyTo(tmp, P * i + c);
                (d1c + ((s0c + s0n) >> 1)).CopyTo(tmp, P * (i + 1) + c);
            }
            s0n.CopyTo(tmp, P * i + c);

            if ((len & 1) != 0)
            {
                s1n = new Vector<int>(tiled, in_even + ((len - 1) / 2) * stride);
                var last = s1n - ((d1n + d1n + two) >> 2);
                last.CopyTo(tmp, P * (len - 1) + c);
                (d1n + ((s0n + last) >> 1)).CopyTo(tmp, P * (len - 2) + c);
            }
            else
            {
                (d1n + s0n).CopyTo(tmp, P * (len - 1) + c);
            }
        }

        CopyColumnsBack(tmp, len, tiled, tiledp_col, stride);
    }

#if NET8_0_OR_GREATER
    /// <summary>
    /// Vector512 version of <see cref="idwt53_v_cas0_mcols"/>: all 16 columns in one register
    /// (upstream's __AVX512F__ build does 32 columns with two registers).
    /// </summary>
    private static void idwt53_v_cas0_mcols512(int[] tmp, int sn, int len, int[] tiled, int tiledp_col, int stride)
    {
        const int P = (int)PARALLEL_COLS_53;
        ReadOnlySpan<int> t = tiled;
        Span<int> o = tmp;
        var two = Vector512.Create(2);
        int in_even = tiledp_col;
        int in_odd = tiledp_col + sn * stride;

        var s1n = Vector512.Create(t.Slice(in_even, P));
        var d1n = Vector512.Create(t.Slice(in_odd, P));
        var s0n = s1n - ((d1n + d1n + two) >> 2);

        int i = 0, j = 1;
        for (; i < len - 3; i += 2, j++)
        {
            var d1c = d1n;
            var s0c = s0n;
            s1n = Vector512.Create(t.Slice(in_even + j * stride, P));
            d1n = Vector512.Create(t.Slice(in_odd + j * stride, P));
            s0n = s1n - ((d1c + d1n + two) >> 2);
            s0c.CopyTo(o.Slice(P * i));
            (d1c + ((s0c + s0n) >> 1)).CopyTo(o.Slice(P * (i + 1)));
        }
        s0n.CopyTo(o.Slice(P * i));

        if ((len & 1) != 0)
        {
            s1n = Vector512.Create(t.Slice(in_even + ((len - 1) / 2) * stride, P));
            var last = s1n - ((d1n + d1n + two) >> 2);
            last.CopyTo(o.Slice(P * (len - 1)));
            (d1n + ((s0n + last) >> 1)).CopyTo(o.Slice(P * (len - 2)));
        }
        else
        {
            (d1n + s0n).CopyTo(o.Slice(P * (len - 1)));
        }

        CopyColumnsBack(tmp, len, tiled, tiledp_col, stride);
    }

    /// <summary>Vector512 version of <see cref="idwt53_v_cas1_mcols"/>.</summary>
    private static void idwt53_v_cas1_mcols512(int[] tmp, int sn, int len, int[] tiled, int tiledp_col, int stride)
    {
        const int P = (int)PARALLEL_COLS_53;
        ReadOnlySpan<int> t = tiled;
        Span<int> o = tmp;
        var two = Vector512.Create(2);
        int in_even = tiledp_col + sn * stride;
        int in_odd = tiledp_col;

        var s1 = Vector512.Create(t.Slice(in_even + stride, P));
        var e0 = Vector512.Create(t.Slice(in_even, P));
        var dc = Vector512.Create(t.Slice(in_odd, P)) - ((e0 + s1 + two) >> 2);
        (e0 + dc).CopyTo(o);

        int i = 1, j = 1;
        int end = len - 2 - ((len & 1) == 0 ? 1 : 0);
        for (; i < end; i += 2, j++)
        {
            var s2 = Vector512.Create(t.Slice(in_even + (j + 1) * stride, P));
            var dn = Vector512.Create(t.Slice(in_odd + j * stride, P)) - ((s1 + s2 + two) >> 2);
            dc.CopyTo(o.Slice(P * i));
            (s1 + ((dn + dc) >> 1)).CopyTo(o.Slice(P * (i + 1)));
            dc = dn;
            s1 = s2;
        }
        dc.CopyTo(o.Slice(P * i));

        if ((len & 1) == 0)
        {
            var dn = Vector512.Create(t.Slice(in_odd + (len / 2 - 1) * stride, P)) - ((s1 + s1 + two) >> 2);
            (s1 + ((dn + dc) >> 1)).CopyTo(o.Slice(P * (len - 2)));
            dn.CopyTo(o.Slice(P * (len - 1)));
        }
        else
        {
            (s1 + dc).CopyTo(o.Slice(P * (len - 1)));
        }

        CopyColumnsBack(tmp, len, tiled, tiledp_col, stride);
    }
#endif

    /// <summary>
    /// Vertical inverse 5x3 transform of PARALLEL_COLS_53 columns, top-most pixel on an odd coordinate.
    /// </summary>
    /// <remarks>2.5.4 - opj_idwt53_v_cas1_mcols_SIMD. Integer only, so identical to idwt3_v_cas1 per column.</remarks>
    // Fully optimised from the first call: under tiering this loop otherwise ran as OSR code for whole decodes.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void idwt53_v_cas1_mcols(int[] tmp, int sn, int len, int[] tiled, int tiledp_col, int stride)
    {
        const int P = (int)PARALLEL_COLS_53;
        int V = Vector<int>.Count;
        var two = new Vector<int>(2);
        Debug.Assert(len > 2);

        if (2 * V == P && ColumnsInRange(tmp, len, tiled, tiledp_col, stride))
        {
            // One sweep for both halves, unchecked; see idwt53_v_cas0_mcols.
            ref int t = ref tiled[tiledp_col];
            ref int o = ref tmp[0];
            int ev = sn * stride;
            var s1a = Ld(ref t, ev + stride); var s1b = Ld(ref t, ev + stride + V);
            var e0a = Ld(ref t, ev); var e0b = Ld(ref t, ev + V);
            var dca = Ld(ref t, 0) - ((e0a + s1a + two) >> 2);
            var dcb = Ld(ref t, V) - ((e0b + s1b + two) >> 2);
            St(ref o, 0, e0a + dca); St(ref o, V, e0b + dcb);

            // Step j (i = 2j - 1) reads tile rows j and sn + j + 1, each once; with the same stride condition as
            // idwt53_v_cas0_mcols, output row j is written into tile row j right after it is read. Not in upstream.
            bool inLoop = (stride & 127) != 0;
            if (inLoop)
            {
                St(ref t, 0, e0a + dca); St(ref t, V, e0b + dcb);
            }
            int i = 1, rs2 = ev + 2 * stride, rd = stride, wo = P;
            int end = len - 2 - ((len & 1) == 0 ? 1 : 0);
            for (; i < end; i += 2, rs2 += stride, rd += stride, wo += P)
            {
                var s2a = Ld(ref t, rs2); var s2b = Ld(ref t, rs2 + V);
                var dna = Ld(ref t, rd) - ((s1a + s2a + two) >> 2);
                var dnb = Ld(ref t, rd + V) - ((s1b + s2b + two) >> 2);
                St(ref o, P * i, dca); St(ref o, P * i + V, dcb);
                St(ref o, P * (i + 1), s1a + ((dna + dca) >> 1));
                St(ref o, P * (i + 1) + V, s1b + ((dnb + dcb) >> 1));
                dca = dna; dcb = dnb;
                s1a = s2a; s1b = s2b;
                if (inLoop)
                {
                    St(ref t, rd, Ld(ref o, wo)); St(ref t, rd + V, Ld(ref o, wo + V));
                }
            }
            int copied = inLoop ? (i - 1) / 2 + 1 : 0;
            St(ref o, P * i, dca); St(ref o, P * i + V, dcb);

            if ((len & 1) == 0)
            {
                int rl = (len / 2 - 1) * stride;
                var dna = Ld(ref t, rl) - ((s1a + s1a + two) >> 2);
                var dnb = Ld(ref t, rl + V) - ((s1b + s1b + two) >> 2);
                St(ref o, P * (len - 2), s1a + ((dna + dca) >> 1));
                St(ref o, P * (len - 2) + V, s1b + ((dnb + dcb) >> 1));
                St(ref o, P * (len - 1), dna); St(ref o, P * (len - 1) + V, dnb);
            }
            else
            {
                St(ref o, P * (len - 1), s1a + dca); St(ref o, P * (len - 1) + V, s1b + dcb);
            }
            CopyColumnsBackUnchecked(tmp, copied, len, tiled, tiledp_col, stride);
            return;
        }

        for (int c = 0; c < P; c += V)
        {
            int in_even = tiledp_col + c + sn * stride;
            int in_odd = tiledp_col + c;

            var s1 = new Vector<int>(tiled, in_even + stride);
            var e0 = new Vector<int>(tiled, in_even);
            // in_odd[0] - ((in_even[0] + s1 + 2) >> 2)
            var dc = new Vector<int>(tiled, in_odd) - ((e0 + s1 + two) >> 2);
            (e0 + dc).CopyTo(tmp, c);

            int i = 1, j = 1;
            int end = len - 2 - ((len & 1) == 0 ? 1 : 0);
            for (; i < end; i += 2, j++)
            {
                var s2 = new Vector<int>(tiled, in_even + (j + 1) * stride);
                var dn = new Vector<int>(tiled, in_odd + j * stride) - ((s1 + s2 + two) >> 2);
                dc.CopyTo(tmp, P * i + c);
                (s1 + ((dn + dc) >> 1)).CopyTo(tmp, P * (i + 1) + c);
                dc = dn;
                s1 = s2;
            }
            dc.CopyTo(tmp, P * i + c);

            if ((len & 1) == 0)
            {
                var dn = new Vector<int>(tiled, in_odd + (len / 2 - 1) * stride) - ((s1 + s1 + two) >> 2);
                (s1 + ((dn + dc) >> 1)).CopyTo(tmp, P * (len - 2) + c);
                dn.CopyTo(tmp, P * (len - 1) + c);
            }
            else
            {
                (s1 + dc).CopyTo(tmp, P * (len - 1) + c);
            }
        }

        CopyColumnsBack(tmp, len, tiled, tiledp_col, stride);
    }
#endif

    //2.5
    private static void idwt53_v(dwt_local dwt, int[] tiled, int tiledp_col, int stride, int nb_cols)
    {
        int sn = dwt.sn;
        int len = sn + dwt.dn;
        if (dwt.cas == 0)
        {
#if NET7_0_OR_GREATER
#if NET8_0_OR_GREATER
            if (Simd.Use512 && len > 1 && nb_cols == PARALLEL_COLS_53)
            {
                idwt53_v_cas0_mcols512(dwt.mem, sn, len, tiled, tiledp_col, stride);
                return;
            }
#endif
            if (UseVectors53 && len > 1 && nb_cols == PARALLEL_COLS_53)
            {
                idwt53_v_cas0_mcols(dwt.mem, sn, len, tiled, tiledp_col, stride);
                return;
            }
#endif
            if (len > 1)
            {
                for (int c = 0; c < nb_cols; c++, tiledp_col++)
                {
                    idwt3_v_cas0(dwt.mem, sn, len, tiled, tiledp_col, stride);
                }
                return;
            }
        }
        else
        {
            if (len == 1)
            {
                for (int c = 0; c < nb_cols; c++, tiledp_col++)
                {
                    tiled[tiledp_col] /= 2;
                }
                return;
            }

            if (len == 2)
            {
                int[] o = dwt.mem;
                for (int c = 0; c < nb_cols; c++, tiledp_col++)
                {
                    int in_even = tiledp_col + sn * stride;
                    int in_odd = tiledp_col;

                    o[1] = tiled[in_odd] - ((tiled[in_even] + 1) >> 1);
                    o[0] = tiled[in_even] + o[1];

                    for (int i = 0; i < len; ++i)
                    {
                        tiled[tiledp_col + i * stride] = o[i];
                    }
                }

                return;
            }

#if NET7_0_OR_GREATER
#if NET8_0_OR_GREATER
            if (Simd.Use512 && len > 2 && nb_cols == PARALLEL_COLS_53)
            {
                idwt53_v_cas1_mcols512(dwt.mem, sn, len, tiled, tiledp_col, stride);
                return;
            }
#endif
            if (UseVectors53 && len > 2 && nb_cols == PARALLEL_COLS_53)
            {
                idwt53_v_cas1_mcols(dwt.mem, sn, len, tiled, tiledp_col, stride);
                return;
            }
#endif
            if (len <= 2) return;
            for (int c = 0; c < nb_cols; c++, tiledp_col++)
            {
                idwt3_v_cas1(dwt.mem, sn, len, tiled, tiledp_col, stride);
            }
            return;
        }
    }

    /// <summary>
    /// Get norm of 9-7 wavelet.
    /// </summary>
    /// <remarks>2.5 - opj_dwt_getnorm_real</remarks>
    internal static double Getnorm_real(uint level, uint orient)
    {
        if (orient == 0 && level >= 10)
        {
            level = 9;
        }
        else if (orient > 0 && level >= 9)
        {
            level = 8;
        }
        return dwt_norms_real[orient][level];
    }

    /// <summary>
    /// Get norm of 5-3 wavelet.
    /// </summary>
    /// <remarks>2.5 - opj_dwt_getnorm</remarks>
    internal static double Getnorm(uint level, uint orient)
    {
        //FIXME ! This is just a band-aid to avoid a buffer overflow
        if (orient == 0 && level >= 10)
        {
            level = 9;
        }
        else if (orient > 0 && level >= 9)
        {
            level = 8;
        }

        return dwt_norms[orient][level];
    }

    internal delegate int GetGainFunc(int orient);

    //2.5
    private static void GetBandCoordinates(TcdTilecomp tilec,
            uint resno,
            uint bandno,
            uint tcx0,
            uint tcy0,
            uint tcx1,
            uint tcy1,
            out uint tbx0,
            out uint tby0,
            out uint tbx1,
            out uint tby1)
    {
        // Compute number of decomposition for this band. See table F-1
        int nb = (resno == 0) ?
                  (int)tilec.numresolutions - 1 :
                  (int)(tilec.numresolutions - resno);
        /* Map above tile-based coordinates to sub-band-based coordinates per */
        /* equation B-15 of the standard */
        uint x0b = bandno & 1;
        uint y0b = bandno >> 1;
        tbx0 = (nb == 0) ? tcx0 :
               (tcx0 <= (1U << (int)(nb - 1)) * x0b) ? 0 :
               MyMath.uint_ceildivpow2(tcx0 - (1U << (nb - 1)) * x0b, nb);
        tby0 = (nb == 0) ? tcy0 :
               (tcy0 <= (1U << (nb - 1)) * y0b) ? 0 :
               MyMath.uint_ceildivpow2(tcy0 - (1U << (nb - 1)) * y0b, nb);
        tbx1 = (nb == 0) ? tcx1 :
               (tcx1 <= (1U << (nb - 1)) * x0b) ? 0 :
               MyMath.uint_ceildivpow2(tcx1 - (1U << (nb - 1)) * x0b, nb);
        tby1 = (nb == 0) ? tcy1 :
               (tcy1 <= (1U << (nb - 1)) * y0b) ? 0 :
               MyMath.uint_ceildivpow2(tcy1 - (1U << (nb - 1)) * y0b, nb);
    }

    //2.5 - opj_dwt_segment_grow
    private static void SegmentGrow(uint filter_width, uint max_size, ref uint start, ref uint end)
    {
        start = MyMath.uint_subs(start, filter_width);
        end = MyMath.uint_adds(end, filter_width);
        end = Math.Min(end, max_size);
    }

    private delegate void DWG_action(dwt_local dwt);

    private sealed class dwt_local
    {
        internal int[] mem;

        /// <summary>
        /// Number of elements in high pass band
        /// </summary>
        internal int dn;

        /// <summary>
        /// Number of elements in low pass band
        /// </summary>
        internal int sn;

        /// <summary>
        /// 0 = start on even coord, 1 = start on odd coord
        /// </summary>
        internal int cas;

        public dwt_local Clone()
        {
            return (dwt_local)MemberwiseClone();
        }
    }

    private sealed class v4dwt_local
    {
        /// <summary>
        /// Each wavelet is 4 floating points.
        /// </summary>
        /// <remarks>
        ///C# note: Org. impl stores the wavelet as a struct with eight
        ///floating points. Here it's stored as a continuous array.
        ///
        /// This so to make it possible to use Buffer.BlockCopy to
        /// copy the values.
        ///</remarks>
        internal float[] wavelet;

        /// <summary>
        /// Number of elements in high pass band
        /// </summary>
        internal int dn;

        /// <summary>
        /// Number of elements in low pass band
        /// </summary>
        internal int sn;

        /// <summary>
        ///  0 = start on even coord, 1 = start on odd coord
        /// </summary>
        internal int cas;

        /// <summary>
        /// Start coord in low pass band
        /// </summary>
        internal uint win_l_x0;

        /// <summary>
        /// End coord in low pass band
        /// </summary>
        internal uint win_l_x1;

        /// <summary>
        /// Start coord in high pass band
        /// </summary>
        internal uint win_h_x0;

        /// <summary>
        /// End coord in high pass band
        /// </summary>
        internal uint win_h_x1;

        public v4dwt_local Clone() { return (v4dwt_local)MemberwiseClone(); }
    }

    /// <summary>
    /// Used by the MT impl
    /// </summary>
    private sealed class decode_h_job
    {
        public readonly dwt_local h;
        public readonly uint rw;
        public readonly uint w;
        public readonly int[] tiled;
        public readonly int tiledp;
        public readonly uint min_j;
        public readonly uint max_j;

        public decode_h_job(
            dwt_local h,
            uint rw, uint w,
            int[] tiled, int tiledp,
            uint min_j, uint max_j
        )
        {
            this.h = h;
            this.rw = rw;
            this.w = w;
            this.tiled = tiled;
            this.tiledp = tiledp;
            this.min_j = min_j;
            this.max_j = max_j;
        }
    }

    /// <summary>
    /// Used by the MT impl
    /// </summary>
    private sealed class encode_v_job
    {
        public readonly dwt_local v;
        public readonly uint rh;
        public readonly uint w;
        public readonly int[] tiled;
        public readonly int tiledp;
        public readonly uint min_j;
        public readonly uint max_j;
        public readonly EncodeAndDeinterleaveVfunc encode_and_deinterleave_v;

        public encode_v_job(
            dwt_local v,
            uint rh, uint w,
            int[] tiled, int tiledp,
            uint min_j, uint max_j,
            EncodeAndDeinterleaveVfunc encode_and_deinterleave_v
        )
        {
            this.v = v;
            this.rh = rh;
            this.w = w;
            this.tiled = tiled;
            this.tiledp = tiledp;
            this.min_j = min_j;
            this.max_j = max_j;
            this.encode_and_deinterleave_v = encode_and_deinterleave_v;
        }
    }

    /// <summary>
    /// Used by the MT impl
    /// </summary>
    private sealed class encode_h_job
    {
        public readonly dwt_local h;
        public readonly uint rw;
        public readonly uint w;
        public readonly int[] tiled;
        public readonly int tiledp;
        public readonly uint min_j;
        public readonly uint max_j;
        public readonly EncodeAndDeinterleaveH_OneRowfunc fn;

        public encode_h_job(
            dwt_local h,
            uint rw, uint w,
            int[] tiled, int tiledp,
            uint min_j, uint max_j,
            EncodeAndDeinterleaveH_OneRowfunc fn
        )
        {
            this.h = h;
            this.rw = rw;
            this.w = w;
            this.tiled = tiled;
            this.tiledp = tiledp;
            this.min_j = min_j;
            this.max_j = max_j;
            this.fn = fn;
        }
    }

    /// <summary>
    /// Used by the MT impl
    /// </summary>
    private sealed class decode_v_job
    {
        public readonly dwt_local v;
        public readonly uint rh;
        public readonly uint w;
        public readonly int[] tiled;
        public readonly int tiledp;
        public readonly uint min_j;
        public readonly uint max_j;

        public decode_v_job(
            dwt_local v,
            uint rh, uint w,
            int[] tiled, int tiledp,
            uint min_j, uint max_j
        )
        {
            this.v = v;
            this.rh = rh;
            this.w = w;
            this.tiled = tiled;
            this.tiledp = tiledp;
            this.min_j = min_j;
            this.max_j = max_j;
        }
    }

    /// <summary>
    /// Used by the MT impl
    /// </summary>
    private sealed class dwt97_decode_h_job
    {
        public readonly v4dwt_local h;
        public readonly uint rw;
        public readonly uint w;
        public readonly int[] aj;
        public readonly int ajp;
        public readonly uint nb_rows;

        public dwt97_decode_h_job(
            v4dwt_local h,
            uint rw, uint w,
            int[] aj, int ajp,
            uint nb_rows
        )
        {
            this.h = h;
            this.rw = rw;
            this.w = w;
            this.aj = aj;
            this.ajp = ajp;
            this.nb_rows = nb_rows;
        }
    }

    /// <summary>
    /// Used by the MT impl
    /// </summary>
    private sealed class dwt97_decode_v_job
    {
        public readonly v4dwt_local v;
        public readonly uint rh;
        public readonly uint w;
        public readonly int[] aj;
        public readonly int ajp;
        public readonly uint nb_columns;

        public dwt97_decode_v_job(
            v4dwt_local v,
            uint rh, uint w,
            int[] aj, int ajp,
            uint nb_columns
        )
        {
            this.v = v;
            this.rh = rh;
            this.w = w;
            this.aj = aj;
            this.ajp = ajp;
            this.nb_columns = nb_columns;
        }
    }
}
