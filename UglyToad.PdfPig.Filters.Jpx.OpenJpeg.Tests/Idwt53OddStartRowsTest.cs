using System;
using System.Reflection;

namespace UglyToad.PdfPig.Filters.Jpx.OpenJpeg.Tests
{
    /// <summary>
    /// The horizontal 5/3 pass over rows starting on an odd coordinate (DWT.idwt53_h_rows, AVX2 kernel for 9 or more
    /// high-pass samples) must give the same samples as the scalar upstream port (DWT.opj_idwt53_h_cas1). The PDF
    /// streams never reach this path; the conformance files do (parity tool).
    /// </summary>
    public class Idwt53OddStartRowsTest
    {
        const BindingFlags Static = BindingFlags.NonPublic | BindingFlags.Static;
        static readonly Type Dwt = typeof(global::OpenJpeg.CompressionInfo).Assembly.GetType("OpenJpeg.Internal.DWT", true);
        static readonly Type DwtLocal = Dwt.GetNestedType("dwt_local", BindingFlags.NonPublic);

        [Fact]
        public void RowsMatchScalarCas1()
        {
            var rows = Dwt.GetMethod("idwt53_h_rows", Static);
            var cas1 = Dwt.GetMethod("opj_idwt53_h_cas1", Static);
            var rnd = new Random(3);
            for (int len = 3; len <= 70; len++)
            {
                int sn = len / 2, w = len + 3, tiledp = 2, nrows = 3;
                var tiled = new int[tiledp + nrows * w];
                for (int i = 0; i < tiled.Length; i++)
                    tiled[i] = len % 3 == 0 ? rnd.Next(int.MinValue, int.MaxValue) : rnd.Next(-5000, 5000); // also wrapping sums

                var expected = (int[])tiled.Clone();
                var tmp = new int[len];
                for (int j = 0; j < nrows; j++)
                    cas1.Invoke(null, new object[] { tmp, sn, len, expected, tiledp + j * w });

                var dwt = Activator.CreateInstance(DwtLocal, true);
                DwtLocal.GetField("mem", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(dwt, Array.ConvertAll(new int[len], _ => rnd.Next()));
                DwtLocal.GetField("sn", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(dwt, sn);
                DwtLocal.GetField("dn", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(dwt, len - sn);
                DwtLocal.GetField("cas", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(dwt, 1);
                rows.Invoke(null, new object[] { dwt, tiled, tiledp, w, 0, nrows });

                Assert.Equal(expected, tiled);
            }
        }
    }
}
