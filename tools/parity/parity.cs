#:project ../../UglyToad.PdfPig.Filters.Jpx.OpenJpeg/UglyToad.PdfPig.Filters.Jpx.OpenJpeg.csproj
#:property TargetFramework=net8.0
#:property Optimize=true
using System.Diagnostics;
using OpenJpeg;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Tokens;

// Bit-exact comparison of the C# decoder against upstream C OpenJPEG (opjdump, see tools/reference).
// Always use -c Release: file-based apps otherwise build the library in Debug.
// Usage:
//   dotnet run -c Release tools/parity/parity.cs -- compare <opjdump.exe> <out.csv> <dir>...   compare every file in the dirs
//   dotnet run -c Release tools/parity/parity.cs -- extract <pdfDir> <outDir>                  dump JPXDecode streams from PDFs
//   dotnet run -c Release tools/parity/parity.cs -- one <file>                                 decode one file, show exceptions
//   dotnet run -c Release tools/parity/parity.cs -- hashes <opjdump.exe> <out.tsv> <dir>...    C reference hashes for the tests
//     Writes "<md5 of input>	<md5 of C decode, FAIL or SKIP (too large)>" lines, sorted. See SampleHash below and
//     UglyToad.PdfPig.Filters.Jpx.OpenJpeg.Tests/ReferenceParityTest.cs, which must hash the same way.
// Status per file: MATCH, BOTH_FAIL (both reject it), MISMATCH, CS_FAIL (only C# fails), C_FAIL_CS_OK.
// Exit code is the number of MISMATCH + CS_FAIL files (0 = parity).
if (args[0] == "one")
{
    var bytes = File.ReadAllBytes(args[1]);
    var ci = new CompressionInfo(true, bytes[0] == 0xFF && bytes[1] == 0x4F ? CodecFormat.Jpeg2K : CodecFormat.Jpeg2P);
    using var ms = new MemoryStream(bytes, false);
    ci.SetupDecoder(ci.OpenCIO(ms, true), new DecompressionParameters());
    Console.WriteLine(ci.ReadHeader(out var im) && ci.Decode(im) && ci.EndDecompress());
    return 0;
}
if (args[0] == "extract")
{
    // Optional 4th arg: keep only the first N distinct streams per PDF (the tests use 20).
    int max = args.Length > 3 ? int.Parse(args[3]) : int.MaxValue;
    Directory.CreateDirectory(args[2]);
    foreach (var pdf in Directory.EnumerateFiles(args[1], "*.pdf"))
    {
        try
        {
            using var doc = PdfDocument.Open(pdf, new ParsingOptions { UseLenientParsing = true, SkipMissingFonts = true });
            int i = 0;
            var seen = new HashSet<string>();
            foreach (var page in doc.GetPages())
                foreach (var img in page.GetImages())
                    if (IsJpxOnly(img.ImageDictionary))
                    {
                        var raw = img.RawMemory.ToArray();
                        if (max != int.MaxValue && (seen.Count >= max || !seen.Add(SampleHash.Md5(raw)))) continue;
                        File.WriteAllBytes(Path.Combine(args[2], $"{Path.GetFileNameWithoutExtension(pdf)}_{i++}.jpx"), raw);
                    }
        }
        catch (Exception e) { Console.WriteLine($"{pdf}: {e.Message}"); }
    }
    return 0;
}

