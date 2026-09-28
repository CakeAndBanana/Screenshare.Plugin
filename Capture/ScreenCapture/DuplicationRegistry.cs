using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace ScreenShare.Plugin.Capture.ScreenCapture;

/// <summary>
/// Duplication registry that manages the registration and unregistration of duplication instances, allowing for the stopping of all duplications when requested.
/// </summary>
internal static class DuplicationRegistry
{
    private const string SlotName = "ScreenShare.Plugin.LiveDuplications";

    /// <summary>
    /// Slots for registered duplication instances, stored in the current AppDomain's data.
    /// Each slot is identified by a unique Guid and contains a StrongBox<bool> indicating whether a stop has been requested for that duplication instance.
    /// </summary>
    private static ConcurrentDictionary<Guid, StrongBox<bool>> Slots
    {
        get
        {
            if (AppDomain.CurrentDomain.GetData(SlotName) is ConcurrentDictionary<Guid, StrongBox<bool>> existing)
                return existing;

            var created = new ConcurrentDictionary<Guid, StrongBox<bool>>();
            AppDomain.CurrentDomain.SetData(SlotName, created);
            return AppDomain.CurrentDomain.GetData(SlotName) as ConcurrentDictionary<Guid, StrongBox<bool>> ?? created;
        }
    }

    /// <summary>
    /// Requests all registered duplication instances to stop by setting their stop flags to true.
    /// </summary>
    /// <returns></returns>
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

    /// <summary>
    /// Registers a new duplication instance by creating a unique Guid and a StrongBox<bool> to track whether a stop has been requested for that instance.
    /// </summary>
    /// <returns></returns>
    internal static (Guid Id, StrongBox<bool> StopRequested) Register()
    {
        var id = Guid.NewGuid();
        var flag = new StrongBox<bool>(false);
        Slots[id] = flag;
        return (id, flag);
    }

    /// <summary>
    /// Unregisters a duplication instance by removing its entry from the registry based on the provided Guid.
    /// </summary>
    /// <param name="id">The Guid of the duplication instance to unregister.</param>
    internal static void Unregister(Guid id) => Slots.TryRemove(id, out _);
}
