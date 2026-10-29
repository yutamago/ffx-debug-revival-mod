// SPDX-License-Identifier: MIT OR Apache-2.0

using Yuta.DebugRevival.Native;

namespace Yuta.DebugRevival.Script;

/// <summary>A script block in game memory; breakpoint bytes read as the instruction they replaced.</summary>
internal sealed unsafe class MemBlockReader(nint block, long size, IReadOnlyDictionary<long, byte> overlay) : IBlockReader {
    public bool in_range(long offset, int length) => offset >= 0 && offset + length <= size;
    public byte u8(long offset) => overlay.TryGetValue(offset, out byte b) ? b : *(byte*)(block + (nint)offset);
}

/// <summary>
///     The event-script debugger, built on the VM's own debugger support
///     (VERIFIED in FFX.exe, see docs/research/script.md):
///     <list type="bullet">
///         <item>ctrl work +8 is the debugger state: 0 run, 1 step, 2 one frame, 3 stopped.
///               <c>Atel_ExecCtrlWorks</c> skips a stopped ctrl work and turns 1 and 2 into 3.</item>
///         <item>ctrl work +0x64 holds 4 breakpoints {id, tag, byte*, -, -, saved byte, hit}.
///               Opcode 0x40 stops a worker on its entry and resumes with the saved instruction.</item>
///     </list>
///     All game memory is read and written from the game thread, inside the
///     <c>Atel_ExecCtrlWorks</c> hook, where no script is mid-instruction;
///     the UI reads the published snapshots.
/// </summary>
public sealed unsafe class ScriptDebugger {
    private const uint BP_TAG = 0x56565244; // "DRVV": our entries
    private const int  MAX_STACK = 19;

    private readonly FhLogger _log;
    private readonly NativeNames _names;

    // Commands from the UI, applied in the hook's pre-step.
    private readonly ConcurrentQueue<Action> _commands = new();

    // Game-thread state.
    private bool _in_pass;
    private (int ctrl, nint worker, bool stepped)? _step;
    private readonly List<(int ctrl, int slot, nint block, nint ptr)> _our_bps = [];
    private int _listing_bp_version = -1;
    private int _bp_version;

    // Published for the UI.
    private readonly CtrlSnap?[] _ctrls = new CtrlSnap?[G.CtrlWorkCount];
    public CtrlSnap? ctrl(int i) => Volatile.Read(ref _ctrls[i]);
    public volatile Listing? listing;
    public volatile VarSnap[]? variables;
    public volatile string status = "";
    public long frame;

    // Selection from the UI.
    public volatile int selected_ctrl;
    public volatile int selected_worker = -1;
    /// <summary>The local worker whose code to list: -1 = the worker that owns the PC.</summary>
    public volatile int listing_override = -1;

    public NativeNames names => _names;

    public ScriptDebugger(FhLogger log, NativeNames names) {
        _log   = log;
        _names = names;
    }

    public bool init(FhModule owner) {
        return Calls.Atel_ExecCtrlWorks.hook(owner, h_exec_ctrl_works)
            && Calls.Script_Run.hook(owner, h_script_run);
    }

    // ---------------------------------------------------------------- hooks

    [UnmanagedCallConv(CallConvs = [ typeof(CallConvCdecl) ])]
    private uint h_exec_ctrl_works(int from, int to) {
        try {
            while (_commands.TryDequeue(out Action? cmd)) cmd();
            validate_breakpoints();
            if (_step is { } s && s.ctrl >= from && s.ctrl < to) {
                // Let the ctrl work's update run; the Script_Run hook lets only the stepped worker through.
                set_state(s.ctrl, (byte)(s.ctrl == 0 ? 1 : 0));
            }
        }
        catch (Exception e) { _log.Error($"script debugger pre: {e}"); }

        _in_pass = true;
        uint r = Calls.Atel_ExecCtrlWorks.chain_from(h_exec_ctrl_works).fnptr!(from, to);
        _in_pass = false;

        try {
            if (_step is { } s && s.ctrl >= from && s.ctrl < to) {
                set_state(s.ctrl, 3);
                status = s.stepped ? $"stepped worker in ctrl {s.ctrl}" : $"ctrl {s.ctrl}: the worker did not run (not runnable or waiting)";
                _step = null;
            }
            for (int c = Math.Max(0, from); c < Math.Min(to, G.CtrlWorkCount); c++) {
                Volatile.Write(ref _ctrls[c], snapshot_ctrl(c));
            }
            if (selected_ctrl >= from && selected_ctrl < to) snapshot_selected();
            Interlocked.Increment(ref frame);
        }
        catch (Exception e) { _log.Error($"script debugger post: {e}"); }
        return r;
    }

