// SPDX-License-Identifier: MIT

using Yuta.DebugRevival.Native;

namespace Yuta.DebugRevival.Features;

/// <summary>
///     Keeps the revived debug code from doing lasting harm: the Basic debug panel's "Achievements Reset" would wipe
///     the player's Steam stats and achievements and never runs, "Game Section" loads a developer save only when
///     allowed, and the developers' file reads no longer damage the game's heap.
/// </summary>
public sealed class Guards(FhLogger log) {
    public volatile bool allow_viewer_saves;

    /// <summary>Set for one call by the mod's own "load developer save" button.</summary>
    internal bool viewer_save_once;

    /// <summary>Whether all three detours are in place. Fahrenheit installs hooks after every module's init, so this is checked on the first frame.</summary>
    public bool verified { get; private set; }
    private bool _checked;

    public bool init(FhModule owner) =>
        Calls.StatsAndAchievements_ResetAll.hook(owner, h_reset_all) && Calls.TkDebugLoadViewerSave.hook(owner, h_viewer_save)
     && Calls.Sg_MemSetCurrentAddr.hook(owner, h_mem_set_current_addr);

    public void verify() {
        if (_checked) return;
        _checked = true;
        verified = detoured(Calls.RESET_ALL_VA) && detoured(Calls.VIEWER_SAVE_VA) && detoured(Calls.MEM_SET_CURRENT_ADDR_VA);
        if (!verified) log.Error("The safety hooks are not installed; the developer panels and the map list stay locked.");
    }

    private static bool detoured(uint va) => Mem.u8(va) == 0xe9;

    // The developers' file reads (Sg_PcRead, used by initEventList for the map list) tag the buffer's PS2 heap header
    // at p - 4. On PC the buffer comes from user_malloc, whose own block header sits there, so the tag broke the
    // game's heap and it crashed at a later free. Only the tag is skipped.
    [UnmanagedCallConv(CallConvs = [ typeof(CallConvCdecl) ])]
    private void h_mem_set_current_addr(nint p) {
        if (p != 0) Calls.Sg_MemSetRtAddr.fnptr!(Mem.i32(Calls.SG_MEM_RT_BASE));
    }

    [UnmanagedCallConv(CallConvs = [ typeof(CallConvCdecl) ])]
    private void h_reset_all() {
        log.Warning("Blocked StatsAndAchievements_ResetAll (Basic debug panel, 'Achievements Reset').");
        Status.refused("blocked: 'Achievements Reset' would wipe your Steam achievements");
    }

    [UnmanagedCallConv(CallConvs = [ typeof(CallConvCdecl) ])]
    private void h_viewer_save(int n) {
        if (!allow_viewer_saves && !viewer_save_once) {
            Status.refused($"blocked developer save {n}"); return;
        }
        viewer_save_once = false;
        Calls.TkDebugLoadViewerSave.chain_from(h_viewer_save).fnptr!(n);
    }
}
