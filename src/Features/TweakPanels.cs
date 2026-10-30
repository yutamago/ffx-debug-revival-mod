// SPDX-License-Identifier: MIT OR Apache-2.0

using Yuta.DebugRevival.Native;

namespace Yuta.DebugRevival.Features;

/// <summary>
///     The PC port's ten tweak panels (DebugMenuManager). FFX.exe still draws whichever one is shown; the cut parts
///     are the switch and the panels' input handlers, which <see cref="update"/> runs (docs/research/field.md, section 2).
/// </summary>
public sealed unsafe class TweakPanels(Guards guards, DebugFlag flag) {
    /// <param name="ViewerOnly">The handler returns at once without the debug flag.</param>
    /// <param name="TextUtil">Offset of the panel's TextUtil; W/HOffset hold the size TextUtil_resize takes.</param>
    public sealed record Panel(int Mode, string Name, uint Instance, uint HandleInput, uint ShownOffset, uint CursorOffset,
                               bool ViewerOnly, string Note, string? Warning, uint TextUtil, uint WOffset, uint HOffset);

    public const int NONE = 10;
    private const int TEXTURE_ANIMATION = 5;

    public static readonly Panel[] PANELS = [
        new(0, "Character lighting",          0x00ccca88, 0x006a8e40, 0xc8,  0,     false, "light direction, intensity and ambient colour of the characters", null, 0x04, 0xe0, 0xe4),
        new(1, "Post process",                0x00ccc89c, 0x00693720, 0x180, 0x17c, false, "fog, glow, shading, sharpening, anti-aliasing and colour", null, 0x08, 0x188, 0x18c),
        new(2, "Character switch",            0x00cccac4, 0x006b8820, 0xd4,  0xd0,  true,  "swaps the player's model", "don't save afterwards", 0x04, 0xdc, 0xe0),
        new(3, "Map switch",                  0x00cccac8, 0x006b9160, 0x1f4, 0,     true,  "pick a map by area and number, Enter jumps there", null, 0x04, 0x1fc, 0x200),
        new(4, "Character animation",         0x00cccadc, 0x006b85c0, 0xc0,  0,     true,  "plays the model's animations, after a Character switch", null, 0x18, 0xc8, 0xcc),
        new(5, "Character texture animation", 0x00cccad4, 0x006b8a20, 0xd0,  0,     true,  "plays the model's texture animations", null, 0x20, 0xd8, 0xdc),
        new(6, "Basic debug",                 0x00cccad0, 0x006b7630, 0x100, 0xfc,  false, "encounters, interface, minimap, 4:3 frame, developer saves and more", null, 0x34, 0x108, 0x10c),
        new(7, "Battle debug",                0x00cccad8, 0x006b81d0, 0x1c4, 0x1c0, false, "pick a battle, never die, control the monsters; line 4 + Enter starts it", null, 0x114, 0x1cc, 0x1d0),
        new(8, "Battle character params",     0x00cccae0, 0x006b7af0, 0xfc,  0xf8,  false, "party and monster stats", "changes them every frame while the cursor is below the first line", 0x20, 0x104, 0x108),
        new(9, "Game params",                 0x00cccae4, 0x006b8ce0, 0xe8,  0xe4,  false, "items, gil, abilities, sphere levels, names, play time", "changes your save", 0x14, 0xf0, 0xf4),
    ];

    public volatile bool input_enabled;

    public sealed record Snapshot(int Mode, int Cursor, string[] Lines);

    public volatile Snapshot snap = new(NONE, -1, []);

    public bool usable(Panel p) => !p.ViewerOnly || flag.on;

    private static int current_mode {
        get {
            nint dmm = *(nint*)Mem.at(G.DebugMenuManager);
            return dmm == 0 ? NONE : *(int*)(dmm + 4);
        }
    }

    public void show(int mode) => GameThread.post($"show panel {mode}", () => {
        if (mode != NONE && !guards.verified) { Status.failed("locked: the safety hooks are not installed"); return; }
        if (mode != NONE && !usable(PANELS[mode])) { Status.refused($"{PANELS[mode].Name} needs the debug flag"); return; }
        if (!set_mode(mode)) { Status.refused("the panels don't exist yet"); return; }
        Status.done(mode == NONE ? "panels hidden" : PANELS[mode].Name);
    });

    /// <summary>Game thread. The texture-animation panel only works with the debug flag.</summary>
    internal void hide_flag_only_panel() {
        if (current_mode == TEXTURE_ANIMATION) set_mode(NONE);
    }

    private static bool set_mode(int mode) {
        nint dmm = *(nint*)Mem.at(G.DebugMenuManager);
        if (dmm == 0) return false;
        Calls.DebugMenuManager_setMode.fnptr!(dmm, mode);
        return true;
    }

    /// <summary>Sets every panel's text scale and position, then rebuilds the shown one. Larger x moves the text left, larger y up.</summary>
    public void set_layout(float scale, float x, float y) => GameThread.post("panel size", () => {
        foreach (Panel p in PANELS) {
            nint self = *(nint*)Mem.at(p.Instance);
            if (self == 0) continue;
            nint tu = self + (nint)p.TextUtil;
            *(float*)(tu + 0x8c) = scale;
            *(float*)(tu + 0x84) = x;
            *(float*)(tu + 0x88) = y;
            if (*(byte*)(self + (nint)p.ShownOffset) != 0) {
                Calls.TextUtil_resize.fnptr!(tu, *(uint*)(self + (nint)p.WOffset), *(uint*)(self + (nint)p.HOffset));
            }
        }
    });

    /// <summary>Game thread, every frame: runs the shown panel's input handler.</summary>
    public void update() {
        if (!input_enabled || !guards.verified) return;
        if (shown() is not (Panel p, nint self) || !usable(p)) return;
        nint app = Calls.PApplication_GetApplication.fnptr!();
        if (app == 0) return;
        Calls.panel_handle_input(p.HandleInput).fnptr!(self, app);
    }

    /// <summary>Game thread, after each update: copies the shown panel's title, lines and cursor.</summary>
    public void snapshot() {
        if (shown() is not (Panel p, nint self)) { snap = new(NONE, -1, []); return; }
        nint tu = self + (nint)p.TextUtil;
        int count = (int)Math.Min(*(uint*)(tu + 0x74) & 0x7fffffff, 64);
        nint arr = *(nint*)(tu + 0x78);
        List<string> lines = [ pstring(*(uint*)(tu + 0x7c)) ];
        for (int i = 0; i < count && arr != 0; i++) lines.Add(pstring(*(uint*)(arr + 4 * i)));
        int cursor = p.CursorOffset == 0 ? -1 : *(int*)(self + (nint)p.CursorOffset);
        snap = new(p.Mode, cursor, [ .. lines ]);
    }

    private static (Panel, nint)? shown() {
        int mode = current_mode;
        if (mode < 0 || mode >= PANELS.Length) return null;
        nint self = *(nint*)Mem.at(PANELS[mode].Instance);
        return self == 0 ? null : (PANELS[mode], self);
    }

    /// <summary>A Phyre PString word: a char* with a tag in bit 0.</summary>
    private static string pstring(uint word) {
        nint p = (nint)(word & ~1u);
        return p != 0 && Mem.readable(p, 1) ? Mem.cstr(p, 160) : "";
    }
}
