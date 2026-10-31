// SPDX-License-Identifier: MIT

using Yuta.DebugRevival.Native;

namespace Yuta.DebugRevival.Script;

/// <summary>A script block in game memory; breakpoint bytes read as the instruction they replaced.</summary>
internal sealed unsafe class MemBlockReader(nint block, long size, IReadOnlyDictionary<long, byte> overlay) : IBlockReader {
    public bool in_range(long offset, int length) => offset >= 0 && offset + length <= size;
    public byte u8(long offset) => overlay.TryGetValue(offset, out byte b) ? b : *(byte*)(block + (nint)offset);
}

/// <summary>
///     The event-script debugger, built on the VM's own support (docs/research/script.md): ctrl work +8 is the
///     debugger state (0 run, 1 step, 2 one frame, 3 stopped) and +0x64 holds 4 breakpoints for opcode 0x40.
///     Game memory is touched only inside the <c>Atel_ExecCtrlWorks</c> hook, where no script is mid-instruction.
/// </summary>
public sealed unsafe class ScriptDebugger {
    private const uint BP_TAG = 0x56565244; // "DRVV" marks our entries
    private const int  MAX_STACK = 19;

    private readonly FhLogger _log;
    private readonly NativeNames _names;

    private readonly ConcurrentQueue<Action> _commands = new();

    /// <summary>One of our breakpoints. <c>Print</c> fingerprints the script it was set in.</summary>
    private sealed record Bp(int Ctrl, int Slot, nint Block, nint Ptr, byte Saved, ulong Print);

    private bool _in_pass;
    private int  _run_depth;
    private (int ctrl, nint worker, bool stepped)? _step;
    private readonly List<Bp> _our_bps = [];
    /// <summary>Breakpoints lifted by Continue, set again once no worker stands on them.</summary>
    private readonly List<Bp> _rearm = [];
    private int _listing_bp_version = -1;
    private bool _listing_original;
    private int _bp_version;

    private D.ExecCtrlWorksFn? _next_exec;
    private D.ScriptRunFn?     _next_run;

    private readonly CtrlSnap?[] _ctrls = new CtrlSnap?[G.CtrlWorkCount];
    public CtrlSnap? ctrl(int i) => Volatile.Read(ref _ctrls[i]);
    public volatile Listing? listing;
    public volatile VarSnap[]? variables;

    public volatile int selected_ctrl;
    public volatile int selected_worker = -1;
    /// <summary>The block worker whose code to list; -1 lists the one that holds the PC.</summary>
    public volatile int listing_override = -1;
    public volatile bool original_names;

    public ScriptDebugger(FhLogger log, NativeNames names) {
        _log   = log;
        _names = names;
    }

    public bool init(FhModule owner) => Calls.Atel_ExecCtrlWorks.hook(owner, h_exec_ctrl_works) && Calls.Script_Run.hook(owner, h_script_run);

    [UnmanagedCallConv(CallConvs = [ typeof(CallConvCdecl) ])]
    private uint h_exec_ctrl_works(int from, int to) {
        try {
            validate_breakpoints();
            rearm_breakpoints();
            while (_commands.TryDequeue(out Action? cmd)) cmd();
            if (_step is { } s && s.ctrl >= from && s.ctrl < to) {
                // The Script_Run hook lets only the stepped worker through.
                set_state(s.ctrl, 1);
            }
        }
        catch (Exception e) { _log.Error($"script debugger pre: {e}"); }

        _next_exec ??= Calls.Atel_ExecCtrlWorks.chain_from(h_exec_ctrl_works).fnptr!;
        _in_pass = true;
        uint r = _next_exec(from, to);
        _in_pass = false;

        try {
            if (_step is { } s && s.ctrl >= from && s.ctrl < to) {
                set_state(s.ctrl, 3);
                if (s.stepped) Status.done("stepped one instruction");
                else Status.refused("the worker did not run: it is waiting or not runnable");
                _step = null;
            }
            for (int c = Math.Max(0, from); c < Math.Min(to, G.CtrlWorkCount); c++) {
                Volatile.Write(ref _ctrls[c], snapshot_ctrl(c));
            }
            int sel = selected_ctrl;
            if (sel >= from && sel < to) snapshot_selected(sel);
        }
        catch (Exception e) { _log.Error($"script debugger post: {e}"); }
        return r;
    }

