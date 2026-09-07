using SharpDX.D3DCompiler;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Device = SharpDX.Direct3D11.Device;

namespace ScreenShare.Plugin.Rendering;

public sealed class Nv12Converter : IDisposable
{
    // BT.709, studio range. The coefficients are the usual full-range BT.709 matrix scaled by
    // 219/255 for luma and 224/255 for chroma, with the 16/255 and 128/255 offsets folded in - the
    // encoder is told the same convention (BT.709, 16-235) so the two cannot disagree.
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

        cbuffer Constants : register(b0)
        {
            float2 SourceTexel;   // 1 / source size, for the chroma taps
            float2 Padding;
        };

        float Luma(float3 rgb)
        {
            return dot(rgb, float3(0.1826f, 0.6142f, 0.0620f)) + 0.0627f;
        }

        float PSMainY(VSOutput input) : SV_TARGET
        {
            float3 rgb = SourceTexture.Sample(LinearSampler, input.uv).rgb;
            return Luma(rgb);
        }

        float2 PSMainUV(VSOutput input) : SV_TARGET
        {
            // Four taps averaged rather than one bilinear sample: the chroma plane is half
            // resolution, so each output texel covers a 2x2 block of source pixels and averaging
            // them is what subsampling is supposed to mean. A single sample would alias.
            float3 rgb =
                (SourceTexture.Sample(LinearSampler, input.uv + float2(-0.5f, -0.5f) * SourceTexel).rgb +
                 SourceTexture.Sample(LinearSampler, input.uv + float2( 0.5f, -0.5f) * SourceTexel).rgb +
                 SourceTexture.Sample(LinearSampler, input.uv + float2(-0.5f,  0.5f) * SourceTexel).rgb +
                 SourceTexture.Sample(LinearSampler, input.uv + float2( 0.5f,  0.5f) * SourceTexel).rgb) * 0.25f;

            float cb = dot(rgb, float3(-0.1007f, -0.3385f,  0.4392f)) + 0.5020f;
            float cr = dot(rgb, float3( 0.4392f, -0.3990f, -0.0402f)) + 0.5020f;
            return float2(cb, cr);
        }
        """;

    private readonly VertexShader vertexShader;
    private readonly PixelShader lumaShader;
    private readonly PixelShader chromaShader;
    private readonly SamplerState sampler;
    private readonly SharpDX.Direct3D11.Buffer constantBuffer;
    private readonly RenderTargetView lumaTarget;
    private readonly RenderTargetView chromaTarget;

    public Texture2D Target { get; }

    public int Width { get; }

    public int Height { get; }

    public Nv12Converter(Device device, int width, int height)
    {
        if (width % 2 != 0 || height % 2 != 0)
            throw new ArgumentException($"NV12 requires even dimensions, got {width}x{height}.");

        Width = width;
        Height = height;

        using CompilationResult vsBytecode = ShaderBytecode.Compile(ShaderSource, "VSMain", "vs_4_0");
        using CompilationResult lumaBytecode = ShaderBytecode.Compile(ShaderSource, "PSMainY", "ps_4_0");
        using CompilationResult chromaBytecode = ShaderBytecode.Compile(ShaderSource, "PSMainUV", "ps_4_0");

        vertexShader = new VertexShader(device, vsBytecode);
        lumaShader = new PixelShader(device, lumaBytecode);
        chromaShader = new PixelShader(device, chromaBytecode);

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

        constantBuffer = new SharpDX.Direct3D11.Buffer(device, new BufferDescription
        {
            SizeInBytes = 16,
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ConstantBuffer,
            CpuAccessFlags = CpuAccessFlags.None,
            OptionFlags = ResourceOptionFlags.None,
            StructureByteStride = 0,
        });

        Target = new Texture2D(device, new Texture2DDescription
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.NV12,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            CpuAccessFlags = CpuAccessFlags.None,
            OptionFlags = ResourceOptionFlags.None,
        });

        lumaTarget = new RenderTargetView(device, Target, new RenderTargetViewDescription
        {
            Format = Format.R8_UNorm,
            Dimension = RenderTargetViewDimension.Texture2D,
            Texture2D = { MipSlice = 0 },
        });

        chromaTarget = new RenderTargetView(device, Target, new RenderTargetViewDescription
        {
            Format = Format.R8G8_UNorm,
            Dimension = RenderTargetViewDimension.Texture2D,
            Texture2D = { MipSlice = 0 },
        });
    }

    public void Convert(DeviceContext context, ShaderResourceView source, int sourceWidth, int sourceHeight)
    {
        Span<float> constants =
        [
            sourceWidth > 0 ? 1f / sourceWidth : 0f,
            sourceHeight > 0 ? 1f / sourceHeight : 0f,
            0f,
            0f,
        ];

        context.UpdateSubresource(constants.ToArray(), constantBuffer);

        context.InputAssembler.InputLayout = null;
        context.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
        context.VertexShader.Set(vertexShader);
        context.PixelShader.SetShaderResource(0, source);
        context.PixelShader.SetSampler(0, sampler);
        context.PixelShader.SetConstantBuffer(0, constantBuffer);

        context.OutputMerger.SetRenderTargets(lumaTarget);
        context.Rasterizer.SetViewport(0, 0, Width, Height);
        context.PixelShader.Set(lumaShader);
        context.Draw(3, 0);

        context.OutputMerger.SetRenderTargets(chromaTarget);
        context.Rasterizer.SetViewport(0, 0, Width / 2, Height / 2);
        context.PixelShader.Set(chromaShader);
        context.Draw(3, 0);

        // Unbound for the same reason GpuDownscaler does it: the source wraps the capture's
        // persistent texture, which the capture thread keeps writing to under this same context.
        context.PixelShader.SetShaderResource(0, null);
        context.OutputMerger.SetRenderTargets((RenderTargetView)null!);
    }

    public void Dispose()
    {
        chromaTarget.Dispose();
        lumaTarget.Dispose();
        Target.Dispose();
        constantBuffer.Dispose();
        sampler.Dispose();
        chromaShader.Dispose();
        lumaShader.Dispose();
        vertexShader.Dispose();
    }
}