    [UnmanagedCallConv(CallConvs = [ typeof(CallConvCdecl) ])]
    private uint h_script_run(nint worker, uint mode, uint callbacks) {
        // Only interfere inside the frame pass: battle code also runs workers in its own
        // loops (MsAtelParseSysTag), and skipping them there would hang the game.
        if (_in_pass && _step is { } s && Mem.u32(G.CurrentCtrlIdx) == (uint)s.ctrl) {
            if (worker != s.worker || s.stepped) return 0;
            _step = s with { stepped = true };
            return Calls.Script_Run.chain_from(h_script_run).fnptr!(worker, 1, callbacks);
        }
        return Calls.Script_Run.chain_from(h_script_run).fnptr!(worker, mode, callbacks);
    }

    // ------------------------------------------------------------- commands

    public void pause(int c)       => _commands.Enqueue(() => { set_state(c, 3); status = $"ctrl {c} paused"; });
    public void resume(int c)      => _commands.Enqueue(() => { set_state(c, 0); status = $"ctrl {c} running"; });
    public void frame_step(int c)  => _commands.Enqueue(() => { set_state(c, 2); status = $"ctrl {c}: one frame"; });

    /// <summary>Runs one instruction of one worker, then leaves its ctrl work stopped.</summary>
    public void step(int c, int worker_index) => _commands.Enqueue(() => {
        CtrlSnap? cs = ctrl(c);
        WorkerSnap? w = cs?.Workers.FirstOrDefault(x => x.Index == worker_index);
        if (w is null || !w.HasLevels) { status = "that worker has no code of its own to step"; return; }
        _step = (c, w.Ptr, false);
    });

    /// <summary>Sets or clears a breakpoint at a code offset of a block (event scripts only).</summary>
    public void toggle_breakpoint(int c, nint block, uint code_offset) => _commands.Enqueue(() => {
        if (c != 0) { status = "breakpoints are for event scripts (ctrl 0) only"; return; }
        nint ctrl = ctrl_ptr(c);
        if ((Mem.u8(G.AtelCtrlWorks) & 1) == 0) { status = "no event loaded"; return; }
        nint code = block + (nint)(*(uint*)(block + 0x30));
        nint ptr  = code + (nint)code_offset;

        int existing = _our_bps.FindIndex(b => b.ctrl == c && b.ptr == ptr);
        if (existing >= 0) {
            clear_bp(_our_bps[existing]);
            _our_bps.RemoveAt(existing);
            _bp_version++;
            status = $"breakpoint at {code_offset:x5} cleared";
            return;
        }
        for (int slot = 0; slot < 4; slot++) {
            nint e = ctrl + 0x64 + slot * 0x10;
            if (*(int*)e >= 0) continue;
            *(uint*)(e + 4)  = BP_TAG;
            *(nint*)(e + 8)  = ptr;
            *(byte*)(e + 0xe) = *(byte*)ptr;
            *(byte*)(e + 0xf) = 0;
            *(int*)e = slot;
            *(byte*)ptr = 0x40;
            _our_bps.Add((c, slot, block, ptr));
            _bp_version++;
            status = $"breakpoint {slot + 1} at {code_offset:x5}";
            return;
        }
        status = "all 4 breakpoint slots of this ctrl work are in use";
    });

    public void clear_all_breakpoints() => _commands.Enqueue(() => {
        foreach (var b in _our_bps) clear_bp(b);
        _our_bps.Clear();
        _bp_version++;
        status = "breakpoints cleared";
    });

    // --------------------------------------------------------------- helpers

    private static nint ctrl_ptr(int c) => Mem.at(G.AtelCtrlWorks) + c * G.CtrlWorkSize;

    private static void set_state(int c, byte v) {
        if (c < 0 || c >= G.CtrlWorkCount) return;
        *(byte*)(ctrl_ptr(c) + 8) = v;
    }

    /// <summary>Restores the byte and frees the entry, if the entry is still ours and the code still there.</summary>
    private static void clear_bp((int ctrl, int slot, nint block, nint ptr) b) {
        nint e = ctrl_ptr(b.ctrl) + 0x64 + b.slot * 0x10;
        bool ours = *(uint*)(e + 4) == BP_TAG && *(nint*)(e + 8) == b.ptr;
        if (ours && *(int*)e >= 0) {
            if (*(byte*)b.ptr == 0x40) *(byte*)b.ptr = *(byte*)(e + 0xe);
            *(int*)e = -1;
            *(uint*)(e + 4) = 0;
            *(byte*)(e + 0xf) = 0;
        }
    }

