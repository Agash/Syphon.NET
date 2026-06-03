namespace Syphon.NET;

/// <summary>
/// Pixel formats supported for published and received frames, expressed as IOSurface FourCC
/// codes. BGRA is the Syphon and Metal default and the recommended choice.
/// </summary>
public enum SyphonPixelFormat : uint
{
    /// <summary>32-bit BGRA, 8 bits per channel. FourCC 'BGRA'.</summary>
    Bgra = 0x42475241,

    /// <summary>32-bit RGBA, 8 bits per channel. FourCC 'RGBA'.</summary>
    Rgba = 0x52474241,
}
