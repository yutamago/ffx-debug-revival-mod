// SPDX-License-Identifier: MIT

using Yuta.DebugRevival.Native;

namespace Yuta.DebugRevival.Features;

/// <summary>
///     The game's debug flag (g_FFXIsDebug), switched at runtime. A normal boot skips what a debug boot sets up,
///     so the hooks here keep the flag away from the code that would crash without it (docs/research/field.md, section 5).
/// </summary>
public sealed unsafe class DebugFlag {
    private bool _buffer_made;
    private bool _events_built;

    /// <summary>The flag as last read on the game thread.</summary>
    public volatile bool on;

    public bool hooks_ok = true;

    private D.UIntToPtrFn? _next_blk;
    private D.Int4Fn? _next_battle_exe;
    private D.RectBuildFn? _build;

    public bool init(FhModule owner) =>
        Calls.yiRectToOMD.hook(owner, h_rect) && Calls.yiRectToOMD_old.hook(owner, h_rect_old)
     && Calls.Ch_BlkGetHandle.hook(owner, h_blk_get_handle) && Calls.MsBattleExe.hook(owner, h_battle_exe);

    private static byte lower_flag() {
        byte saved = Mem.u8(G.FFXIsDebug);
        if (saved != 0) Mem.set_u8(G.FFXIsDebug, 0);
        return saved;
    }

    private static void restore_flag(byte saved) {
        if (saved != 0) Mem.set_u8(G.FFXIsDebug, saved);
    }

    // Under the flag this loads only characters from the debug lists, which only a debug boot fills.
    [UnmanagedCallConv(CallConvs = [ typeof(CallConvCdecl) ])]
    private nint h_blk_get_handle(uint key) {
        _next_blk ??= Calls.Ch_BlkGetHandle.chain_from(h_blk_get_handle).fnptr!;
        byte saved = lower_flag();
        try { return _next_blk(key); }
        finally { restore_flag(saved); }
    }

    // Under the flag this points battle sound at the developers' /usr/local/ffx/ tree.
    [UnmanagedCallConv(CallConvs = [ typeof(CallConvCdecl) ])]
    private uint h_battle_exe(int mode, int field, int group, int formation) {
        _next_battle_exe ??= Calls.MsBattleExe.chain_from(h_battle_exe).fnptr!;
        byte saved = lower_flag();
        try { return _next_battle_exe(mode, field, group, formation); }
        finally { restore_flag(saved); }
    }

    // Under the flag both wrappers ask yiRectToOMDBuild for "unlimited", which writes past its heap buffers.
    [UnmanagedCallConv(CallConvs = [ typeof(CallConvCdecl) ])]
    private uint h_rect(uint a1, uint a2, uint a3, uint a4, uint a5, uint a6, uint a7, uint a8) => build(a1, a2, a3, a4, a5, a6, a7);

    [UnmanagedCallConv(CallConvs = [ typeof(CallConvCdecl) ])]
    private uint h_rect_old(uint a1, uint a2, uint a3, uint a4, uint a5, uint a6, uint a7) => build(a1, a2, a3, a4, a5, a6, a7);

    private uint build(uint a1, uint a2, uint a3, uint a4, uint a5, uint a6, uint a7) {
        _build ??= Calls.yiRectToOMDBuild.fnptr!;
        return _build(Mem.at(Calls.g_YiRender), a1, a2, a3, a4, a5, a6, a7, 0);
    }

    public void set(bool value, TweakPanels panels) => GameThread.post(value ? "debug flag on" : "debug flag off", () => {
        if (value && !hooks_ok) { Status.failed("the debug flag stays off: its safety hooks are not installed"); return; }
        if (value) {
            Mem.set_u8(G.FFXIsDebug, 1);
            // A normal boot never allocates the debug line buffer the flag's debug pass writes to.
            if (!_buffer_made) { Calls.Sg_DbInitAll.fnptr!(); _buffer_made = true; }
            ensure_event_list();
        } else {
            panels.hide_flag_only_panel();
            Mem.set_u8(G.FFXIsDebug, 0);
            Mem.set_u8(G.L2ExecFlag, 0); // only rewritten under the flag, so "L2 held" would stick
        }
        on = value;
        Status.done(value ? "debug flag on" : "debug flag off");
    });

    /// <summary>Builds the developers' event list once. Game thread.</summary>
    public void ensure_event_list() {
        if (_events_built) return;
        Calls.initEventList.fnptr!();
        _events_built = true;
    }

    /// <summary>Game thread, every frame.</summary>
    public void update() => on = Mem.u8(G.FFXIsDebug) != 0;
}
