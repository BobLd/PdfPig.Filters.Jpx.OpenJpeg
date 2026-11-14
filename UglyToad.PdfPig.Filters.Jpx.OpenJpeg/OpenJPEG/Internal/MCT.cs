using System.Numerics;
using System.Runtime.InteropServices;
using System.Diagnostics;

namespace OpenJpeg.Internal;

/// <summary>
/// Multi-component transforms (MCT)
/// </summary>
internal static class MCT
{
    private static readonly double[] mct_norms = [1.732, .8292, .8292];
    private static readonly double[] mct_norms_real = [1.732, 1.805, 1.573];
    internal static readonly uint[] ELEMENT_SIZE = [2, 4, 4, 8];

    internal static void CalculateNorms(double[] pNorms,
                                        uint pNbComps,
                                        float[] pMatrix)
    {

        for (uint i = 0; i < pNbComps; ++i)
        {
            pNorms[i] = 0;
            uint lIndex = i;

            for (uint j = 0; j < pNbComps; ++j)
            {
                float lCurrentValue = pMatrix[lIndex];
                lIndex += pNbComps;
                pNorms[i] += lCurrentValue * lCurrentValue;
            }
            pNorms[i] = Math.Sqrt(pNorms[i]);
        }
    }

    //2.1
    internal static double[] GetMctNorms()
    {
        return mct_norms;
    }

    //2.1
    internal static double[] GetMctNormsReal()
    {
        return mct_norms_real;
    }

    internal static double Getnorm(int compno)
    {
        return mct_norms[compno];
    }

    internal static double Getnorm_real(int compno)
    {
        return mct_norms_real[compno];
    }

    /// <summary>
    /// Encodes using a custom Multi-component transform matrix.
    /// </summary>
    /// <param name="mct">The custom multi-component transform matrix</param>
    /// <param name="n">Number of elements in mct</param>
    /// <param name="data">Data to transform</param>
    /// <param name="isSigned">Ignored</param>
    /// <remarks>2.5 - opj_mct_encode_custom</remarks>
    internal static void EncodeCustom(float[] mct, int n, int[][] data, bool isSigned)
    {
        Debug.Assert(false, "Untested code"); //<-- Org code has multi dimensional pointers.
        uint NMatCoeff = (uint)(data.Length * data.Length); //C#: data.Length = pNbComp
        uint multiplicator = 1 << 13;

        //C# Org impl creates two arrays with one malloc (that's why there's data.Length + NMatCoeff)
        int[] current_data = new int[data.Length];
        int[] current_matrix = new int[NMatCoeff];

        for (int i = 0; i < current_matrix.Length; i++)
        {
            current_matrix[i] = (int)(mct[i] * multiplicator);
        }

        //C# d = pointer on data's second dimension
        int[] d = new int[data.Length];
        for (int i = 0, cm_ptr = 0; i < n; i++)
        {
            //Current data is filled up with one int from each component
            for (int j = 0; j < data.Length; j++)
            {
                current_data[j] = data[j][d[j]];
            }

            for (int j = 0; j < data.Length; j++)
            {
                var data_j = data[j];
                var d_j = d[j];
                data_j[d_j] = 0;
                for (int k = 0; k < data.Length; k++)
                {
                    data_j[d_j] += MyMath.fix_mul(current_matrix[cm_ptr++], current_data[k]);
                }

                //C# Incrementing the pointer to the data, not the pointer to the pointers
                d[j]++;
            }
        }
    }

    /// <remarks>
    /// 2.5
    ///
    /// C# Original implementation shifts between floats and integers. We work
    ///    around this headace by using an interopt struct. It's probably not
    ///    the best solution.
    ///
    ///    What I need is to be able to convert a int to a float, without actually
    ///    converting anything.
    /// </remarks>
    internal static void DecodeCustom(float[] mct, int n, int[][] data, uint n_comps, bool isSigned)
    {
        IntOrFloat[] current_data = new IntOrFloat[n_comps];
        IntOrFloat[] current_result = new IntOrFloat[n_comps];

        for (int i = 0, d = 0; i < n; i++)
        {
            int mct_c = 0;
            for (int j = 0; j < data.Length; j++)
            {
                current_data[j].I = data[j][d];
            }

            for (int j = 0; j < data.Length; j++)
            {
                current_result[j].F = 0;
                for (int k = 0; k < n_comps; k++)
                {
                    current_result[j].F += mct[mct_c++] * current_data[k].F;
                }

                data[j][d++] = current_result[j].I;
            }
        }
    }

