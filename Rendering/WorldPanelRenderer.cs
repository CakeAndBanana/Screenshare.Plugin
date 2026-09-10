using System.Numerics;
using System.Runtime.InteropServices;
using SharpDX.D3DCompiler;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using Buffer = SharpDX.Direct3D11.Buffer;
using Device = SharpDX.Direct3D11.Device;

namespace ScreenShare.Plugin.Rendering;

public enum PanelDepthMode
{
    None,

    Occlude,

    ShowDepthBuffer,

    ShowDepthComparison,

    ShowOcclusionTest,
}

public readonly record struct WorldPanelDraw(
    Matrix4x4 ViewProjection,
    Vector3 Center,
    Vector3 RightHalf,
    Vector3 UpHalf,
    float Opacity,
    PanelDepthMode DepthMode,
    float DepthSign,
    Vector2 DisplaySize,
    nint VideoShaderResourceView,
    nint DepthShaderResourceView);

public sealed class WorldPanelRenderer : IDisposable
{
    private const string ShaderSource = """
        cbuffer PanelConstants : register(b0)
        {
            row_major float4x4 ViewProj;
            float4 Center;      // xyz = panel centre in world space
            float4 RightHalf;   // xyz = half-width axis
            float4 UpHalf;      // xyz = half-height axis
            float4 Params;      // x = opacity, y = depth mode, z = depth sign
            float4 Screen;      // xy = 1 / backbuffer size
        };

        struct VSOutput
        {
            float4 pos : SV_POSITION;
            float2 uv : TEXCOORD0;
        };

        VSOutput VSMain(uint id : SV_VertexID)
        {
            // Triangle strip over 4 vertices: (0,0) (1,0) (0,1) (1,1).
            float2 corner = float2(id & 1, (id >> 1) & 1);

            VSOutput o;
            float3 world = Center.xyz
                         + RightHalf.xyz * (corner.x * 2.0f - 1.0f)
                         + UpHalf.xyz * (corner.y * 2.0f - 1.0f);
            o.pos = mul(float4(world, 1.0f), ViewProj);

            // corner.y counts upward from the panel's bottom edge; texture v counts down from the
            // top row, so v is flipped or the stream renders upside down.
            //
            // u is flipped for handedness: the panel's +X axis points to the viewer's LEFT when
            // seen from the side it was placed to face, so an unflipped u put the picture's left
            // edge on the viewer's right and text came out mirrored. Corrected here rather than by
            // rotating the placement, because this fixes screens that are already placed - and it
            // gives the physically expected result, where the back of the screen shows the mirror
            // image, as the back of a real projection screen would.
            o.uv = float2(1.0f - corner.x, 1.0f - corner.y);
            return o;
        }

        Texture2D VideoTexture : register(t0);
        Texture2D SceneDepth : register(t1);
        SamplerState LinearSampler : register(s0);
        SamplerState PointSampler : register(s1);

        float4 PSMain(VSOutput input) : SV_TARGET
        {
            // SV_Position.xy is the pixel centre in viewport pixels, so this lands mid-texel with
            // no extra half-pixel correction. Sampling by normalized UV rather than loading a
            // texel is what lets the depth buffer be a different resolution than the backbuffer,
            // which it is whenever the 3D resolution scale is not 100%.
            float2 screenUv = input.pos.xy * Screen.xy;

            // No convention conversion: this fragment was projected with the GAME'S OWN projection
            // matrix, so its depth is already in the same space as the depth buffer, whatever that
            // space happens to be. An earlier version converted with 1 - z here, on the mistaken
            // belief that the two differed - which corrupted a value that had been correct.
            float ourZ = input.pos.z;

            if (Params.y > 3.5f)
            {
                float testScene = SceneDepth.SampleLevel(PointSampler, screenUv, 0).r;
                float keep = (testScene - ourZ) * Params.z;
                return keep < 0.0f ? float4(1.0f, 0.0f, 0.0f, 1.0f) : float4(0.0f, 1.0f, 0.0f, 1.0f);
            }

            if (Params.y > 2.5f)
            {
                // Scene depth in red, our own (converted) depth in green - see ShowDepthComparison.
                // Once the conventions match this reads yellow wherever the panel is close to the
                // geometry behind it, instead of flatly one colour or the other.
                float sceneRaw = SceneDepth.SampleLevel(PointSampler, screenUv, 0).r;
                return float4(sceneRaw, ourZ, 0.0f, 1.0f);
            }

            if (Params.y > 1.5f)
            {
                float raw = SceneDepth.SampleLevel(PointSampler, screenUv, 0).r;
                return float4(raw, raw, raw, 1.0f);
            }

            if (Params.y > 0.5f)
            {
                float sceneZ = SceneDepth.SampleLevel(PointSampler, screenUv, 0).r;

                // Params.z is +1 when nearer means a smaller depth value (standard Z) and -1 for
                // the reversed-Z arrangement, which flips which side of the comparison survives.
                clip((sceneZ - ourZ) * Params.z);
            }

            float4 color = VideoTexture.Sample(LinearSampler, input.uv);
            return float4(color.rgb, Params.x);
        }
        """;

