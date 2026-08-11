using SharpDX.D3DCompiler;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using Device = SharpDX.Direct3D11.Device;

namespace ScreenShare.Plugin.Rendering;

/// <summary>
/// Downscales a texture entirely on the GPU (a full-screen triangle sampled with bilinear
/// filtering into a smaller render target) instead of reading the full source resolution back to
/// the CPU and letting ffmpeg's software scaler shrink it afterward.
///
/// This exists because AsyncTextureReadback's CPU transfer cost scales with the SOURCE
/// (native screen) resolution regardless of the chosen encode resolution - confirmed as the
/// likely cause of a skip-rate cliff under real streaming load (a ~47%-then-3% swing from
/// triple-buffering alone wasn't enough once a 30fps target was requested, with skip rates
/// reported near 90%). Shrinking on the GPU first means the CPU-bound readback only ever has to
/// move the much smaller encode-resolution frame, which is the actual fix rather than a bigger
/// buffer margin working around the same underlying cost.
///
/// No vertex/index buffer is needed - the classic "full-screen triangle from SV_VertexID alone"
/// trick covers the whole viewport with a single triangle larger than the screen, clipped by the
/// rasterizer, which is cheaper than a quad (2 triangles, shared edge) for this exact purpose.
/// </summary>
public sealed class GpuDownscaler : IDisposable
{
    private const string ShaderSource = """
        struct VSOutput
        {
            float4 pos : SV_POSITION;
            float2 uv : TEXCOORD0;
        };

        VSOutput VSMain(uint id : SV_VertexID)
        {
            VSOutput o;
            o.uv = float2((id << 1) & 2, id & 2);
            o.pos = float4(o.uv * 2.0f - 1.0f, 0.0f, 1.0f);
            o.pos.y = -o.pos.y;
            return o;
        }

        Texture2D SourceTexture : register(t0);
        SamplerState LinearSampler : register(s0);

        float4 PSMain(VSOutput input) : SV_TARGET
        {
            return SourceTexture.Sample(LinearSampler, input.uv);
        }
        """;

    private readonly VertexShader vertexShader;
    private readonly PixelShader pixelShader;
    private readonly SamplerState sampler;

    public GpuDownscaler(Device device)
    {
        using CompilationResult vsBytecode = ShaderBytecode.Compile(ShaderSource, "VSMain", "vs_4_0");
        using CompilationResult psBytecode = ShaderBytecode.Compile(ShaderSource, "PSMain", "ps_4_0");

        vertexShader = new VertexShader(device, vsBytecode);
        pixelShader = new PixelShader(device, psBytecode);
        sampler = new SamplerState(device, SamplerStateDescription.Default());
    }

    /// <summary>Draws <paramref name="source"/> into <paramref name="destination"/> at the
    /// destination's own size. Caller must hold the device's ImmediateContextLock - this issues
    /// ordinary draw calls against the shared ImmediateContext just like any other GPU work here.</summary>
    public void Blit(DeviceContext context, ShaderResourceView source, RenderTargetView destination, int destWidth, int destHeight)
    {
        context.OutputMerger.SetRenderTargets(destination);
        context.Rasterizer.SetViewport(0, 0, destWidth, destHeight);
        context.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
        context.VertexShader.Set(vertexShader);
        context.PixelShader.Set(pixelShader);
        context.PixelShader.SetShaderResource(0, source);
        context.PixelShader.SetSampler(0, sampler);
        context.Draw(3, 0);

        // Unbind - the source stays alive across ticks (it wraps the capture's persistent
        // texture) and must not be left bound as a PS input while the capture thread's own
        // CopyResource touches it under the same shared ImmediateContext.
        context.PixelShader.SetShaderResource(0, null);
        context.OutputMerger.SetRenderTargets((RenderTargetView)null!);
    }

    public void Dispose()
    {
        vertexShader.Dispose();
        pixelShader.Dispose();
        sampler.Dispose();
    }
}