    //2.5 - opj_mct_encode_real
    internal static void EncodeReal(int[] c0, int[] c1, int[] c2, int n)
    {
        IntOrFloat r = new IntOrFloat(), g = new IntOrFloat(), b = new IntOrFloat();
        IntOrFloat y = new IntOrFloat(), u = new IntOrFloat(), v = new IntOrFloat();
        for (int i = 0; i < n; ++i)
        {
            r.I = c0[i];
            g.I = c1[i];
            b.I = c2[i];
            y.F = 0.299f * r.F + 0.587f * g.F + 0.114f * b.F;
            u.F = -0.16875f * r.F - 0.331260f * g.F + 0.5f * b.F;
            v.F = 0.5f * r.F - 0.41869f * g.F - 0.08131f * b.F;
            c0[i] = y.I;
            c1[i] = u.I;
            c2[i] = v.I;
        }
    }

    /// <summary>
    /// Apply a reversible multi-component transform to an image
    /// </summary>
    /// <param name="c0">Samples for red component</param>
    /// <param name="c1">Samples for green component</param>
    /// <param name="c2">Samples blue component</param>
    /// <param name="n">Number of samples for each component</param>
    /// <remarks>2.5 - opj_mct_encode</remarks>
    internal static void Encode(int[] c0, int[] c1, int[] c2, int n)
    {
        //C# Snip SSE code

        for (int i = 0; i < n; ++i)
        {
            int r = c0[i];
            int g = c1[i];
            int b = c2[i];
            int y = (r + (g * 2) + b) >> 2;
            int u = b - g;
            int v = r - g;
            c0[i] = y;
            c1[i] = u;
            c2[i] = v;
        }
    }

    /// <summary>
    /// Apply a reversible multi-component inverse transform to an image
    /// </summary>
    /// <param name="c0">Samples for luminance component</param>
    /// <param name="c1">Samples for red chrominance component</param>
    /// <param name="c2">Samples for blue chrominance component</param>
    /// <param name="n">Number of samples for each component</param>
    ///<remarks>
    ///2.5
    ///
    /// There's a SSE2 variant of this algo.
    /// </remarks>
    internal static void Decode(Span<int> c0, Span<int> c1, Span<int> c2, int n)
    {
        int i = 0;
#if NET7_0_OR_GREATER
        // Vector version of the loop below (upstream has SSE2/AVX2 for this). Integer only: exact.
        if (Vector.IsHardwareAccelerated)
        {
            int V = Vector<int>.Count;
            for (; i <= n - V; i += V)
            {
                var y = new Vector<int>(c0.Slice(i));
                var u = new Vector<int>(c1.Slice(i));
                var v = new Vector<int>(c2.Slice(i));
                var g = y - ((u + v) >> 2);
                (v + g).CopyTo(c0.Slice(i));
                g.CopyTo(c1.Slice(i));
                (u + g).CopyTo(c2.Slice(i));
            }
        }
#endif
        for (; i < n; ++i)
        {
            int y = c0[i];
            int u = c1[i];
            int v = c2[i];
            int g = y - ((u + v) >> 2);
            int r = v + g;
            int b = u + g;
            c0[i] = r;
            c1[i] = g;
            c2[i] = b;
        }
    }

