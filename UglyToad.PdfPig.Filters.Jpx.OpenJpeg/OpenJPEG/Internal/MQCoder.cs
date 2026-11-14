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

using System;
using System.Runtime.CompilerServices;
using System.Diagnostics;

namespace OpenJpeg.Internal
{
    /// <summary>
    /// MQ-coder.
    /// 
    /// The MQ arithmetic coding was orgiginaly developed for JBIG image compression.
    /// 
    /// Rather than coding the binary value, MQ encodies if the symbol being coded
    /// is what we expected (Most Probably, Less Probably). 
    /// </summary>
    internal sealed class MQCoder
    {
        const uint BYPASS_CT_INIT = 0xDEADBEEF;

        /// <summary>
        /// Temporary buffer where bits are coded or decoded
        /// </summary>
        internal uint c;

        /// <summary>
        /// Number of bits already read or free to write
        /// </summary>
        internal uint ct;

        /// <summary>
        /// Maximum length to decode
        /// </summary>
        uint _end;

        /// <summary>
        /// Source data
        /// </summary>
        byte[] _data;

        /// <summary>
        /// Original value of the 2 bytes at end[0] and end[1]
        /// </summary>
        readonly byte[] _backup = new byte[Constants.COMMON_CBLK_DATA_EXTRA];

        /// <summary>
        /// aaaaaaaaaaa
        /// </summary>
        internal uint a;

        /// <summary>
        /// Read position in the data array
        /// </summary>
        int _bp, _start;

        /// <summary>
        /// Only used by decoder, to count the number of times a terminating 0xFF >0x8F marker is read
        /// </summary>
        internal uint _end_of_byte_stream_counter;

        /// <summary>
        /// State of each context, as its packed <see cref="MQCState.Table"/> entry. Upstream keeps a pointer
        /// to an opj_mqc_state_t per context; holding the entry itself means the common decode path is one load.
        /// </summary>
        internal readonly uint[] ctxs = new uint[Constants.MQC_NUMCTXS];

        /// <summary>Current context, an index into <see cref="ctxs"/> (upstream: mqc->curctx).</summary>
        internal int curctx;

        /// <summary>
        /// lut_ctxno_zc shifted by (1 << 9) * bandno
        /// </summary>
        /// <remarks>
        /// This is a pointer into the byte array T1Luts.lut_ctxno_zc
        /// </remarks>
        internal int lut_ctxno_zc_orient;

        internal int NumBytes { get { return _bp - _start; } }

        //C# - Creates the ctxs "struct". Might be overdesigned, as this
        //     was some of the first code I converted and I was usure whet
        //     c pointer peculiarities I needed to support.
        internal MQCoder()
        {
        }

        /// <remarks>
        /// 2.5 - opj_mqc_init_dec_common
        /// </remarks>
        void CommonInit(byte[] data, uint offset, uint length, uint extra_writable_bytes)
        {
            Debug.Assert(extra_writable_bytes >= Constants.COMMON_CBLK_DATA_EXTRA);
            _data = data;
            _start = (int) offset;
            _end = offset + length;

            // Insert an artificial 0xFF 0xFF marker at end of the code block
            // data so that the bytein routines stop on it. This saves us comparing
            // the bp and end pointers
            // But before inserting it, backup th bytes we will overwrite
            Array.Copy(_data, _end, _backup, 0, Constants.COMMON_CBLK_DATA_EXTRA);
            _data[_end + 0] = 0xFF;
            _data[_end + 1] = 0xFF;
            _bp = (int)offset;
        }