    /// <summary>
    ///     Drops breakpoints whose script went away. A new event resets the table
    ///     (AtelRequireCtrlWork) without restoring bytes; if the block is still the
    ///     same, the 0x40 left behind would hang the VM, so it is restored here.
    /// </summary>
    private void validate_breakpoints() {
        for (int i = _our_bps.Count - 1; i >= 0; i--) {
            var b = _our_bps[i];
            nint ctrl = ctrl_ptr(b.ctrl);
            nint e = ctrl + 0x64 + b.slot * 0x10;
            bool in_use     = (*(byte*)ctrl & 1) != 0;
            bool same_block = in_use && *(nint*)(ctrl + 0x48) == b.block;
            bool entry_ours = *(int*)e >= 0 && *(uint*)(e + 4) == BP_TAG && *(nint*)(e + 8) == b.ptr;
            if (same_block && entry_ours) continue;
            if (same_block && *(byte*)b.ptr == 0x40) {
                byte saved = *(byte*)(e + 0xe);
                *(byte*)b.ptr = saved;
            }
            _our_bps.RemoveAt(i);
            _bp_version++;
            status = "a breakpoint was dropped: its script was unloaded";
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
            bps[s] = new BreakpointSnap(s, *(int*)e, *(uint*)(e + 4), *(nint*)(e + 8), *(byte*)(e + 0xe), *(byte*)(e + 0xf));
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
            return new WorkerSnap(i, w, block, local, kind, 0, 0, 0, 0, false, 0, false, 0, 0, [], [], [], 0, 0, 0, [], []);
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
            *(ushort*)(w + 0x2c), level, *(ushort*)(w + 0x34), *(ushort*)(w + 0x36), true,
            pc_valid ? (uint)(pc - code) : 0, pc_valid,
            *(byte*)(L + 0x1f), *(byte*)(L + 0x1e), calls, iregs, fregs,
            *(uint*)(L + 0x20), *(uint*)(L + 0x24), *(float*)(L + 0x28),
            stack, [ .. sigs ]);
    }

    /// <summary>Listing and variables for the UI's selection; rebuilt only when the selection or breakpoints change.</summary>
    private void snapshot_selected() {
        CtrlSnap? cs = ctrl(selected_ctrl);
        WorkerSnap? w = cs?.Workers.FirstOrDefault(x => x.Index == selected_worker);
        if (cs is null || w is null || w.Block == 0) { listing = null; variables = null; return; }

        // The worker whose code holds the PC: inside a JSR that is the callee's, a 0x30-byte namespace worker.
        int local = listing_override >= 0 ? listing_override : w.LocalIndex;
        long size = Mem.readable_end(w.Block, 0x400000) - w.Block;
        if (size <= 0x38) { listing = null; return; }
        // Code past the last worker's start runs to the block's end; keep that within reason.
        size = Math.Min(size, (long)*(uint*)(w.Block + 0x30) + *(uint*)w.Block + 0x10000);

        Dictionary<long, byte> overlay = [];
        foreach (BreakpointSnap b in cs.Breakpoints) {
            if (b.Id >= 0 && b.Ptr >= w.Block && b.Ptr < w.Block + (nint)size) overlay[b.Ptr - w.Block] = b.Saved;
        }
        ScriptBlock? block = ScriptBlock.parse(new MemBlockReader(w.Block, size, overlay), size, out string error);

        if (block is not null && listing_override < 0 && w.PcValid) {
            WorkerInfo? owner = block.Workers.FirstOrDefault(x => w.Pc >= x.Start && w.Pc < x.End);
            if (owner is not null) local = owner.Index;
        }

        if (block is null || local < 0 || local >= block.Workers.Count) {
            listing = new Listing(w.Block, 0, local, [], [], block is null ? error : "this worker has no code in its block");
        }
        else if (listing is not { } old || old.Block != w.Block || old.LocalIndex != local || _listing_bp_version != _bp_version) {
            List<Line> lines = block.disassemble(local, _names.annotate);
            Dictionary<uint, int> line_of = [];
            for (int k = 0; k < lines.Count; k++) {
                if (!lines[k].IsLabel) line_of.TryAdd(lines[k].Offset, k);
            }
            listing = new Listing(w.Block, w.Block + (nint)block.CodeStart, local, lines, line_of, "");
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
            int type    = (int)(word >> 28);
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
