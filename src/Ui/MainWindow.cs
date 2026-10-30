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

    private const float PANEL_SCALE = 0.5f, PANEL_X = 3.0f, PANEL_Y = 0f;

    public bool open;

    // Battle tab
    private int _field_index;
    private int _encounter_index;
    private BattleDebug.Cheat? _confirm_cheat;

    // Field tab
    private int _map = -1;   // -1 until the field snapshot names the current map
    private int _entrance;
    private int _viewer_save = 1;
    private string _map_filter = "";
    private bool _show_test_maps = true;
    private bool _maps_requested;

    // Log tab
    private string _log_filter = "";
    private bool _log_autoscroll = true;

    // Panels tab
    private float _panel_scale = PANEL_SCALE, _panel_x = PANEL_X, _panel_y = PANEL_Y;

    public void draw() {
        if (ImGui.IsKeyPressed(ImGuiKey.F9, false)) open = !open;
        if (!open) return;

        ImGui.SetNextWindowSize(new Vector2(980, 640), ImGuiCond.FirstUseEver);
        if (ImGui.Begin("Debug Revival", ref open)) {
            bool dbg = flag.on;
            if (ImGui.Checkbox("Debug flag", ref dbg)) flag.set(dbg, panels);
            Txt.Help("The developers' debug mode. While it's on:\n"
                   + "- L1 walks through walls, L2 and L1+R1 change the game speed\n"
                   + "- you can always leave a map with a map jump\n"
                   + "- a New Game starts at story step 5 instead of 0\n"
                   + "- outside a battle, L1+R1+L2+R2+X starts the battle picked in the Battle tab\n"
                   + "- the four model-viewer panels work");
            string status = GameThread.last_status;
            if (status.Length > 0) {
                ImGui.SameLine();
                Txt.C(YELLOW, status);
            }
            if (ImGui.BeginTabBar("tabs")) {
                if (ImGui.BeginTabItem("Scripts"))  { script.draw(); ImGui.EndTabItem(); }
                if (ImGui.BeginTabItem("PS2 menu")) { draw_gu(); ImGui.EndTabItem(); }
                if (ImGui.BeginTabItem("Panels"))   { draw_panels(); ImGui.EndTabItem(); }
                if (ImGui.BeginTabItem("Battle"))   { draw_battle(); ImGui.EndTabItem(); }
                if (ImGui.BeginTabItem("Field"))    { draw_field(); ImGui.EndTabItem(); }
                if (ImGui.BeginTabItem("Log"))      { draw_log(); ImGui.EndTabItem(); }
                ImGui.EndTabBar();
            }
        }
        ImGui.End();
    }

    // ------------------------------------------------------------------ panels

    private void draw_panels() {
        TweakPanels.Snapshot snap = panels.snap;
        bool input = panels.input_enabled;
        if (ImGui.Checkbox("Panel keys", ref input)) panels.input_enabled = input;
        Txt.Help("The game draws the shown panel in small text at the top left. With Panel keys on, "
               + "I/K or d-pad up/down pick a line, J/L or left/right change it, Enter or Circle activates it. "
               + "The game sees those keys too.");
        ImGui.SameLine();
        if (ImGui.Button("Hide panels")) panels.show(TweakPanels.NONE);
        ImGui.Separator();
        foreach (TweakPanels.Panel p in TweakPanels.PANELS) {
            bool usable = panels.usable(p);
            ImGui.BeginDisabled(!usable);
            if (ImGui.RadioButton($"{p.Name}##p{p.Mode}", snap.Mode == p.Mode)) panels.show(p.Mode);
            ImGui.EndDisabled();
            ImGui.SameLine();
            Txt.D(usable ? p.Note : $"needs the debug flag: {p.Note}");
            if (p.Warning is string warning) { ImGui.SameLine(); Txt.C(RED, warning); }
            if (snap.Mode == p.Mode && snap.Cursor >= 0) { ImGui.SameLine(); Txt.T($"[line {snap.Cursor}]"); }
        }

        ImGui.SeparatorText("Size and position");
        bool changed = false;
        ImGui.SetNextItemWidth(220);
        changed |= ImGui.SliderFloat("Text size", ref _panel_scale, 0.15f, 0.6f, "%.2f");
        ImGui.SetNextItemWidth(220);
        changed |= ImGui.SliderFloat("Up / down", ref _panel_y, -6f, 12f, "%.1f");
        ImGui.SetNextItemWidth(220);
        changed |= ImGui.SliderFloat("Left / right", ref _panel_x, -2f, 6f, "%.1f");
        ImGui.SameLine();
        if (ImGui.Button("Original size")) {
            (_panel_scale, _panel_x, _panel_y) = (PANEL_SCALE, PANEL_X, PANEL_Y);
            changed = true;
        }
        if (changed) panels.set_layout(_panel_scale, _panel_x, _panel_y);

        if (snap.Lines.Length > 0 && ImGui.CollapsingHeader("Panel text", ImGuiTreeNodeFlags.DefaultOpen)) {
            foreach (string l in snap.Lines) Txt.T(l);
        }
    }

    // ---------------------------------------------------------------- PS2 menu

    private void draw_gu() {
        bool open = gu.open;
        if (ImGui.Checkbox("Show", ref open)) gu.show(open);
        ImGui.SameLine();
        bool cap = gu.capture_mouse;
        if (ImGui.Checkbox("Mouse controls the menu", ref cap)) gu.capture_mouse = cap;
        Txt.Help("The developers' own debug menu from the PS2 version: a column of buttons at the right edge of the "
               + "screen. Click the top buttons or turn the mouse wheel to switch pages. With 'Mouse controls the menu' "
               + "on, the mouse works the menu wherever this window isn't. Items that would hang the game or clear "
               + "your story progress are blocked.");

        ImGui.SeparatorText("Battle windows");
        Txt.D("The developers' battle windows. A few more only work inside a battle and are left out.");
        foreach (var (n, name) in GuToolkit.BATTLE_WINDOWS) {
            if (ImGui.Button($"{name}##bw{n}")) gu.open_battle_window(n);
            ImGui.SameLine();
        }
        ImGui.NewLine();
    }

    // ------------------------------------------------------------------ battle

    private void draw_battle() {
        BattleDebug.Snapshot? s = battle.snap;
        if (s is null) { Txt.D("Waiting for the game."); return; }
        bool in_battle = s.BtlStatus != 0;
        Txt.T(in_battle ? "In battle." : "Not in battle.");

        if (ImGui.CollapsingHeader("Switches", ImGuiTreeNodeFlags.DefaultOpen)) {
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
                Txt.D(t.Effect);
                string? risk = t.Risk switch {
                    BattleDebug.Risk.Stalls   => "stalls the battle while on",
                    BattleDebug.Risk.Progress => "loses the battle's progress",
                    BattleDebug.Risk.Untested => "untested",
                    _ => null,
                };
                if (risk is not null) { ImGui.SameLine(); Txt.C(RED, risk); }
            }
        }

        if (ImGui.CollapsingHeader("Cheats")) {
            foreach (BattleDebug.Cheat c in BattleDebug.CHEATS) {
                ImGui.BeginDisabled(c.BattleOnly && !in_battle);
                if (ImGui.Button($"{c.Label}##c{c.Va:x}")) {
                    if (c.SaveChanging) { _confirm_cheat = c; ImGui.OpenPopup("Confirm cheat"); }
                    else battle.run_cheat(c);
                }
                ImGui.EndDisabled();
                ImGui.SameLine();
                Txt.D(c.Effect);
            }
            ImGui.BeginDisabled(!in_battle);
            if (ImGui.Button("Party to 1 HP")) battle.weak(false, true);
            ImGui.SameLine();
            if (ImGui.Button("Party to full HP")) battle.weak(false, false);
            ImGui.SameLine();
            if (ImGui.Button("Monsters to 1 HP")) battle.weak(true, true);
            ImGui.SameLine();
            if (ImGui.Button("Monsters to full HP")) battle.weak(true, false);
            ImGui.EndDisabled();

            bool popup_open = true;
            if (ImGui.BeginPopupModal("Confirm cheat", ref popup_open, ImGuiWindowFlags.AlwaysAutoResize)) {
                BattleDebug.Cheat? c = _confirm_cheat;
                Txt.T(c is null ? "" : $"{c.Label}: {c.Effect}.");
                Txt.C(RED, c is { StoryBreaking: true } ? "Out of story order. Your next save keeps this." : "Your next save keeps this.");
                if (ImGui.Button("Do it") && c is not null) { battle.run_cheat(c); ImGui.CloseCurrentPopup(); }
                ImGui.SameLine();
                if (ImGui.Button("Cancel")) ImGui.CloseCurrentPopup();
                ImGui.EndPopup();
            }
        }

        if (ImGui.CollapsingHeader("Start a battle")) {
            Txt.D("Pick a battle area and an encounter in it, look it up, then start it from the field.");
            ImGui.SetNextItemWidth(120);
            ImGui.InputInt("Field", ref _field_index);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(120);
            ImGui.InputInt("Encounter", ref _encounter_index);
            ImGui.SameLine();
            if (ImGui.Button("Look up")) battle.pick(_field_index, _encounter_index);
            ImGui.SameLine();
            ImGui.BeginDisabled(in_battle);
            if (ImGui.Button("Start")) battle.start_picked();
            ImGui.EndDisabled();
            Txt.T(battle.picker_result);
        }

        if (ImGui.CollapsingHeader("Actors", ImGuiTreeNodeFlags.DefaultOpen)) {
            bool status_line = battle.native_status_line;
            if (ImGui.Checkbox("Game's debug status line", ref status_line)) battle.native_status_line = status_line;
            Txt.Help("The developers' one-line battle status, drawn by the game in battle while a message window is shown. Experimental.");
            draw_actors(s);
        }
    }

    private void draw_actors(BattleDebug.Snapshot s) {
        if (s.Actors.Length == 0) return;
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
        if (s is null) { Txt.D("Waiting for the game."); return; }
        if (!_maps_requested) { field.load_maps(); _maps_requested = true; }
        if (_map < 0) _map = s.EventId;

        Txt.T($"Map {s.EventId} {map_name(s.EventId)}{(s.Place.Length > 0 ? $"   {s.Place}" : "")}");

        ImGui.SeparatorText("Movement");
        bool noclip = field.noclip;
        if (ImGui.Checkbox("Walk through walls", ref noclip)) field.noclip = noclip;
        ImGui.SameLine();
        bool encounters = s.Encounters != 0;
        if (ImGui.Checkbox("Random encounters", ref encounters)) field.set_encounters(encounters);
        int speed = s.ExecFrames;
        ImGui.SetNextItemWidth(160);
        if (ImGui.SliderInt("Game speed", ref speed, 1, 8, "x%d")) field.set_exec_frames(speed);

        ImGui.SeparatorText("Free camera");
        if (ImGui.Button(s.FreeCamType > 0 ? "Stop flying" : "Fly")) {
            field.toggle_fly();
            open = false; // while this window is open the game gets no mouse or keyboard input
        }
        ImGui.SameLine();
        if (ImGui.Button(s.PhotoPause ? "Resume" : "Photo pause")) field.toggle_photo_pause();
        ImGui.SameLine();
        float fly_speed = s.FlySpeed;
        ImGui.SetNextItemWidth(160);
        if (ImGui.SliderFloat("Fly speed", ref fly_speed, 5f, 400f, "%.0f")) field.set_fly_speed(fly_speed);
        bool keys = field.bind_camera_keys;
        if (ImGui.Checkbox("F10 fly, F11 photo pause", ref keys)) field.bind_camera_keys = keys;
        Txt.Help("Fly closes this window, since the game gets no mouse or keyboard input while it's open; F9 brings it back. "
               + "Move the mouse to look, Up/Down move forward and back, Left/Right strafe. Right-drag moves closer, "
               + "middle-drag pans, left-drag turns faster. Photo pause freezes the world and mutes the sound.");
        string[] op = gu.op_lines;
        if (op.Length > 0) Txt.D(string.Join("   ", op));

        ImGui.SeparatorText("Map jump");
        Txt.C(RED, "Jumping out of story order can break your progress.");
        FieldTools.MapEntry[]? maps = field.maps;
        if (maps is not null) {
            ImGui.SetNextItemWidth(160);
            ImGui.InputTextWithHint("##mapfilter", "name or number", ref _map_filter, 32);
            ImGui.SameLine();
            ImGui.Checkbox("Test maps", ref _show_test_maps);
            ImGui.SetNextItemWidth(320);
            if (ImGui.BeginCombo("Map", $"{_map}: {map_name(_map)}")) {
                foreach (FieldTools.MapEntry m in maps) {
                    if (!_show_test_maps && is_test(m.Name)) continue;
                    if (_map_filter.Length > 0 && !m.Name.Contains(_map_filter, StringComparison.OrdinalIgnoreCase)
                     && !m.Id.ToString(CultureInfo.InvariantCulture).StartsWith(_map_filter, StringComparison.Ordinal)) continue;
                    ImGui.BeginDisabled(!m.HasScript);
                    if (ImGui.Selectable($"{m.Id,3}: {m.Name}##m{m.Id}", m.Id == _map)) _map = m.Id;
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
        if (ImGui.Button("Model viewer")) field.go_to_model_viewer(panels);
        Txt.Help("Goes to the developers' model viewer map and turns the debug flag on, so its panels work and you can leave again.");

        ImGui.SeparatorText("Developer saves");
        Txt.W("Saves the developers used to start each part of the story. Loading one replaces the running game and "
            + "takes you to its map. Your own save files stay as they are until you save.");
        ImGui.SetNextItemWidth(120);
        ImGui.InputInt("Section", ref _viewer_save);
        _viewer_save = Math.Clamp(_viewer_save, 1, FieldTools.VIEWER_SAVE_COUNT);
        ImGui.SameLine();
        ImGui.BeginDisabled(!s.JumpAllowed);
        if (ImGui.Button("Load")) field.load_viewer_save(_viewer_save);
        ImGui.EndDisabled();
        ImGui.SameLine();
        bool allow = guards.allow_viewer_saves;
        if (ImGui.Checkbox("Basic debug panel may load them", ref allow)) guards.allow_viewer_saves = allow;
    }

    private string map_name(int id) => field.maps?.FirstOrDefault(m => m.Id == id)?.Name ?? "";

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
        ImGui.InputTextWithHint("##logfilter", "filter", ref _log_filter, 128);
        ImGui.SameLine();
        Txt.D($"{log.total} lines");
        Txt.Help("Messages the game's code still writes to the developers' console. The released game never shows them.");
        if (ImGui.BeginChild("log", Vector2.Zero, ImGuiChildFlags.Borders, ImGuiWindowFlags.HorizontalScrollbar)) {
            foreach (string line in log.lines()) {
                if (_log_filter.Length > 0 && !line.Contains(_log_filter, StringComparison.OrdinalIgnoreCase)) continue;
                Txt.T(line);
            }
            if (_log_autoscroll && ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 20) ImGui.SetScrollHereY(1.0f);
        }
        ImGui.EndChild();
    }
}
