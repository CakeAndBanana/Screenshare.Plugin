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

    // Missed frames tolerated before the hook stops insisting on the exact back-buffer pointer.
    //
    // Normally the game binds the very texture RenderTargetManager reports as the swap chain's back
    // buffer, and matching it by pointer is precise. A post-processing overlay that redirects the
    // final composite into a texture of its own breaks that: on the frames its passes run, the
    // pointer never appears, no pass matches, and the panel is simply absent - which is the
    // flickering reported with ReShade loaded, only for the in-world panel and never for the popout
    // (which draws through ImGui at present time and cannot be affected).
    //
    // Drawing in every pass does not help there, because the problem is not WHICH bind was chosen -
    // it is that no bind was recognised at all. So after enough misses the hook widens the test to
    // any target the same size as the back buffer.
    //
    // Deliberately gated on real misses rather than on detecting an overlay. Widening the test is a
    // genuine risk - a full-resolution intermediate target would then match, and the panel would be
    // drawn into something the game later overwrites or uses elsewhere - so it is only worth taking
    // for someone whose panel is already not being drawn.
    private const int MatchBySizeAfterMisses = 60;

    private delegate void DoSetTargetCommandDelegate(ImmediateContext* context, RenderCommandSetTarget* command);

    private readonly WorldPanelRenderer renderer;
    private readonly DeviceContext context;
    private readonly Hook<DoSetTargetCommandDelegate>? hook;

    private WorldPanelDraw pending;
    private bool hasPending;

    private long pendingAtMs;

    // Set once the panel has been missing often enough that pointer-matching the swap chain's back
    // buffer is clearly not finding it - see MatchBySizeAfterMisses.
    private bool matchTargetsBySize;
    private int backBufferSwitches;
    private int depthBoundSwitches;
    private bool loggedFailure;

    private int framesWithoutScenePass;

    // Per-frame, both reset in EndFrame: whether the 3D scene has been seen yet, and whether the
    // panel has already been drawn at the transition out of it.
    private bool sawScenePass;
    private bool drewThisFrame;

    // One-shot capture of a single frame's back-buffer binds, so the shape of the frame can be read
    // instead of inferred. Nameplates sit behind the panel, which means they are drawn inside the
    // 3D phase rather than with the rest of the interface - and drawing before them needs whatever
    // distinguishes their pass from the world's, which is not something guessing has found.
    private const int MaxRecordedPasses = 192;
    private readonly (bool Depth, int Width, int Height, int DepthWidth)[] recorded = new (bool, int, int, int)[MaxRecordedPasses];
    private int recordedCount;
    // Captured automatically, once, on the first frame after the hook installs. No button, because
    // it is not a thing anyone should have to know to press - it costs one log line per session and
    // is the difference between reading the frame's shape and guessing at it.
    private bool recordRequested = true;
    private bool recording;

    public bool IsActive => hook is { IsEnabled: true };

    public int LastFrameBackBufferSwitches { get; private set; }

    // How many of those also had a depth buffer bound. The gap between the two numbers is the
    // interface: the game binds the back buffer once per HUD element, and none of those bind depth.
    public int LastFrameSceneSwitches { get; private set; }

    // Set when the hook has had to widen how it recognises the back buffer, which is worth
    // surfacing because it means something unusual is going on with the renderer.
    public string? FallbackReason { get; private set; }

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

            // Logged at install rather than on failure: the panel being painted over by an
            // overlay's own pass looks like a missed pass from in here, so the two are worth being
            // able to tell apart in a log someone sends later.
            if (OverlayDetector.Detected is { } overlay)
            {
                ServiceCollector.PluginLog.Information(
                    $"ScreenShare: {overlay} is loaded. It adds its own render passes, which is a known " +
                    "cause of the in-world screen flickering while the popout window stays steady.");
            }
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
            depthBoundSwitches = 0;
            sawScenePass = false;
            drewThisFrame = false;
            hasPending = false;
            return;
        }

        // Queued but never drawn: the frame never showed a 3D-to-2D transition on the back buffer.
        // Counted because it is the only way this loop loses the panel on its own, and because
        // enough of them is what justifies the wider match below.
        if (hasPending && !drewThisFrame)
        {
            if (!matchTargetsBySize && ++framesWithoutScenePass >= MatchBySizeAfterMisses)
            {
                matchTargetsBySize = true;
                FallbackReason = "the swap chain's back buffer was never bound by pointer, so any " +
                                 "target of the same size is now accepted - something is redirecting " +
                                 "the final composite, which a post-processing overlay does";
                ServiceCollector.PluginLog.Information($"ScreenShare in-world screen: {FallbackReason}.");
            }
        }
        else
        {
            framesWithoutScenePass = 0;
        }

        if (recording)
        {
            recording = false;
            LogRecordedFrame();
        }
        else if (recordRequested)
        {
            // Started at a frame boundary rather than mid-frame, so the capture covers one whole
            // frame instead of whatever was left of the current one.
            recordRequested = false;
            recording = true;
            recordedCount = 0;
        }

        LastFrameBackBufferSwitches = backBufferSwitches;
        LastFrameSceneSwitches = depthBoundSwitches;
        backBufferSwitches = 0;
        depthBoundSwitches = 0;
        sawScenePass = false;
        drewThisFrame = false;
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

            backBufferSwitches++;

            if (recording && recordedCount < MaxRecordedPasses)
            {
                Texture* recordedTarget = command->RenderTargets[0];
                Texture* recordedDepth = command->DepthBuffer;
                recorded[recordedCount++] = (
                    recordedDepth is not null,
                    recordedTarget is null ? 0 : (int)recordedTarget->ActualWidth,
                    recordedTarget is null ? 0 : (int)recordedTarget->ActualHeight,
                    recordedDepth is null ? 0 : (int)recordedDepth->ActualWidth);
            }

            // A bound depth buffer means this is still the 3D scene; the interface is 2D and binds
            // none. The game binds the back buffer once per interface element, which is why that
            // count runs past a hundred while the scene uses a handful.
            // The FIRST pass of the 3D scene, drawn into as the world itself is about to be, with
            // depth written so everything the game draws afterwards can see the panel is there.
            //
            // Ordering alone could never finish this. Drawing last put the panel over the world but
            // also over the character nameplates, which are drawn during the 3D phase; drawing
            // earlier let the world paint over the panel. The panel has to be IN the scene, not
            // layered on top of it - so it goes in first and the game's depth test does the rest,
            // exactly as it does for any other solid object.
            if (command->DepthBuffer is null)
                return;

            depthBoundSwitches++;

            if (sawScenePass || drewThisFrame || !hasPending)
                return;

            sawScenePass = true;

            // The submitting side has stopped and this geometry is from whenever it stopped. Drawing
            // it would pin a stale view-projection to the screen; EndFrame clears the rest of the
            // state when it next runs.
            if (PendingIsStale)
                return;

            drewThisFrame = true;
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

    private void LogRecordedFrame()
    {
        var text = new System.Text.StringBuilder();
        text.Append($"ScreenShare frame structure: {recordedCount} back-buffer binds, ")
            .Append("'D' = depth bound (3D scene), '.' = no depth (interface). ");

        for (int i = 0; i < recordedCount; i++)
        {
            (bool depth, int width, int height, int depthWidth) = recorded[i];
            text.Append(i > 0 ? " " : string.Empty)
                .Append(i)
                .Append(depth ? ":D" : ":.")
                .Append(width).Append('x').Append(height);

            if (depth && depthWidth != width)
                text.Append("(depth ").Append(depthWidth).Append(')');
        }

        ServiceCollector.PluginLog.Information(text.ToString());
    }

    private bool IsBackBufferTarget(RenderCommandSetTarget* command)
    {
        if (command->RenderTargetCount == 0)
            return false;

        RenderTargetManager* targets = RenderTargetManager.Instance();
        if (targets is null)
            return false;

        Texture* backBuffer = targets->SwapChainBackBuffer;
        if (backBuffer is null)
            return false;

        Texture* target = command->RenderTargets[0];
        if (target == backBuffer)
            return true;

        // Only reached once the exact pointer has stopped turning up - see MatchBySizeAfterMisses.
        // Matching on size alone would otherwise catch full-resolution intermediates and draw the
        // panel into buffers the game uses for something else entirely.
        return matchTargetsBySize
            && target is not null
            && target->ActualWidth == backBuffer->ActualWidth
            && target->ActualHeight == backBuffer->ActualHeight;
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
            renderer.DrawInScene(context, in pending, writeDepth: true);
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
