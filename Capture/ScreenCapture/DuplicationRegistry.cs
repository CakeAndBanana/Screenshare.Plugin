using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace ScreenShare.Plugin.Capture.ScreenCapture;

/// <summary>
/// Lets a freshly loaded plugin instance ask captures left behind by a PREVIOUS instance to stop.
///
/// Desktop Duplication allows one duplication per output per process, and the process here is the
/// game - it outlives every plugin reload. A capture whose Dispose never ran keeps a background
/// thread alive holding its duplication, and because that thread is a foreground-of-its-own
/// background thread inside the game, nothing ever ends it. Every later attempt on that monitor
/// then fails with E_INVALIDARG for the rest of the game session, which reads as "something is
/// already using it" with no indication that the something is us.
///
/// Measured, not theorised: after a session of reloads, BOTH outputs refused duplication, and a
/// brand-new independent D3D11 device in the same process refused too - so it was neither our
/// device nor the adapter nor exclusive fullscreen.
///
/// The registry lives in <see cref="AppDomain"/> data, which belongs to the default load context
/// and therefore survives the plugin's assembly being unloaded and reloaded. Only corelib types
/// cross that boundary safely, so the shared state is a ConcurrentDictionary of StrongBox&lt;bool&gt;
/// - flags an old capture loop polls - rather than any type of ours. Asking a thread to stop and
/// letting it release its own duplication is also far safer than reaching in and releasing a COM
/// pointer that some still-running loop believes it owns.
/// </summary>
internal static class DuplicationRegistry
{
    private const string SlotName = "ScreenShare.Plugin.LiveDuplications";

    private static ConcurrentDictionary<Guid, StrongBox<bool>> Slots
    {
        get
        {
            // Racy only in the sense that two instances could each build a dictionary; the loser's
            // is discarded by GetOrAdd-style re-read below, and plugin loads are not concurrent in
            // practice anyway.
            if (AppDomain.CurrentDomain.GetData(SlotName) is ConcurrentDictionary<Guid, StrongBox<bool>> existing)
                return existing;

            var created = new ConcurrentDictionary<Guid, StrongBox<bool>>();
            AppDomain.CurrentDomain.SetData(SlotName, created);
            return AppDomain.CurrentDomain.GetData(SlotName) as ConcurrentDictionary<Guid, StrongBox<bool>> ?? created;
        }
    }

    /// <summary>Signals every capture registered by any plugin instance - including earlier ones -
    /// to stop. Called before a new capture is created, so a stranded predecessor releases the
    /// monitor first rather than blocking it forever.
    ///
    /// Returns how many were asked to stop, which is worth logging: a non-zero count is direct
    /// evidence of a leaked capture rather than an external application holding the display.</summary>
    internal static int RequestStopAll()
    {
        int asked = 0;
        foreach (KeyValuePair<Guid, StrongBox<bool>> slot in Slots)
        {
            if (!slot.Value.Value)
                asked++;

            slot.Value.Value = true;
        }

        return asked;
    }

    /// <summary>Registers a capture and returns the flag its loop must poll. Removing the entry on
    /// clean disposal is what keeps the registry from growing across a long session.</summary>
    internal static (Guid Id, StrongBox<bool> StopRequested) Register()
    {
        var id = Guid.NewGuid();
        var flag = new StrongBox<bool>(false);
        Slots[id] = flag;
        return (id, flag);
    }

    internal static void Unregister(Guid id) => Slots.TryRemove(id, out _);
}
