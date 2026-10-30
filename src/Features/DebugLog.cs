// SPDX-License-Identifier: MIT OR Apache-2.0

using Yuta.DebugRevival.Native;

namespace Yuta.DebugRevival.Features;

/// <summary>
///     The developers' console: <c>dbgPrintf</c> and <c>scePrintf</c> are a bare <c>ret</c> in FFX.exe, but their
///     459 callers still push the arguments. Stays empty if Fahrenheit's print control hooks them first without chaining.
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

    public bool init(FhModule owner) => Calls.dbgPrintf.hook(owner, h_dbg_printf) && Calls.scePrintf.hook(owner, h_sce_printf);

    private D.PrintfFn? _next_dbg;
    private D.PrintfFn? _next_sce;

    [UnmanagedCallConv(CallConvs = [ typeof(CallConvCdecl) ])]
    private void h_dbg_printf(byte* fmt,
        nint va0,  nint va1,  nint va2,  nint va3,  nint va4,  nint va5,  nint va6,  nint va7,
        nint va8,  nint va9,  nint va10, nint va11, nint va12, nint va13, nint va14, nint va15) {
        record(fmt, [ va0, va1, va2, va3, va4, va5, va6, va7, va8, va9, va10, va11, va12, va13, va14, va15 ]);
        _next_dbg ??= Calls.dbgPrintf.chain_from(h_dbg_printf).fnptr!;
        _next_dbg(fmt, va0, va1, va2, va3, va4, va5, va6, va7, va8, va9, va10, va11, va12, va13, va14, va15);
    }

    [UnmanagedCallConv(CallConvs = [ typeof(CallConvCdecl) ])]
    private void h_sce_printf(byte* fmt,
        nint va0,  nint va1,  nint va2,  nint va3,  nint va4,  nint va5,  nint va6,  nint va7,
        nint va8,  nint va9,  nint va10, nint va11, nint va12, nint va13, nint va14, nint va15) {
        record(fmt, [ va0, va1, va2, va3, va4, va5, va6, va7, va8, va9, va10, va11, va12, va13, va14, va15 ]);
        _next_sce ??= Calls.scePrintf.chain_from(h_sce_printf).fnptr!;
        _next_sce(fmt, va0, va1, va2, va3, va4, va5, va6, va7, va8, va9, va10, va11, va12, va13, va14, va15);
    }

    // These prints never ran in the PC build, so a %s may get a bad pointer. Strings are read only from readable
    // memory, %n is skipped, and each numeric conversion goes to the C runtime alone (an x86 va_list is a pointer
    // to the stacked slots, which the 16 slots reproduce).
    private void record(byte* fmt, ReadOnlySpan<nint> args) {
        if (!capture || fmt == null || !Mem.readable((nint)fmt, 1)) return;
        try {
            add(format(fmt, args));
        }
        catch (Exception) {
            // A malformed line is dropped; the game must not go down over a debug print.
        }
    }

    private static string format(byte* fmt, ReadOnlySpan<nint> args) {
        nint* va = stackalloc nint[16];
        for (int k = 0; k < 16; k++) va[k] = args[k];
        byte* spec = stackalloc byte[32];
        byte* outb = stackalloc byte[256];

        StringBuilder sb = new();
        int ai = 0;
        string src = safe_cstr((nint)fmt, 1024);
        for (int i = 0; i < src.Length; i++) {
            char c = src[i];
            if (c != '%') { sb.Append(c); continue; }
            int start = i++;
            if (i >= src.Length) break;
            if (src[i] == '%') { sb.Append('%'); continue; }

            int first_arg = ai;
            while (i < src.Length && "-+ #0".Contains(src[i])) i++;
            if (i < src.Length && src[i] == '*') { ai++; i++; } else while (i < src.Length && char.IsAsciiDigit(src[i])) i++;
            if (i < src.Length && src[i] == '.') {
                i++;
                if (i < src.Length && src[i] == '*') { ai++; i++; } else while (i < src.Length && char.IsAsciiDigit(src[i])) i++;
            }
            bool wide64 = false;
            while (i < src.Length && "hlLqjztwI".Contains(src[i])) {
                if (src[i] == 'I' && i + 2 < src.Length && src[i + 1] == '6' && src[i + 2] == '4') { wide64 = true; i += 3; continue; }
                if (src[i] == 'I' && i + 2 < src.Length && src[i + 1] == '3' && src[i + 2] == '2') { i += 3; continue; }
                if (src[i] == 'l' && i + 1 < src.Length && src[i + 1] == 'l') { wide64 = true; i += 2; continue; }
                if (src[i] is 'q' or 'L' or 'j') wide64 = true;
                i++;
            }
            if (i >= src.Length) break;
            char conv = src[i];

            if (conv is 's' or 'S') {
                if (ai >= 16) break;
                nint p = va[ai++];
                sb.Append(p == 0 ? "(null)" : safe_cstr(p, 512));
                continue;
            }
            if (conv == 'n') { ai++; continue; }
            int slots = conv switch {
                'd' or 'i' or 'u' or 'x' or 'X' or 'o' or 'c' or 'C' or 'p' => wide64 ? 2 : 1,
                'e' or 'E' or 'f' or 'F' or 'g' or 'G' or 'a' or 'A' => 2,
                _ => -1,
            };
            if (slots < 0) { sb.Append(src, start, i - start + 1); continue; }
            if (ai + slots > 16 || i - start + 1 >= 31) break;
            ai += slots;
            for (int k = 0; k <= i - start; k++) spec[k] = (byte)src[start + k];
            spec[i - start + 1] = 0;
            int n = _vsnprintf(outb, 255, spec, va + first_arg);
            if (n < 0 || n > 255) n = 255;
            sb.Append(Encoding.Latin1.GetString(outb, n));
        }
        return sb.ToString();
    }

    /// <summary>A NUL-terminated string that stops at the first unreadable page.</summary>
    private static string safe_cstr(nint p, int max) {
        if (!Mem.readable(p, 1)) return "(bad pointer)";
        StringBuilder sb = new();
        for (int k = 0; k < max; k++) {
            nint q = p + k;
            if (k > 0 && (q & 0xfff) == 0 && !Mem.readable(q, 1)) break;
            byte b = *(byte*)q;
            if (b == 0) break;
            sb.Append((char)b);
        }
        return sb.ToString();
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
