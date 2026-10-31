// SPDX-License-Identifier: MIT

using Yuta.DebugRevival.Native;

namespace Yuta.DebugRevival.Features;

/// <summary>
///     Keeps the Basic debug panel from doing lasting harm: "Achievements Reset" would wipe the player's Steam
///     stats and achievements and never runs, and "Game Section" loads a developer save only when allowed.
/// </summary>
public sealed class Guards(FhLogger log) {
    public volatile bool allow_viewer_saves;

    /// <summary>Set for one call by the mod's own "load developer save" button.</summary>
    internal bool viewer_save_once;

    /// <summary>Whether both detours are in place. Fahrenheit installs hooks after every module's init, so this is checked on the first frame.</summary>
    public bool verified { get; private set; }
    private bool _checked;

    public bool init(FhModule owner) =>
        Calls.StatsAndAchievements_ResetAll.hook(owner, h_reset_all) && Calls.TkDebugLoadViewerSave.hook(owner, h_viewer_save);

    public void verify() {
        if (_checked) return;
        _checked = true;
        verified = detoured(Calls.RESET_ALL_VA) && detoured(Calls.VIEWER_SAVE_VA);
        if (!verified) log.Error("The safety hooks are not installed; the developer panels stay locked.");
    }

    private static bool detoured(uint va) => Mem.u8(va) == 0xe9;

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