    [UnmanagedCallConv(CallConvs = [ typeof(CallConvCdecl) ])]
    private uint h_script_run(nint worker, uint mode, uint callbacks) {
        _next_run ??= Calls.Script_Run.chain_from(h_script_run).fnptr!;
        // Only the event update's own calls are filtered; nested runs from natives pass through.
        if (_run_depth == 0 && _in_pass && _step is { } s
         && Mem.u32(G.CurrentCtrlIdx) == (uint)s.ctrl
         && *(nint*)Mem.at(G.ScriptContext) == ctrl_ptr(s.ctrl)) {
            if (worker != s.worker || s.stepped) return 0; // the game's own "not runnable" result
            _step = s with { stepped = true };
            mode = 1;
        }
        _run_depth++;
        try {
            return _next_run(worker, mode, callbacks);
        }
        finally {
            _run_depth--;
        }
    }

    public void pause(int c)      => _commands.Enqueue(() => { set_state(c, 3); Status.done($"ctrl {c} paused"); });
    public void frame_step(int c) => _commands.Enqueue(() => { set_state(c, 2); Status.done($"ctrl {c}: one frame"); });

    /// <summary>Continues. The breakpoint it stopped on stays lifted until no worker stands on it, since an instruction that spans frames would stop again at once.</summary>
    public void resume(int c) => _commands.Enqueue(() => {
        foreach (Bp b in _our_bps.ToArray()) {
            nint e = ctrl_ptr(b.Ctrl) + 0x64 + b.Slot * 0x10;
            if (b.Ctrl == c && *(byte*)(e + 0xf) != 0) {
                clear_bp(b);
                _our_bps.Remove(b);
                _rearm.Add(b);
                _bp_version++;
            }
        }
        set_state(c, 0);
        Status.done($"ctrl {c} running");
    });

    /// <summary>Runs one instruction of one event worker, then leaves the event stopped.</summary>
    public void step(int c, int worker_index) => _commands.Enqueue(() => {
        if (c != 0) { Status.refused("single steps work only in the event's scripts (ctrl 0)"); return; }
        if (worker_index < 0) { Status.refused("select a worker first"); return; }
        CtrlSnap? cs = ctrl(c);
        WorkerSnap? w = cs?.Workers.FirstOrDefault(x => x.Index == worker_index);
        if (w is null || !w.HasLevels) { Status.refused("that worker has no code of its own to step"); return; }
        _step = (c, w.Ptr, false);
    });

    /// <summary>Sets or clears a breakpoint. <paramref name="print"/> fingerprints the listing the click came from, so a click on an outdated listing is refused.</summary>
    public void toggle_breakpoint(int c, nint block, ulong print, uint code_offset) => _commands.Enqueue(() => {
        if (c != 0) { Status.refused("breakpoints work only in the event's scripts (ctrl 0)"); return; }
        nint ctrl = ctrl_ptr(c);
        if ((*(byte*)ctrl & 1) == 0 || *(nint*)(ctrl + 0x48) != block || fingerprint(block) != print) {
            Status.refused("the script changed; click again in the new listing");
            return;
        }
        uint code_len = *(uint*)block;
        if (code_offset >= code_len) { Status.refused("outside the event's code"); return; }
        nint ptr = block + (nint)(*(uint*)(block + 0x30)) + (nint)code_offset;

        int existing = _our_bps.FindIndex(b => b.Ctrl == c && b.Ptr == ptr);
        if (existing >= 0) {
            clear_bp(_our_bps[existing]);
            _our_bps.RemoveAt(existing);
            _bp_version++;
            Status.done($"breakpoint at {code_offset:x5} cleared");
            return;
        }
        int rearming = _rearm.FindIndex(b => b.Ctrl == c && b.Ptr == ptr);
        if (rearming >= 0) {
            _rearm.RemoveAt(rearming);
            _bp_version++;
            Status.done($"breakpoint at {code_offset:x5} cleared");
            return;
        }
        if (*(byte*)ptr == 0x40) { Status.refused("there is a breakpoint byte there already"); return; }
        if (arm(new Bp(c, -1, block, ptr, *(byte*)ptr, print)) is int slot) Status.done($"breakpoint {slot + 1} at {code_offset:x5}");
        else Status.refused("all 4 breakpoints are in use");
    });