    /// <summary>
    /// Apply a reversible multi-component inverse transform to an image
    /// </summary>
    /// <remarks>
    /// 2.5 - opj_mct_decode_real
    /// </remarks>
    /// <param name="c0">Samples for luminance component</param>
    /// <param name="c1">Samples for red chrominance component</param>
    /// <param name="c2">Samples for blue chrominance component</param>
    /// <param name="n">Number of samples for each component</param>
    internal static void DecodeReal(Span<int> c0, Span<int> c1, Span<int> c2, int n)
    {
        IntOrFloat y = new IntOrFloat();
        IntOrFloat u = new IntOrFloat();
        IntOrFloat v = new IntOrFloat();
        IntOrFloat r = new IntOrFloat();
        IntOrFloat g = new IntOrFloat();
        IntOrFloat b = new IntOrFloat();

        int i = 0;
        // Vector version of the loop below, same operations in the same order, so bit-identical.
        // The int arrays hold float bits at this stage.
        if (Vector.IsHardwareAccelerated)
        {
            var y0 = MemoryMarshal.Cast<int, Vector<float>>(c0.Slice(0, n));
            var u1 = MemoryMarshal.Cast<int, Vector<float>>(c1.Slice(0, n));
            var v2 = MemoryMarshal.Cast<int, Vector<float>>(c2.Slice(0, n));
            var crv = new Vector<float>(1.402f);
            var cgu = new Vector<float>(0.34413f);
            var cgv = new Vector<float>(0.71414f);
            var cbu = new Vector<float>(1.772f);
            for (int k = 0; k < y0.Length; k++)
            {
                var vy = y0[k];
                var vu = u1[k];
                var vv = v2[k];
                y0[k] = vy + (vv * crv);
                u1[k] = vy - (vu * cgu) - (vv * cgv);
                v2[k] = vy + (vu * cbu);
            }
            i = y0.Length * Vector<float>.Count;
        }

        //C# Liberal application of (float) to reduce math precision to the same as the org. impl
        for (; i < n; ++i)
        {
            y.I = c0[i];
            u.I = c1[i];
            v.I = c2[i];
            r.F = y.F + (v.F * 1.402f);
            g.F = y.F - (u.F * 0.34413f) - (v.F * 0.71414f);
            b.F = y.F + (u.F * 1.772f);
            c0[i] = r.I;
            c1[i] = g.I;
            c2[i] = b.I;
        }
    }

    /// <summary>DC level shift of one component: add Shift, clamp to Min .. Max (TileCoder.DcLevelShiftDecode).</summary>
    internal readonly struct DcShift
    {
        internal readonly int Shift, Min, Max;
        internal DcShift(int shift, int min, int max) { Shift = shift; Min = min; Max = max; }
    }

    // The two kernels below run once per tile, so under tiering a single-tile image never reaches tier-1 code:
    // fully optimised from the first call. Plain loops, nothing for PGO.

    /// <summary>
    /// <see cref="Decode"/> followed by the reversible DC level shift of the three components, in one pass over
    /// the samples instead of two. Same integer operations per sample, so the same values. Not in upstream.
    /// </summary>
#if NETCOREAPP3_0_OR_GREATER
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
#endif
    internal static void DecodeDcShift(Span<int> c0, Span<int> c1, Span<int> c2, int n, DcShift s0, DcShift s1, DcShift s2)
    {
        int i = 0;
#if NET7_0_OR_GREATER
        if (Vector.IsHardwareAccelerated)
        {
            int V = Vector<int>.Count;
            Vector<int> sh0 = new(s0.Shift), mn0 = new(s0.Min), mx0 = new(s0.Max);
            Vector<int> sh1 = new(s1.Shift), mn1 = new(s1.Min), mx1 = new(s1.Max);
            Vector<int> sh2 = new(s2.Shift), mn2 = new(s2.Min), mx2 = new(s2.Max);
            for (; i <= n - V; i += V)
            {
                var y = new Vector<int>(c0.Slice(i));
                var u = new Vector<int>(c1.Slice(i));
                var v = new Vector<int>(c2.Slice(i));
                var g = y - ((u + v) >> 2);
                Vector.Min(Vector.Max(v + g + sh0, mn0), mx0).CopyTo(c0.Slice(i));
                Vector.Min(Vector.Max(g + sh1, mn1), mx1).CopyTo(c1.Slice(i));
                Vector.Min(Vector.Max(u + g + sh2, mn2), mx2).CopyTo(c2.Slice(i));
            }
        }
#endif
        for (; i < n; ++i)
        {
            int y = c0[i];
            int u = c1[i];
            int v = c2[i];
            int g = y - ((u + v) >> 2);
            c0[i] = MyMath.int_clamp(v + g + s0.Shift, s0.Min, s0.Max);
            c1[i] = MyMath.int_clamp(g + s1.Shift, s1.Min, s1.Max);
            c2[i] = MyMath.int_clamp(u + g + s2.Shift, s2.Min, s2.Max);
        }
    }

