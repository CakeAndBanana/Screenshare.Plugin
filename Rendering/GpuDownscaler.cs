using SharpDX.D3DCompiler;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using Device = SharpDX.Direct3D11.Device;

namespace ScreenShare.Plugin.Rendering;

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

    public void Blit(DeviceContext context, ShaderResourceView source, RenderTargetView destination, int destWidth, int destHeight) =>
        BlitToViewport(context, source, destination, 0, 0, destWidth, destHeight);

    public void BlitToViewport(
        DeviceContext context, ShaderResourceView source, RenderTargetView destination,
        int x, int y, int width, int height)
    {
        context.OutputMerger.SetRenderTargets(destination);
        context.Rasterizer.SetViewport(x, y, width, height);
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