    public void clear_all_breakpoints() => _commands.Enqueue(() => {
        foreach (Bp b in _our_bps) clear_bp(b);
        _our_bps.Clear();
        _rearm.Clear();
        _bp_version++;
        Status.done("breakpoints cleared");
    });

    private static nint ctrl_ptr(int c) => Mem.at(G.AtelCtrlWorks) + c * G.CtrlWorkSize;

    private static void set_state(int c, byte v) {
        if (c < 0 || c >= G.CtrlWorkCount) return;
        *(byte*)(ctrl_ptr(c) + 8) = v;
    }

    /// <summary>Identifies a loaded script by the event id and the block's header.</summary>
    private static ulong fingerprint(nint block) {
        ulong h = 0xcbf29ce484222325UL;
        void mix(uint v) { h = (h ^ v) * 0x100000001b3UL; }
        mix((uint)*Fahrenheit.FFX.Globals.event_id);
        for (int k = 0; k < 0x40; k += 4) mix(*(uint*)(block + k));
        return h;
    }

    /// <summary>Takes a free slot of the ctrl work's table and patches 0x40 in; returns the slot.</summary>
    private int? arm(Bp b) {
        nint ctrl = ctrl_ptr(b.Ctrl);
        for (int slot = 0; slot < 4; slot++) {
            nint e = ctrl + 0x64 + slot * 0x10;
            if (*(int*)e >= 0) continue;
            *(uint*)(e + 4)   = BP_TAG;
            *(nint*)(e + 8)   = b.Ptr;
            *(byte*)(e + 0xe) = b.Saved;
            *(byte*)(e + 0xf) = 0;
            *(int*)e = slot;
            *(byte*)b.Ptr = 0x40;
            _our_bps.Add(b with { Slot = slot });
            _bp_version++;
            return slot;
        }
        return null;
    }

    /// <summary>Restores the byte and frees the entry, if the entry is still ours.</summary>
    private static void clear_bp(Bp b) {
        nint e = ctrl_ptr(b.Ctrl) + 0x64 + b.Slot * 0x10;
        bool ours = *(int*)e >= 0 && *(uint*)(e + 4) == BP_TAG && *(nint*)(e + 8) == b.Ptr;
        if (!ours) return;
        if (*(byte*)b.Ptr == 0x40) *(byte*)b.Ptr = b.Saved;
        *(int*)e = -1;
        *(uint*)(e + 4) = 0;
        *(byte*)(e + 0xf) = 0;
    }

    /// <summary>Whether the event still runs the script a breakpoint was set in.</summary>
    private static bool same_script(Bp b) {
        nint ctrl = ctrl_ptr(b.Ctrl);
        return (*(byte*)ctrl & 1) != 0 && *(nint*)(ctrl + 0x48) == b.Block && fingerprint(b.Block) == b.Print;
    }

    /// <summary>
    ///     Drops breakpoints whose script went away. A new event resets the table without restoring bytes, but the
    ///     byte is put back only in the same script: a new one at the same address may hold 0x40 as an operand.
    /// </summary>
    private void validate_breakpoints() {
        for (int i = _our_bps.Count - 1; i >= 0; i--) {
            Bp b = _our_bps[i];
            nint e = ctrl_ptr(b.Ctrl) + 0x64 + b.Slot * 0x10;
            bool same = same_script(b);
            bool entry_ours = *(int*)e >= 0 && *(uint*)(e + 4) == BP_TAG && *(nint*)(e + 8) == b.Ptr;
            if (same && entry_ours) continue;
            if (same && *(byte*)b.Ptr == 0x40) *(byte*)b.Ptr = b.Saved;
            _our_bps.RemoveAt(i);
            _bp_version++;
            Status.refused("a breakpoint was dropped: its script was unloaded");
        }
        _rearm.RemoveAll(b => !same_script(b));
    }

