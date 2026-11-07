using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Running;

// Examples (from the repo root):
//   dotnet run -c Release --project UglyToad.PdfPig.Filters.Jpx.OpenJpeg.Benchmarks -f net8.0 -- --filter *DecodeBenchmarks*
//   dotnet run -c Release --project UglyToad.PdfPig.Filters.Jpx.OpenJpeg.Benchmarks -f net8.0 -- --filter *NativeComparison*
// Version-to-version comparison: benchmarks/compare-versions.ps1. JSON results land in BenchmarkDotNet.Artifacts/results.
var config = DefaultConfig.Instance.AddExporter(JsonExporter.Full);
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, config);
