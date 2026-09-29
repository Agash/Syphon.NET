using Metal;

namespace Syphon.NET;

// Copies a region of a texture into another with its rows reversed, as the Syphon framework's Metal
// server does for a flipped frame. A blit cannot flip, so this is a compute kernel, compiled once per
// device from source.
internal static class FlipCopy
{
    private const string Source = """
        #include <metal_stdlib>
        using namespace metal;

        kernel void syphon_flip_copy(
            texture2d<half, access::read> source [[texture(0)]],
            texture2d<half, access::write> destination [[texture(1)]],
            constant uint4& region [[buffer(0)]],
            uint2 position [[thread_position_in_grid]])
        {
            if (position.x >= region.z || position.y >= region.w)
            {
                return;
            }

            uint2 from = uint2(region.x + position.x, region.y + region.w - 1 - position.y);
            destination.write(source.read(from), position);
        }
        """;

    private static readonly Lock s_gate = new();
    private static readonly Dictionary<nint, IMTLComputePipelineState> s_pipelines = [];

    public static unsafe void Encode(
        IMTLCommandBuffer commandBuffer,
        IMTLTexture source,
        MTLRegion region,
        IMTLTexture destination
    )
    {
        if (!source.Usage.HasFlag(MTLTextureUsage.ShaderRead))
        {
            throw new ArgumentException(
                "A flipped frame is copied by a shader, which needs a texture made with MTLTextureUsage.ShaderRead.",
                nameof(source)
            );
        }

        IMTLComputePipelineState pipeline = Pipeline(source.Device);
        IMTLComputeCommandEncoder encoder =
            commandBuffer.ComputeCommandEncoder
            ?? throw new SyphonException("The command buffer gave no compute encoder.");
        encoder.SetComputePipelineState(pipeline);
        encoder.SetTexture(source, 0);
        encoder.SetTexture(destination, 1);
        uint* bounds = stackalloc uint[4]
        {
            (uint)region.Origin.X,
            (uint)region.Origin.Y,
            (uint)region.Size.Width,
            (uint)region.Size.Height,
        };
        encoder.SetBytes((nint)bounds, 4 * sizeof(uint), 0);
        nint side = (nint)Math.Min(16, Math.Sqrt(pipeline.MaxTotalThreadsPerThreadgroup));
        MTLSize group = new(side, side, 1);
        MTLSize groups = new(
            (region.Size.Width + side - 1) / side,
            (region.Size.Height + side - 1) / side,
            1
        );
        encoder.DispatchThreadgroups(groups, group);
        encoder.EndEncoding();
    }

    private static IMTLComputePipelineState Pipeline(IMTLDevice device)
    {
        lock (s_gate)
        {
            if (s_pipelines.TryGetValue(device.Handle, out IMTLComputePipelineState? cached))
            {
                return cached;
            }

            using MTLCompileOptions options = new();
            IMTLLibrary library =
                device.CreateLibrary(Source, options, out NSError? error)
                ?? throw new SyphonException(
                    $"The flip shader did not compile: {error?.LocalizedDescription}"
                );
            using IMTLFunction function =
                library.CreateFunction("syphon_flip_copy")
                ?? throw new SyphonException("The flip shader has no kernel.");
            IMTLComputePipelineState pipeline =
                device.CreateComputePipelineState(function, out error)
                ?? throw new SyphonException(
                    $"The flip pipeline could not be created: {error?.LocalizedDescription}"
                );
            s_pipelines[device.Handle] = pipeline;
            return pipeline;
        }
    }
}