    /// <summary>
    /// <see cref="DecodeReal"/> followed by the irreversible DC level shift of the three components
    /// (clamp(round_half_even(f) + shift, min, max)), in one pass over the samples instead of two. Same float
    /// operations in the same order, and the same rounding as TileCoder.DcShiftFloat: a vector of 8 takes the
    /// scalar formula when one of its values is outside |f| &lt; 2^22 (or NaN). Not in upstream. For precisions
    /// of 24 bits or less.
    /// </summary>
#if NETCOREAPP3_0_OR_GREATER
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
#endif
    internal static void DecodeRealDcShift(Span<int> c0, Span<int> c1, Span<int> c2, int n, DcShift s0, DcShift s1, DcShift s2)
    {
        int i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            int V = Vector<float>.Count;
            var y0 = MemoryMarshal.Cast<int, Vector<float>>(c0.Slice(0, n));
            var u1 = MemoryMarshal.Cast<int, Vector<float>>(c1.Slice(0, n));
            var v2 = MemoryMarshal.Cast<int, Vector<float>>(c2.Slice(0, n));
            var o0 = MemoryMarshal.Cast<int, Vector<int>>(c0.Slice(0, n));
            var o1 = MemoryMarshal.Cast<int, Vector<int>>(c1.Slice(0, n));
            var o2 = MemoryMarshal.Cast<int, Vector<int>>(c2.Slice(0, n));
            var crv = new Vector<float>(1.402f);
            var cgu = new Vector<float>(0.34413f);
            var cgv = new Vector<float>(0.71414f);
            var cbu = new Vector<float>(1.772f);
            var magic = new Vector<float>(12582912f); // 1.5 * 2^23
            var limit = new Vector<float>(4194304f);  // 2^22
            Vector<int> sh0 = new(s0.Shift), mn0 = new(s0.Min), mx0 = new(s0.Max);
            Vector<int> sh1 = new(s1.Shift), mn1 = new(s1.Min), mx1 = new(s1.Max);
            Vector<int> sh2 = new(s2.Shift), mn2 = new(s2.Min), mx2 = new(s2.Max);
            for (int k = 0; k < y0.Length; k++)
            {
                var vy = y0[k];
                var vu = u1[k];
                var vv = v2[k];
                var r = vy + (vv * crv);
                var g = vy - (vu * cgu) - (vv * cgv);
                var b = vy + (vu * cbu);
                if (Vector.LessThanAll(Vector.Abs(r), limit) && Vector.LessThanAll(Vector.Abs(g), limit)
                    && Vector.LessThanAll(Vector.Abs(b), limit))
                {
                    o0[k] = Vector.Min(Vector.Max(Vector.ConvertToInt32((r + magic) - magic) + sh0, mn0), mx0);
                    o1[k] = Vector.Min(Vector.Max(Vector.ConvertToInt32((g + magic) - magic) + sh1, mn1), mx1);
                    o2[k] = Vector.Min(Vector.Max(Vector.ConvertToInt32((b + magic) - magic) + sh2, mn2), mx2);
                }
                else
                {
                    for (int l = 0, at = k * V; l < V; l++, at++)
                    {
                        c0[at] = DcShiftFloat(r[l], s0);
                        c1[at] = DcShiftFloat(g[l], s1);
                        c2[at] = DcShiftFloat(b[l], s2);
                    }
                }
            }
            i = y0.Length * V;
        }

