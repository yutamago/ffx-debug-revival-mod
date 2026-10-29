// SPDX-License-Identifier: MIT OR Apache-2.0

using Yuta.DebugRevival.Script;

namespace Yuta.DebugRevival.Ui;

/// <summary>The script debugger's tab: ctrl works, workers, disassembly, stack, signals, variables.</summary>
public sealed class ScriptDebuggerUi(ScriptDebugger dbg) {
    private static readonly Vector4 RED    = new(1.0f, 0.35f, 0.35f, 1);
    private static readonly Vector4 YELLOW = new(1.0f, 0.85f, 0.3f, 1);
    private static readonly Vector4 GREY   = new(0.6f, 0.6f, 0.6f, 1);

    private bool _follow_pc = true;
    private bool _show_namespaces;
    private uint _last_pc = uint.MaxValue;
    private int  _last_worker = -1;

    public void draw() {
        int sel = dbg.selected_ctrl;
        CtrlSnap? cs = dbg.ctrl(sel);

        ImGui.SetNextItemWidth(260);
        if (ImGui.BeginCombo("Ctrl work", label(sel, cs))) {
            for (int c = 0; c < Native.G.CtrlWorkCount; c++) {
                if (ImGui.Selectable(label(c, dbg.ctrl(c)), c == sel)) {
                    dbg.selected_ctrl = c;
                    dbg.selected_worker = -1;
                }
            }
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        if (ImGui.Button("Pause")) dbg.pause(sel);
        ImGui.SameLine();
        if (ImGui.Button("Continue")) dbg.resume(sel);
        ImGui.SameLine();
        if (ImGui.Button("One frame")) dbg.frame_step(sel);
        ImGui.SameLine();
        ImGui.BeginDisabled(dbg.selected_worker < 0);
        if (ImGui.Button("Step worker")) dbg.step(sel, dbg.selected_worker);
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("Clear breakpoints")) dbg.clear_all_breakpoints();

        if (cs is null) { ImGui.TextDisabled("No snapshot yet: the scripts have not run since the mod loaded."); return; }
        if (!cs.InUse) { ImGui.TextDisabled($"Ctrl work {sel} is not in use."); return; }

        ImGui.Text($"State: {cs.state_text}   workers: {cs.Actors} with code, {cs.Total} in all   block: 0x{cs.Block:x8}");
        foreach (BreakpointSnap b in cs.Breakpoints) {
            if (b.Id >= 0 && b.Hit != 0 && cs.State == 3) {
                ImGui.TextColored(RED, $"Stopped at breakpoint {b.Slot + 1}. Continue runs the instruction it replaced.");
            }
        }
        if (sel != 0) ImGui.TextColored(GREY, "Breakpoints work in the event's scripts (ctrl 0). Battle code also runs scripts outside the frame, so pausing a battle ctrl work may not freeze everything.");
        if (dbg.status.Length > 0) ImGui.TextColored(YELLOW, dbg.status);

        Vector2 avail = ImGui.GetContentRegionAvail();
        if (ImGui.BeginChild("workers", new Vector2(330, avail.Y), ImGuiChildFlags.Borders)) {
            ImGui.Checkbox("Show subroutine workers", ref _show_namespaces);
            draw_workers(cs);
        }
        ImGui.EndChild();
        ImGui.SameLine();
        if (ImGui.BeginChild("detail", new Vector2(0, avail.Y), ImGuiChildFlags.Borders)) {
            WorkerSnap? w = cs.Workers.FirstOrDefault(x => x.Index == dbg.selected_worker);
            if (w is null) ImGui.TextDisabled("Select a worker.");
            else draw_worker(cs, w);
        }
        ImGui.EndChild();
    }

    private static string label(int c, CtrlSnap? cs) {
        string state = cs is null ? "-" : !cs.InUse ? "unused" : $"{cs.state_text}, {cs.Actors} workers";
        return $"{c}: {CtrlSnap.role(c)} ({state})";
    }

