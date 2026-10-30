// SPDX-License-Identifier: MIT OR Apache-2.0

namespace Yuta.DebugRevival.Native;

/// <summary>
///     Actions the UI queues for the game thread, which runs the next update while the render thread draws.
///     Drained before each <c>Sg_MainLoop</c>, so the queue waits while the PC menu is open.
/// </summary>
public static class GameThread {
    private static readonly ConcurrentQueue<(string what, Action act)> _queue = new();

    /// <summary>The last action's outcome, for the status bar.</summary>
    public static volatile string last_status = "";

    public static void post(string what, Action act) => _queue.Enqueue((what, act));

    /// <summary>Runs the queued actions. Game thread only.</summary>
    public static void drain(FhLogger log) {
        while (_queue.TryDequeue(out var item)) {
            try {
                item.act();
            }
            catch (Exception e) {
                last_status = $"{item.what}: failed ({e.Message})";
                log.Error($"{item.what}: {e}");
            }
        }
    }
}