if (args[0] == "hashes")
{
    var dumper = Path.GetFullPath(args[1]);
    var tmpRef = Path.Combine(Path.GetTempPath(), $"parity_hash_{Environment.ProcessId}.ojd");
    var lines = new SortedSet<string>(StringComparer.Ordinal);
    foreach (var file in args.Skip(3).SelectMany(d => Directory.EnumerateFiles(d)))
    {
        File.Delete(tmpRef);
        var proc = Process.Start(new ProcessStartInfo(dumper, $"\"{file}\" \"{tmpRef}\"") { RedirectStandardError = true, RedirectStandardOutput = true })!;
        bool done = proc.WaitForExit(60000); if (!done) proc.Kill();
        string output = done && proc.ExitCode == 3 ? "SKIP"
            : done && proc.ExitCode == 0 && File.Exists(tmpRef) ? SampleHash.FromOjd(File.ReadAllBytes(tmpRef)) : "FAIL";
        lines.Add($"{SampleHash.Md5(File.ReadAllBytes(file))}	{output}");
    }
    File.Delete(tmpRef);
    File.WriteAllLines(args[2], lines);
    Console.WriteLine($"{lines.Count} hashes written to {args[2]}");
    return 0;
}

// PARITY_THREADS=1 decodes single-threaded (the inline code path); default uses all cores.
bool singleThread = Environment.GetEnvironmentVariable("PARITY_THREADS") == "1";
string opjdump = Path.GetFullPath(args[1]);
var csv = new StreamWriter(args[2]) { AutoFlush = true };
csv.WriteLine("file,status,detail,c_ms,cs_ms");
var tmp = Path.Combine(Path.GetTempPath(), $"parity_ref_{Environment.ProcessId}.ojd");
int ok = 0, total = 0, bad = 0;
foreach (var file in args.Skip(3).SelectMany(d => Directory.EnumerateFiles(d)).Where(f => !f.EndsWith(".txt") && !f.EndsWith("COPYRIGHT")))
{
    total++;
    File.Delete(tmp);
    var sw = Stopwatch.StartNew();
    var p = Process.Start(new ProcessStartInfo(opjdump, $"\"{file}\" \"{tmp}\"") { RedirectStandardError = true, RedirectStandardOutput = true })!;
    bool cDone = p.WaitForExit(60000); if (!cDone) p.Kill();
    long cMs = sw.ElapsedMilliseconds;
    if (cDone && p.ExitCode == 3) { csv.WriteLine($"{Path.GetFileName(file)},SKIP_HUGE,,{cMs},"); Console.WriteLine($"SKIP_HUGE      {Path.GetFileName(file)}"); ok++; continue; }
    bool cOk = cDone && p.ExitCode == 0 && File.Exists(tmp);

    sw.Restart();
    JPXImage? img = null; string? err = null;
    var t = Task.Run(() =>
    {
        try
        {
            var bytes = File.ReadAllBytes(file);
            var fmt = bytes[0] == 0xFF && bytes[1] == 0x4F ? CodecFormat.Jpeg2K : CodecFormat.Jpeg2P;
            var ci = new CompressionInfo(true, fmt);
            using var ms = new MemoryStream(bytes, false);
            var cio = ci.OpenCIO(ms, true);
            ci.SetupDecoder(cio, new DecompressionParameters { DisableMultiThreading = singleThread });
            if (!ci.ReadHeader(out var im) || !ci.Decode(im) || !ci.EndDecompress()) err = "decode=false"; else img = im;
        }
        catch (Exception e) { err = e.GetType().Name + ": " + e.Message.Split('\n')[0].Replace(',', ';'); }
    });
    if (!t.Wait(60000)) err = "TIMEOUT";
    long csMs = sw.ElapsedMilliseconds;

    string status, detail = "";
    if (!cOk && img == null) status = "BOTH_FAIL";
    else if (!cOk) status = "C_FAIL_CS_OK";
    else if (img == null) { status = "CS_FAIL"; detail = err ?? ""; }
    else try { (status, detail) = Compare(File.ReadAllBytes(tmp), img); } catch (Exception e) { status = "MISMATCH"; detail = "compare " + e.GetType().Name; }
    if (status is "MATCH" or "BOTH_FAIL") ok++;
    if (status is "MISMATCH" or "CS_FAIL") bad++;
    csv.WriteLine($"{Path.GetFileName(file)},{status},{detail},{cMs},{csMs}");
    Console.WriteLine($"{status,-14} {Path.GetFileName(file)} {detail} c={cMs}ms cs={csMs}ms");
}
File.Delete(tmp);
Console.WriteLine($"PARITY {ok}/{total}, {bad} failures");
return bad;

