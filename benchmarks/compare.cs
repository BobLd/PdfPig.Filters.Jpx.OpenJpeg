// Compares two BenchmarkDotNet JSON exports (*-report-full.json) and flags regressions.
// Usage: dotnet run benchmarks/compare.cs -- <baselineDir> <currentDir> [thresholdPercent=5]
// Exit code 1 if any benchmark's mean time or allocations regressed by more than the threshold.
using System.Text.Json;

if (args.Length < 2) { Console.Error.WriteLine("usage: compare <baselineDir> <currentDir> [thresholdPercent]"); return 2; }
double threshold = args.Length > 2 ? double.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : 5;

var baseline = Load(args[0]);
var current = Load(args[1]);
if (baseline.Count == 0 || current.Count == 0)
{
    Console.Error.WriteLine($"No benchmark results found (baseline: {baseline.Count}, current: {current.Count}).");
    return 2;
}
bool regressed = false;
Console.WriteLine($"{"Benchmark",-80} {"Base ms",10} {"New ms",10} {"Time",8} {"Base MB",10} {"New MB",10} {"Alloc",8}");
foreach (var (name, cur) in current.OrderBy(k => k.Key))
{
    if (!baseline.TryGetValue(name, out var b)) { Console.WriteLine($"{name,-80} (new)"); continue; }
    double t = cur.MeanNs / b.MeanNs - 1, a = b.Bytes == 0 ? 0 : (double)cur.Bytes / b.Bytes - 1;
    bool bad = t * 100 > threshold || a * 100 > threshold;
    regressed |= bad;
    Console.WriteLine($"{name,-80} {b.MeanNs / 1e6,10:F2} {cur.MeanNs / 1e6,10:F2} {t,8:+0.0%;-0.0%} {b.Bytes / 1048576.0,10:F1} {cur.Bytes / 1048576.0,10:F1} {a,8:+0.0%;-0.0%}{(bad ? "  REGRESSION" : "")}");
}
return regressed ? 1 : 0;

static Dictionary<string, (double MeanNs, long Bytes)> Load(string dir)
{
    var result = new Dictionary<string, (double, long)>();
    foreach (var file in Directory.EnumerateFiles(dir, "*-report-full.json", SearchOption.AllDirectories))
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        foreach (var bm in doc.RootElement.GetProperty("Benchmarks").EnumerateArray())
        {
            if (!bm.TryGetProperty("Statistics", out var stats) || stats.ValueKind != JsonValueKind.Object) continue;
            long bytes = bm.TryGetProperty("Memory", out var mem) && mem.ValueKind == JsonValueKind.Object
                ? mem.GetProperty("BytesAllocatedPerOperation").GetInt64() : 0;
            result[bm.GetProperty("FullName").GetString()!] = (stats.GetProperty("Mean").GetDouble(), bytes);
        }
    }
    return result;
}
