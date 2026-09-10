using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace ScreenShare.Plugin.Capture.ScreenCapture;

internal static class DuplicationRegistry
{
    private const string SlotName = "ScreenShare.Plugin.LiveDuplications";

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

    internal static (Guid Id, StrongBox<bool> StopRequested) Register()
    {
        var id = Guid.NewGuid();
        var flag = new StrongBox<bool>(false);
        Slots[id] = flag;
        return (id, flag);
    }

    internal static void Unregister(Guid id) => Slots.TryRemove(id, out _);
}
