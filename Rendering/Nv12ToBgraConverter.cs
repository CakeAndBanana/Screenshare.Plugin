using SharpDX.D3DCompiler;
using SharpDX.DXGI;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using Device = SharpDX.Direct3D11.Device;

namespace ScreenShare.Plugin.Rendering;

public sealed class Nv12ToBgraConverter : IDisposable
{
    // BT.709 studio range, inverted from the encode side's matrix so the two round-trip. The
    // offsets come off first (16/255 luma, 128/255 chroma), then the standard BT.709 YCbCr-to-RGB
    // matrix scaled by 255/219 and 255/224 for the studio-range compression.
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

        Texture2D<float>  LumaTexture   : register(t0);
        Texture2D<float2> ChromaTexture : register(t1);
        SamplerState LinearSampler : register(s0);

        float4 PSMain(VSOutput input) : SV_TARGET
        {
            float  y  = LumaTexture.Sample(LinearSampler, input.uv);
            float2 cbcr = ChromaTexture.Sample(LinearSampler, input.uv);

            float yn = (y - 0.0627f) * 1.1644f;
            float cb = cbcr.x - 0.5020f;
            float cr = cbcr.y - 0.5020f;

            float3 rgb = float3(
                yn + 1.7927f * cr,
                yn - 0.2132f * cb - 0.5329f * cr,
                yn + 2.1124f * cb);

            return float4(saturate(rgb), 1.0f);
        }
        """;

    private readonly VertexShader vertexShader;
    private readonly PixelShader pixelShader;
    private readonly SamplerState sampler;

    public Nv12ToBgraConverter(Device device)
    {
        using CompilationResult vsBytecode = ShaderBytecode.Compile(ShaderSource, "VSMain", "vs_4_0");
        using CompilationResult psBytecode = ShaderBytecode.Compile(ShaderSource, "PSMain", "ps_4_0");

        vertexShader = new VertexShader(device, vsBytecode);
        pixelShader = new PixelShader(device, psBytecode);

        sampler = new SamplerState(device, new SamplerStateDescription
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp,
            ComparisonFunction = Comparison.Never,
            MinimumLod = 0f,
            MaximumLod = float.MaxValue,
        });
    }

    public static (ShaderResourceView Luma, ShaderResourceView Chroma) CreateViews(Device device, Texture2D nv12)
    {
        var luma = new ShaderResourceView(device, nv12, new ShaderResourceViewDescription
        {
            Format = Format.R8_UNorm,
            Dimension = SharpDX.Direct3D.ShaderResourceViewDimension.Texture2D,
            Texture2D = { MipLevels = 1, MostDetailedMip = 0 },
        });

        try
        {
            var chroma = new ShaderResourceView(device, nv12, new ShaderResourceViewDescription
            {
                Format = Format.R8G8_UNorm,
                Dimension = SharpDX.Direct3D.ShaderResourceViewDimension.Texture2D,
                Texture2D = { MipLevels = 1, MostDetailedMip = 0 },
            });

            return (luma, chroma);
        }
        catch
        {
            luma.Dispose();
            throw;
        }
    }

    public void Convert(
        DeviceContext context,
        ShaderResourceView luma,
        ShaderResourceView chroma,
        RenderTargetView destination,
        int width,
        int height)
    {
        context.OutputMerger.SetRenderTargets(destination);
        context.Rasterizer.SetViewport(0, 0, width, height);
        context.InputAssembler.InputLayout = null;
        context.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
        context.VertexShader.Set(vertexShader);
        context.PixelShader.Set(pixelShader);
        context.PixelShader.SetShaderResource(0, luma);
        context.PixelShader.SetShaderResource(1, chroma);
        context.PixelShader.SetSampler(0, sampler);
        context.Draw(3, 0);

        // Unbound for the same reason the other passes do it: these views wrap textures the decode
        // thread writes to under this same shared context, and leaving one bound as a pixel-shader
        // input while that happens is exactly the hazard D3D warns about.
        context.PixelShader.SetShaderResource(0, null);
        context.PixelShader.SetShaderResource(1, null);
        context.OutputMerger.SetRenderTargets((RenderTargetView)null!);
    }

    public void Dispose()
    {
        sampler.Dispose();
        pixelShader.Dispose();
        vertexShader.Dispose();
    }
}
