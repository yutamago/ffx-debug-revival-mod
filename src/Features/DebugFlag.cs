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

    /// <summary>
    ///     Keeps the field map's part selection capped while the flag is on.
    ///     With the flag, yiRectToOMD and yiRectToOMD_old pass "unlimited" to
    ///     yiRectToOMDBuild, which then ignores the packet and vertex caps and
    ///     writes past its heap buffers; the heap fails a moment later (the
    ///     crash seen when the flag went on). Both wrappers are replaced by
    ///     the release behaviour: the same call with unlimited = 0.
    /// </summary>
    public bool init(FhModule owner) =>
        Calls.yiRectToOMD.hook(owner, h_rect) && Calls.yiRectToOMD_old.hook(owner, h_rect_old)
     && Calls.Ch_BlkGetHandle.hook(owner, h_blk_get_handle) && Calls.MsBattleExe.hook(owner, h_battle_exe);

    private D.UIntToPtrFn? _next_blk;
    private D.Int4Fn? _next_battle_exe;

    /// <summary>
    ///     With the flag, Ch_BlkGetHandle loads a character only if it is in the
    ///     developers' debug lists (Ch_InitAll reads them at boot, only in a debug
    ///     boot; on a normal start they are empty), so every character loaded
    ///     after the flag went on was "not exist" and its callers went on with a
    ///     bad block: the heap broke a few seconds later. The call runs with the
    ///     flag lowered, i.e. as in release.
    /// </summary>
    [UnmanagedCallConv(CallConvs = [ typeof(CallConvCdecl) ])]
    private nint h_blk_get_handle(uint key) {
        _next_blk ??= Calls.Ch_BlkGetHandle.chain_from(h_blk_get_handle).fnptr!;
        byte saved = Mem.u8(G.FFXIsDebug);
        if (saved == 0) return _next_blk(key);
        Mem.set_u8(G.FFXIsDebug, 0);
        try { return _next_blk(key); }
        finally { Mem.set_u8(G.FFXIsDebug, saved); }
    }

    /// <summary>
    ///     With the flag, MsBattleExe points battle sound at the developers'
    ///     /usr/local/ffx/.../ps2/sound (SsSetProjectBase), which the PC build
    ///     does not have. The call runs with the flag lowered.
    /// </summary>
    [UnmanagedCallConv(CallConvs = [ typeof(CallConvCdecl) ])]
    private uint h_battle_exe(int mode, int field, int group, int formation) {
        _next_battle_exe ??= Calls.MsBattleExe.chain_from(h_battle_exe).fnptr!;
        byte saved = Mem.u8(G.FFXIsDebug);
        if (saved == 0) return _next_battle_exe(mode, field, group, formation);
        Mem.set_u8(G.FFXIsDebug, 0);
        try { return _next_battle_exe(mode, field, group, formation); }
        finally { Mem.set_u8(G.FFXIsDebug, saved); }
    }

    private D.RectBuildFn? _build;

    [UnmanagedCallConv(CallConvs = [ typeof(CallConvCdecl) ])]
    private uint h_rect(uint a1, uint a2, uint a3, uint a4, uint a5, uint a6, uint a7, uint a8) {
        _build ??= Calls.yiRectToOMDBuild.fnptr!;
        return _build(Mem.at(Calls.g_YiRender), a1, a2, a3, a4, a5, a6, a7, 0);
    }

    [UnmanagedCallConv(CallConvs = [ typeof(CallConvCdecl) ])]
    private uint h_rect_old(uint a1, uint a2, uint a3, uint a4, uint a5, uint a6, uint a7) {
        _build ??= Calls.yiRectToOMDBuild.fnptr!;
        return _build(Mem.at(Calls.g_YiRender), a1, a2, a3, a4, a5, a6, a7, 0);
    }

    public bool hooks_ok = true;

    public void set(bool value, TweakPanels panels) => GameThread.post(value ? "debug flag on" : "debug flag off", () => {
        if (value && !hooks_ok) { GameThread.last_status = "the debug flag stays off: its safety hook is not installed"; return; }
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