        /// <summary>
        /// Initialize Raw for decoder
        /// </summary>
        /// <remarks>2.5 - opj_mqc_raw_init_dec</remarks>
        /// <param name="bp">Data</param>
        /// <param name="start_pos">Offset into data</param>
        /// <param name="len">Length of data</param>
        internal void InitRawDec(byte[] bp, uint start_pos, uint len, uint extra_writable_bytes)
        {
            CommonInit(bp, start_pos, len, extra_writable_bytes);
            c = 0;
            ct = 0;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="bp">The actual buffer</param>
        /// <param name="start_pos">Pointer to the start of the buffer where the bytes will be written</param>
        /// <param name="len"></param>
        /// <remarks>2.5 - opj_mqc_init_enc</remarks>
        internal void InitEnc(byte[] bp)
        {
            // To avoid the curctx pointer to be dangling, but not strictly
            // required as the current context is always set before encoding
            Setcurctx(T1_CTXNO.ZC);

            // As specified in Figure C.10 - Initialization of the encoder
            // (C.2.8 Initialization of the encoder (INITENC))
            a = 0x8000;
            c = 0;

            // Yes, we point before the start of the buffer, but this is safe
            // given opj_tcd_code_block_enc_allocate_data()
            // C# impl. note. Special handeling is needed for this, as we can't
            //    have data before a buffer.
            _data = bp;
            _bp = -1;
            ct = 12;

            _start = 0;
            _end_of_byte_stream_counter = 0;
        }

        /// <summary>
        /// Return number of extra bytes to add to opj_mqc_numbytes() for the²
        /// size of a non-terminating BYPASS pass
        /// </summary>
        /// <param name="erterm">if ERTERM is enabled</param>
        /// <remarks>2.5 - opj_mqc_bypass_get_extra_bytes</remarks>
        internal uint BypassGetExtraBytes(bool erterm)
        {
            Debug.Assert(_bp - 1 > 0, "C# Trouble ahead");
            return (ct < 7 || (ct == 7 && (erterm || _data[_bp - 1] != 0xff))) ? 1u : 0;
        }

        /// <summary>
        /// BYPASS mode switch, initialization operation
        /// </summary>
        /// <remarks>2.5 - opj_mqc_bypass_init_enc</remarks>
        internal void BypassInitEnc()
        {
            c = 0;

            // in theory we should initialize to 8, but use this special value
            // as a hint that opj_mqc_bypass_enc() has never been called, so
            // as to avoid the 0xff 0x7f elimination trick in opj_mqc_bypass_flush_enc()
            // to trigger when we don't have output any bit during this bypass sequence
            // Any value > 8 will do
            ct = BYPASS_CT_INIT;
        }

        //2.5 - opj_mqc_restart_init_enc
        internal void RestartInitEnc()
        {
            a = 0x8000;
            c = 0;
            ct = 12;
            _bp--;
            if (_data[_bp] == 0xff)
                ct = 13;
        }

        //2.5 - opj_mqc_reset_enc
        internal void ResetEnc()
        {
            ResetStates();
            SetState(T1_CTXNO.UNI, 0, 46);
            SetState(T1_CTXNO.AGG, 0, 3);
            SetState(T1_CTXNO.ZC, 0, 4);
        }

        /// <summary>
        /// Initialize MQ for decoder
        /// </summary>
        /// <remarks>2.5 - opj_mqc_init_dec</remarks>
        /// <param name="bp">The buffer from which the bytes will be read</param>
        /// <param name="start_pos">Offset into the data buffer</param>
        /// <param name="len">Length of data</param>
        internal void InitDec(byte[] bp, uint start_pos, uint len, uint extra_writable_bytes)
        {
            // Implements ISO 15444-1 C.3.5 Initialization of the decoder (INITDEC)
            // Note: alternate "J.1 - Initialization of the software-conventions
            // decoder" has been tried, but does
            // not bring any improvement.
            // See https://github.com/uclouvain/openjpeg/issues/921
            CommonInit(bp, start_pos, len, extra_writable_bytes);
            Setcurctx(0);

            _end_of_byte_stream_counter = 0;
            if (len == 0)
                c = 0xFF << 16;
            else
                c = (uint)_data[start_pos] << 16;

            ByteIn();
            c <<= 7;
            ct -= 7;
            a = 0x8000;
        }

        /// <summary>
        /// Set the current context used for coding/decoding
        /// </summary>
        /// <param name="ctxno">Number that identifies the context</param>
        /// <remarks>
        /// 2.5 - opj_mqc_setcurctx
        /// 
        /// Org. impl. is a macro
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void Setcurctx(T1_CTXNO i) { curctx = (int)i; }

        /// <summary>
        /// Set the current context used for coding/decoding
        /// </summary>
        /// <param name="ctxno">Number that identifies the context</param>
        /// <remarks>
        /// 2.5 - opj_mqc_setcurctx
        /// 
        /// Org. impl. is a macro
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void Setcurctx(int ctxno) { curctx = ctxno; }

        /// <summary>
        /// Fill mqc->c with 1's for flushing
        /// </summary>
        /// <remarks>2.5 - opj_mqc_setbits</remarks>
        internal void Setbits() {
            uint tempc = c + a;
            c |= 0xffffu;
            if (c >= tempc)
                c -= 0x8000;
        }

        //2.5 - opj_mqc_bypass_flush_enc
        internal void FlushEnc(bool erterm)
        {
            Debug.Assert(_bp - 1 > 0, "C# Trouble ahead");

            /* Is there any bit remaining to be flushed ? */
            /* If the last output byte is 0xff, we can discard it, unless */
            /* erterm is required (I'm not completely sure why in erterm */
            /* we must output 0xff 0x2a if the last byte was 0xff instead of */
            /* discarding it, but Kakadu requires it when decoding */
            /* in -fussy mode) */
            if (ct < 7 || (ct == 7 && (erterm || _data[_bp - 1] != 0xff)))
            {
                byte bit_value = 0;
                /* If so, fill the remaining lsbs with an alternating sequence of */
                /* 0,1,... */
                /* Note: it seems the standard only requires that for a ERTERM flush */
                /* and doesn't specify what to do for a regular BYPASS flush */
                while (ct > 0)
                {
                    ct--;
                    c += (uint)(bit_value << (int)ct);
                    bit_value = (byte)(1U - bit_value);
                }
                _data[_bp] = (byte)c;
                /* Advance pointer so that opj_mqc_numbytes() returns a valid value */
                _bp++;
            }
            else if (ct == 7 && _data[_bp -1] == 0xff)
            {
                /* Discard last 0xff */
                Debug.Assert(!erterm);
                _bp--;
            }
            else if (ct == 8 && !erterm &&
                       _data[_bp -1 ] == 0x7f && _data[_bp - 2] == 0xff)
            {
                /* Tiny optimization: discard terminating 0xff 0x7f since it is */
                /* interpreted as 0xff 0x7f [0xff 0xff] by the decoder, and given */
                /* the bit stuffing, in fact as 0xff 0xff [0xff ..] */
                /* Happens once on opj_compress -i ../MAPA.tif -o MAPA.j2k  -M 1 */
                _bp -= 2;
            }

            Debug.Assert(_data[_bp - 1] != 0xff);
        }

        /// <summary>
        /// Flush the encoder, so that all remaining data is written
        /// </summary>
        /// <remarks>2.5 - opj_mqc_flush</remarks>
        internal void Flush()
        {
            Setbits();
            c <<= (int)ct;
            Byteout();
            c <<= (int)ct;
            Byteout();

            if (_data[_bp] != 0xff)
                _bp++;
        }

        //2.5 - opj_mqc_segmark_enc
        internal void SegmarkEnc()
        {
            curctx = 18;

            for (int i = 1; i < 5; i++)
            {
                Encode((i % 2) != 0);
            }
        }

        /// <summary>
        /// BYPASS mode switch, coding operation
        /// </summary>
        /// <remarks>2.5 - opj_mqc_bypass_enc</remarks>
        internal void BypassEnc(uint d)
        {
            if (ct == BYPASS_CT_INIT)
                ct = 8;
            ct--;
            c += d << (int) ct;
            if (ct == 0)
            {
                _data[_bp] = (byte) c;
                ct = 8;
                //If the previous byte was 0xff, make sure that the next msb is 0
                Debug.Assert(_bp >= 0, "C# Trouble ahead");
                if (_data[_bp] == 0xff)
                    ct = 7;
                _bp++;
                c = 0;
            }
        }

        /// <summary>
        /// ERTERM mode switch (PTERM)
        /// </summary>
        /// <remarks>2.5 - opj_mqc_erterm_enc</remarks>
        internal void ErtermEnc()
        {
            int k = (int)(11 - ct + 1);

            while (k > 0)
            {
                c <<= (int) ct;
                ct = 0;
                Byteout();
                k -= (int) ct;
            }

            if (_data[_bp] != 0xff)
                Byteout();
        }

        //2.5 - opj_mqc_encode_macro
        internal void Encode(bool d)
        {
            if (MQCState.Mps(ctxs[curctx]) == d)
                Codemps();
            else
                Codelps();
        }

        //2.5 - opj_mqc_codelps_macro
        void Codelps()
        {
            uint e = ctxs[curctx];
            uint qeval = e & 0xFFFF;
            a -= qeval;
            if (a < qeval)
                c += qeval;
            else
                a = qeval;
            ctxs[curctx] = MQCState.Nlps(e);
            Renorme();
        }

        //2.5 - opj_mqc_codemps_macro
        void Codemps()
        {
            uint e = ctxs[curctx];
            uint qeval = e & 0xFFFF;
            a -= qeval;
            if ((a & 0x8000) == 0)
            {
                if (a < qeval)
                    a = qeval;
                else
                    c += qeval;
                ctxs[curctx] = MQCState.Nmps(e);
                Renorme();
            }
            else
            {
                c += qeval;
            }
        }

        //2.5 - opj_mqc_renorme_macro
        // C# Note, the macro works on funciton local variables, which
        //    is why it only updates ct when doing byteout. These local
        //    variables will later be "Uploaded" into the mqc struct.
        //    We don't do any uploading, so we'll just manipulate the
        //    actual members.
        void Renorme()
        {
            do
            {
                a <<= 1;
                c <<= 1;
                ct--;
                if (ct == 0)
                    Byteout();
            } while ((a & 0x8000) == 0);
        }

        //2.5 - opj_mqc_raw_decode
        internal bool RawDecode()
        {
            if (ct == 0)
            {
                // Given opj_mqc_raw_init_dec() we know that at some point we will
                // have a 0xFF 0xFF artificial marker
                if (c == 0xff)
                {
                    if (_data[_bp] > 0x8f)
                    {
                        c = 0xff;
                        ct = 8;
                    }
                    else
                    {
                        c = _data[_bp++];
                        ct = 7;
                    }
                }
                else
                {
                    c = _data[_bp++];
                    ct = 8;
                }
            }
            ct--;
            return ((c >> (int)ct) & 0x01) == 0x01;
        }

        //2.5 - opj_mqc_decode_macro, with opj_mqc_mpsexchange_macro / opj_mqc_lpsexchange_macro inlined
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool Decode()
        {
            uint[] states = ctxs;
            int cx = curctx;
            uint e = states[cx];
            uint qeval = e & 0xFFFF;
            bool mps = MQCState.Mps(e);
            bool d;

            a -= qeval;
            if ((c >> 16) < qeval)
            {
                // LPS exchange
                if (a < qeval)
                {
                    d = mps;
                    states[cx] = MQCState.Nmps(e);
                }
                else
                {
                    d = !mps;
                    states[cx] = MQCState.Nlps(e);
                }
                a = qeval;
                Renormd();
            }
            else
            {
                c -= qeval << 16;
                if ((a & 0x8000) == 0)
                {
                    // MPS exchange
                    if (a < qeval)
                    {
                        d = !mps;
                        states[cx] = MQCState.Nlps(e);
                    }
                    else
                    {
                        d = mps;
                        states[cx] = MQCState.Nmps(e);
                    }
                    Renormd();
                }
                else
                {
                    d = mps;
                }
            }

            return d;
        }

        // The hot T1 passes copy a, c and ct into an MqcRegs local (upstream's DOWNLOAD_MQC_VARIABLES), decode with
        // Decode(ref MqcRegs, ...), and copy them back (UPLOAD_MQC_VARIABLES). The copies are plain field assignments:
        // a non-inlined call returning the struct would make the JIT keep it in memory.

        /// <summary>
        /// <see cref="Decode()"/> on downloaded registers. Same arithmetic, so the same decoded bits.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool Decode(ref MqcRegs r, uint[] states, int cx, MQCoder mqc)
        {
            uint e = states[cx];
            uint qeval = e & 0xFFFF;
            bool mps = MQCState.Mps(e);
            bool d;

            r.a -= qeval;
            if ((r.c >> 16) < qeval)
            {
                // LPS exchange: LPS unless a < qeval. Chosen without a branch (a < qeval depends on the data).
                int lps = r.a >= qeval ? 1 : 0;
                d = mps ^ (lps != 0);
                states[cx] = MQCState.Table[(e >> (16 + 8 * lps)) & 0x7F];
                r.a = qeval;
                Renormd(ref r, mqc);
            }
            else
            {
                r.c -= qeval << 16;
                if ((r.a & 0x8000) == 0)
                {
                    // MPS exchange: MPS unless a < qeval
                    int lps = r.a < qeval ? 1 : 0;
                    d = mps ^ (lps != 0);
                    states[cx] = MQCState.Table[(e >> (16 + 8 * lps)) & 0x7F];
                    Renormd(ref r, mqc);
                }
                else
                {
                    d = mps;
                }
            }

            return d;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void Renormd(ref MqcRegs r, MQCoder mqc)
        {
#if NETCOREAPP3_0_OR_GREATER
            int n = System.Numerics.BitOperations.LeadingZeroCount(r.a) - 16;
            if ((uint)n <= r.ct)
            {
                r.a <<= n;
                r.c <<= n;
                r.ct -= (uint)n;
                return;
            }
#endif
            do
            {
                if (r.ct == 0)
                {
                    ulong v = mqc.ByteIn(r.c);
                    r.c = (uint)v;
                    r.ct = (uint)(v >> 32);
                }
                r.a <<= 1;
                r.c <<= 1;
                r.ct--;
            } while (r.a < 0x8000);
        }

        /// <summary>
        /// <see cref="ByteIn()"/> for downloaded registers: takes c, returns the new c in the low 32 bits and the
        /// new ct in the high 32 bits, so the caller's registers are never passed by reference.
        /// </summary>
        // Inlined although rare (once per 8 bits): a call in the T1 passes clobbers the caller-saved registers, so the
        // JIT kept the pass's loop variables on the stack for the whole loop. Inlined, the passes are 5-9% faster.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        ulong ByteIn(uint c)
        {
            uint ct;
            uint l_c = _data[_bp + 1];
            if (_data[_bp] == 0xff)
            {
                if (l_c > 0x8f)
                {
                    c += 0xff00;
                    ct = 8;
                    _end_of_byte_stream_counter++;
                }
                else
                {
                    _bp++;
                    c += l_c << 9;
                    ct = 7;
                }
            }
            else
            {
                _bp++;
                c += l_c << 8;
                ct = 8;
            }
            return c | ((ulong)ct << 32);
        }

        //2.5 - opj_mqc_renormd_macro
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void Renormd()
        {
#if NETCOREAPP3_0_OR_GREATER
            // The loop below runs exactly n = lzcnt(a) - 16 times (1 <= a < 0x8000 here). When no byte
            // is needed meanwhile (n <= ct), all n steps collapse into one shift: same result.
            int n = System.Numerics.BitOperations.LeadingZeroCount(a) - 16;
            if ((uint)n <= ct)
            {
                a <<= n;
                c <<= n;
                ct -= (uint)n;
                return;
            }
#endif
            do
            {
                if (ct == 0) ByteIn();
                a <<= 1;
                c <<= 1;
                ct--;
            } while (a < 0x8000);
        }

        //C# - This method dosn't exist in 2.5, but the equivalent code it identical.
        internal void CheckPTerm(CompressionInfo cinfo)
        {
            if (this._bp + 2 < _end)
            {
                cinfo.WarnMT("\"PTERM check failure: {0} remaining bytes in code block ({1} used / {2})",
                    _end - _bp, _bp - _start, _end - _start);
            }
            else if (_end_of_byte_stream_counter > 2)
            {
                cinfo.WarnMT("PTERM check failure: {0} synthetized 0xFF markers read",
                    _end_of_byte_stream_counter);
            }
        }

        //2.5 - opq_mqc_finish_dec
        internal void FinishDec()
        {
            //Restore the bytes overwritten by opj_mqc_init_dec_common()
            Array.Copy(_backup, 0, _data, _end, Constants.COMMON_CBLK_DATA_EXTRA);
        }

        //2.5 - opj_mqc_resetstates
        internal void ResetStates()
        {
            uint s0 = MQCState.Table[0];
            for (int i = 0; i < ctxs.Length; i++)
                ctxs[i] = s0;
        }

        //2.5 - opj_mqc_setstate
        internal void SetState(T1_CTXNO ctxno, int msb, int prob)
        {
            ctxs[(int)ctxno] = MQCState.Table[msb + (prob << 1)];
        }

        /// <summary>
        /// Output a byte, doing bit-stuffing if necessary.
        /// After a 0xff byte, the next byte must be smaller than 0x90.
        /// </summary>
        /// <remarks>2.5 - opj_mqc_byteout</remarks>
        void Byteout()
        {
            //C# impl. note:
            //The org impl. do not check for _data_pos >= 0,
            //instead it has padded the array with two bytes at
            //the front. 
            if (_bp >= 0 && _data[_bp] == 0xff)
            {
                _bp++;
                _data[_bp] = (byte) (c >> 20);
                c &= 0xfffff;
                ct = 7;
            }
            else
            {
                if ((c & 0x8000000) == 0)
                {
                    _bp++;
                    _data[_bp] = (byte) (c >> 19);
                    c &= 0x7ffff;
                    ct = 8;
                }
                else
                {
                    //C#: I assume that ++_data[-1] will never be 0xff,
                    //    and yes it's ++_data[pos], not _data[++pos]
                    //Potential bug:
                    // If the -1 byte is suppose to reach 255 after a while.
                    // However, AFAICT, this is only done once per _data_pos
                    if (_bp >= 0 && ++_data[_bp] == 0xff)
                    {
                        c &= 0x7ffffff;
                        _bp++;
                        _data[_bp] = (byte)(c >> 20);
                        c &= 0xfffff;
                        ct = 7;
                    }
                    else
                    {
                        _bp++;
                        _data[_bp] = (byte)(c >> 19);
                        c &= 0x7ffff;
                        ct = 8;
                    }
                }
            }
        }

        /// <summary>
        /// Reads in one byte
        /// </summary>
        /// <remarks>
        /// 2.5 - opj_mqc_bytein_macro
        /// </remarks>
        [MethodImpl(MethodImplOptions.NoInlining)] // rare: once per 8 decoded bits
        void ByteIn()
        {
            // Given opj_mqc_init_dec() we know that at some point we will
            // have a 0xFF 0xFF artificial marker
            uint l_c = _data[_bp + 1];
            if (_data[_bp] == 0xff)
            {
                if (l_c > 0x8f)
                {
                    c += 0xff00;
                    ct = 8;
                    _end_of_byte_stream_counter++;
                }
                else
                {
                    _bp++;
                    c += l_c << 9;
                    ct = 7;
                }
            }
            else
            {
                _bp++;
                c += l_c << 8;
                ct = 8;
            }        
        }

        /// <remarks>
        /// 2.5 - opj_t1_getctxno_zc
        /// 
        /// I assume the "zc" referes to "zero coding."
        /// </remarks>
        internal byte Getctxno_zc(T1 f)
        {
            return (byte) T1Luts.lut_ctxno_zc[lut_ctxno_zc_orient + (uint)(f & T1.SIGMA_NEIGHBOURS)];
        }
    }

    /// <summary>
    /// MQ decoder registers held in a local by the T1 passes, so the JIT can keep them in registers.
    /// The current context is not part of it: it is passed to each decode, which keeps it short-lived.
    /// </summary>
    internal struct MqcRegs
    {
        internal uint a, c, ct;
    }

    /// <summary>
    /// The 47 x 2 MQ coder states (upstream mqc_states). Index = state * 2 + MPS, so the MPS is bit 0.
    /// </summary>
    internal static class MQCState
    {
        /// <summary>
        /// Packed per state: Qe in bits 0-15, next state index on MPS in bits 16-22, next state index on LPS in
        /// bits 24-30, MPS in bit 31. 128 entries (94 used) so that a 7-bit index needs no bounds check.
        /// </summary>
        internal static readonly uint[] Table;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool Mps(uint e) => (int)e < 0;

        /// <summary>Entry of the next state after an MPS (upstream: (*curctx)->nmps).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static uint Nmps(uint e) => Table[(e >> 16) & 0x7F];

        /// <summary>Entry of the next state after an LPS (upstream: (*curctx)->nlps).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static uint Nlps(uint e) => Table[(e >> 24) & 0x7F];

        // Static constructor: runs after the mcqints initializer below (field initializers run in textual order).
        static MQCState() { Table = BuildTable(); }

        static uint[] BuildTable()
        {
            var t = new uint[128];
            for (int i = 0; i < mcqints.GetLength(0); i++)
            {
                t[i] = (uint)mcqints[i, 0] | ((uint)mcqints[i, 2] << 16) | ((uint)mcqints[i, 3] << 24) |
                       ((uint)mcqints[i, 1] << 31);
            }
            return t;
        }

        /// <summary>
        /// Information for bulding the MCQState array
        /// </summary>
        static int[,] mcqints =
        {
            {0x5601, 0, 2, 3},
            {0x5601, 1, 3, 2},
            {0x3401, 0, 4, 12},
            {0x3401, 1, 5, 13},
            {0x1801, 0, 6, 18},
            {0x1801, 1, 7, 19},
            {0x0ac1, 0, 8, 24},
            {0x0ac1, 1, 9, 25},
            {0x0521, 0, 10, 58},
            {0x0521, 1, 11, 59},
            {0x0221, 0, 76, 66},
            {0x0221, 1, 77, 67},
            {0x5601, 0, 14, 13},
            {0x5601, 1, 15, 12},
            {0x5401, 0, 16, 28},
            {0x5401, 1, 17, 29},
            {0x4801, 0, 18, 28},
            {0x4801, 1, 19, 29},
            {0x3801, 0, 20, 28},
            {0x3801, 1, 21, 29},
            {0x3001, 0, 22, 34},
            {0x3001, 1, 23, 35},
            {0x2401, 0, 24, 36},
            {0x2401, 1, 25, 37},
            {0x1c01, 0, 26, 40},
            {0x1c01, 1, 27, 41},
            {0x1601, 0, 58, 42},
            {0x1601, 1, 59, 43},
            {0x5601, 0, 30, 29},
            {0x5601, 1, 31, 28},
            {0x5401, 0, 32, 28},
            {0x5401, 1, 33, 29},
            {0x5101, 0, 34, 30},
            {0x5101, 1, 35, 31},
            {0x4801, 0, 36, 32},
            {0x4801, 1, 37, 33},
            {0x3801, 0, 38, 34},
            {0x3801, 1, 39, 35},
            {0x3401, 0, 40, 36},
            {0x3401, 1, 41, 37},
            {0x3001, 0, 42, 38},
            {0x3001, 1, 43, 39},
            {0x2801, 0, 44, 38},
            {0x2801, 1, 45, 39},
            {0x2401, 0, 46, 40},
            {0x2401, 1, 47, 41},
            {0x2201, 0, 48, 42},
            {0x2201, 1, 49, 43},
            {0x1c01, 0, 50, 44},
            {0x1c01, 1, 51, 45},
            {0x1801, 0, 52, 46},
            {0x1801, 1, 53, 47},
            {0x1601, 0, 54, 48},
            {0x1601, 1, 55, 49},
            {0x1401, 0, 56, 50},
            {0x1401, 1, 57, 51},
            {0x1201, 0, 58, 52},
            {0x1201, 1, 59, 53},
            {0x1101, 0, 60, 54},
            {0x1101, 1, 61, 55},
            {0x0ac1, 0, 62, 56},
            {0x0ac1, 1, 63, 57},
            {0x09c1, 0, 64, 58},
            {0x09c1, 1, 65, 59},
            {0x08a1, 0, 66, 60},
            {0x08a1, 1, 67, 61},
            {0x0521, 0, 68, 62},
            {0x0521, 1, 69, 63},
            {0x0441, 0, 70, 64},
            {0x0441, 1, 71, 65},
            {0x02a1, 0, 72, 66},
            {0x02a1, 1, 73, 67},
            {0x0221, 0, 74, 68},
            {0x0221, 1, 75, 69},
            {0x0141, 0, 76, 70},
            {0x0141, 1, 77, 71},
            {0x0111, 0, 78, 72},
            {0x0111, 1, 79, 73},
            {0x0085, 0, 80, 74},
            {0x0085, 1, 81, 75},
            {0x0049, 0, 82, 76},
            {0x0049, 1, 83, 77},
            {0x0025, 0, 84, 78},
            {0x0025, 1, 85, 79},
            {0x0015, 0, 86, 80},
            {0x0015, 1, 87, 81},
            {0x0009, 0, 88, 82},
            {0x0009, 1, 89, 83},
            {0x0005, 0, 90, 84},
            {0x0005, 1, 91, 85},
            {0x0001, 0, 90, 86},
            {0x0001, 1, 91, 87},
            {0x5601, 0, 92, 92},
            {0x5601, 1, 93, 93},
        };
    }
}
