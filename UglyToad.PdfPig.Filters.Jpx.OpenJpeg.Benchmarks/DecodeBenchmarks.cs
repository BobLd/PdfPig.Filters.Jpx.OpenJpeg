using BenchmarkDotNet.Attributes;
using OpenJpeg;
using UglyToad.PdfPig.Tokens;

namespace UglyToad.PdfPig.Filters.Jpx.OpenJpeg.Benchmarks;

/// <summary>
/// Managed decoder only. Uses public API that exists in older versions too, so
/// benchmarks/compare-versions.ps1 can run this class against a baseline git ref.
/// </summary>
[MemoryDiagnoser]
public class DecodeBenchmarks
{
    private byte[] _data = [];

    [ParamsSource(nameof(Files))]
    public string File { get; set; } = "";

    /// <summary>1 = single thread, 0 = all cores.</summary>
    [Params(1, 0)]
    public int Threads { get; set; }

    public static IEnumerable<string> Files => Samples.All;

    [GlobalSetup]
    public void Setup() => _data = Samples.Load(File);

    /// <summary>Codestream decode only, as used through the OpenJPEG API.</summary>
    [Benchmark]
    public JPXImage Decode() => ManagedDecoder.Decode(_data, Threads);
}

/// <summary>What PdfPig calls: decode plus conversion to the output byte layout (default threading).</summary>
[MemoryDiagnoser]
public class FilterBenchmarks
{
    private byte[] _data = [];
    private readonly DictionaryToken _emptyDictionary = new(new Dictionary<NameToken, IToken>());

    [ParamsSource(nameof(Files))]
    public string File { get; set; } = "";

    public static IEnumerable<string> Files => Samples.All;

    [GlobalSetup]
    public void Setup() => _data = Samples.Load(File);

    [Benchmark]
    public int Filter() => new OpenJpegJpxDecodeFilter().Decode(_data, _emptyDictionary, null!, 0).Length;
}

internal static class ManagedDecoder
{
    public static JPXImage Decode(byte[] data, int threads)
    {
        var format = data[0] == 0xFF && data[1] == 0x4F ? CodecFormat.Jpeg2K : CodecFormat.Jpeg2P;
        var cinfo = new CompressionInfo(true, format);
        using var ms = new MemoryStream(data, false);
        var cio = cinfo.OpenCIO(ms, true);
        cinfo.SetupDecoder(cio, new DecompressionParameters { DisableMultiThreading = threads == 1 });
        if (!cinfo.ReadHeader(out var image) || !cinfo.Decode(image) || !cinfo.EndDecompress())
        {
            throw new InvalidOperationException("Managed decode failed");
        }
        return image;
    }
}
