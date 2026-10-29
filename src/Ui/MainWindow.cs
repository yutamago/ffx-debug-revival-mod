// SPDX-License-Identifier: MIT OR Apache-2.0

using Yuta.DebugRevival.Features;
using Yuta.DebugRevival.Native;

namespace Yuta.DebugRevival.Ui;

/// <summary>The mod's window (F9): one tab per area.</summary>
public sealed class MainWindow(
    ScriptDebuggerUi script, TweakPanels panels, BattleDebug battle, FieldTools field, DebugLog log, Guards guards) {

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

    public void draw() {
        if (ImGui.IsKeyPressed(ImGuiKey.F9, false)) open = !open;
        if (!open) return;

        ImGui.SetNextWindowSize(new Vector2(980, 640), ImGuiCond.FirstUseEver);
        if (ImGui.Begin("Debug Revival (F9)", ref open)) {
            string status = GameThread.last_status;
            if (status.Length > 0) ImGui.TextColored(YELLOW, status);
            if (ImGui.BeginTabBar("tabs")) {
                if (ImGui.BeginTabItem("Scripts")) { script.draw(); ImGui.EndTabItem(); }
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
        ImGui.TextWrapped("The PC port's developer panels. The game still draws them (small text, top left); "
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
            ImGui.BeginDisabled(p.ViewerOnly);
            if (ImGui.RadioButton($"{p.Name}##p{p.Mode}", mode == p.Mode)) panels.show(p.Mode);
            ImGui.EndDisabled();
            ImGui.SameLine();
            bool danger = p.Mode is 8 or 9;
            if (danger) ImGui.TextColored(RED, p.Note);
            else ImGui.TextDisabled(p.Note);
            if (mode == p.Mode && TweakPanels.cursor(p) is int c and >= 0) {
                ImGui.SameLine();
                ImGui.Text($"[line {c}]");
            }
        }
        ImGui.Separator();
        ImGui.TextWrapped("Blocked for safety: Basic debug line 12 'Achievements Reset' (it would wipe your Steam stats and achievements; "
                        + $"blocked {guards.blocked_steam_resets} times). Line 9 'Game Section' + Enter loads a developer save over your game; "
                        + "it only works when allowed in the Field tab.");
    }

    // ------------------------------------------------------------------ battle

    private void draw_battle() {
        BattleDebug.Snapshot? s = battle.snap;
        if (s is null) { ImGui.TextDisabled("Waiting for the game loop."); return; }
        ImGui.Text(s.BtlStatus == 0 ? "Not in battle." : $"In battle (status {s.BtlStatus}).");

        if (ImGui.CollapsingHeader("Switches", ImGuiTreeNodeFlags.DefaultOpen)) {
            ImGui.TextDisabled("The developers' BattleConfig switches. They take effect at once unless noted.");
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
                if (colour is Vector4 col) ImGui.TextColored(col, t.Effect + note);
                else ImGui.TextDisabled(t.Effect);
            }
        }

        if (ImGui.CollapsingHeader("Cheats")) {
            ImGui.TextColored(RED, "These change your game state; saving afterwards keeps the change.");
            foreach (BattleDebug.Cheat c in BattleDebug.CHEATS) {
                if (ImGui.Button($"{c.Label}##c{c.Va:x}")) {
                    if (c.SaveChanging) { _confirm_cheat = c; ImGui.OpenPopup("Confirm cheat"); }
                    else battle.run_cheat(c);
                }
                ImGui.SameLine();
                ImGui.TextDisabled(c.Effect + (c.StoryBreaking ? " (out of story order)" : "") + (c.BattleOnly ? " (battle only)" : ""));
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
                ImGui.Text(c is null ? "" : $"{c.Label}: {c.Effect}.");
                ImGui.TextColored(RED, "This edits the game state that your next save writes.");
                if (ImGui.Button("Do it") && c is not null) { battle.run_cheat(c); ImGui.CloseCurrentPopup(); }
                ImGui.SameLine();
                if (ImGui.Button("Cancel")) ImGui.CloseCurrentPopup();
                ImGui.EndPopup();
            }
        }

        if (ImGui.CollapsingHeader("Start a battle")) {
            ImGui.TextWrapped("The developers' scene picker: a battle field (record index in the battle field list) and an encounter "
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
            ImGui.Text(battle.picker_result);
        }

        if (ImGui.CollapsingHeader("Actors", ImGuiTreeNodeFlags.DefaultOpen)) {
            bool status_line = battle.native_status_line;
            if (ImGui.Checkbox("Game's own debug status line (experimental)", ref status_line)) battle.native_status_line = status_line;
            ImGui.SameLine();
            ImGui.TextDisabled("drawn by FFX.exe's debug text buffer when a message window is drawn");
            draw_actors(s);
        }
    }

    private void draw_actors(BattleDebug.Snapshot s) {
        if (s.Actors.Length == 0) { ImGui.TextDisabled("No actors."); return; }
        if (!ImGui.BeginTable("actors", 12, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.Borders)) return;
        foreach (string h in (string[])[ "#", "chr", "HP", "max", "MP", "max", "STR/DEF/MAG/MDF", "AGI/LCK/EVA/ACC", "OD", "CTB", "status", "KO" ]) ImGui.TableSetupColumn(h);
        ImGui.TableHeadersRow();
        foreach (BattleDebug.ActorRow a in s.Actors) {
            ImGui.TableNextRow();
            ImGui.TableNextColumn(); ImGui.Text(a.Index < 0x12 ? $"P{a.Index}" : $"M{a.Index - 0x14}");
            ImGui.TableNextColumn(); ImGui.Text($"0x{a.ChrId:x}");
            ImGui.TableNextColumn(); edit_int($"hp{a.Index}", a.Hp, v => battle.set_actor(a.Index, 0x5d0, v, false));
            ImGui.TableNextColumn(); ImGui.Text(a.MaxHp.ToString(CultureInfo.InvariantCulture));
            ImGui.TableNextColumn(); edit_int($"mp{a.Index}", a.Mp, v => battle.set_actor(a.Index, 0x5d4, v, false));
            ImGui.TableNextColumn(); ImGui.Text(a.MaxMp.ToString(CultureInfo.InvariantCulture));
            ImGui.TableNextColumn(); ImGui.Text($"{a.Str}/{a.Def}/{a.Mag}/{a.MDef}");
            ImGui.TableNextColumn(); ImGui.Text($"{a.Agi}/{a.Luck}/{a.Eva}/{a.Acc}");
            ImGui.TableNextColumn(); edit_int($"od{a.Index}", a.OdGauge, v => battle.set_actor(a.Index, 0x5bc, v, true), $"/{a.OdMax}");
            ImGui.TableNextColumn(); edit_int($"ctb{a.Index}", a.Ctb, v => battle.set_actor(a.Index, 0x65c, v, true));
            ImGui.TableNextColumn(); ImGui.Text($"{a.Status:x4} {a.Status2:x4}");
            ImGui.TableNextColumn(); ImGui.Text(a.Ko == 2 ? "KO" : "");
        }
        ImGui.EndTable();
    }

    private static void edit_int(string id, int value, Action<int> set, string suffix = "") {
        int v = value;
        ImGui.SetNextItemWidth(70);
        if (ImGui.InputInt($"##{id}", ref v, 0, 0, ImGuiInputTextFlags.EnterReturnsTrue)) set(v);
        if (suffix.Length > 0) { ImGui.SameLine(); ImGui.Text(suffix); }
    }

    // ------------------------------------------------------------------- field

    private void draw_field() {
        FieldTools.Snapshot? s = field.snap;
        if (s is null) { ImGui.TextDisabled("Waiting for the game loop."); return; }

        ImGui.Text($"Map {s.EventId} (0x{s.EventId:x}) {s.EventName}");
        ImGui.SameLine();
        if (ImGui.SmallButton("name")) field.name_current_event();
        ImGui.Text(s.JumpAllowed ? "The event allows leaving the map now." : "The event does not allow leaving the map now.");

        ImGui.SeparatorText("Movement");
        bool noclip = field.noclip;
        if (ImGui.Checkbox("Walk through walls", ref noclip)) field.noclip = noclip;
        ImGui.SameLine();
        ImGui.TextDisabled("the debug pad's L1");
        int speed = s.ExecFrames;
        ImGui.SetNextItemWidth(160);
        if (ImGui.SliderInt("Game frames per frame", ref speed, 1, 8)) field.set_exec_frames(speed);
        ImGui.SameLine();
        ImGui.TextDisabled("the debug pad's L1+R1 speed-up");
        bool encounters = s.Encounters != 0;
        if (ImGui.Checkbox("Random encounters", ref encounters)) field.set_encounters(encounters);

        ImGui.SeparatorText("Free camera");
        ImGui.Text($"Fly mode: {(s.FreeCamType switch { -1 => "-", 0 => "off", _ => "on" })}   photo pause: {(s.PhotoPause ? "on" : "off")}");
        if (ImGui.Button("Toggle fly mode")) field.toggle_fly();
        ImGui.SameLine();
        if (ImGui.Button("Toggle photo pause")) field.toggle_photo_pause();
        bool keys = field.bind_camera_keys;
        if (ImGui.Checkbox("Keys: F10 fly mode, F11 photo pause", ref keys)) field.bind_camera_keys = keys;
        ImGui.TextDisabled("The PC port's fly camera (mouse and keyboard) and world freeze; they shipped without key bindings.");

        ImGui.SeparatorText("Map jump");
        ImGui.SetNextItemWidth(120);
        ImGui.InputInt("Map", ref _map);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(120);
        ImGui.InputInt("Entrance", ref _entrance);
        ImGui.SameLine();
        ImGui.BeginDisabled(!s.JumpAllowed);
        if (ImGui.Button("Jump")) field.jump(_map, _entrance);
        ImGui.EndDisabled();
        ImGui.TextColored(RED, "Jumping out of story order can break progression.");

        ImGui.SeparatorText("Developer saves");
        ImGui.TextWrapped("FFX_Data holds about 110 saves the developers used to jump into each part of the story "
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
        ImGui.TextDisabled($"{log.total} lines");
        ImGui.TextDisabled("dbgPrintf and scePrintf, empty in the shipped game. If nothing arrives, Fahrenheit's print control may be taking them first.");
        if (ImGui.BeginChild("log", Vector2.Zero, ImGuiChildFlags.Borders, ImGuiWindowFlags.HorizontalScrollbar)) {
            foreach (string line in log.lines()) {
                if (_log_filter.Length > 0 && !line.Contains(_log_filter, StringComparison.OrdinalIgnoreCase)) continue;
                ImGui.TextUnformatted(line);
            }
            if (_log_autoscroll && ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 20) ImGui.SetScrollHereY(1.0f);
        }
        ImGui.EndChild();
    }

    private static void draw_about() {
        ImGui.TextWrapped("Debug Revival turns on the debug code the developers left in FFX.exe. What it covers:");
        ImGui.BulletText("Scripts: the event-script VM's own debugger state and breakpoints, with a disassembler matching ffx-decomp's.");
        ImGui.BulletText("Panels: the PC port's ten tweak panels, drawn by the game itself.");
        ImGui.BulletText("Battle: the BattleConfig switches, the Ms debug cheats, the scene picker and an actor inspector.");
        ImGui.BulletText("Field: noclip and speed from the debug pad, encounters, map jumps, developer saves, the free camera.");
        ImGui.BulletText("Log: dbgPrintf/scePrintf, compiled to nothing in the shipped game.");
        ImGui.Spacing();
        ImGui.TextWrapped("Not revivable as they were: the PS2 debug windows (Gu toolkit, rc map tool, effect inspector) and the "
                        + "original on-screen script debugger draw through PS2 GS packets that the PC port never sends, and the "
                        + "remote debugger link (Deci2) is compiled out. The Scripts and Battle tabs rebuild what they showed.");
    }
}
