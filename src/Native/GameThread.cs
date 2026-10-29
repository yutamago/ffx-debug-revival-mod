// SPDX-License-Identifier: MIT OR Apache-2.0

namespace Yuta.DebugRevival.Native;

/// <summary>
///     Work for the game thread. <c>render_imgui</c> runs on the render thread
///     while the game thread already runs the next update, so the UI never
///     calls game functions or writes game state itself: it queues an action,
///     and the queue is drained before each <c>Sg_MainLoop</c> (Fahrenheit's
///     PreUpdate). The queue waits while the PC menu is open, since the game
///     skips <c>Sg_MainLoop</c> then.
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

    public static int pending => _queue.Count;
}