    /// <summary>Sets lifted breakpoints again once no worker of their ctrl work stands on them.</summary>
    private void rearm_breakpoints() {
        for (int i = _rearm.Count - 1; i >= 0; i--) {
            Bp b = _rearm[i];
            CtrlSnap? cs = ctrl(b.Ctrl);
            nint code = b.Block + (nint)(*(uint*)(b.Block + 0x30));
            if (cs is null || cs.Workers.Any(w => w.PcValid && w.Block == b.Block && code + (nint)w.Pc == b.Ptr)) continue;
            _rearm.RemoveAt(i);
            if (*(byte*)b.Ptr == b.Saved) arm(b);
        }
    }

    private static int[] class_sizes(nint ctrl, out int actors, out int total) {
        actors = *(ushort*)(ctrl + 0x0c);
        total  = *(ushort*)(ctrl + 0x10);
        int n5b8 = *(ushort*)(ctrl + 0x14), n558 = *(ushort*)(ctrl + 0x16), n2e8 = *(ushort*)(ctrl + 0x18);
        int nb58 = Math.Max(0, actors - n558 - n2e8 - n5b8);
        return [ nb58, n558, n5b8, n2e8 ];
    }

    /// <summary>Byte offset of worker <paramref name="i"/> from ctrl+0x1c's first worker (AtelGetBasicWork's layout).</summary>
    private static long worker_offset(int[] counts, int i) {
        int[] sizes = [ 0xb58, 0x558, 0x5b8, 0x2e8 ];
        long off = 0;
        for (int k = 0; k < 4; k++) {
            if (i < counts[k]) return off + (long)i * sizes[k];
            off += (long)counts[k] * sizes[k];
            i -= counts[k];
        }
        return off + (long)i * 0x30;
    }

    private static int local_index(nint block, nint header) {
        if (block == 0 || header == 0) return -1;
        int n = *(ushort*)(block + 0x34);
        for (int w = 0; w < n; w++) {
            if (block + (nint)(*(uint*)(block + 0x38 + 4 * w)) == header) return w;
        }
        return -1;
    }

    private CtrlSnap snapshot_ctrl(int c) {
        nint ctrl = ctrl_ptr(c);
        bool in_use = (*(byte*)ctrl & 1) != 0;
        nint first = *(nint*)(ctrl + 0x1c);
        BreakpointSnap[] bps = new BreakpointSnap[4];
        for (int s = 0; s < 4; s++) {
            nint e = ctrl + 0x64 + s * 0x10;
            bps[s] = new BreakpointSnap(s, *(int*)e, *(nint*)(e + 8), *(byte*)(e + 0xe), *(byte*)(e + 0xf));
        }
        if (!in_use || first == 0) {
            return new CtrlSnap(c, in_use, *(byte*)(ctrl + 8), 0, 0, 0, [], bps);
        }

        int[] counts = class_sizes(ctrl, out int actors, out int total);
        total = Math.Min(total, 512);
        List<WorkerSnap> workers = new(total);
        for (int i = 0; i < total; i++) {
            nint w = first + (nint)worker_offset(counts, i);
            workers.Add(snapshot_worker(i, w, i < actors));
        }
        return new CtrlSnap(c, true, *(byte*)(ctrl + 8), (ushort)actors, (ushort)total, *(nint*)(ctrl + 0x48), [ .. workers ], bps);
    }

