using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;

namespace UglyToad.PdfPig.Filters.Jpx.OpenJpeg.Benchmarks;

/// <summary>
/// Managed port vs upstream C OpenJPEG on the same input, in the same process.
/// Needs the native library: tools/reference/build.ps1 -Simd (or set OPENJPEG_NATIVE_DIR).
/// Native allocations are not visible to the memory diagnoser, so only compare the Mean column.
/// </summary>
[MemoryDiagnoser]
public unsafe class NativeComparisonBenchmarks
{
    private byte[] _data = [];
    private byte* _native;

    [ParamsSource(nameof(Files))]
    public string File { get; set; } = "";

    /// <summary>1 = single thread, 0 = all cores.</summary>
    [Params(1, 0)]
    public int Threads { get; set; }

    public static IEnumerable<string> Files => Samples.All;

    [GlobalSetup]
    public void Setup()
    {
        NativeOpenJpeg.Load();
        _data = Samples.Load(File);
        _native = (byte*)NativeMemory.Alloc((nuint)_data.Length);
        _data.CopyTo(new Span<byte>(_native, _data.Length));
    }

    [GlobalCleanup]
    public void Cleanup() => NativeMemory.Free(_native);

    [Benchmark(Baseline = true)]
    public int Native() => NativeOpenJpeg.Decode(_native, _data.Length, Threads);

    [Benchmark]
    public int Managed() => ManagedDecoder.Decode(_data, Threads).NumberOfComponents;
}
