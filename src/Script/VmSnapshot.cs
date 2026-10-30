// SPDX-License-Identifier: MIT OR Apache-2.0

namespace Yuta.DebugRevival.Script;

/// <summary>A signal queued on a worker (node stride 0x18).</summary>
public sealed record SignalSnap(ushort Entry, ushort From, ushort To, int Level, int State, byte Kind);

/// <summary>One JSR frame of a level: the callee worker and where it returns to.</summary>
public sealed record CallFrame(ushort Callee, uint ReturnOffset);

/// <summary>One worker as the game left it after a frame's scripts ran.</summary>
public sealed record WorkerSnap(
    int          Index,
    nint         Ptr,
    nint         Block,
    int          LocalIndex,   // index of the worker header in its block, -1 if not found
    ushort       Kind,
    ushort       Entry,
    byte         Level,
    ushort       Flags34,
    ushort       Flags36,
    bool         HasLevels,
    uint         Pc,           // code offset of the current level's PC
    bool         PcValid,
    byte         Wait,
    byte         NativeState,
    CallFrame[]  Calls,
    int[]        IntRegs,
    float[]      FloatRegs,
    uint         RegA,
    uint         RegB,
    float        FloatAcc,
    (uint value, byte kind)[] Stack,
    SignalSnap[] Signals) {

    public string run_state => (Flags34 & 0x1c00) switch {
        0x0000 => "idle",
        0x0400 => "running",
        0x0800 => "waits (request)",
        0x0c00 => "entry ended",
        0x1000 => "halted",
        0x1400 => "runaway",
        _      => $"0x{Flags34 & 0x1c00:x4}",
    };

    public string wait_text => Wait switch {
        0 => "",
        1 => "waits for a reply",
        2 => "waits for a queued request",
        _ => $"wait {Wait}",
    };
}

/// <summary>One of the 4 breakpoint entries of a ctrl work (+0x64, 16 bytes each).</summary>
public sealed record BreakpointSnap(int Slot, int Id, uint Tag, nint Ptr, byte Saved, byte Hit);

/// <summary>One ctrl work: the event's (0), battle's (2, 3), the menu's (6), ...</summary>
public sealed record CtrlSnap(
    int              Index,
    bool             InUse,
    byte             State,      // +8: 0 run, 1 step, 2 one frame, 3 stopped
    ushort           Actors,
    ushort           Total,
    nint             Block,
    WorkerSnap[]     Workers,
    BreakpointSnap[] Breakpoints) {

    public static string role(int index) => index switch {
        0 => "event",
        2 or 3 => "battle",
        6 => "menu",
        _ => "other",
    };

    public string state_text => State switch {
        0 => "running",
        1 => "step",
        2 => "one frame",
        3 => "stopped",
        _ => $"state {State}",
    };
}

/// <summary>A variable of the selected worker with its current value.</summary>
public sealed record VarSnap(int Index, string Storage, string Type, ushort Length, uint Offset, string Value);

/// <summary>The disassembly of one worker's code, with an offset-to-line map for the PC marker.</summary>
public sealed record Listing(nint Block, nint Code, ulong Print, int LocalIndex, List<Line> Lines, Dictionary<uint, int> LineOf, string Error);
