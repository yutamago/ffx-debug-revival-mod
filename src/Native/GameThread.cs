// SPDX-License-Identifier: MIT

namespace Yuta.DebugRevival.Native;

/// <summary>
///     Actions the UI queues for the game thread, which runs the next update while the render thread draws.
///     Drained before each <c>Sg_MainLoop</c>, so the queue waits while the PC menu is open.
/// </summary>
public static class GameThread {
    private static readonly ConcurrentQueue<(string what, Action act)> _queue = new();

    public static void post(string what, Action act) => _queue.Enqueue((what, act));

    /// <summary>Runs the queued actions. Game thread only.</summary>
    public static void drain(FhLogger log) {
        while (_queue.TryDequeue(out var item)) {
            try {
                item.act();
            }
            catch (Exception e) {
                Status.failed($"{item.what} failed: {e.Message}");
                log.Error($"{item.what}: {e}");
            }
        }
    }
}

public enum Tone { Done, Refused, Failed }

public sealed record StatusLine(string Text, Tone Tone, long Stamp);

/// <summary>The outcome of the last action, for the window's status line.</summary>
public static class Status {
    private static volatile StatusLine? _line;

    public static StatusLine? line => _line;

    public static void done(string text)    => set(text, Tone.Done);
    public static void refused(string text) => set(text, Tone.Refused);
    public static void failed(string text)  => set(text, Tone.Failed);

    private static void set(string text, Tone tone) => _line = new StatusLine(text, tone, Environment.TickCount64);
}
