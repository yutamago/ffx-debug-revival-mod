// SPDX-License-Identifier: MIT OR Apache-2.0

using Yuta.DebugRevival.Features;
using Yuta.DebugRevival.Native;

namespace Yuta.DebugRevival.Ui;

/// <summary>The mod's window (F9): one tab per area.</summary>
public sealed class MainWindow(
    ScriptDebuggerUi script, TweakPanels panels, BattleDebug battle, FieldTools field, DebugLog log, Guards guards,
    DebugFlag flag, GuToolkit gu) {

    private static readonly Vector4 RED    = new(1.0f, 0.35f, 0.35f, 1);
    private static readonly Vector4 YELLOW = new(1.0f, 0.85f, 0.3f, 1);

    public bool open;

    // Battle tab
    private int _field_index;
    private int _encounter_index;
    private BattleDebug.Cheat? _confirm_cheat;

    // Field tab
    private int _map = 0x17;
    private int _entrance;
    private int _viewer_save = 1;
    private string _log_filter = "";
    private bool _log_autoscroll = true;
    private string _map_filter = "";
    private bool _show_test_maps = true;
    private float _fly_speed = 60f;

    // Panels tab
    private float _panel_scale = 0.5f, _panel_x = 3.0f, _panel_y;

    public void draw() {
        if (ImGui.IsKeyPressed(ImGuiKey.F9, false)) open = !open;
        if (!open) return;

        ImGui.SetNextWindowSize(new Vector2(980, 640), ImGuiCond.FirstUseEver);
        if (ImGui.Begin("Debug Revival (F9)", ref open)) {
            bool dbg = flag.on;
            if (ImGui.Checkbox("Debug flag", ref dbg)) flag.set(dbg, panels);
            if (ImGui.IsItemHovered()) {
                ImGui.SetTooltip("The developers' debug mode (g_FFXIsDebug), switched on in the running game.\n"
                               + "While on: L1 walks through walls, L2 and L1+R1 change the game speed, events never refuse a map jump,\n"
                               + "a New Game starts at story flag 5, outside a battle L1+R1+L2+R2+X starts the picked battle,\n"
                               + "and the four model-viewer panels work. Battle sound may not load right while it is on.");
            }
            ImGui.SameLine();
            Txt.D(dbg ? "on: the game runs as the developers' build did (hover for what changes)" : "off");
            string status = GameThread.last_status;
            if (status.Length > 0) Txt.C(YELLOW, status);
            if (ImGui.BeginTabBar("tabs")) {
                if (ImGui.BeginTabItem("Scripts")) { script.draw(); ImGui.EndTabItem(); }
                if (ImGui.BeginTabItem("PS2 menu")) { draw_gu(); ImGui.EndTabItem(); }
                if (ImGui.BeginTabItem("Panels"))  { draw_panels(); ImGui.EndTabItem(); }
                if (ImGui.BeginTabItem("Battle"))  { draw_battle(); ImGui.EndTabItem(); }
                if (ImGui.BeginTabItem("Field"))   { draw_field(); ImGui.EndTabItem(); }
                if (ImGui.BeginTabItem("Log"))     { draw_log(); ImGui.EndTabItem(); }
                if (ImGui.BeginTabItem("About"))   { draw_about(); ImGui.EndTabItem(); }
                ImGui.EndTabBar();
            }
        }
        ImGui.End();
    }

    // ------------------------------------------------------------------ panels

    private void draw_panels() {
        Txt.W("The PC port's developer panels. The game still draws them (small text, top left); "
            + "only the switch was cut. With 'Panel keys' on, the shown panel reads its keys every frame: "
            + "I/K or d-pad up/down pick a line, J/L or left/right change it, Enter or Circle activates. "
            + "Those keys also reach the game.");
        int mode = panels.current_mode;
        bool input = panels.input_enabled;
        if (ImGui.Checkbox("Panel keys", ref input)) panels.input_enabled = input;
        ImGui.SameLine();
        if (ImGui.Button("Hide panels")) panels.show(10);
        ImGui.Separator();
        foreach (TweakPanels.Panel p in TweakPanels.PANELS) {
            ImGui.BeginDisabled(!panels.usable(p));
            if (ImGui.RadioButton($"{p.Name}##p{p.Mode}", mode == p.Mode)) panels.show(p.Mode);
            ImGui.EndDisabled();
            ImGui.SameLine();
            bool danger = p.Mode is 8 or 9;
            string note = p.ViewerOnly && !panels.usable(p) ? $"needs the debug flag (top of this window); {p.Note}" : p.Note;
            if (danger) Txt.C(RED, note);
            else Txt.D(note);
            if (mode == p.Mode && TweakPanels.cursor(p) is int c and >= 0) {
                ImGui.SameLine();
                Txt.T($"[line {c}]");
            }
        }

        ImGui.SeparatorText("Size and position (all panels)");
        bool changed = false;
        ImGui.SetNextItemWidth(220);
        changed |= ImGui.SliderFloat("Text size", ref _panel_scale, 0.15f, 0.6f, "%.2f");
        ImGui.SetNextItemWidth(220);
        changed |= ImGui.SliderFloat("Up / down", ref _panel_y, -6f, 12f, "%.1f");
        ImGui.SetNextItemWidth(220);
        changed |= ImGui.SliderFloat("Left / right", ref _panel_x, -2f, 6f, "%.1f");
        if (changed) panels.set_layout(_panel_scale, _panel_x, _panel_y);
        ImGui.SameLine();
        if (ImGui.Button("Original")) { _panel_scale = 0.5f; _panel_x = 3.0f; _panel_y = 0f; panels.set_layout(_panel_scale, _panel_x, _panel_y); }
        Txt.D("0.5 / 0 / 3 is the developers' layout. Smaller text or 'up' keeps long panels on screen.");

        string[] lines = panels.lines;
        if (mode < TweakPanels.PANELS.Length && lines.Length > 0 && ImGui.CollapsingHeader("Panel text (as the game draws it)", ImGuiTreeNodeFlags.DefaultOpen)) {
            foreach (string l in lines) Txt.T(l);
        }

        ImGui.Separator();
        Txt.W("Blocked for safety: Basic debug line 12 'Achievements Reset' (it would wipe your Steam stats and achievements; "
            + $"blocked {guards.blocked_steam_resets} times). Line 9 'Game Section' + Enter loads a developer save over your game; "
            + "it only works when allowed in the Field tab.");
    }

    // ---------------------------------------------------------------- PS2 menu

    private void draw_gu() {
        Txt.W("The developers' own debug menu from the PS2 build, drawn by the game's original code: "
            + "a column of buttons at the right edge of the screen (CHR, MAP, BATTLE, CAMERA, EVENT, EFFECT, SYSTEM tabs; "
            + "click the tab button to switch, or use the mouse wheel). It is mouse-driven: with 'Mouse to the PS2 menu' on, "
            + "your mouse controls its cursor wherever this window is not.");
        bool open = gu.open;
        if (ImGui.Checkbox("Show the PS2 debug menu", ref open)) gu.show(open);
        ImGui.SameLine();
        bool cap = gu.capture_mouse;
        if (ImGui.Checkbox("Mouse to the PS2 menu", ref cap)) gu.capture_mouse = cap;
        if (gu.blocked.Length > 0) Txt.C(YELLOW, gu.blocked);

        ImGui.SeparatorText("Battle windows");
        Txt.D("The developers' battle windows. BattleInfo, BattleConsole, BattlePos and the Shake editor are left out: "
            + "they read data that exists only in a battle and cannot be closed safely afterwards.");
        foreach (var (n, name) in GuToolkit.BATTLE_WINDOWS) {
            if (ImGui.Button($"{name}##bw{n}")) gu.open_battle_window(n);
            ImGui.SameLine();
        }
        ImGui.NewLine();

        ImGui.SeparatorText("Blocked items");
        Txt.D("CLR FLG (would clear the story flags), Capture and the rc tool's Save (would hang: they need the developers' PC), "
            + "CacheCLR, MAP Reload and the map/user lists, EVENT Reload/Select (use the Field tab's map jump), MemDumpD, Movie.");
    }

    // ------------------------------------------------------------------ battle

    private void draw_battle() {
        BattleDebug.Snapshot? s = battle.snap;
        if (s is null) { Txt.D("Waiting for the game loop."); return; }
        Txt.T(s.BtlStatus == 0 ? "Not in battle." : $"In battle (status {s.BtlStatus}).");

        if (ImGui.CollapsingHeader("Switches", ImGuiTreeNodeFlags.DefaultOpen)) {
            Txt.D("The developers' BattleConfig switches. They take effect at once unless noted.");
            string? group = null;
            for (int i = 0; i < BattleDebug.TOGGLES.Length; i++) {
                BattleDebug.Toggle t = BattleDebug.TOGGLES[i];
                if (t.Group != group) {
                    group = t.Group;
                    ImGui.SeparatorText(group);
                }
                bool on = s.ToggleValues[i] != 0;
                if (ImGui.Checkbox($"{t.Label}##t{i}", ref on)) battle.set_toggle(i, on);
                ImGui.SameLine();
                Vector4? colour = t.Risk == BattleDebug.Risk.None ? null : RED;
                string note = t.Risk switch {
                    BattleDebug.Risk.Stalls   => " (stalls the battle while on)",
                    BattleDebug.Risk.Progress => " (loses the battle's progress)",
                    BattleDebug.Risk.Untested => " (untested)",
                    _ => "",
                };
                if (colour is Vector4 col) Txt.C(col, t.Effect + note);
                else Txt.D(t.Effect);
            }
        }

        if (ImGui.CollapsingHeader("Cheats")) {
            Txt.C(RED, "These change your game state; saving afterwards keeps the change.");
            foreach (BattleDebug.Cheat c in BattleDebug.CHEATS) {
                if (ImGui.Button($"{c.Label}##c{c.Va:x}")) {
                    if (c.SaveChanging) { _confirm_cheat = c; ImGui.OpenPopup("Confirm cheat"); }
                    else battle.run_cheat(c);
                }
                ImGui.SameLine();
                Txt.D(c.Effect + (c.StoryBreaking ? " (out of story order)" : "") + (c.BattleOnly ? " (battle only)" : ""));
            }
            if (ImGui.Button("Party to 1 HP")) battle.weak(false, true);
            ImGui.SameLine();
            if (ImGui.Button("Party to full HP")) battle.weak(false, false);
            ImGui.SameLine();
            if (ImGui.Button("Monsters to 1 HP")) battle.weak(true, true);
            ImGui.SameLine();
            if (ImGui.Button("Monsters to full HP")) battle.weak(true, false);

            bool popup_open = true;
            if (ImGui.BeginPopupModal("Confirm cheat", ref popup_open, ImGuiWindowFlags.AlwaysAutoResize)) {
                BattleDebug.Cheat? c = _confirm_cheat;
                Txt.T(c is null ? "" : $"{c.Label}: {c.Effect}.");
                Txt.C(RED, "This edits the game state that your next save writes.");
                if (ImGui.Button("Do it") && c is not null) { battle.run_cheat(c); ImGui.CloseCurrentPopup(); }
                ImGui.SameLine();
                if (ImGui.Button("Cancel")) ImGui.CloseCurrentPopup();
                ImGui.EndPopup();
            }
        }

        if (ImGui.CollapsingHeader("Start a battle")) {
            Txt.W("The developers' scene picker: a battle field (record index in the battle field list) and an encounter "
                            + "index within it. 'Look up' names it; 'Start' requests it like the debug pad combo did. "
                            + "Formations that need story state may soft-lock.");
            ImGui.SetNextItemWidth(120);
            ImGui.InputInt("Field index", ref _field_index);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(120);
            ImGui.InputInt("Encounter", ref _encounter_index);
            ImGui.SameLine();
            if (ImGui.Button("Look up")) battle.pick(_field_index, _encounter_index);
            ImGui.SameLine();
            ImGui.BeginDisabled(s.BtlStatus != 0);
            if (ImGui.Button("Start")) battle.start_picked();
            ImGui.EndDisabled();
            Txt.T(battle.picker_result);
        }

        if (ImGui.CollapsingHeader("Actors", ImGuiTreeNodeFlags.DefaultOpen)) {
            bool status_line = battle.native_status_line;
            if (ImGui.Checkbox("Game's own debug status line (experimental)", ref status_line)) battle.native_status_line = status_line;
            ImGui.SameLine();
            Txt.D("drawn by FFX.exe's debug text buffer when a message window is drawn");
            draw_actors(s);
        }
    }

    private void draw_actors(BattleDebug.Snapshot s) {
        if (s.Actors.Length == 0) { Txt.D("No actors."); return; }
        if (!ImGui.BeginTable("actors", 12, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.Borders)) return;
        foreach (string h in (string[])[ "#", "chr", "HP", "max", "MP", "max", "STR/DEF/MAG/MDF", "AGI/LCK/EVA/ACC", "OD", "CTB", "status", "KO" ]) ImGui.TableSetupColumn(h);
        ImGui.TableHeadersRow();
        foreach (BattleDebug.ActorRow a in s.Actors) {
            ImGui.TableNextRow();
            ImGui.TableNextColumn(); Txt.T(a.Index < 0x12 ? $"P{a.Index}" : $"M{a.Index - 0x14}");
            ImGui.TableNextColumn(); Txt.T($"0x{a.ChrId:x}");
            ImGui.TableNextColumn(); edit_int($"hp{a.Index}", a.Hp, v => battle.set_actor(a.Index, 0x5d0, v, false));
            ImGui.TableNextColumn(); Txt.T(a.MaxHp.ToString(CultureInfo.InvariantCulture));
            ImGui.TableNextColumn(); edit_int($"mp{a.Index}", a.Mp, v => battle.set_actor(a.Index, 0x5d4, v, false));
            ImGui.TableNextColumn(); Txt.T(a.MaxMp.ToString(CultureInfo.InvariantCulture));
            ImGui.TableNextColumn(); Txt.T($"{a.Str}/{a.Def}/{a.Mag}/{a.MDef}");
            ImGui.TableNextColumn(); Txt.T($"{a.Agi}/{a.Luck}/{a.Eva}/{a.Acc}");
            ImGui.TableNextColumn(); edit_int($"od{a.Index}", a.OdGauge, v => battle.set_actor(a.Index, 0x5bc, v, true), $"/{a.OdMax}");
            ImGui.TableNextColumn(); edit_int($"ctb{a.Index}", a.Ctb, v => battle.set_actor(a.Index, 0x65c, v, true));
            ImGui.TableNextColumn(); Txt.T($"{a.Status:x4} {a.Status2:x4}");
            ImGui.TableNextColumn(); Txt.T(a.Ko == 2 ? "KO" : "");
        }
        ImGui.EndTable();
    }

    private static void edit_int(string id, int value, Action<int> set, string suffix = "") {
        int v = value;
        ImGui.SetNextItemWidth(70);
        if (ImGui.InputInt($"##{id}", ref v, 0, 0, ImGuiInputTextFlags.EnterReturnsTrue)) set(v);
        if (suffix.Length > 0) { ImGui.SameLine(); Txt.T(suffix); }
    }

    // ------------------------------------------------------------------- field

    private void draw_field() {
        FieldTools.Snapshot? s = field.snap;
        if (s is null) { Txt.D("Waiting for the game loop."); return; }

        Txt.T($"Map {s.EventId} (0x{s.EventId:x}) {map_name(s.EventId)}{(s.Place.Length > 0 ? $"   place: {s.Place}" : "")}");
        Txt.T(s.JumpAllowed ? (s.FlagOn ? "Map jumps are allowed (debug flag)." : "The event allows leaving the map now.")
                            : "The event does not allow leaving the map now (the debug flag lifts that).");

        ImGui.SeparatorText("Movement");
        bool noclip = field.noclip;
        if (ImGui.Checkbox("Walk through walls", ref noclip)) field.noclip = noclip;
        ImGui.SameLine();
        Txt.D("the debug pad's L1");
        int speed = s.ExecFrames;
        ImGui.SetNextItemWidth(160);
        if (ImGui.SliderInt("Game frames per frame", ref speed, 1, 8)) field.set_exec_frames(speed);
        ImGui.SameLine();
        Txt.D("the debug pad's L1+R1 speed-up");
        bool encounters = s.Encounters != 0;
        if (ImGui.Checkbox("Random encounters", ref encounters)) field.set_encounters(encounters);

        ImGui.SeparatorText("Free camera");
        Txt.T($"Fly mode: {(s.FreeCamType switch { -1 => "-", 0 => "off", _ => "on" })}   photo pause: {(s.PhotoPause ? "on" : "off")}");
        if (ImGui.Button("Toggle fly mode")) {
            field.toggle_fly();
            open = false; // this window would take the mouse and keys; F9 brings it back
        }
        ImGui.SameLine();
        if (ImGui.Button("Toggle photo pause")) field.toggle_photo_pause();
        ImGui.SameLine();
        ImGui.SetNextItemWidth(160);
        if (ImGui.SliderFloat("Fly speed", ref _fly_speed, 5f, 400f, "%.0f")) field.set_fly_speed(_fly_speed);
        bool keys = field.bind_camera_keys;
        if (ImGui.Checkbox("Keys: F10 fly mode, F11 photo pause", ref keys)) field.bind_camera_keys = keys;
        Txt.D("Flying: move the mouse to look (no button), Up/Down arrows move forward/back, Left/Right strafe, "
            + "right-drag dollies, middle-drag pans, left-drag turns faster. The window closes when fly mode starts, "
            + "because while it is open the game gets no mouse or keyboard input. Photo pause freezes the world and mutes the sound.");
        string[] op = gu.op_lines;
        if (op.Length > 0) Txt.D("Effect debugger line: " + string.Join("   ", op));

        ImGui.SeparatorText("Map jump");
        FieldTools.MapEntry[]? maps = field.maps;
        if (maps is null) {
            if (ImGui.Button("Load the map list")) field.load_maps();
            ImGui.SameLine();
            Txt.D("reads the developers' event list from the game");
        } else {
            ImGui.SetNextItemWidth(160);
            ImGui.InputTextWithHint("##mapfilter", "filter (name or number)", ref _map_filter, 32);
            ImGui.SameLine();
            ImGui.Checkbox("Show test maps", ref _show_test_maps);
            ImGui.SetNextItemWidth(320);
            if (ImGui.BeginCombo("Map", $"{_map}: {map_name(_map)}")) {
                foreach (FieldTools.MapEntry m in maps) {
                    if (!_show_test_maps && is_test(m.Name)) continue;
                    if (_map_filter.Length > 0 && !m.Name.Contains(_map_filter, StringComparison.OrdinalIgnoreCase)
                     && !m.Id.ToString(CultureInfo.InvariantCulture).StartsWith(_map_filter, StringComparison.Ordinal)) continue;
                    ImGui.BeginDisabled(!m.HasScript);
                    string label = $"{m.Id,3}: {(m.Name.Length > 0 ? m.Name : "(no name)")}{(m.HasScript ? "" : "  (no script)")}";
                    if (ImGui.Selectable($"{label}##m{m.Id}", m.Id == _map)) _map = m.Id;
                    ImGui.EndDisabled();
                }
                ImGui.EndCombo();
            }
        }
        ImGui.SetNextItemWidth(120);
        ImGui.InputInt("Entrance", ref _entrance);
        ImGui.SameLine();
        ImGui.BeginDisabled(!s.JumpAllowed);
        if (ImGui.Button("Jump")) field.jump(_map, _entrance);
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("Model viewer (grid00)")) field.go_to_model_viewer(panels);
        if (ImGui.IsItemHovered()) {
            ImGui.SetTooltip("The developers' model viewer scene (event 0, 'event00', where a debug build starts).\n"
                           + "Turns the debug flag on, so the viewer panels work and you can jump away again.");
        }
        Txt.C(RED, "Jumping out of story order can break progression. The names are the game's internal map names.");

        ImGui.SeparatorText("Developer saves");
        Txt.W("FFX_Data holds about 110 saves the developers used to jump into each part of the story "
            + "(the Basic debug panel's 'Game Section'). Loading one replaces the running game and warps to its map; "
            + "your save files are untouched until you save.");
        ImGui.SetNextItemWidth(120);
        ImGui.InputInt("Section", ref _viewer_save);
        _viewer_save = Math.Clamp(_viewer_save, 1, 0x71);
        ImGui.SameLine();
        ImGui.BeginDisabled(!s.JumpAllowed);
        if (ImGui.Button("Load")) field.load_viewer_save(_viewer_save);
        ImGui.EndDisabled();
        bool allow = guards.allow_viewer_saves;
        if (ImGui.Checkbox("Allow the Basic debug panel to load them too", ref allow)) guards.allow_viewer_saves = allow;
    }

    private string map_name(int id) {
        FieldTools.MapEntry[]? maps = field.maps;
        if (maps is null) return "";
        foreach (FieldTools.MapEntry m in maps) if (m.Id == id) return m.Name;
        return "";
    }

    private static bool is_test(string name) =>
        name.StartsWith("test", StringComparison.Ordinal) || name.StartsWith("scene", StringComparison.Ordinal)
     || name.StartsWith("sample", StringComparison.Ordinal) || name is "event00" or "camtest0" or "sysfp" or "soundtest" or "startmap0";

    // --------------------------------------------------------------------- log

    private void draw_log() {
        bool capture = log.capture;
        if (ImGui.Checkbox("Capture", ref capture)) log.capture = capture;
        ImGui.SameLine();
        ImGui.Checkbox("Auto-scroll", ref _log_autoscroll);
        ImGui.SameLine();
        if (ImGui.Button("Clear")) log.clear();
        ImGui.SameLine();
        ImGui.SetNextItemWidth(240);
        ImGui.InputText("Filter", ref _log_filter, 128);
        ImGui.SameLine();
        Txt.D($"{log.total} lines");
        Txt.D("dbgPrintf and scePrintf, empty in the shipped game. If nothing arrives, Fahrenheit's print control may be taking them first.");
        if (ImGui.BeginChild("log", Vector2.Zero, ImGuiChildFlags.Borders, ImGuiWindowFlags.HorizontalScrollbar)) {
            foreach (string line in log.lines()) {
                if (_log_filter.Length > 0 && !line.Contains(_log_filter, StringComparison.OrdinalIgnoreCase)) continue;
                Txt.T(line);
            }
            if (_log_autoscroll && ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 20) ImGui.SetScrollHereY(1.0f);
        }
        ImGui.EndChild();
    }

    private static void draw_about() {
        Txt.W("Debug Revival turns on the debug code the developers left in FFX.exe. What it covers:");
        Txt.B("Scripts: the event-script VM's own debugger state and breakpoints, with a disassembler matching ffx-decomp's.");
        Txt.B("Panels: the PC port's ten tweak panels, drawn by the game itself.");
        Txt.B("Battle: the BattleConfig switches, the Ms debug cheats, the scene picker and an actor inspector.");
        Txt.B("Field: noclip and speed from the debug pad, encounters, map jumps, developer saves, the free camera.");
        Txt.B("Log: dbgPrintf/scePrintf, compiled to nothing in the shipped game.");
        ImGui.Spacing();
        Txt.W("Not revivable as they were: the PS2 debug windows (Gu toolkit, rc map tool, effect inspector) and the "
                        + "original on-screen script debugger draw through PS2 GS packets that the PC port never sends, and the "
                        + "remote debugger link (Deci2) is compiled out. The Scripts and Battle tabs rebuild what they showed.");
    }
}
