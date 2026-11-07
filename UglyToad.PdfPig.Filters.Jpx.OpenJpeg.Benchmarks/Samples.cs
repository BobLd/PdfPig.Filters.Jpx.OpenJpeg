namespace UglyToad.PdfPig.Filters.Jpx.OpenJpeg.Benchmarks;

/// <summary>
/// JPX streams extracted from the test PDFs, picked to cover the main decode paths.
/// Keep the names stable: they are the benchmark keys used when comparing versions.
/// </summary>
public static class Samples
{
    public static IEnumerable<string> All =>
    [
        "MOZILLA-10719-0_3",      // 3507x2480 RGB, lossless 5/3, 140 tiles, 4.2 MB
        "MOZILLA-LINK-3167-0_10", // 1625x2219 RGB, lossy 9/7, 15 tiles
        "MOZILLA-LINK-3181-0_5",  // 2346x3140 RGB, lossy 9/7, 1 tile, low bitrate (DWT/MCT bound)
        "PDFBOX-1845-0_49",       // 2480x3507 grey, lossy 9/7, 1 tile
        "MOZILLA-10026-0_0",      // 2496x3512 1-bit, lossless 5/3
        "MOZILLA-8179-0_1",       // 541x411 grey, lossless 5/3, 6 layers
        "MOZILLA-LINK-4649-0_10", // 964x725 4 components, lossy 9/7, 12 tiles
    ];

    public static byte[] Load(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Data", name + ".jpx"));
}