    [StructLayout(LayoutKind.Sequential)]
    private struct PanelConstants
    {
        public Matrix4x4 ViewProjection;
        public Vector4 Center;
        public Vector4 RightHalf;
        public Vector4 UpHalf;
        public Vector4 Params;
        public Vector4 Screen;
    }

    private readonly VertexShader vertexShader;
    private readonly PixelShader pixelShader;
    private readonly SamplerState linearSampler;
    private readonly SamplerState pointSampler;
    private readonly Buffer constantBuffer;
    private readonly BlendState sceneBlendState;
    private readonly DepthStencilState sceneDepthStencilState;
    private readonly DepthStencilState sceneDepthWriteState;
    private readonly DepthStencilState sceneDepthWriteReversedState;
    private readonly RasterizerState sceneRasterizerState;
    private readonly BorrowedShaderResourceView video = new();
    private readonly BorrowedShaderResourceView depth = new();

    public WorldPanelRenderer(Device gameDevice)
    {
        using CompilationResult vsBytecode = ShaderBytecode.Compile(ShaderSource, "VSMain", "vs_4_0");
        using CompilationResult psBytecode = ShaderBytecode.Compile(ShaderSource, "PSMain", "ps_4_0");

        vertexShader = new VertexShader(gameDevice, vsBytecode);
        pixelShader = new PixelShader(gameDevice, psBytecode);

        linearSampler = new SamplerState(gameDevice, new SamplerStateDescription
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp,
            ComparisonFunction = Comparison.Never,
            MinimumLod = 0f,
            MaximumLod = float.MaxValue,
        });

        pointSampler = new SamplerState(gameDevice, new SamplerStateDescription
        {
            Filter = Filter.MinMagMipPoint,
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp,
            ComparisonFunction = Comparison.Never,
            MinimumLod = 0f,
            MaximumLod = float.MaxValue,
        });

        var blend = new BlendStateDescription();
        blend.RenderTarget[0] = new RenderTargetBlendDescription
        {
            IsBlendEnabled = true,
            SourceBlend = BlendOption.SourceAlpha,
            DestinationBlend = BlendOption.InverseSourceAlpha,
            BlendOperation = BlendOperation.Add,
            SourceAlphaBlend = BlendOption.One,
            DestinationAlphaBlend = BlendOption.InverseSourceAlpha,
            AlphaBlendOperation = BlendOperation.Add,
            RenderTargetWriteMask = ColorWriteMaskFlags.All,
        };
        sceneBlendState = new BlendState(gameDevice, blend);

        sceneDepthStencilState = new DepthStencilState(gameDevice, new DepthStencilStateDescription
        {
            IsDepthEnabled = false,
            DepthWriteMask = DepthWriteMask.Zero,
            DepthComparison = Comparison.Always,
            IsStencilEnabled = false,
        });

        sceneDepthWriteState = new DepthStencilState(gameDevice, new DepthStencilStateDescription
        {
            IsDepthEnabled = true,
            DepthWriteMask = DepthWriteMask.All,
            DepthComparison = Comparison.LessEqual,
            IsStencilEnabled = false,
        });

        sceneDepthWriteReversedState = new DepthStencilState(gameDevice, new DepthStencilStateDescription
        {
            IsDepthEnabled = true,
            DepthWriteMask = DepthWriteMask.All,
            DepthComparison = Comparison.GreaterEqual,
            IsStencilEnabled = false,
        });

        sceneRasterizerState = new RasterizerState(gameDevice, new RasterizerStateDescription
        {
            FillMode = FillMode.Solid,
            CullMode = CullMode.None,
            IsFrontCounterClockwise = false,
            DepthBias = 0,
            DepthBiasClamp = 0f,
            SlopeScaledDepthBias = 0f,
            IsDepthClipEnabled = true,
            IsScissorEnabled = false,
            IsMultisampleEnabled = false,
            IsAntialiasedLineEnabled = false,
        });

