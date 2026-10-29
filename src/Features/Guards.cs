// SPDX-License-Identifier: MIT OR Apache-2.0

using Yuta.DebugRevival.Native;

namespace Yuta.DebugRevival.Features;

/// <summary>
///     Hooks that keep the revived developer tools from doing lasting harm.
///     <list type="bullet">
///         <item><c>StatsAndAchievements_ResetAll</c> (Basic debug panel, line 12)
///               calls <c>SteamUserStats()->ResetAllStats(true)</c>, which wipes the
///               player's Steam stats and every achievement. Nothing else in the game
///               calls it; with this mod loaded it never runs.</item>
///         <item><c>TkDebugLoadViewerSave</c> (Basic debug panel, line 9 + Enter)
///               copies a developer save over the running game and warps. It only
///               runs when the player allowed it in the Field tab.</item>
///     </list>
/// </summary>
public sealed class Guards(FhLogger log) {
    /// <summary>Lets the Basic debug panel's "Game Section" load developer saves.</summary>
    public volatile bool allow_viewer_saves;

    /// <summary>Set for one call by the mod's own "load developer save" button.</summary>
    internal bool viewer_save_once;

    public int blocked_steam_resets;
    public int blocked_viewer_saves;

    public bool init(FhModule owner) {
        return Calls.StatsAndAchievements_ResetAll.hook(owner, h_reset_all)
            && Calls.TkDebugLoadViewerSave.hook(owner, h_viewer_save);
    }

    [UnmanagedCallConv(CallConvs = [ typeof(CallConvCdecl) ])]
    private void h_reset_all() {
        // Deliberately not chained.
        Interlocked.Increment(ref blocked_steam_resets);
        log.Warning("Blocked StatsAndAchievements_ResetAll: the Basic debug panel's 'Achievements Reset' would wipe the Steam stats and achievements.");
        GameThread.last_status = "blocked: 'Achievements Reset' would wipe your Steam achievements";
    }

    [UnmanagedCallConv(CallConvs = [ typeof(CallConvCdecl) ])]
    private void h_viewer_save(int n) {
        if (!allow_viewer_saves && !viewer_save_once) {
            Interlocked.Increment(ref blocked_viewer_saves);
            GameThread.last_status = $"blocked developer save {n}: allow it in the Field tab first";
            return;
        }
        viewer_save_once = false;
        Calls.TkDebugLoadViewerSave.chain_from(h_viewer_save).fnptr!(n);
    }
}
