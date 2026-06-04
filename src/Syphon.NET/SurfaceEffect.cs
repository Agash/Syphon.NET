using Syphon.NET.Interop;

namespace Syphon.NET;

/// <summary>
/// A general-purpose GPU helper (not part of the Syphon protocol) that runs a Metal fragment shader
/// over one or more input <see cref="IOSurface"/> planes and renders the result into a BGRA output
/// surface - zero-copy, no CPU readback, on the same shared Metal device backing servers and clients.
/// Useful for transforming a surface before <see cref="SyphonServer.Publish(IOSurface, bool)"/> or
/// after receiving one (colour conversion, channel folding, and similar): you supply the shader and
/// describe the inputs, so the transform's pixel maths lives in your code, not here.
/// </summary>
/// <remarks>
/// Your fragment source is compiled against a built-in preamble that provides the vertex stage and a
/// sampler, so a fragment can use the stage-in struct <c>VOut { float4 pos [[position]]; float2 uv; }</c>
/// (with <c>uv</c> in <c>[0,1]</c>) and the linear clamped sampler <c>sy_samp</c>. For example a
/// fragment that copies a single BGRA input:
/// <code>
/// fragment float4 copy(VOut in [[stage_in]], texture2d&lt;float&gt; src [[texture(0)]]) {
///     return src.sample(sy_samp, in.uv);
/// }
/// </code>
/// The returned surface is owned by this effect and reused across calls; it is valid until the next
/// <see cref="Render"/> or until disposal. Copy or consume it before the next call. Not thread-safe.
/// </remarks>
public sealed class SurfaceEffect : IDisposable
{
    private nint _handle;
    private nint[] _surfaces = [];
    private uint[] _planes = [];
    private uint[] _formats = [];

    /// <summary>
    /// Compile <paramref name="fragmentFunction"/> from <paramref name="fragmentSource"/> (Metal
    /// Shading Language, appended to the built-in preamble) into a render pipeline.
    /// </summary>
    /// <exception cref="InvalidOperationException">The shader failed to compile or link.</exception>
    public SurfaceEffect(string fragmentSource, string fragmentFunction)
    {
        ArgumentNullException.ThrowIfNull(fragmentSource);
        ArgumentNullException.ThrowIfNull(fragmentFunction);
        SyphonRuntime.EnsureInitialized();
        _handle = SyphonNative.sy_effect_create(fragmentSource, fragmentFunction);
        if (_handle == 0)
            throw new InvalidOperationException(
                $"Failed to compile the Metal effect '{fragmentFunction}'. Check the shader source compiles.");
    }

    /// <summary>
    /// Render the effect into a <paramref name="outputWidth"/> x <paramref name="outputHeight"/> BGRA
    /// surface, binding each input at fragment texture indices <c>0..inputs.Length-1</c>. Returns a
    /// non-owning view over the effect-owned output surface, valid until the next call or disposal.
    /// </summary>
    /// <exception cref="InvalidOperationException">The GPU pass failed.</exception>
    public IOSurface Render(int outputWidth, int outputHeight, ReadOnlySpan<SurfaceInput> inputs)
    {
        ObjectDisposedException.ThrowIf(_handle == 0, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(outputWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(outputHeight);
        if (inputs.IsEmpty) throw new ArgumentException("At least one input is required.", nameof(inputs));

        if (_surfaces.Length < inputs.Length)
        {
            _surfaces = new nint[inputs.Length];
            _planes = new uint[inputs.Length];
            _formats = new uint[inputs.Length];
        }

        for (int i = 0; i < inputs.Length; i++)
        {
            if (!inputs[i].Surface.IsValid)
                throw new ArgumentException($"Input {i} surface is not valid.", nameof(inputs));
            _surfaces[i] = inputs[i].Surface.Handle;
            _planes[i] = (uint)inputs[i].Plane;
            _formats[i] = (uint)inputs[i].Format;
        }

        nint result = SyphonNative.sy_effect_render(
            _handle, (uint)outputWidth, (uint)outputHeight, _surfaces, _planes, _formats, inputs.Length);
        if (result == 0) throw new InvalidOperationException("The Metal effect pass failed.");
        return new IOSurface(result);
    }

    /// <summary>Release the effect and its output surface.</summary>
    public void Dispose()
    {
        if (_handle == 0) return;
        SyphonNative.sy_effect_destroy(_handle);
        _handle = 0;
    }
}

/// <summary>One input binding for a <see cref="SurfaceEffect"/>: a plane of a surface viewed as a texture.</summary>
/// <param name="Surface">The input surface.</param>
/// <param name="Format">The pixel format to view the plane as.</param>
/// <param name="Plane">The IOSurface plane index (0 for non-planar surfaces; 0/1 for NV12-family).</param>
public readonly record struct SurfaceInput(IOSurface Surface, MetalPixelFormat Format, int Plane = 0);

/// <summary>The Metal pixel formats a <see cref="SurfaceEffect"/> input plane may be viewed as.</summary>
public enum MetalPixelFormat
{
    /// <summary>Single 8-bit channel (e.g. an NV12 luma plane). <c>MTLPixelFormatR8Unorm</c>.</summary>
    R8Unorm = 10,

    /// <summary>Two 8-bit channels (e.g. an NV12 CbCr plane). <c>MTLPixelFormatRG8Unorm</c>.</summary>
    Rg8Unorm = 30,

    /// <summary>8-bit BGRA. <c>MTLPixelFormatBGRA8Unorm</c>.</summary>
    Bgra8Unorm = 80,
}
