using SharpDX.D3DCompiler;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using Device = SharpDX.Direct3D11.Device;

namespace ScreenShare.Plugin.Rendering;

/// <summary>
/// Downscales a texture using GPU shaders. 
/// This class is designed to be used with Direct3D 11 and provides a simple interface for blitting a source texture to a destination render target with optional viewport specifications.
/// </summary>
public sealed class GpuDownscaler : IDisposable
{
    // Shader source code for the vertex and pixel shaders
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

    /// <summary>
    /// Initializes a new instance of the GpuDownscaler class, compiling the shader source code and creating the necessary Direct3D resources (vertex shader, pixel shader, and sampler state) for downscaling operations.
    /// </summary>
    /// <param name="device">The Direct3D11 device to use for creating the shader resources.</param>
    public GpuDownscaler(Device device)
    {
        // Compile the vertex and pixel shaders from the shader source code
        using CompilationResult vsBytecode = ShaderBytecode.Compile(ShaderSource, "VSMain", "vs_4_0");
        using CompilationResult psBytecode = ShaderBytecode.Compile(ShaderSource, "PSMain", "ps_4_0");

        // Create the vertex shader, pixel shader, and sampler state using the compiled bytecode
        vertexShader = new VertexShader(device, vsBytecode);
        pixelShader = new PixelShader(device, psBytecode);
        sampler = new SamplerState(device, SamplerStateDescription.Default());
    }

    /// <summary>
    /// Blits a source texture to a destination render target, optionally specifying the destination width and height.
    /// This method sets up the rendering pipeline, including the shaders and sampler state, and performs the draw call to copy the texture data.
    /// </summary>
    /// <param name="context">The device context to use for rendering.</param>
    /// <param name="source">The source shader resource view.</param>
    /// <param name="destination">The destination render target view.</param>
    /// <param name="destWidth">The width of the destination.</param>
    /// <param name="destHeight">The height of the destination.</param>
    public void Blit(DeviceContext context, ShaderResourceView source, RenderTargetView destination, int destWidth, int destHeight) =>
        BlitToViewport(context, source, destination, 0, 0, destWidth, destHeight);

    /// <summary>
    /// Blits a source texture to a destination render target within a specified viewport defined by x, y, width, and height.
    /// </summary>
    /// <param name="context">The device context to use for rendering.</param>
    /// <param name="source">The source shader resource view.</param>
    /// <param name="destination">The destination render target view.</param>
    /// <param name="x">The x-coordinate of the viewport.</param>
    /// <param name="y">The y-coordinate of the viewport.</param>
    /// <param name="width">The width of the viewport.</param>
    /// <param name="height">The height of the viewport.</param>
    public void BlitToViewport(
        DeviceContext context, ShaderResourceView source, RenderTargetView destination,
        int x, int y, int width, int height)
    {
        // Set the render target and viewport for the rendering operation
        context.OutputMerger.SetRenderTargets(destination);
        context.Rasterizer.SetViewport(x, y, width, height);
        context.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
        context.VertexShader.Set(vertexShader);
        context.PixelShader.Set(pixelShader);
        context.PixelShader.SetShaderResource(0, source);
        context.PixelShader.SetSampler(0, sampler);
        context.Draw(3, 0);

        // Unbind the shader resource and render target to avoid keeping references to them
        context.PixelShader.SetShaderResource(0, null);
        context.OutputMerger.SetRenderTargets((RenderTargetView)null!);
    }

    /// <summary>
    /// Disposes of the GpuDownscaler instance, releasing the Direct3D resources (vertex shader, pixel shader, and sampler state) to free up GPU memory.
    /// </summary>
    public void Dispose()
    {
        vertexShader.Dispose();
        pixelShader.Dispose();
        sampler.Dispose();
    }
}
