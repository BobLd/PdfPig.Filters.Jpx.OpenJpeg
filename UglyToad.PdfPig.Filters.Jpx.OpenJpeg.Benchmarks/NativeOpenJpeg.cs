using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace UglyToad.PdfPig.Filters.Jpx.OpenJpeg.Benchmarks;

/// <summary>
/// Minimal P/Invoke binding to upstream C OpenJPEG (openjp2), used only to compare speed with the managed port.
/// Build the library with <c>tools/reference/build.ps1 -Simd</c>.
/// </summary>
internal static unsafe partial class NativeOpenJpeg
{
    private const string Lib = "openjp2";

    /// <summary>Finds openjp2 in OPENJPEG_NATIVE_DIR, or in .reference/build-simd/bin, then .reference/build/bin.</summary>
    public static string? FindLibraryDirectory()
    {
        var env = Environment.GetEnvironmentVariable("OPENJPEG_NATIVE_DIR");
        if (!string.IsNullOrEmpty(env)) return env;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            foreach (var sub in new[] { "build-simd", "build" })
            {
                var candidate = Path.Combine(dir.FullName, ".reference", sub, "bin");
                if (Directory.Exists(candidate) && Directory.EnumerateFiles(candidate, "*openjp2*").Any()) return candidate;
            }
        }
        return null;
    }

    public static void Load()
    {
        var dir = FindLibraryDirectory() ?? throw new InvalidOperationException(
            "Native openjp2 not found. Run tools/reference/build.ps1 -Simd or set OPENJPEG_NATIVE_DIR.");
        NativeLibrary.SetDllImportResolver(typeof(NativeOpenJpeg).Assembly, (name, _, _) =>
        {
            if (name != Lib) return IntPtr.Zero;
            foreach (var file in new[] { "openjp2.dll", "libopenjp2.so", "libopenjp2.dylib" })
            {
                var path = Path.Combine(dir, file);
                if (File.Exists(path)) return NativeLibrary.Load(path);
            }
            return IntPtr.Zero;
        });
    }

    private struct MemoryState
    {
        public byte* Data;
        public long Length;
        public long Position;
    }

    /// <summary>Decodes a J2K/JP2 buffer that lives in native memory. Returns the number of components.</summary>
    public static int Decode(byte* data, long length, int threads)
    {
        var format = length > 1 && data[0] == 0xFF && data[1] == 0x4F ? 0 /* OPJ_CODEC_J2K */ : 2 /* OPJ_CODEC_JP2 */;
        var codec = opj_create_decompress(format);
        if (codec == IntPtr.Zero) throw new InvalidOperationException("opj_create_decompress failed");

        // opj_dparameters_t is ~8.5 KB (two OPJ_PATH_LEN paths). Only the library reads it, so a zeroed
        // oversized buffer avoids mirroring the struct layout.
        var parameters = NativeMemory.AllocZeroed(32 * 1024);
        var state = (MemoryState*)NativeMemory.AllocZeroed((nuint)sizeof(MemoryState));
        IntPtr stream = IntPtr.Zero, image = IntPtr.Zero;
        try
        {
            opj_set_default_decoder_parameters(parameters);
            if (opj_setup_decoder(codec, parameters) == 0) throw new InvalidOperationException("opj_setup_decoder failed");
            if (threads != 1) opj_codec_set_threads(codec, threads == 0 ? Environment.ProcessorCount : threads);

            state->Data = data;
            state->Length = length;
            stream = opj_stream_create(1024 * 1024, 1);
            opj_stream_set_read_function(stream, &Read);
            opj_stream_set_skip_function(stream, &Skip);
            opj_stream_set_seek_function(stream, &Seek);
            opj_stream_set_user_data(stream, state, IntPtr.Zero);
            opj_stream_set_user_data_length(stream, (ulong)length);

            if (opj_read_header(stream, codec, &image) == 0 ||
                opj_decode(codec, stream, image) == 0 ||
                opj_end_decompress(codec, stream) == 0)
            {
                throw new InvalidOperationException("Native decode failed");
            }
            return (int)((OpjImageHeader*)image)->numcomps;
        }
        finally
        {
            if (image != IntPtr.Zero) opj_image_destroy(image);
            if (stream != IntPtr.Zero) opj_stream_destroy(stream);
            opj_destroy_codec(codec);
            NativeMemory.Free(parameters);
            NativeMemory.Free(state);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct OpjImageHeader
    {
        public uint x0, y0, x1, y1, numcomps;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nuint Read(void* buffer, nuint count, void* user)
    {
        var s = (MemoryState*)user;
        var left = s->Length - s->Position;
        if (left <= 0) return unchecked((nuint)(-1));
        var n = (long)Math.Min((ulong)left, count);
        Buffer.MemoryCopy(s->Data + s->Position, buffer, (long)count, n);
        s->Position += n;
        return (nuint)n;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static long Skip(long count, void* user)
    {
        var s = (MemoryState*)user;
        var target = s->Position + count;
        if (target < 0 || target > s->Length) return -1;
        s->Position = target;
        return count;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Seek(long position, void* user)
    {
        var s = (MemoryState*)user;
        if (position < 0 || position > s->Length) return 0;
        s->Position = position;
        return 1;
    }

    [LibraryImport(Lib)] private static partial IntPtr opj_create_decompress(int format);
    [LibraryImport(Lib)] private static partial void opj_set_default_decoder_parameters(void* parameters);
    [LibraryImport(Lib)] private static partial int opj_setup_decoder(IntPtr codec, void* parameters);
    [LibraryImport(Lib)] private static partial int opj_codec_set_threads(IntPtr codec, int numThreads);
    [LibraryImport(Lib)] private static partial IntPtr opj_stream_create(nuint bufferSize, int isInput);
    [LibraryImport(Lib)] private static partial void opj_stream_set_read_function(IntPtr stream, delegate* unmanaged[Cdecl]<void*, nuint, void*, nuint> fn);
    [LibraryImport(Lib)] private static partial void opj_stream_set_skip_function(IntPtr stream, delegate* unmanaged[Cdecl]<long, void*, long> fn);
    [LibraryImport(Lib)] private static partial void opj_stream_set_seek_function(IntPtr stream, delegate* unmanaged[Cdecl]<long, void*, int> fn);
    [LibraryImport(Lib)] private static partial void opj_stream_set_user_data(IntPtr stream, void* data, IntPtr freeFn);
    [LibraryImport(Lib)] private static partial void opj_stream_set_user_data_length(IntPtr stream, ulong length);
    [LibraryImport(Lib)] private static partial int opj_read_header(IntPtr stream, IntPtr codec, IntPtr* image);
    [LibraryImport(Lib)] private static partial int opj_decode(IntPtr codec, IntPtr stream, IntPtr image);
    [LibraryImport(Lib)] private static partial int opj_end_decompress(IntPtr codec, IntPtr stream);
    [LibraryImport(Lib)] private static partial void opj_image_destroy(IntPtr image);
    [LibraryImport(Lib)] private static partial void opj_stream_destroy(IntPtr stream);
    [LibraryImport(Lib)] private static partial void opj_destroy_codec(IntPtr codec);
}
