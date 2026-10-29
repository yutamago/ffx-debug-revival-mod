// SPDX-License-Identifier: MIT OR Apache-2.0

using Yuta.DebugRevival.Native;

namespace Yuta.DebugRevival.Features;

/// <summary>
///     The PC port's ten "tweak" panels (DebugMenuManager). FFX.exe still creates,
///     initialises and renders all ten every frame, each gated only by its own
///     "shown" byte; what was cut is the switch (F9 / Select+Square,
///     DebugMenuManager_handleInput) and every panel's input handler. Here
///     <c>setMode</c> shows a panel and, when asked, the panel's own input
///     handler runs once per frame on the game thread. Its keys (I/K/J/L, Enter,
///     d-pad, Circle) also reach the game, as they did for the developers.
/// </summary>
public sealed unsafe class TweakPanels(Guards guards) {
    public sealed record Panel(int Mode, string Name, uint Instance, uint HandleInput, uint ShownOffset, uint CursorOffset, bool ViewerOnly, string Note);

    /// <summary>Mode -> panel (setMode's switch; VERIFIED in FFX.exe).</summary>
    public static readonly Panel[] PANELS = [
        new(0, "Character lighting",         0x00ccca88, 0x006a8e40, 0xc8,  0,     false, "light index, intensity, direction, ambient colour"),
        new(1, "Post process",               0x00ccc89c, 0x00693720, 0x180, 0x17c, false, "fog, glow, HDAO, bright, unsharp mask, SMAA, colour correction"),
        new(2, "Character switch",           0x00cccac4, 0x006b8820, 0xd4,  0xd0,  true,  "model viewer only (map grid00)"),
        new(3, "Map switch",                 0x00cccac8, 0x006b9160, 0x1f4, 0,     true,  "needs the debug flag and the developers' event list"),
        new(4, "Character animation",        0x00cccadc, 0x006b85c0, 0xc0,  0,     true,  "model viewer only"),
        new(5, "Character texture animation",0x00cccad4, 0x006b8a20, 0xd0,  0,     true,  "model viewer only"),
        new(6, "Basic debug",                0x00cccad0, 0x006b7630, 0x100, 0xfc,  false, "encounters, UI, minimap, 4:3 frame, Thunder Plains variant, developer saves, CRC check"),
        new(7, "Battle debug",               0x00cccad8, 0x006b81d0, 0x1c4, 0x1c0, false, "battle map/group picker, player never dies, monster input; line 4 + Enter starts the battle"),
        new(8, "Battle character params",    0x00cccae0, 0x006b7af0, 0xfc,  0xf8,  false, "EDITS LIVE BATTLE STATS EVERY FRAME while the cursor is below line 0"),
        new(9, "Game params",                0x00cccae4, 0x006b8ce0, 0xe8,  0xe4,  false, "EDITS YOUR SAVE: items, gil, abilities, SP, blitzball, names, play time"),
    ];

    /// <summary>The panel the developers' F9 cycle would show; 10 = none.</summary>
    public int current_mode {
        get {
            nint dmm = *(nint*)Mem.at(G.DebugMenuManager);
            return dmm == 0 ? 10 : *(int*)(dmm + 4);
        }
    }

    /// <summary>Let the shown panel read its keys each frame.</summary>
    public volatile bool input_enabled;

    public void show(int mode) => GameThread.post($"show panel {mode}", () => {
        if (mode != 10 && !guards.verified) { GameThread.last_status = "locked: the safety hooks are not installed (see the log)"; return; }
        nint dmm = *(nint*)Mem.at(G.DebugMenuManager);
        if (dmm == 0) { GameThread.last_status = "the debug menu manager does not exist yet"; return; }
        Calls.DebugMenuManager_setMode.fnptr!(dmm, mode);
        GameThread.last_status = mode == 10 ? "panels hidden" : $"panel: {PANELS[mode].Name}";
    });

    /// <summary>Game thread, every frame: drives the shown panel's input handler.</summary>
    public void update() {
        if (!input_enabled || !guards.verified) return;
        int mode = current_mode;
        if (mode < 0 || mode >= PANELS.Length) return;
        Panel p = PANELS[mode];
        if (p.ViewerOnly) return; // their handlers return at once without the debug flag
        nint self = *(nint*)Mem.at(p.Instance);
        if (self == 0) return;
        nint app = Calls.PApplication_GetApplication.fnptr!();
        if (app == 0) return;
        Calls.panel_handle_input(p.HandleInput).fnptr!(self, app);
    }

    /// <summary>The selected line of a panel, or -1 when the panel has no cursor field.</summary>
    public static int cursor(Panel p) {
        if (p.CursorOffset == 0) return -1;
        nint self = *(nint*)Mem.at(p.Instance);
        return self == 0 ? -1 : *(int*)(self + (nint)p.CursorOffset);
    }
}
