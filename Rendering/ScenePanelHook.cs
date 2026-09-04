using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using ScreenShare.Plugin.Services;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.Mathematics.Interop;
using D3DBuffer = SharpDX.Direct3D11.Buffer;
using D3DPixelShader = SharpDX.Direct3D11.PixelShader;
using D3DVertexShader = SharpDX.Direct3D11.VertexShader;
using Device = SharpDX.Direct3D11.Device;

namespace ScreenShare.Plugin.Rendering;

/// <summary>
/// Draws the world panel inside the game's own render pass instead of on top of it, so the game's
/// HUD covers the screen rather than the other way round.
///
/// The ImGui path (<see cref="WorldPanelDispatch"/>) can never do this: Dalamud composites ImGui
/// after the game has finished drawing its 2D UI, so anything drawn there is necessarily on top of
/// the hotbars. Getting underneath them means drawing earlier in the frame, which means hooking the
/// game's renderer - there is no other seam.
///
/// The hook is on ImmediateContext.DoSetTargetCommand, which fires every time the game switches
/// render target. Once the target is the swapchain back buffer, the 3D scene has been composited
/// and the UI has not yet been drawn: that is the window we want. WHICH of those switches is the
/// right one isn't knowable from reading code - it depends on the game's pass structure - so the
/// occurrence index is a setting with a live per-frame count next to it, dialled in against a
/// running game rather than guessed. Measured as 4 on patch-current retail; the debug-only tuner
/// stays so it can be found again if a patch reshapes the passes.
///
/// This is the most fragile thing in the plugin by a wide margin. It runs inside the game's own
/// render pipeline on reverse-engineered structures, so it is off by default, every failure
/// disables it rather than retrying, and the ImGui path stays intact as the fallback.
/// </summary>
public sealed unsafe class ScenePanelHook : IDisposable
{
    private delegate void DoSetTargetCommandDelegate(ImmediateContext* context, RenderCommandSetTarget* command);

    private readonly WorldPanelRenderer renderer;
    private readonly DeviceContext context;
    private readonly Hook<DoSetTargetCommandDelegate>? hook;

    private WorldPanelDraw pending;
    private bool hasPending;
    private int backBufferSwitches;
    private bool loggedFailure;

    /// <summary>True once the hook is installed and drawing can actually happen here. False means
    /// the caller should fall back to the ImGui path rather than silently not drawing.</summary>
    public bool IsActive => hook is { IsEnabled: true };

    /// <summary>How many times the game switched to the back buffer during the last completed
    /// frame. This is what makes <see cref="DrawAtOccurrence"/> tunable instead of guesswork - if
    /// it reads 2, then only 0 and 1 are meaningful choices.</summary>
    public int LastFrameBackBufferSwitches { get; private set; }

    /// <summary>Which back-buffer switch to draw after, zero-based.</summary>
    public int DrawAtOccurrence { get; set; }

    public ScenePanelHook(Device gameDevice)
    {
        renderer = new WorldPanelRenderer(gameDevice);
        context = gameDevice.ImmediateContext;

        try
        {
            hook = ServiceCollector.GameInterop.HookFromAddress<DoSetTargetCommandDelegate>(
                (nint)ImmediateContext.MemberFunctionPointers.DoSetTargetCommand,
                DoSetTargetCommandDetour);
            hook.Enable();
        }
        catch (Exception ex)
        {
            ServiceCollector.PluginLog.Error(ex, "ScreenShare could not hook the render pipeline; the in-world screen will draw over the game UI instead.");
            hook = null;
        }
    }

    /// <summary>Queues the panel for the current frame. Unlike the ImGui path this doesn't schedule
    /// anything - the draw happens whenever the game next switches to the back buffer, which may
    /// already have happened this frame, so a queued panel simply waits for the next one.</summary>
    public void Submit(in WorldPanelDraw draw)
    {
        pending = draw;
        hasPending = true;
    }

    /// <summary>Call once per frame from the render thread. Rolls the per-frame counter over and
    /// clears any panel that was queued but never drawn, so a stale frame's geometry can't appear
    /// a frame late if the game's pass structure changes (a cutscene, a loading screen).</summary>
    public void EndFrame()
    {
        LastFrameBackBufferSwitches = backBufferSwitches;
        backBufferSwitches = 0;
        hasPending = false;
    }

