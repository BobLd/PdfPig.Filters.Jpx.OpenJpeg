// Patches stage timers into a copy of the decoder source, or restores it. Used by ab.ps1; see tools/README.md.
//
//   dotnet run tools/perf/instrument.cs -- apply <srcRoot> <probe>    probe: tile | t1 | dwt53 | dwt97
//   dotnet run tools/perf/instrument.cs -- restore <srcRoot>
//
// srcRoot is a repository root (the working tree or a baseline worktree). Timers are matched by whole source lines,
// so a probe fails loudly (and restores) when the code it times has changed: update the anchors below.
// Patched files get a .perfbak copy; OpenJPEG/PerfProbe.cs holds the counters (read by probe.cs).
using System.Text;

if (args.Length < 2) return Usage();
var lib = Path.Combine(args[1], "UglyToad.PdfPig.Filters.Jpx.OpenJpeg", "OpenJPEG");
if (!Directory.Exists(lib)) { Console.Error.WriteLine($"No decoder source in {lib}"); return 2; }
if (args[0] == "restore") { Restore(lib); return 0; }
if (args[0] != "apply" || args.Length < 3) return Usage();

// Each anchor: file, kind, source line(s) to match, counter name, how many matches are allowed.
//   stmt  - a statement on its own line, wrapped in a timer
//   expr  - "if (!<call>)" on its own line, the call wrapped in a timer
//   range - timer started before the first line and added before the second, slot = base + index expression
var probes = new Dictionary<string, Anchor[]>
{
    ["tile"] = new[]
    {
        new Anchor("Internal/TileCoder.cs", "expr", "T2Decode(src, src_len, cstr_index)", "T2"),
        new Anchor("Internal/TileCoder.cs", "expr", "T1Decode()", "T1"),
        new Anchor("Internal/TileCoder.cs", "expr", "DWTDecode()", "DWT"),
        new Anchor("Internal/TileCoder.cs", "expr", "MCTDecode()", "MCT"),
        new Anchor("Internal/TileCoder.cs", "stmt", "DcLevelShiftDecode();", "DC shift"),
        new Anchor("J2K.cs", "expr", "UpdateImageData()", "tile to image", Max: 99),
    },
    ["t1"] = new[]
    {
        // In DecodeCblk: the switch over the three coding passes (passtype 0, 1, 2)
        new Anchor("Internal/Tier1Coding.cs", "range", "                    switch (passtype)", "sigpass|refpass|clnpass",
            End: "                    if ((cblksty & CCP_CBLKSTY.RESET) != 0 && type == T1_TYPE.MQ)", Index: "passtype"),
    },
    ["dwt53"] = new[]
    {
        new Anchor("Internal/DWT.cs", "stmt", "idwt53_h_rows(h, tilec.data, tiledp, (int)w, 0, (int)rh);", "53 horizontal"),
        new Anchor("Internal/DWT.cs", "stmt", "idwt53_v_cols(v, tilec.data, tiledp, (int)w, 0, (int)rw);", "53 vertical"),
    },
    ["dwt97"] = new[]
    {
        new Anchor("Internal/DWT.cs", "stmt", "v8dwt_interleave_h(h, aj_ar, aj, w, NB_ELTS_V8);", "97 h-interleave"),
        new Anchor("Internal/DWT.cs", "stmt", "v8dwt_decode(h);", "97 h-lifting", Max: 99),
        new Anchor("Internal/DWT.cs", "stmt", "WriteBackRows8(h.wavelet, ajf, aj, w, (int)rw);", "97 h-writeback"),
        new Anchor("Internal/DWT.cs", "stmt", "v8dwt_interleave_v_pair(v, v2, aj_ar, aj + done, w);", "97 v-interleave"),
        new Anchor("Internal/DWT.cs", "stmt", "v8dwt_interleave_v(v, aj_ar, aj + done, w, NB_ELTS_V8);", "97 v-interleave"),
        new Anchor("Internal/DWT.cs", "stmt", "v8dwt_decode(v);", "97 v-lifting", Max: 99),
        new Anchor("Internal/DWT.cs", "stmt", "v8dwt_decode(v2);", "97 v-lifting", Max: 99),
        new Anchor("Internal/DWT.cs", "stmt", "CopyWaveletRowsBackPair(v.wavelet, v2.wavelet, rh, aj_ar, aj + done, w);", "97 v-writeback"),
        new Anchor("Internal/DWT.cs", "stmt", "CopyWaveletRowsBack(v.wavelet, rh, aj_ar, aj + done, w);", "97 v-writeback"),
    },
};
if (!probes.TryGetValue(args[2], out var anchors)) return Usage();
if (File.Exists(Path.Combine(lib, "PerfProbe.cs"))) { Console.Error.WriteLine($"{args[1]} is already instrumented: restore it first"); return 2; }