        constantBuffer = new Buffer(gameDevice, new BufferDescription
        {
            SizeInBytes = Marshal.SizeOf<PanelConstants>(),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ConstantBuffer,
            CpuAccessFlags = CpuAccessFlags.None,
            OptionFlags = ResourceOptionFlags.None,
            StructureByteStride = 0,
        });
    }

    public void Draw(DeviceContext context, in WorldPanelDraw draw) => DrawCore(context, in draw, setScissor: true);

    private void DrawCore(DeviceContext context, in WorldPanelDraw draw, bool setScissor)
    {
        var constants = new PanelConstants
        {
            ViewProjection = draw.ViewProjection,
            Center = new Vector4(draw.Center, 0f),
            RightHalf = new Vector4(draw.RightHalf, 0f),
            UpHalf = new Vector4(draw.UpHalf, 0f),
            Params = new Vector4(draw.Opacity, (float)draw.DepthMode, draw.DepthSign, 0f),
            Screen = new Vector4(
                draw.DisplaySize.X > 0f ? 1f / draw.DisplaySize.X : 0f,
                draw.DisplaySize.Y > 0f ? 1f / draw.DisplaySize.Y : 0f,
                0f,
                0f),
        };

        context.UpdateSubresource(ref constants, constantBuffer);

        if (setScissor)
        {
            context.Rasterizer.SetScissorRectangle(0, 0, (int)draw.DisplaySize.X, (int)draw.DisplaySize.Y);
        }

        context.InputAssembler.InputLayout = null;
        context.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleStrip;
        context.VertexShader.Set(vertexShader);
        context.VertexShader.SetConstantBuffer(0, constantBuffer);
        context.PixelShader.Set(pixelShader);
        context.PixelShader.SetConstantBuffer(0, constantBuffer);
        context.PixelShader.SetShaderResource(0, video.For(draw.VideoShaderResourceView));
        context.PixelShader.SetShaderResource(1, depth.For(draw.DepthShaderResourceView));
        context.PixelShader.SetSampler(0, linearSampler);
        context.PixelShader.SetSampler(1, pointSampler);

        context.Draw(4, 0);

        context.PixelShader.SetShaderResource(0, null);
        context.PixelShader.SetShaderResource(1, null);
    }

    public void DrawInScene(DeviceContext context, in WorldPanelDraw draw, bool writeDepth)
    {
        DepthStencilState depthState = writeDepth
            ? (draw.DepthSign < 0f ? sceneDepthWriteReversedState : sceneDepthWriteState)
            : sceneDepthStencilState;

        context.OutputMerger.SetBlendState(sceneBlendState, new SharpDX.Mathematics.Interop.RawColor4(0f, 0f, 0f, 0f), unchecked((int)0xFFFFFFFF));
        context.OutputMerger.SetDepthStencilState(depthState, 0);
        context.Rasterizer.State = sceneRasterizerState;
        context.Rasterizer.SetViewport(0f, 0f, draw.DisplaySize.X, draw.DisplaySize.Y);

        DrawCore(context, in draw, setScissor: false);
    }

    public void Dispose()
    {
        sceneRasterizerState.Dispose();
        sceneDepthStencilState.Dispose();
        sceneDepthWriteState.Dispose();
        sceneDepthWriteReversedState.Dispose();
        sceneBlendState.Dispose();
        video.Dispose();
        depth.Dispose();
        constantBuffer.Dispose();
        pointSampler.Dispose();
        linearSampler.Dispose();
        pixelShader.Dispose();
        vertexShader.Dispose();
    }

    private sealed class BorrowedShaderResourceView : IDisposable
    {
        private ShaderResourceView? view;
        private nint pointer;

        public ShaderResourceView? For(nint shaderResourceView)
        {
            if (shaderResourceView == pointer)
                return view;

            Release();

            if (shaderResourceView != nint.Zero)
            {
                Marshal.AddRef(shaderResourceView);
                view = new ShaderResourceView(shaderResourceView);
                pointer = shaderResourceView;
            }

            return view;
        }

        public void Dispose() => Release();

        private void Release()
        {
            view?.Dispose();
            view = null;
            pointer = nint.Zero;
        }
    }
}
