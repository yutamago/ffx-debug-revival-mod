// SPDX-License-Identifier: MIT OR Apache-2.0

using Yuta.DebugRevival.Native;

namespace Yuta.DebugRevival.Features;

/// <summary>
///     The developers' console. <c>dbgPrintf</c> (459 call sites) and
///     <c>scePrintf</c> are compiled to a bare <c>ret</c> in FFX.exe; their
///     arguments are still pushed, so a hook can format and keep the text.
///     Fahrenheit's print control hooks the same functions; if its hook runs
///     first without passing the call on, this log stays empty (see the Log tab).
/// </summary>
public sealed unsafe class DebugLog {
    private const int MAX_LINES = 4000;

    private readonly Queue<string> _lines = new();
    private readonly Lock _lock = new();
    private string _partial = "";

    public volatile bool capture = true;
    public long total;

    [DllImport("msvcrt.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int _vsnprintf(byte* buffer, nuint count, byte* format, nint* args);

    public bool init(FhModule owner) {
        return Calls.dbgPrintf.hook(owner, h_dbg_printf)
            && Calls.scePrintf.hook(owner, h_sce_printf);
    }

    [UnmanagedCallConv(CallConvs = [ typeof(CallConvCdecl) ])]
    private void h_dbg_printf(byte* fmt,
        nint va0,  nint va1,  nint va2,  nint va3,  nint va4,  nint va5,  nint va6,  nint va7,
        nint va8,  nint va9,  nint va10, nint va11, nint va12, nint va13, nint va14, nint va15) {
        record(fmt, [ va0, va1, va2, va3, va4, va5, va6, va7, va8, va9, va10, va11, va12, va13, va14, va15 ]);
        Calls.dbgPrintf.chain_from(h_dbg_printf).fnptr!(fmt, va0, va1, va2, va3, va4, va5, va6, va7, va8, va9, va10, va11, va12, va13, va14, va15);
    }

    [UnmanagedCallConv(CallConvs = [ typeof(CallConvCdecl) ])]
    private void h_sce_printf(byte* fmt,
        nint va0,  nint va1,  nint va2,  nint va3,  nint va4,  nint va5,  nint va6,  nint va7,
        nint va8,  nint va9,  nint va10, nint va11, nint va12, nint va13, nint va14, nint va15) {
        record(fmt, [ va0, va1, va2, va3, va4, va5, va6, va7, va8, va9, va10, va11, va12, va13, va14, va15 ]);
        Calls.scePrintf.chain_from(h_sce_printf).fnptr!(fmt, va0, va1, va2, va3, va4, va5, va6, va7, va8, va9, va10, va11, va12, va13, va14, va15);
    }

    /// <summary>Formats with the C runtime: on x86 a va_list is a pointer to the stacked arguments, which the 16 slots reproduce.</summary>
    private void record(byte* fmt, ReadOnlySpan<nint> args) {
        if (!capture || fmt == null) return;
        try {
            byte* buf = stackalloc byte[1024];
            nint* va = stackalloc nint[16];
            for (int i = 0; i < 16; i++) va[i] = args[i];
            int n = _vsnprintf(buf, 1023, fmt, va);
            if (n < 0) n = 1023;
            buf[Math.Min(n, 1023)] = 0;
            string text = Encoding.Latin1.GetString(buf, Math.Min(n, 1023));
            add(text);
        }
        catch (Exception) {
            // A bad format must not take the game down; drop the line.
        }
    }

    private void add(string text) {
        lock (_lock) {
            text = _partial + text;
            string[] parts = text.Split('\n');
            for (int i = 0; i < parts.Length - 1; i++) push(parts[i].TrimEnd('\r'));
            _partial = parts[^1];
            if (_partial.Length > 512) { push(_partial); _partial = ""; }
        }
    }

    private void push(string line) {
        _lines.Enqueue(line);
        total++;
        while (_lines.Count > MAX_LINES) _lines.Dequeue();
    }

    public string[] lines() {
        lock (_lock) return [ .. _lines ];
    }

    public void clear() {
        lock (_lock) { _lines.Clear(); _partial = ""; }
    }
}
