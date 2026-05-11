using Sts2Mod.StateBridge.Contracts;

namespace Sts2Mod.StateBridge.Server;

internal static class ActionLogStore
{
    private static readonly object Gate = new();
    private const int Capacity = 100;
    private static readonly List<InferredAction> Log = [];

    public static void Add(InferredAction action)
    {
        lock (Gate)
        {
            Log.Add(action);
            if (Log.Count > Capacity)
            {
                Log.RemoveRange(0, Log.Count - Capacity);
            }
        }
    }

    public static IReadOnlyList<InferredAction> GetAll()
    {
        lock (Gate)
        {
            return [.. Log];
        }
    }

    public static IReadOnlyList<InferredAction> Clear()
    {
        lock (Gate)
        {
            var snapshot = (IReadOnlyList<InferredAction>)[.. Log];
            Log.Clear();
            return snapshot;
        }
    }
}