const string P = "global::OpenJpeg.PerfProbe";
var names = new List<string>();
int Slot(string name) { int k = names.IndexOf(name); if (k < 0) { names.Add(name); k = names.Count - 1; } return k; }
var files = new Dictionary<string, (string[] Lines, Encoding Enc)>();
try
{
    foreach (var a in anchors)
    {
        var path = Path.Combine(lib, a.File);
        if (!files.TryGetValue(path, out var f))
        {
            var bytes = File.ReadAllBytes(path);
            bool bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            var enc = new UTF8Encoding(bom);
            f = (enc.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0)).Split('\n'), enc);
            files[path] = f;
        }
        var lines = f.Lines;
        int found = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].TrimEnd('\r'), cr = lines[i].EndsWith('\r') ? "\r" : "";
            string indent = line[..^line.TrimStart().Length];
            switch (a.Kind)
            {
                case "stmt" when line.Trim() == a.Line:
                    lines[i] = $"{indent}{{ long _pp = {P}.Now(); {a.Line} {P}.Add({Slot(a.Name)}, _pp); }}{cr}";
                    found++;
                    break;
                case "expr" when line.Trim() == $"if (!{a.Line})":
                    lines[i] = $"{indent}if (!{P}.T({Slot(a.Name)}, {P}.Now(), {a.Line})){cr}";
                    found++;
                    break;
                case "range" when line == a.Line:
                    int end = Array.FindIndex(lines, i + 1, l => l.TrimEnd('\r') == a.End);
                    if (end < 0) throw new InvalidOperationException($"{a.File}: end line not found after '{a.Line.Trim()}'");
                    int slot0 = -1;
                    foreach (var n in a.Name.Split('|')) { int s = Slot(n); if (slot0 < 0) slot0 = s; }
                    string endIndent = a.End![..^a.End!.TrimStart().Length];
                    lines[end] = $"{endIndent}{P}.Add({slot0} + (int)({a.Index}), _ppr);\n{lines[end]}";
                    lines[i] = $"{indent}long _ppr = {P}.Now();\n{lines[i]}";
                    found++;
                    break;
            }
        }
        if (found < 1 || found > a.Max)
            throw new InvalidOperationException($"{a.File}: '{a.Line.Trim()}' matched {found} lines (expected 1{(a.Max > 1 ? " or more" : "")}). Update the anchor in tools/perf/instrument.cs.");
    }
    foreach (var (path, f) in files)
    {
        File.Copy(path, path + ".perfbak", overwrite: false);
        File.WriteAllText(path, string.Join('\n', f.Lines), f.Enc);
    }
    File.WriteAllText(Path.Combine(lib, "PerfProbe.cs"), $$"""
        // Generated by tools/perf/instrument.cs; removed by its restore command. Not part of the library.
        namespace OpenJpeg
        {
            internal static class PerfProbe
            {
                internal static readonly string[] Names = { {{string.Join(", ", names.Select(n => $"\"{n}\""))}} };
                internal static readonly long[] Ticks = new long[{{names.Count}}];
                internal static long Now() => System.Diagnostics.Stopwatch.GetTimestamp();
                internal static void Add(int k, long start) => Ticks[k] += System.Diagnostics.Stopwatch.GetTimestamp() - start;
                internal static bool T(int k, long start, bool result) { Add(k, start); return result; }
            }
        }
        """);
    Console.WriteLine($"{args[2]}: {string.Join(", ", names)} -> {args[1]}");
    return 0;
}
catch (Exception e)
{
    Restore(lib);
    Console.Error.WriteLine(e.Message);
    return 1;
}

static void Restore(string lib)
{
    foreach (var bak in Directory.EnumerateFiles(lib, "*.perfbak", SearchOption.AllDirectories))
        File.Move(bak, bak[..^".perfbak".Length], overwrite: true);
    File.Delete(Path.Combine(lib, "PerfProbe.cs"));
}

static int Usage()
{
    Console.Error.WriteLine("usage: instrument.cs apply <srcRoot> tile|t1|dwt53|dwt97  |  instrument.cs restore <srcRoot>");
    return 2;
}

record Anchor(string File, string Kind, string Line, string Name, string? End = null, string? Index = null, int Max = 1);
