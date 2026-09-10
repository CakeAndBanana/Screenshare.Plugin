using Dalamud.Bindings.ImGui;
using ScreenShare.Plugin.Services;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.Mathematics.Interop;
using D3DBuffer = SharpDX.Direct3D11.Buffer;
using Device = SharpDX.Direct3D11.Device;

namespace ScreenShare.Plugin.Rendering;

public sealed class WorldPanelDispatch : IDisposable
{
    private readonly WorldPanelRenderer renderer;
    private readonly DeviceContext context;
    private readonly ImDrawCallback callback;

    private WorldPanelDraw pending;
    private bool loggedDrawFailure;

    public unsafe WorldPanelDispatch(Device gameDevice)
    {
        renderer = new WorldPanelRenderer(gameDevice);
        context = gameDevice.ImmediateContext;
        callback = OnRenderCallback;
    }

    public unsafe void Submit(in WorldPanelDraw draw)
    {
        pending = draw;
        ImGui.GetBackgroundDrawList().AddCallback(callback, null);
    }

    private unsafe void OnRenderCallback(ImDrawList* parentList, ImDrawCmd* cmd)
    {
        try
        {
            DrawWithStateRestored();
        }
        catch (Exception ex)
        {
            if (!loggedDrawFailure)
            {
                loggedDrawFailure = true;
                ServiceCollector.PluginLog.Error(ex, "ScreenShare in-world screen draw failed; it will keep being skipped.");
            }
        }
    }

    private void DrawWithStateRestored()
    {
        InputLayout? inputLayout = context.InputAssembler.InputLayout;
        PrimitiveTopology topology = context.InputAssembler.PrimitiveTopology;
        VertexShader? vertexShader = context.VertexShader.Get();
        D3DBuffer[] vertexConstantBuffers = context.VertexShader.GetConstantBuffers(0, 1);
        PixelShader? pixelShader = context.PixelShader.Get();
        D3DBuffer[] pixelConstantBuffers = context.PixelShader.GetConstantBuffers(0, 1);
        SamplerState[] pixelSamplers = context.PixelShader.GetSamplers(0, 2);
        RawRectangle[] scissors = context.Rasterizer.GetScissorRectangles<RawRectangle>();

        try
        {
            renderer.Draw(context, in pending);
        }
        finally
        {
            context.InputAssembler.InputLayout = inputLayout;
            context.InputAssembler.PrimitiveTopology = topology;
            context.VertexShader.Set(vertexShader);
            context.VertexShader.SetConstantBuffers(0, vertexConstantBuffers);
            context.PixelShader.Set(pixelShader);
            context.PixelShader.SetConstantBuffers(0, pixelConstantBuffers);
            context.PixelShader.SetSamplers(0, pixelSamplers);

            if (scissors.Length > 0)
            {
                RawRectangle previous = scissors[0];
                context.Rasterizer.SetScissorRectangle(previous.Left, previous.Top, previous.Right, previous.Bottom);
            }

            inputLayout?.Dispose();
            vertexShader?.Dispose();
            pixelShader?.Dispose();
            DisposeAll(vertexConstantBuffers);
            DisposeAll(pixelConstantBuffers);
            DisposeAll(pixelSamplers);
        }
    }

    private static void DisposeAll<T>(T[] resources)
        where T : IDisposable?
    {
        foreach (T resource in resources)
            resource?.Dispose();
    }

    public void Dispose() => renderer.Dispose();
}
