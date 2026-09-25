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
    private const int MatchBySizeAfterMisses = 60;

    private delegate void DoSetTargetCommandDelegate(ImmediateContext* context, RenderCommandSetTarget* command);

    private readonly WorldPanelRenderer renderer;
    private readonly DeviceContext context;
    private readonly Hook<DoSetTargetCommandDelegate>? hook;

    private WorldPanelDraw pending;
    private bool hasPending;

    private long pendingAtMs;

    private bool matchTargetsBySize;
    private int backBufferSwitches;
    private int depthBoundSwitches;
    private bool loggedFailure;

    private int framesWithoutScenePass;
    private bool sawScenePass;
    private bool drewThisFrame;

    private const int MaxRecordedPasses = 192;
    private readonly (bool Depth, int Width, int Height, int DepthWidth)[] recorded = new (bool, int, int, int)[MaxRecordedPasses];
    private int recordedCount;
    private int drawnAtBind = -1;

    // Flicker reports come from machines we can't test on, so the log has to show what a bad frame
    // looked like: every change in the frame's back-buffer binds, and every frame the screen missed.
    private const int MaxLayoutLogs = 100;
    private const long LayoutLogIntervalMs = 1000;
    private int lastLayoutHash;
    private bool loggedAnyLayout;
    private long lastLayoutLogMs;
    private int layoutLogsWritten;
    private int layoutChangesSinceLog;
    private int missedFramesSinceLog;

    private int submitThreadId;
    private bool loggedThreads;

    public bool IsActive => hook is { IsEnabled: true };

    public int LastFrameBackBufferSwitches { get; private set; }
    public int LastFrameSceneSwitches { get; private set; }
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

            ServiceCollector.PluginLog.Information(
                $"ScreenShare graphics modules in the game process: {OverlayDetector.DescribeGraphicsModules()}.");
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
        submitThreadId = Environment.CurrentManagedThreadId;
    }

    private bool PendingIsStale => Environment.TickCount64 - pendingAtMs > StaleAfterMs;

    public void EndFrame()
    {
        if (PendingIsStale)
        {
            backBufferSwitches = 0;
            depthBoundSwitches = 0;
            sawScenePass = false;
            drewThisFrame = false;
            hasPending = false;
            recordedCount = 0;
            drawnAtBind = -1;
            return;
        }

        bool missed = hasPending && !drewThisFrame;
        LogLayoutIfChanged(missed);

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

        LastFrameBackBufferSwitches = backBufferSwitches;
        LastFrameSceneSwitches = depthBoundSwitches;
        backBufferSwitches = 0;
        depthBoundSwitches = 0;
        sawScenePass = false;
        drewThisFrame = false;
        hasPending = false;
        recordedCount = 0;
        drawnAtBind = -1;
    }

    private void DoSetTargetCommandDetour(ImmediateContext* immediateContext, RenderCommandSetTarget* command)
    {
        hook!.Original(immediateContext, command);

        try
        {
            if (!IsBackBufferTarget(command))
                return;

            backBufferSwitches++;

            if (recordedCount < MaxRecordedPasses)
            {
                Texture* recordedTarget = command->RenderTargets[0];
                Texture* recordedDepth = command->DepthBuffer;
                recorded[recordedCount++] = (
                    recordedDepth is not null,
                    recordedTarget is null ? 0 : (int)recordedTarget->ActualWidth,
                    recordedTarget is null ? 0 : (int)recordedTarget->ActualHeight,
                    recordedDepth is null ? 0 : (int)recordedDepth->ActualWidth);
            }

            if (command->DepthBuffer is null)
                return;

            depthBoundSwitches++;

            if (sawScenePass || drewThisFrame || !hasPending)
                return;

            sawScenePass = true;

            if (PendingIsStale)
                return;

            drewThisFrame = true;
            drawnAtBind = recordedCount - 1;
            LogThreadsOnce();
            DrawWithStateRestored();
        }
        catch (Exception ex)
        {
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

    private void LogThreadsOnce()
    {
        if (loggedThreads)
            return;

        loggedThreads = true;
        int renderThreadId = Environment.CurrentManagedThreadId;
        ServiceCollector.PluginLog.Information(
            $"ScreenShare in-world screen threads: queued on {submitThreadId}, drawn on {renderThreadId}" +
            (submitThreadId == renderThreadId ? " (same thread)." : " (different threads - frames can race)."));
    }

    private void LogLayoutIfChanged(bool missed)
    {
        if (!hasPending)
            return;

        var hash = default(HashCode);
        for (int i = 0; i < recordedCount; i++)
            hash.Add(recorded[i]);

        int layoutHash = hash.ToHashCode();
        bool changed = !loggedAnyLayout || layoutHash != lastLayoutHash;
        lastLayoutHash = layoutHash;

        if (changed)
            layoutChangesSinceLog++;

        if (missed)
            missedFramesSinceLog++;

        if (!changed && !missed)
            return;

        long now = Environment.TickCount64;
        if (layoutLogsWritten >= MaxLayoutLogs || (loggedAnyLayout && now - lastLayoutLogMs < LayoutLogIntervalMs))
            return;

        loggedAnyLayout = true;
        lastLayoutLogMs = now;
        layoutLogsWritten++;

        LogRecordedFrame(missed);

        layoutChangesSinceLog = 0;
        missedFramesSinceLog = 0;

        if (layoutLogsWritten == MaxLayoutLogs)
            ServiceCollector.PluginLog.Information("ScreenShare frame layout logging stopped after " + MaxLayoutLogs + " entries.");
    }

    private void LogRecordedFrame(bool missed)
    {
        var text = new System.Text.StringBuilder();
        text.Append($"ScreenShare frame layout: {recordedCount} back-buffer binds, ")
            .Append(missed ? "screen MISSED this frame" : $"screen drawn at bind {drawnAtBind}")
            .Append($"; since the previous entry {layoutChangesSinceLog} layout change(s), ")
            .Append($"{missedFramesSinceLog} missed frame(s). ")
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

        return matchTargetsBySize
            && target is not null
            && target->ActualWidth == backBuffer->ActualWidth
            && target->ActualHeight == backBuffer->ActualHeight;
    }

    private void DrawWithStateRestored()
    {
        RawViewportF[] viewports = context.Rasterizer.GetViewports<RawViewportF>();
        ScissorRects scissors = ScissorRects.Capture(context);
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

            scissors.Restore(context);

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