    private static WorkerSnap snapshot_worker(int i, nint w, bool has_levels) {
        nint header = *(nint*)w;
        nint block  = *(nint*)(w + 4);
        ushort kind = header != 0 ? *(ushort*)header : (ushort)0;
        int local   = local_index(block, header);
        if (!has_levels) {
            return new WorkerSnap(i, w, block, local, kind, 0, 0, 0, false, 0, false, 0, [], [], [], 0, 0, 0, [], []);
        }

        int nlev = kind is 5 or 6 ? 2 : 9;
        byte level = *(byte*)(w + 0x32);
        nint L = w + 0x12c + Math.Min(level, (byte)(nlev - 1)) * 0x4c;
        nint code = block != 0 ? block + (nint)(*(uint*)(block + 0x30)) : 0;
        nint pc = *(nint*)(L + 0x18);
        uint code_len = block != 0 ? *(uint*)block : 0;
        bool pc_valid = code != 0 && pc >= code && pc < code + (nint)code_len + 0x10000;

        int depth = Math.Clamp((int)*(sbyte*)(L + 0x1d), 0, 3);
        CallFrame[] calls = new CallFrame[depth];
        for (int k = 0; k < depth; k++) calls[k] = new CallFrame(*(ushort*)(L + 0x10 + 2 * k), *(uint*)(L + 4 * k));

        int[] iregs = new int[4];
        for (int k = 0; k < 4; k++) iregs[k] = *(int*)(w + 0x48 + 4 * k);
        float[] fregs = new float[10];
        for (int k = 0; k < 10; k++) fregs[k] = *(float*)(w + 0x58 + 4 * k);

        int sp = Math.Clamp(*(int*)(w + 0xc4), 0, MAX_STACK);
        var stack = new (uint, byte)[sp];
        for (int k = 0; k < sp; k++) stack[k] = (*(uint*)(w + 0xc8 + 4 * k), *(byte*)(w + 0xc4 + 0x54 + k));

        List<SignalSnap> sigs = [];
        nint s = *(nint*)(w + 0x80);
        for (int n = 0; s != 0 && n < 64; n++, s = *(nint*)s) {
            byte ls = *(byte*)(s + 0x0e);
            sigs.Add(new SignalSnap(*(ushort*)(s + 8), *(ushort*)(s + 0xa), *(ushort*)(s + 0xc), ls & 0xf, (ls >> 4) & 7, *(byte*)(s + 0x0f)));
        }

        return new WorkerSnap(
            i, w, block, local, kind,
            *(ushort*)(w + 0x2c), level, *(ushort*)(w + 0x34), true,
            pc_valid ? (uint)(pc - code) : 0, pc_valid,
            *(byte*)(L + 0x1f), calls, iregs, fregs,
            *(uint*)(L + 0x20), *(uint*)(L + 0x24), *(float*)(L + 0x28),
            stack, [ .. sigs ]);
    }

    /// <summary>Listing and variables for the UI's selection; rebuilt only when the selection or breakpoints change.</summary>
    private void snapshot_selected(int sel) {
        CtrlSnap? cs = ctrl(sel);
        WorkerSnap? w = cs?.Workers.FirstOrDefault(x => x.Index == selected_worker);
        if (cs is null || w is null || w.Block == 0) { listing = null; variables = null; return; }

        // Inside a JSR the PC is in the callee's code, a 0x30-byte namespace worker.
        int local = listing_override >= 0 ? listing_override : w.LocalIndex;
        long size = Mem.readable_end(w.Block, 0x400000) - w.Block;
        if (size <= 0x38) { listing = null; variables = null; return; }
        // The last worker's code runs to the block's end; cap it.
        size = Math.Min(size, (long)*(uint*)(w.Block + 0x30) + *(uint*)w.Block + 0x10000);

        Dictionary<long, byte> overlay = [];
        foreach (BreakpointSnap b in cs.Breakpoints) {
            if (b.Id >= 0 && b.Ptr >= w.Block && b.Ptr < w.Block + (nint)size) overlay[b.Ptr - w.Block] = b.Saved;
        }
        ScriptBlock? block = ScriptBlock.parse(new MemBlockReader(w.Block, size, overlay), size, out string error);
        ulong print = fingerprint(w.Block);

        if (block is not null && listing_override < 0 && w.PcValid) {
            WorkerInfo? owner = block.Workers.FirstOrDefault(x => w.Pc >= x.Start && w.Pc < x.End);
            if (owner is not null) local = owner.Index;
        }

        if (block is null || local < 0 || local >= block.Workers.Count) {
            listing = new Listing(w.Block, 0, 0, local, [], [], block is null ? error : "this worker has no code in its block");
        }
        else if (listing is not { } old || old.Block != w.Block || old.Print != print || old.LocalIndex != local || _listing_bp_version != _bp_version || _listing_original != original_names) {
            _listing_original = original_names;
            List<Line> lines = block.disassemble(local, _names.annotate, _listing_original);
            Dictionary<uint, int> line_of = [];
            for (int k = 0; k < lines.Count; k++) {
                if (!lines[k].IsLabel) line_of.TryAdd(lines[k].Offset, k);
            }
            listing = new Listing(w.Block, w.Block + (nint)block.CodeStart, print, local, lines, line_of, "");
            _listing_bp_version = _bp_version;
        }

        variables = block is not null && w.LocalIndex >= 0 && w.LocalIndex < block.Workers.Count
            ? read_variables(cs, w, block, w.LocalIndex)
            : [];
    }