static int MapCs(COLOR_SPACE c) => c switch
{
    COLOR_SPACE.UNSPECIFIED => 0, COLOR_SPACE.sRGB => 1, COLOR_SPACE.GRAY => 2, COLOR_SPACE.sYCC => 3,
    COLOR_SPACE.eYCC => 4, COLOR_SPACE.CMYK => 5, _ => -1
};
static (string, string) Compare(byte[] r, JPXImage img)
{
    var ints = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, int>(r.AsSpan(4));
    int n = ints[0], pos = 2;
    if (n != img.NumberOfComponents) return ("MISMATCH", $"ncomps c={n} cs={img.NumberOfComponents}");
    if (ints[1] != MapCs(img.GetColorSpace())) return ("MISMATCH", $"colorspace c={ints[1]} cs={img.GetColorSpace()}");
    for (int c = 0; c < n; c++)
    {
        var k = img.Components[c];
        int w = ints[pos], h = ints[pos + 1], prec = ints[pos + 6], sg = ints[pos + 7]; pos += 8;
        if (w != k.Width || h != k.Height || prec != k.Prec || (sg != 0) != k.Signed || ints[pos - 6] != k.DX || ints[pos - 5] != k.DY)
            return ("MISMATCH", $"comp{c} hdr c={w}x{h}p{prec}s{sg} cs={k.Width}x{k.Height}p{k.Prec}s{k.Signed}");
        var d = k.Data; if (d == null || d.Length < w * h) return ("MISMATCH", $"comp{c} cs data len {d?.Length} < {w * h}");
        int diffs = 0, first = -1, maxd = 0;
        for (int i = 0; i < w * h; i++)
        {
            int a = ints[pos + i], b = d[i];
            if (a != b) { diffs++; if (first < 0) first = i; maxd = Math.Max(maxd, Math.Abs(a - b)); }
        }
        pos += w * h;
        if (diffs > 0) return ("MISMATCH", $"comp{c} diffs={diffs}/{w * h} first={first} maxdiff={maxd}");
    }
    return ("MATCH", "");
}

/// <summary>
/// Hash of decoded samples, built from public fields only: numcomps, colour space (upstream OPJ_CLRSPC_* numbering),
/// then per component w, h, dx, dy, prec, sgnd and the w*h samples, all little-endian int32.
/// Keep in sync with ReferenceParityTest.Hash in the test project.
/// </summary>
static class SampleHash
{
    public static string Md5(byte[] data) => Convert.ToHexString(System.Security.Cryptography.MD5.HashData(data));

    public static string FromOjd(byte[] r)
    {
        var ints = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, int>(r.AsSpan(4));
        var outp = new List<int> { ints[0], ints[1] };
        int pos = 2;
        for (int c = 0; c < ints[0]; c++)
        {
            int w = ints[pos], h = ints[pos + 1];
            outp.AddRange([w, h, ints[pos + 2], ints[pos + 3], ints[pos + 6], ints[pos + 7]]);
            pos += 8;
            outp.AddRange(ints.Slice(pos, w * h).ToArray());
            pos += w * h;
        }
        return Md5(System.Runtime.InteropServices.MemoryMarshal.AsBytes(outp.ToArray().AsSpan()).ToArray());
    }
}

/// <summary>True if JPXDecode is the only filter, as a name or a one-element array. Keep in sync with ReferenceParityTest.</summary>
static partial class Program
{
    internal static bool IsJpxOnly(DictionaryToken dict) =>
        (dict.TryGet(NameToken.Filter, out NameToken name) && name.Data == "JPXDecode") ||
        (dict.TryGet(NameToken.Filter, out ArrayToken array) && array.Data.Count == 1 &&
         array.Data[0] is NameToken only && only.Data == "JPXDecode");
}
