// SPDX-License-Identifier: MIT OR Apache-2.0

using Yuta.DebugRevival.Native;

namespace Yuta.DebugRevival.Features;

/// <summary>
///     The game's debug flag (g_FFXIsDebug), switched at runtime. The game
///     only allocates its debug line buffer at boot when the flag is already
///     set, and its debug pass writes through that buffer every frame once
///     the flag is on, so the buffer is created first (Sg_DbInitAll, which
///     only allocates while the flag is set). The developers' event list
///     (initEventList) is built as well, for the map list and the Map switch
///     panel. Facts: docs/research/field.md section 5.
///     <para>While on, the game behaves as the developers' build did: L1
///     walks through walls, L2 and L1+R1 change the speed, events never
///     refuse a map jump, a New Game starts at story flag 5, and outside a
///     battle L1+R1+L2+R2+X starts the battle picker's battle.</para>
/// </summary>
public sealed unsafe class DebugFlag {
    private bool _buffer_made;
    private bool _events_built;

    /// <summary>The flag as last read on the game thread.</summary>
    public volatile bool on;

    public void set(bool value, TweakPanels panels) => GameThread.post(value ? "debug flag on" : "debug flag off", () => {
        if (value) {
            Mem.set_u8(G.FFXIsDebug, 1);
            if (!_buffer_made) { Calls.Sg_DbInitAll.fnptr!(); _buffer_made = true; }
            ensure_event_list();
            GameThread.last_status = "debug flag on";
        } else {
            // The texture-animation panel only works with the flag; hide it first.
            if (panels.current_mode == 5) Calls.DebugMenuManager_setMode.fnptr!(*(nint*)Mem.at(G.DebugMenuManager), 10);
            Mem.set_u8(G.FFXIsDebug, 0);
            Mem.set_u8(G.L2ExecFlag, 0); // "L2 held" sticks otherwise
            GameThread.last_status = "debug flag off";
        }
        on = value;
    });

    /// <summary>Builds the developers' event list once (game thread). Safe late; idempotent.</summary>
    public void ensure_event_list() {
        if (_events_built) return;
        Calls.initEventList.fnptr!();
        _events_built = true;
    }

    /// <summary>Game thread, every frame.</summary>
    public void update() => on = Mem.u8(G.FFXIsDebug) != 0;
}