        IntOrFloat y = new IntOrFloat();
        IntOrFloat u = new IntOrFloat();
        IntOrFloat v = new IntOrFloat();
        for (; i < n; ++i)
        {
            y.I = c0[i];
            u.I = c1[i];
            v.I = c2[i];
            c0[i] = DcShiftFloat(y.F + (v.F * 1.402f), s0);
            c1[i] = DcShiftFloat(y.F - (u.F * 0.34413f) - (v.F * 0.71414f), s1);
            c2[i] = DcShiftFloat(y.F + (u.F * 1.772f), s2);
        }
    }

    // Array overloads of the four decoders above: FusedMctDcShiftTest calls them by reflection, which cannot pass a Span.
    internal static void Decode(int[] c0, int[] c1, int[] c2, int n) => Decode(c0.AsSpan(), c1.AsSpan(), c2.AsSpan(), n);
    internal static void DecodeReal(int[] c0, int[] c1, int[] c2, int n) => DecodeReal(c0.AsSpan(), c1.AsSpan(), c2.AsSpan(), n);
    internal static void DecodeDcShift(int[] c0, int[] c1, int[] c2, int n, DcShift s0, DcShift s1, DcShift s2) =>
        DecodeDcShift(c0.AsSpan(), c1.AsSpan(), c2.AsSpan(), n, s0, s1, s2);
    internal static void DecodeRealDcShift(int[] c0, int[] c1, int[] c2, int n, DcShift s0, DcShift s1, DcShift s2) =>
        DecodeRealDcShift(c0.AsSpan(), c1.AsSpan(), c2.AsSpan(), n, s0, s1, s2);

    /// <summary>The scalar irreversible DC level shift of TileCoder.DcShiftFloat, for one value.</summary>
    private static int DcShiftFloat(float f, DcShift s)
    {
        if (f > int.MaxValue)
            return s.Max;
        if (f < int.MinValue)
            return s.Min;
        // Banker's rounding (round half to even), as lrintf; addition on int64 to avoid overflows
        return (int)MyMath.int64_clamp((long)Math.Round(f) + s.Shift, s.Min, s.Max);
    }
}

internal enum MCT_ELEMENT_TYPE
{
    NULL = -1,

    /// <summary>
    /// MCT data is stored as signed shorts
    /// </summary>
    MCT_TYPE_INT16 = 0,

    /// <summary>
    /// MCT data is stored as signed integers
    /// </summary>
    MCT_TYPE_INT32 = 1,

    /// <summary>
    /// MCT data is stored as floats
    /// </summary>
    MCT_TYPE_FLOAT = 2,

    /// <summary>
    /// MCT data is stored as doubles
    /// </summary>
    MCT_TYPE_DOUBLE = 3,
}

internal enum MCT_ARRAY_TYPE
{
    MCT_TYPE_DEPENDENCY = 0,
    MCT_TYPE_DECORRELATION = 1,
    MCT_TYPE_OFFSET = 2,
}

/// <summary>
/// Multi component transform data
/// </summary>
internal sealed class MctData : ICloneable
{
    public MCT_ELEMENT_TYPE element_type;
    public MCT_ARRAY_TYPE array_type;
    public int index;
    public ShortOrIntOrFloatOrDoubleAr data;
    public int data_size;

    public object Clone() { return MemberwiseClone(); }
}

internal sealed class SimpleMccDecorrelationData : ICloneable
{
    public uint index;
    public uint n_comps;
    public ArPtr<MctData> decorrelation_array;
    public ArPtr<MctData> offset_array;
    public bool is_irreversible;

    public object Clone()
    {
        var clone = (SimpleMccDecorrelationData)MemberwiseClone();
        if (decorrelation_array is not null)
        {
            var ap = new ArPtr<MctData>(new MctData[decorrelation_array.Data.Length], decorrelation_array.Pos);
            for (int c = 0; c < ap.Data.Length; c++)
            {
                ap.Data[c] = (MctData)decorrelation_array.Data[c].Clone();
            }
            clone.decorrelation_array = ap;
        }
        if (offset_array is not null)
        {
            var ap = new ArPtr<MctData>(new MctData[offset_array.Data.Length], offset_array.Pos);
            for (int c = 0; c < ap.Data.Length; c++)
            {
                ap.Data[c] = (MctData)offset_array.Data[c].Clone();
            }
            clone.offset_array = ap;
        }
        return clone;
    }
}
