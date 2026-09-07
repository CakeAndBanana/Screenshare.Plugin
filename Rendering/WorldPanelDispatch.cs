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

    // Held in a field for the object's whole life, not built per call: the delegate has to outlive
    // every frame it was handed to ImGui for, and reusing one instance also reuses the single
    // native thunk the marshaller creates for it.
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
        // This runs on the far side of a native call boundary: an exception escaping here doesn't
        // become a caught .NET error, it takes the game's process with it. Nothing in the draw is
        // worth that, so everything is swallowed and reported once.
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
        // Every one of these getters hands back an AddRef'd COM object (or an array of them), so
        // each has to be released again below or the plugin leaks a reference per frame.
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

            // ImGui rebinds this before each of its own draws anyway, so restoring it is about not
            // surprising whatever else might run between here and then.
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