    private void draw_workers(CtrlSnap cs) {
        if (!ImGui.BeginTable("wt", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingFixedFit)) return;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("#");
        ImGui.TableSetupColumn("type");
        ImGui.TableSetupColumn("entry");
        ImGui.TableSetupColumn("state");
        ImGui.TableSetupColumn("pc");
        ImGui.TableHeadersRow();
        foreach (WorkerSnap w in cs.Workers) {
            if (!w.HasLevels && !_show_namespaces) continue;
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            bool selected = w.Index == dbg.selected_worker;
            if (ImGui.Selectable($"w{w.Index}##w{w.Index}", selected, ImGuiSelectableFlags.SpanAllColumns)) {
                dbg.selected_worker = w.Index;
                dbg.listing_override = -1;
            }
            ImGui.TableNextColumn();
            ImGui.Text(w.Kind.ToString(CultureInfo.InvariantCulture));
            ImGui.TableNextColumn();
            ImGui.Text(w.HasLevels ? $"e{w.Entry}" : "");
            ImGui.TableNextColumn();
            ImGui.Text(w.HasLevels ? w.run_state : "namespace");
            ImGui.TableNextColumn();
            ImGui.Text(w.PcValid ? $"{w.Pc:x5}" : "");
        }
        ImGui.EndTable();
    }

    private void draw_worker(CtrlSnap cs, WorkerSnap w) {
        ImGui.Text($"Worker w{w.Index}  type {w.Kind}  entry e{w.Entry}  level {w.Level}  {w.run_state}  {w.wait_text}");
        if (w.Calls.Length > 0) {
            ImGui.Text("Call stack: " + string.Join("  <-  ", w.Calls.Reverse().Select(f => $"w{f.Callee} (returns to {f.ReturnOffset:x5})")));
        }
        if (!ImGui.BeginTabBar("worker_tabs")) return;
        if (ImGui.BeginTabItem("Code"))      { draw_code(cs, w);    ImGui.EndTabItem(); }
        if (ImGui.BeginTabItem("Stack"))     { draw_stack(w);       ImGui.EndTabItem(); }
        if (ImGui.BeginTabItem("Signals"))   { draw_signals(w);     ImGui.EndTabItem(); }
        if (ImGui.BeginTabItem("Variables")) { draw_variables();    ImGui.EndTabItem(); }
        ImGui.EndTabBar();
    }

    private void draw_code(CtrlSnap cs, WorkerSnap w) {
        Listing? l = dbg.listing;
        ImGui.Checkbox("Follow PC", ref _follow_pc);
        ImGui.SameLine();
        ImGui.TextDisabled(cs.Index == 0 ? "Click an instruction to set or clear a breakpoint (4 at most)." : "");
        if (l is null) { ImGui.TextDisabled("No listing yet."); return; }
        if (l.Error.Length > 0) { ImGui.TextColored(RED, l.Error); return; }
        ImGui.TextDisabled($"code of block worker {l.LocalIndex}" + (l.LocalIndex != w.LocalIndex ? $" (w{w.Index} is inside a call)" : ""));

        HashSet<uint> bp_offsets = [];
        nint code = l.Code;
        foreach (BreakpointSnap b in cs.Breakpoints) {
            if (b.Id >= 0 && code != 0 && b.Ptr >= code) bp_offsets.Add((uint)(b.Ptr - code));
        }

        if (!ImGui.BeginChild("code", Vector2.Zero, ImGuiChildFlags.None, ImGuiWindowFlags.HorizontalScrollbar)) { ImGui.EndChild(); return; }
        float lh = ImGui.GetTextLineHeightWithSpacing();
        int pc_line = w.PcValid && l.LineOf.TryGetValue(w.Pc, out int pl) ? pl : -1;
        if (_follow_pc && pc_line >= 0 && (w.Pc != _last_pc || w.Index != _last_worker)) {
            ImGui.SetScrollY(Math.Max(0, pc_line * lh - ImGui.GetWindowHeight() / 2));
        }
        _last_pc = w.Pc;
        _last_worker = w.Index;

        // Draw only the visible lines.
        int first = Math.Max(0, (int)(ImGui.GetScrollY() / lh) - 1);
        int count = (int)(ImGui.GetWindowHeight() / lh) + 3;
        int last = Math.Min(l.Lines.Count, first + count);
        if (first > 0) ImGui.Dummy(new Vector2(1, first * lh));
        for (int i = first; i < last; i++) {
            Line line = l.Lines[i];
            if (line.IsLabel) {
                ImGui.TextColored(GREY, line.Text);
                continue;
            }
            bool is_pc = i == pc_line;
            bool is_bp = bp_offsets.Contains(line.Offset);
            string text = $"{(is_bp ? "*" : " ")}{(is_pc ? ">" : " ")} {line.Offset:x5}:{line.Text}";
            if (is_pc) ImGui.PushStyleColor(ImGuiCol.Text, YELLOW);
            else if (is_bp) ImGui.PushStyleColor(ImGuiCol.Text, RED);
            if (ImGui.Selectable($"{text}##{i}", is_pc) && cs.Index == 0) {
                dbg.toggle_breakpoint(cs.Index, l.Block, line.Offset);
            }
            if (is_pc || is_bp) ImGui.PopStyleColor();
        }
        if (last < l.Lines.Count) ImGui.Dummy(new Vector2(1, (l.Lines.Count - last) * lh));
        ImGui.EndChild();
    }

