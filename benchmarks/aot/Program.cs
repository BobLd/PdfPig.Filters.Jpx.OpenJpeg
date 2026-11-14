using System.Diagnostics;
using OpenJpeg;
using UglyToad.PdfPig.Filters.Jpx.OpenJpeg.Benchmarks;

// Usage: AotDecode [iterations=10]. Prints, per sample and thread mode, the first decode and the median of the rest.
int iterations = args.Length > 0 ? int.Parse(args[0]) : 10;
Console.WriteLine($"{"File",-24} {"Threads",7} {"First ms",9} {"Median ms",10} {"First MB",9} {"Median MB",10}");
foreach (var name in Samples.All)
{
    var data = Samples.Load(name);
    foreach (var threads in new[] { 1, 0 })
    {
        var times = new double[iterations];
        var mb = new double[iterations];
        for (int i = 0; i < iterations; i++)
        {
            long before = GC.GetTotalAllocatedBytes(true);
            var sw = Stopwatch.StartNew();
            Decode(data, threads);
            times[i] = sw.Elapsed.TotalMilliseconds;
            mb[i] = (GC.GetTotalAllocatedBytes(true) - before) / 1048576.0;
        }
        var rest = times.Skip(1).OrderBy(t => t).ToArray();
        var restMb = mb.Skip(1).OrderBy(t => t).ToArray();
        Console.WriteLine($"{name,-24} {threads,7} {times[0],9:F1} {rest[rest.Length / 2],10:F1} {mb[0],9:F1} {restMb[restMb.Length / 2],10:F1}");
    }
}

static void Decode(byte[] data, int threads)
{
    var format = data[0] == 0xFF && data[1] == 0x4F ? CodecFormat.Jpeg2K : CodecFormat.Jpeg2P;
    var cinfo = new CompressionInfo(true, format);
    using var ms = new MemoryStream(data, false);
    cinfo.SetupDecoder(cinfo.OpenCIO(ms, true), new DecompressionParameters { DisableMultiThreading = threads == 1 });
    if (!cinfo.ReadHeader(out var image) || !cinfo.Decode(image) || !cinfo.EndDecompress())
        throw new InvalidOperationException("Decode failed");
}