    private void DoSetTargetCommandDetour(ImmediateContext* immediateContext, RenderCommandSetTarget* command)
    {
        // Original first: it performs the actual target switch, so the back buffer is only bound
        // and ready to be drawn into once it has run.
        hook!.Original(immediateContext, command);

        try
        {
            if (!IsBackBufferTarget(command))
                return;

            // Counted unconditionally, even when there's nothing to draw - the count is what the
            // occurrence setting is dialled against, and it would be useless if it only advanced
            // on frames that happened to have a panel queued.
            int occurrence = backBufferSwitches++;
            if (!hasPending || occurrence != DrawAtOccurrence)
                return;

            DrawWithStateRestored();
            hasPending = false;
        }
        catch (Exception ex)
        {
            // Inside the game's render pipeline, across a native boundary: an escaping exception
            // takes the process down. Disable rather than fail every frame for the rest of the
            // session - the caller falls back to the ImGui path once IsActive goes false.
            if (!loggedFailure)
            {
                loggedFailure = true;
                ServiceCollector.PluginLog.Error(ex, "ScreenShare in-scene panel draw failed; disabling the render hook.");
            }

            try
            {
                hook?.Disable();
            }
            catch (Exception disableEx)
            {
                ServiceCollector.PluginLog.Error(disableEx, "ScreenShare could not disable the render hook after a draw failure.");
            }
        }
    }

    /// <summary>Whether this target switch is binding the swapchain back buffer - the point at
    /// which the 3D scene is composited and the UI has not yet been drawn over it.</summary>
    private static bool IsBackBufferTarget(RenderCommandSetTarget* command)
    {
        if (command->RenderTargetCount == 0)
            return false;

        RenderTargetManager* targets = RenderTargetManager.Instance();
        if (targets is null)
            return false;

        Texture* backBuffer = targets->SwapChainBackBuffer;
        return backBuffer is not null && command->RenderTargets[0] == backBuffer;
    }

    /// <summary>
    /// Draws the panel and puts every piece of pipeline state back exactly as it was.
    ///
    /// Far more thorough than the ImGui path's equivalent, and it has to be: there we were
    /// borrowing a renderer that rebuilds most of its own state per draw command, whereas here we
    /// are interrupting the game mid-frame and it is entitled to assume nothing changed underneath
    /// it. Depth-stencil state matters most - the game may well have depth writes enabled, and a
    /// panel that wrote depth would corrupt everything drawn after it.
    /// </summary>
    private void DrawWithStateRestored()
    {
        RawViewportF[] viewports = context.Rasterizer.GetViewports<RawViewportF>();
        RawRectangle[] scissors = context.Rasterizer.GetScissorRectangles<RawRectangle>();
        RasterizerState? rasterizerState = context.Rasterizer.State;
        BlendState? blendState = context.OutputMerger.GetBlendState(out RawColor4 blendFactor, out int sampleMask);
        DepthStencilState? depthStencilState = context.OutputMerger.GetDepthStencilState(out int stencilRef);
        InputLayout? inputLayout = context.InputAssembler.InputLayout;
        PrimitiveTopology topology = context.InputAssembler.PrimitiveTopology;
        D3DVertexShader? vertexShader = context.VertexShader.Get();
        D3DBuffer[] vertexConstantBuffers = context.VertexShader.GetConstantBuffers(0, 1);
        D3DPixelShader? pixelShader = context.PixelShader.Get();
        D3DBuffer[] pixelConstantBuffers = context.PixelShader.GetConstantBuffers(0, 1);
        ShaderResourceView[] pixelResources = context.PixelShader.GetShaderResources(0, 2);
        SamplerState[] pixelSamplers = context.PixelShader.GetSamplers(0, 2);

        try
        {
            renderer.DrawInScene(context, in pending);
        }
        finally
        {
            if (viewports.Length > 0)
                context.Rasterizer.SetViewports(viewports);

            if (scissors.Length > 0)
            {
                RawRectangle previous = scissors[0];
                context.Rasterizer.SetScissorRectangle(previous.Left, previous.Top, previous.Right, previous.Bottom);
            }

            context.Rasterizer.State = rasterizerState;
            context.OutputMerger.SetBlendState(blendState, blendFactor, sampleMask);
            context.OutputMerger.SetDepthStencilState(depthStencilState, stencilRef);
            context.InputAssembler.InputLayout = inputLayout;
            context.InputAssembler.PrimitiveTopology = topology;
            context.VertexShader.Set(vertexShader!);
            context.VertexShader.SetConstantBuffers(0, vertexConstantBuffers);
            context.PixelShader.Set(pixelShader!);
            context.PixelShader.SetConstantBuffers(0, pixelConstantBuffers);
            context.PixelShader.SetShaderResources(0, pixelResources);
            context.PixelShader.SetSamplers(0, pixelSamplers);

            rasterizerState?.Dispose();
            blendState?.Dispose();
            depthStencilState?.Dispose();
            inputLayout?.Dispose();
            vertexShader?.Dispose();
            pixelShader?.Dispose();
            DisposeAll(vertexConstantBuffers);
            DisposeAll(pixelConstantBuffers);
            DisposeAll(pixelResources);
            DisposeAll(pixelSamplers);
        }
    }

    private static void DisposeAll<T>(T[] resources)
        where T : IDisposable?
    {
        foreach (T resource in resources)
            resource?.Dispose();
    }

    public void Dispose()
    {
        hook?.Disable();
        hook?.Dispose();
        renderer.Dispose();
    }
}