    private static void draw_stack(WorkerSnap w) {
        ImGui.Text($"I0..I3: {string.Join(", ", w.IntRegs)}");
        ImGui.Text($"F0..F9: {string.Join(", ", w.FloatRegs.Select(RustFloat.debug))}");
        ImGui.Text($"A: {w.RegA} (0x{w.RegA:x})   B: {w.RegB} (0x{w.RegB:x})   float acc: {RustFloat.debug(w.FloatAcc)}");
        ImGui.Separator();
        ImGui.Text($"Value stack ({w.Stack.Length} of 19), top last:");
        for (int k = 0; k < w.Stack.Length; k++) {
            (uint v, byte kind) = w.Stack[k];
            string s = kind == 2 ? RustFloat.debug(BitConverter.UInt32BitsToSingle(v)) : $"{(int)v} (0x{v:x})";
            ImGui.Text($"  [{k}] {(kind == 2 ? "float" : "int  ")} {s}");
        }
    }

    private static void draw_signals(WorkerSnap w) {
        if (w.Signals.Length == 0) { ImGui.TextDisabled("No signals queued."); return; }
        if (!ImGui.BeginTable("sig", 6, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit)) return;
        foreach (string h in (string[])[ "entry", "from", "to", "level", "state", "kind" ]) ImGui.TableSetupColumn(h);
        ImGui.TableHeadersRow();
        foreach (SignalSnap s in w.Signals) {
            ImGui.TableNextRow();
            ImGui.TableNextColumn(); ImGui.Text($"e{s.Entry}");
            ImGui.TableNextColumn(); ImGui.Text(s.From == 0xffff ? "-" : $"w{s.From}");
            ImGui.TableNextColumn(); ImGui.Text(s.To == 0xffff ? "-" : $"w{s.To}");
            ImGui.TableNextColumn(); ImGui.Text(s.Level.ToString(CultureInfo.InvariantCulture));
            ImGui.TableNextColumn(); ImGui.Text(s.State switch { 1 => "queued", 2 => "running", _ => s.State.ToString(CultureInfo.InvariantCulture) });
            ImGui.TableNextColumn(); ImGui.Text(s.Kind switch { 0 => "REQ", 1 => "REQSW", 2 => "REQEW", 3 => "reply", _ => s.Kind.ToString(CultureInfo.InvariantCulture) });
        }
        ImGui.EndTable();
    }

    private void draw_variables() {
        VarSnap[]? vars = dbg.variables;
        if (vars is null || vars.Length == 0) { ImGui.TextDisabled("No variables."); return; }
        ImGui.TextDisabled("Storage: GBL save data, LCL ctrl area, FIX/MY/DAT script block areas, ITR the worker's registers, LC2 block area.");
        if (!ImGui.BeginTable("vars", 6, ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingFixedFit)) return;
        ImGui.TableSetupScrollFreeze(0, 1);
        foreach (string h in (string[])[ "var", "storage", "type", "len", "offset", "value" ]) ImGui.TableSetupColumn(h);
        ImGui.TableHeadersRow();
        foreach (VarSnap v in vars) {
            ImGui.TableNextRow();
            ImGui.TableNextColumn(); ImGui.Text($"v{v.Index}");
            ImGui.TableNextColumn(); ImGui.Text(v.Storage);
            ImGui.TableNextColumn(); ImGui.Text(v.Type);
            ImGui.TableNextColumn(); ImGui.Text(v.Length.ToString(CultureInfo.InvariantCulture));
            ImGui.TableNextColumn(); ImGui.Text($"0x{v.Offset:x}");
            ImGui.TableNextColumn(); ImGui.Text(v.Value);
        }
        ImGui.EndTable();
    }
}
