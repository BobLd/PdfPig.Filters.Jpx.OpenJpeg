#:project ../../UglyToad.PdfPig.Filters.Jpx.OpenJpeg/UglyToad.PdfPig.Filters.Jpx.OpenJpeg.csproj
#:property TargetFramework=net8.0
#:property PublishAot=false
// Decodes the files single-threaded (all cores with PROBE_MT=1), round after round, and prints the stage timers that tools/perf/instrument.cs
// patched in, in ms per round (one decode of each file). Used by ab.ps1; see tools/README.md.
//
//   dotnet run -c Release tools/perf/probe.cs -- [--warm N] [--rounds N] <file>...
//
// Warm-up rounds are not counted: files that decode in a few ms need tens of them before tier-1 code runs.
using OpenJpeg;

int warm = 5, rounds = 10;
var paths = new List<string>();
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--warm") warm = int.Parse(args[++i]);
    else if (args[i] == "--rounds") rounds = int.Parse(args[++i]);
    else paths.Add(args[i]);
}
if (paths.Count == 0) { Console.Error.WriteLine("usage: probe.cs [--warm N] [--rounds N] <file>..."); return 2; }

var probe = typeof(CompressionInfo).Assembly.GetType("OpenJpeg.PerfProbe");
const System.Reflection.BindingFlags F = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
var ticks = (long[]?)probe?.GetField("Ticks", F)?.GetValue(null);
var names = (string[]?)probe?.GetField("Names", F)?.GetValue(null);
var data = paths.Select(File.ReadAllBytes).ToArray();

for (int r = 0; r < warm; r++) foreach (var d in data) Decode(d);
if (ticks != null) Array.Clear(ticks);
long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
for (int r = 0; r < rounds; r++) foreach (var d in data) Decode(d);
double ms(long t) => t * 1000.0 / System.Diagnostics.Stopwatch.Frequency / rounds;
Console.WriteLine($"perf round {ms(System.Diagnostics.Stopwatch.GetTimestamp() - t0):F3}");
if (ticks == null) Console.Error.WriteLine("No timers: run instrument.cs apply first.");
else for (int k = 0; k < ticks.Length; k++) Console.WriteLine($"perf {names![k]} {ms(ticks[k]):F3}");
return 0;

static void Decode(byte[] bytes)
{
    var ci = new CompressionInfo(true, bytes[0] == 0xFF && bytes[1] == 0x4F ? CodecFormat.Jpeg2K : CodecFormat.Jpeg2P);
    using var ms = new MemoryStream(bytes, false);
    ci.SetupDecoder(ci.OpenCIO(ms, true), new DecompressionParameters { DisableMultiThreading = Environment.GetEnvironmentVariable("PROBE_MT") != "1" });
    if (!ci.ReadHeader(out var img) || !ci.Decode(img) || !ci.EndDecompress()) throw new Exception("decode failed");
}
