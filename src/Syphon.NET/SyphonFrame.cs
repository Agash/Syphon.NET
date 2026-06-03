using Syphon.NET.Interop;

namespace Syphon.NET;

/// <summary>
/// A frame received from a <see cref="SyphonClient"/>. Owns a retained reference to the backing
/// IOSurface; dispose it once the frame has been consumed (copied, uploaded, or encoded).
/// </summary>
public sealed class SyphonFrame : IDisposable
{
    private nint _surface;

    internal SyphonFrame(nint retainedSurface) => _surface = retainedSurface;

    /// <summary>The zero-copy surface holding the frame's pixels.</summary>
    public IOSurface Surface => new(_surface);

    /// <summary>Copy the frame's pixels into <paramref name="destination"/> as tightly packed rows.</summary>
    /// <returns>The number of bytes written (<c>Width * 4 * Height</c>).</returns>
    public int CopyTo(Span<byte> destination)
    {
        IOSurface s = Surface;
        int width = s.Width, height = s.Height, stride = s.BytesPerRow;
        int rowBytes = width * 4;
        int needed = rowBytes * height;
        if (destination.Length < needed)
            throw new ArgumentException($"Destination too small: need {needed} bytes.", nameof(destination));

        using IOSurface.Lock locked = s.LockBytes(readOnly: true);
        ReadOnlySpan<byte> src = locked.Bytes;
        for (int row = 0; row < height; row++)
            src.Slice(row * stride, rowBytes).CopyTo(destination.Slice(row * rowBytes, rowBytes));
        return needed;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        nint s = Interlocked.Exchange(ref _surface, 0);
        if (s != 0) CoreFoundation.CFRelease(s);
    }
}