    private static readonly string[] STORAGE = [ "GBL", "LCL", "FIX", "MY", "DAT", "ITR", "LC2", "-" ];
    private static readonly string[] TYPES   = [ "u8", "i8", "u16", "i16", "u32", "i32", "f32", "?7" ];

    private VarSnap[] read_variables(CtrlSnap cs, WorkerSnap w, ScriptBlock block, int local) {
        nint ctrl   = ctrl_ptr(cs.Index);
        nint header = w.Block + (nint)block.Workers[local].Header;
        int  n      = Math.Min(block.variable_count(local), 512);
        VarSnap[] vars = new VarSnap[n];
        for (int i = 0; i < n; i++) {
            if (block.variable(local, (ushort)i) is not { } desc) {
                vars[i] = new VarSnap(i, "?", "?", 0, 0, "");
                continue;
            }
            (uint word, ushort length) = desc;
            int type    = Math.Min((int)(word >> 28), 7);
            int storage = (int)((word >> 25) & 7);
            uint offset = word & 0xffffff;
            nint base_ = storage switch {
                0 => *(nint*)(ctrl + 0x2c),
                1 => *(nint*)(ctrl + 0x30),
                2 => w.Block + (nint)(*(uint*)(header + 0x28)),
                3 => *(nint*)(ctrl + 0x54) != 0 ? 0 : w.Block + (nint)(*(uint*)(header + 0x2c)),
                4 => w.Block + (nint)(*(uint*)(header + 0x30)),
                5 => w.Ptr + 0x48,
                6 => w.Block + (nint)(*(uint*)(w.Block + 0x20)),
                _ => 0,
            };
            string value;
            if (storage == 3 && base_ == 0) value = "(resolved by a callback)";
            else if (base_ == 0 || type > 6) value = "-";
            else {
                int size = type switch { 0 or 1 => 1, 2 or 3 => 2, _ => 4 };
                int count = Math.Clamp((int)length, 1, 4);
                nint p = base_ + (nint)offset;
                if (!Mem.readable(p, size * count)) value = "(unreadable)";
                else {
                    StringBuilder sb = new();
                    for (int k = 0; k < count; k++) {
                        if (k > 0) sb.Append(", ");
                        nint q = p + k * size;
                        sb.Append(type switch {
                            0 => (*(byte*)q).ToString(CultureInfo.InvariantCulture),
                            1 => (*(sbyte*)q).ToString(CultureInfo.InvariantCulture),
                            2 => (*(ushort*)q).ToString(CultureInfo.InvariantCulture),
                            3 => (*(short*)q).ToString(CultureInfo.InvariantCulture),
                            4 => $"{*(uint*)q} (0x{*(uint*)q:x})",
                            5 => (*(int*)q).ToString(CultureInfo.InvariantCulture),
                            _ => RustFloat.debug(*(float*)q),
                        });
                    }
                    if (length > count) sb.Append($", ... ({length})");
                    value = sb.ToString();
                }
            }
            vars[i] = new VarSnap(i, STORAGE[storage], TYPES[type], length, offset, value);
        }
        return vars;
    }
}
