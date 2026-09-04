using Dalamud.Bindings.ImGui;
using ScreenShare.Plugin.Services;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.Mathematics.Interop;
using D3DBuffer = SharpDX.Direct3D11.Buffer;
using Device = SharpDX.Direct3D11.Device;

namespace ScreenShare.Plugin.Rendering;

/// <summary>
/// Gets <see cref="WorldPanelRenderer"/>'s draw to happen at the one moment in the frame when the
/// game's backbuffer is bound and the render thread is ours to borrow: inside ImGui's own render
/// pass, via a draw callback on the background draw list.
///
/// Background rather than foreground so the panel layers correctly - it is a thing in the world,
/// and every ImGui window (this plugin's included) belongs on top of it.
///
/// Pipeline state is saved and restored by hand rather than by asking ImGui to rebuild its own.
/// The usual way to do that is the ImDrawCallback_ResetRenderState sentinel, but Dalamud's
/// bindings expose ImDrawCallback as a managed delegate with no conversion from
/// ImDrawCallbackEnum.ResetRenderState, so the sentinel simply isn't reachable through the typed
/// API - confirmed against Dalamud 15, not assumed. What gets restored is exactly what ImGui's
/// DX11 backend sets once per frame in SetupRenderState and never re-sets per draw command:
/// input layout, topology, both shaders, their constant buffers, and the pixel sampler. The
/// scissor rectangle joins them because the panel has to override it (see WorldPanelRenderer).
/// Shader resources are the one exception - the backend rebinds slot 0 for every command and never
/// uses slot 1 - so leaving those unbound is both correct and cheaper than round-tripping them.
/// </summary>
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

    /// <summary>Queues the panel for this frame. Call from the render thread during
    /// UiBuilder.Draw; the actual GPU work happens later in the same frame, when ImGui renders.
    ///
    /// Exactly one panel exists per frame, which is why the parameters live in a single field
    /// rather than a per-callback allocation - a second concurrent panel would need real
    /// per-callback storage (ImGui's void* user data) instead.</summary>
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

    /// <summary>Entries come back null for any slot that had nothing bound, which is normal rather
    /// than exceptional - ImGui leaves most sampler and constant-buffer slots empty.</summary>
    private static void DisposeAll<T>(T[] resources)
        where T : IDisposable?
    {
        foreach (T resource in resources)
            resource?.Dispose();
    }

    public void Dispose() => renderer.Dispose();
}
