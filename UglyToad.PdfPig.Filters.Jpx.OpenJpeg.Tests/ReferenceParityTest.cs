using System.Security.Cryptography;
using OpenJpeg;
using UglyToad.PdfPig.Tokens;

namespace UglyToad.PdfPig.Filters.Jpx.OpenJpeg.Tests
{
    /// <summary>
    /// Checks that the decoded samples are bit-identical to upstream C OpenJPEG.
    /// Reference/jpx-reference.tsv maps the MD5 of each JPX stream to the MD5 of the C decoder's output.
    /// To regenerate it (after adding a PDF or updating upstream), see tools/README.md.
    /// </summary>
    public class ReferenceParityTest
    {
        // Must match the cap passed to "parity extract" when the reference file was generated.
        private const int MaxImagesPerDocument = 20;

        private static readonly Dictionary<string, string> Reference = File
            .ReadLines(Path.Combine("Reference", "jpx-reference.tsv"))
            .Select(l => l.Split('\t'))
            .ToDictionary(p => p[0], p => p[1]);

        private static readonly ParsingOptions ParsingOptions = new ParsingOptions
        {
            UseLenientParsing = true,
            SkipMissingFonts = true
        };

        public static TheoryData<string> Documents => [.. Directory.EnumerateFiles("Documents", "*.pdf")];

        [Theory]
        [MemberData(nameof(Documents))]
        public void DecodedSamplesMatchUpstreamOpenJpeg(string docPath)
        {
            var seen = new HashSet<string>();
            using var doc = PdfDocument.Open(docPath, ParsingOptions);
            foreach (var page in doc.GetPages())
            {
                foreach (var image in page.GetImages())
                {
                    if (seen.Count >= MaxImagesPerDocument) return;
                    if (!IsJpxOnly(image.ImageDictionary)) continue;

                    var raw = image.RawMemory.ToArray();
                    var inputHash = Md5(raw);
                    if (!seen.Add(inputHash)) continue;

                    Assert.True(Reference.TryGetValue(inputHash, out var expected),
                        $"No reference for JPX stream {inputHash} on page {page.Number}. Regenerate Reference/jpx-reference.tsv.");
                    // FAIL: upstream rejects it, being more lenient is fine. SKIP: too large to dump.
                    if (expected == "FAIL" || expected == "SKIP") continue;

                    Assert.Equal(expected, Hash(Decode(raw)));
                }
            }
        }

        /// <summary>JPXDecode as the only filter, as a name or a one-element array. Same rule as tools/parity.</summary>
        private static bool IsJpxOnly(DictionaryToken dict) =>
            (dict.TryGet(NameToken.Filter, out NameToken name) && name.Data == "JPXDecode") ||
            (dict.TryGet(NameToken.Filter, out ArrayToken array) && array.Data.Count == 1 &&
             array.Data[0] is NameToken only && only.Data == "JPXDecode");

        private static JPXImage Decode(byte[] data)
        {
            var format = data[0] == 0xFF && data[1] == 0x4F ? CodecFormat.Jpeg2K : CodecFormat.Jpeg2P;
            var cinfo = new CompressionInfo(true, format);
            using var ms = new MemoryStream(data, false);
            cinfo.SetupDecoder(cinfo.OpenCIO(ms, true), new DecompressionParameters());
            Assert.True(cinfo.ReadHeader(out var image), "ReadHeader failed");
            Assert.True(cinfo.Decode(image), "Decode failed");
            Assert.True(cinfo.EndDecompress(), "EndDecompress failed");
            return image;
        }

        /// <summary>Same layout as SampleHash in tools/parity/parity.cs.</summary>
        private static string Hash(JPXImage image)
        {
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms))
            {
                w.Write(image.NumberOfComponents);
                w.Write(UpstreamColorSpace(image.GetColorSpace()));
                foreach (var c in image.Components)
                {
                    w.Write(c.Width); w.Write(c.Height); w.Write((int)c.DX); w.Write((int)c.DY); w.Write(c.Prec); w.Write(c.Signed ? 1 : 0);
                    var data = c.Data;
                    for (int i = 0, n = c.Width * c.Height; i < n; i++) w.Write(data[i]);
                }
            }
            return Md5(ms.ToArray());
        }

        private static int UpstreamColorSpace(COLOR_SPACE cs) => cs switch
        {
            COLOR_SPACE.UNSPECIFIED => 0,
            COLOR_SPACE.sRGB => 1,
            COLOR_SPACE.GRAY => 2,
            COLOR_SPACE.sYCC => 3,
            COLOR_SPACE.eYCC => 4,
            COLOR_SPACE.CMYK => 5,
            _ => -1
        };

        private static string Md5(byte[] data)
        {
            using var md5 = MD5.Create();
            return BitConverter.ToString(md5.ComputeHash(data)).Replace("-", "");
        }
    }
}
