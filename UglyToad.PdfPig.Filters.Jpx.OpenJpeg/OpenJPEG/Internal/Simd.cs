#if NET8_0_OR_GREATER
using System.Runtime.Intrinsics;
#endif

namespace OpenJpeg.Internal;

/// <summary>Choices of SIMD width shared by the transforms.</summary>
internal static class Simd
{
#if NET8_0_OR_GREATER
    /// <summary>
    /// Use the Vector512 (AVX-512) paths. The AppContext switch "OpenJpeg.ForceVector512" turns them on even
    /// without hardware support, where .NET emulates Vector512 in software: slow, but it lets tools/parity
    /// check those paths on any machine (PARITY_FORCE_V512=1).
    /// </summary>
    internal static readonly bool Use512 = Vector512.IsHardwareAccelerated ||
        (AppContext.TryGetSwitch("OpenJpeg.ForceVector512", out bool force) && force);
#endif
}
