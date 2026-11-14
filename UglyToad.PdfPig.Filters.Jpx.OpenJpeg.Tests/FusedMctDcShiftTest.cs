using System;
using System.Linq;
using System.Reflection;

namespace UglyToad.PdfPig.Filters.Jpx.OpenJpeg.Tests
{
    /// <summary>
    /// The MCT kernels that also do the DC level shift (MCT.DecodeDcShift, MCT.DecodeRealDcShift) must give the same
    /// samples as the MCT followed by TileCoder's DC level shift. The corpora never reach the per-vector fallback
    /// for values outside |f| &lt; 2^22, so it is checked here.
    /// </summary>
    public class FusedMctDcShiftTest
    {
        const BindingFlags Static = BindingFlags.NonPublic | BindingFlags.Static;
        static readonly Assembly Lib = typeof(global::OpenJpeg.CompressionInfo).Assembly;
        static readonly Type Mct = Lib.GetType("OpenJpeg.Internal.MCT", true);
        static readonly Type TileCoder = Lib.GetType("OpenJpeg.Internal.TileCoder", true);
        static readonly Type DcShift = Lib.GetType("OpenJpeg.Internal.MCT+DcShift", true);

        static object Dc(int shift, int min, int max) =>
            DcShift.GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(int), typeof(int), typeof(int) }, null)
                   .Invoke(new object[] { shift, min, max });

        // The int[] overloads: the Span ones cannot be invoked by reflection
        static MethodInfo Kernel(string name, bool withDc)
        {
            var arrays = new[] { typeof(int[]), typeof(int[]), typeof(int[]), typeof(int) };
            var types = withDc ? arrays.Concat(new[] { DcShift, DcShift, DcShift }).ToArray() : arrays;
            return Mct.GetMethod(name, Static, null, types, null);
        }

        static int[] Bits(float[] f) => Array.ConvertAll(f, x => BitConverter.ToInt32(BitConverter.GetBytes(x), 0));

        [Fact]
        public void IrreversibleMatchesMctThenDcShift()
        {
            // 8-bit unsigned (shift 128, 0 .. 255) and 12-bit signed (-2048 .. 2047) components
            var shifts = new[] { (128, 0, 255), (0, -2048, 2047), (128, 0, 255) };
            var rnd = new Random(1);
            const int n = 16 * 9 + 5; // whole vectors for 4, 8 and 16 lanes, plus a scalar tail
            var comps = new float[3][];
            for (int c = 0; c < 3; c++)
            {
                comps[c] = new float[n];
                for (int i = 0; i < n; i++)
                    comps[c][i] = (float)(rnd.NextDouble() * 300 - 150);
            }
            // Halves (round to even), and values that leave |f| < 2^22 or are NaN, in different vectors
            comps[0][3] = 2.5f; comps[1][4] = -3.5f; comps[2][5] = 0.5f;
            comps[0][20] = 5e6f; comps[1][40] = -3e9f; comps[2][60] = 5e9f;
            comps[1][70] = float.NaN; comps[0][90] = 4194304f; comps[2][n - 2] = 1e8f;

            var expected = Array.ConvertAll(comps, Bits);
            Kernel("DecodeReal", false).Invoke(null, new object[] { expected[0], expected[1], expected[2], n });
            var dcShiftFloat = TileCoder.GetMethod("DcShiftFloat", Static);
            for (int c = 0; c < 3; c++)
                dcShiftFloat.Invoke(null, new object[] { expected[c], 0, n, 1, 0, shifts[c].Item1, shifts[c].Item2, shifts[c].Item3, true });

            var actual = Array.ConvertAll(comps, Bits);
            Kernel("DecodeRealDcShift", true).Invoke(null, new object[] { actual[0], actual[1], actual[2], n,
                Dc(shifts[0].Item1, shifts[0].Item2, shifts[0].Item3), Dc(shifts[1].Item1, shifts[1].Item2, shifts[1].Item3),
                Dc(shifts[2].Item1, shifts[2].Item2, shifts[2].Item3) });

            for (int c = 0; c < 3; c++)
                Assert.Equal(expected[c], actual[c]);
        }

        [Fact]
        public void ReversibleMatchesMctThenDcShift()
        {
            var rnd = new Random(2);
            const int n = 16 * 9 + 5;
            var comps = new int[3][];
            for (int c = 0; c < 3; c++)
            {
                comps[c] = new int[n];
                for (int i = 0; i < n; i++)
                    comps[c][i] = rnd.Next(-400, 400); // also outside the clamp range
            }

            var expected = Array.ConvertAll(comps, a => (int[])a.Clone());
            Kernel("Decode", false).Invoke(null, new object[] { expected[0], expected[1], expected[2], n });
            var dcShiftInt = TileCoder.GetMethod("DcShiftInt", Static);
            for (int c = 0; c < 3; c++)
                dcShiftInt.Invoke(null, new object[] { expected[c], 0, n, 1, 0, 128, 0, 255 });

            var actual = Array.ConvertAll(comps, a => (int[])a.Clone());
            Kernel("DecodeDcShift", true).Invoke(null, new object[] { actual[0], actual[1], actual[2], n,
                Dc(128, 0, 255), Dc(128, 0, 255), Dc(128, 0, 255) });

            for (int c = 0; c < 3; c++)
                Assert.Equal(expected[c], actual[c]);
        }
    }
}
