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

public sealed unsafe class ScenePanelHook : IDisposable
{
    private const int MissedFramesBeforeEscalating = 30;

    private const long StaleAfterMs = 200;

    private delegate void DoSetTargetCommandDelegate(ImmediateContext* context, RenderCommandSetTarget* command);

    private readonly WorldPanelRenderer renderer;
    private readonly DeviceContext context;
    private readonly Hook<DoSetTargetCommandDelegate>? hook;

    private WorldPanelDraw pending;
    private bool hasPending;
    private bool submittedThisFrame;

    private long pendingAtMs;
    private int backBufferSwitches;
    private bool loggedFailure;

    // Automatic escalation to every-pass drawing. A correctly chosen pass essentially never misses,
    // so repeated misses are evidence the choice is wrong on this machine rather than a transient.
    private int missedFrames;
    private bool escalatedToEveryPass;

    private int previousSwitches = -1;

    public bool IsActive => hook is { IsEnabled: true };

    public int LastFrameBackBufferSwitches { get; private set; }

    public bool IsDrawingEveryPass => escalatedToEveryPass || DrawAtOccurrence < 0;

    public int MissedFrames => missedFrames;

    public string? EscalationReason { get; private set; }

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

    public void Submit(in WorldPanelDraw draw)
    {
        pending = draw;
        hasPending = true;
        submittedThisFrame = true;
        pendingAtMs = Environment.TickCount64;
    }

    private bool PendingIsStale => Environment.TickCount64 - pendingAtMs > StaleAfterMs;

    public void EndFrame()
    {
        // Resuming after the submitting side stopped - the player un-hid the game UI, most likely.
        // Everything accumulated in between is meaningless: the back-buffer count has been adding
        // up across however many frames went by, and comparing it to anything would read as a wild
        // change in the game's pass structure and escalate for no reason. Start clean instead.
        if (PendingIsStale)
        {
            backBufferSwitches = 0;
            previousSwitches = -1;
            hasPending = false;
            submittedThisFrame = false;
            return;
        }

        // A panel still queued at the frame boundary was never drawn: the pass it was waiting for
        // did not come up. That is the measurable form of the flicker - the panel is simply absent
        // from those frames - so it is counted rather than inferred from what it looks like.
        if (hasPending && !escalatedToEveryPass && DrawAtOccurrence >= 0 && ++missedFrames >= MissedFramesBeforeEscalating)
        {
            escalatedToEveryPass = true;
            EscalationReason = $"render pass {DrawAtOccurrence} never came up on {missedFrames} frames";
            ServiceCollector.PluginLog.Information(
                $"ScreenShare in-world screen: {EscalationReason}, switching to drawing in every pass.");
        }

        CheckPassCountStability();

        LastFrameBackBufferSwitches = backBufferSwitches;
        backBufferSwitches = 0;
        hasPending = false;
        submittedThisFrame = false;
    }

    private void CheckPassCountStability()
    {
        if (escalatedToEveryPass || DrawAtOccurrence < 0)
            return;

        if (!submittedThisFrame || backBufferSwitches == 0)
        {
            previousSwitches = -1;
            return;
        }

        if (previousSwitches > 0 && backBufferSwitches != previousSwitches)
        {
            escalatedToEveryPass = true;
            EscalationReason =
                $"the game's back-buffer pass count changed ({previousSwitches} to {backBufferSwitches}), " +
                $"so pass {DrawAtOccurrence} can't be relied on";
            ServiceCollector.PluginLog.Information(
                $"ScreenShare in-world screen: {EscalationReason}; switching to drawing in every pass.");
        }

        previousSwitches = backBufferSwitches;
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
            if (!hasPending)
                return;

            // The submitting side has stopped and this geometry is from whenever it stopped. Drawing
            // it would pin a stale view-projection to the screen; EndFrame clears the rest of the
            // state when it next runs.
            if (PendingIsStale)
                return;

            if (!IsDrawingEveryPass)
            {
                if (occurrence != DrawAtOccurrence)
                    return;

                DrawWithStateRestored();

                // Fixed mode draws exactly once per frame, so the panel is consumed here.
                hasPending = false;
                return;
            }

            // Every-pass: draw in this one and every later one this frame. Deliberately does NOT
            // clear hasPending - EndFrame does that at the frame boundary.
            DrawWithStateRestored();
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
