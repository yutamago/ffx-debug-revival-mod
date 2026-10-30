// SPDX-License-Identifier: MIT OR Apache-2.0

// Shared with tools/DisasmCheck, which has no global usings of its own.
using System.Globalization;
using System.Numerics;
using System.Text;

namespace Yuta.DebugRevival.Script;

/// <summary>Reads a script block: offsets are relative to the block's start.</summary>
public interface IBlockReader {
    /// <summary>Whether <paramref name="length"/> bytes at <paramref name="offset"/> can be read.</summary>
    bool in_range(long offset, int length);
    byte u8(long offset);
}

/// <summary>A script block held in a byte array (a file's script section).</summary>
public sealed class ArrayBlockReader(byte[] bytes) : IBlockReader {
    public bool in_range(long offset, int length) => offset >= 0 && offset + length <= bytes.Length;
    public byte u8(long offset) => bytes[offset];
}

public static class BlockReaderExt {
    public static ushort u16(this IBlockReader r, long o) => (ushort)(r.u8(o) | (r.u8(o + 1) << 8));
    public static uint u32(this IBlockReader r, long o) => (uint)(r.u16(o) | (r.u16(o + 2) << 16));
}

/// <summary>One decoded instruction.</summary>
public readonly record struct Instr(uint Offset, byte Opcode, ushort? Value) {
    public uint Length => Value is null ? 1u : 3u;
}

/// <summary>One worker of a script block: its tables and its code range.</summary>
public sealed record WorkerInfo(
    int    Index,
    uint   Header,
    ushort Kind,
    uint[] Entries,
    uint[] Labels,
    uint   Start,
    uint   End);

/// <summary>A line of disassembly: an instruction, or an entry or label line.</summary>
public readonly record struct Line(uint Offset, bool IsLabel, string Text);

/// <summary>
///     Parses an event-script block and disassembles it in the format of ffx-decomp's <c>ffx script disasm</c>, so
///     the two compare line for line. Block: +0x00 code length, +0x30 code start, +0x34 worker count, +0x38 worker
///     header offsets. Header: +0x02 variables, +0x04 int and +0x06 float constants, +0x08 entries, +0x0a labels,
///     with their tables at +0x14 to +0x24.
/// </summary>
public sealed class ScriptBlock {
    private const int WORKER_HEADER_LEN = 0x34;

    private readonly IBlockReader _r;

    public uint CodeStart { get; }
    public IReadOnlyList<WorkerInfo> Workers { get; }

    private ScriptBlock(IBlockReader r, uint code_start, List<WorkerInfo> workers) {
        _r        = r;
        CodeStart = code_start;
        Workers   = workers;
    }

    /// <summary>Parses a block, or returns null with the reason when it is malformed.</summary>
    /// <param name="block_size">How many bytes the block may span; code past the last worker's start runs to here.</param>
    public static ScriptBlock? parse(IBlockReader r, long block_size, out string error) {
        error = "";
        if (!r.in_range(0, 0x38)) { error = "truncated block"; return null; }
        uint code     = r.u32(0x30);
        uint code_len = r.u32(0x00);
        if ((long)code + code_len > block_size) { error = "code outside the block"; return null; }

        int count = r.u16(0x34);
        List<WorkerInfo> workers = new(count);
        for (int w = 0; w < count; w++) {
            if (!r.in_range(0x38 + 4 * w, 4)) { error = "truncated worker table"; return null; }
            uint header = r.u32(0x38 + 4 * w);
            if (!r.in_range(header, WORKER_HEADER_LEN)) { error = "worker header outside the block"; return null; }
            uint[]? entries = table(r, header, 0x08, 0x20);
            uint[]? labels  = table(r, header, 0x0a, 0x24);
            if (entries is null || labels is null) { error = "worker table outside the block"; return null; }
            workers.Add(new WorkerInfo(w, header, r.u16(header), entries, labels, 0, 0));
        }

        // A worker's code runs from its lowest entry to the next worker's lowest entry.
        uint[] starts = new uint[count];
        for (int w = 0; w < count; w++) {
            if (workers[w].Entries.Length == 0) { error = "worker without entries"; return null; }
            starts[w] = workers[w].Entries.Min();
        }
        int[] order = Enumerable.Range(0, count).OrderBy(i => starts[i]).ToArray();
        uint block_end = (uint)(block_size - code);
        for (int k = 0; k < order.Length; k++) {
            int i = order[k];
            uint end = k + 1 < order.Length
                ? starts[order[k + 1]]
                : (starts[i] >= code_len ? block_end : code_len);
            workers[i] = workers[i] with { Start = starts[i], End = end };
        }
        return new ScriptBlock(r, code, workers);
    }

    private static uint[]? table(IBlockReader r, uint header, int count_at, int offset_at) {
        int  n  = r.u16(header + count_at);
        uint at = r.u32(header + offset_at);
        if (!r.in_range(at, 4 * n)) return null;
        uint[] t = new uint[n];
        for (int i = 0; i < n; i++) t[i] = r.u32(at + 4 * i);
        return t;
    }

    /// <summary>Decodes the instructions of a code range; stops at the first instruction that leaves it.</summary>
    public List<Instr> decode(uint start, uint end) {
        List<Instr> list = [];
        uint pc = start;
        while (pc < end) {
            long at = CodeStart + pc;
            if (!_r.in_range(at, 1)) break;
            byte b = _r.u8(at);
            ushort? v = null;
            if ((b & 0x80) != 0) {
                if (!_r.in_range(at + 1, 2)) break;
                v = _r.u16(at + 1);
            }
            Instr ins = new(pc, (byte)(b & 0x7f), v);
            if (pc + ins.Length > end) break;
            list.Add(ins);
            pc += ins.Length;
        }
        return list;
    }

    public int? int_const(int worker, ushort i) {
        uint h = Workers[worker].Header;
        if (i >= _r.u16(h + 4)) return null;
        long at = _r.u32(h + 0x18) + 4L * i;
        return _r.in_range(at, 4) ? (int)_r.u32(at) : null;
    }

    public float? float_const(int worker, ushort i) {
        uint h = Workers[worker].Header;
        if (i >= _r.u16(h + 6)) return null;
        long at = _r.u32(h + 0x1c) + 4L * i;
        return _r.in_range(at, 4) ? BitConverter.UInt32BitsToSingle(_r.u32(at)) : null;
    }

    /// <summary>Variable <paramref name="i"/>: its location word and array length.</summary>
    public (uint word, ushort length)? variable(int worker, ushort i) {
        uint h = Workers[worker].Header;
        if (i >= _r.u16(h + 2)) return null;
        long at = _r.u32(h + 0x14) + 8L * i;
        return _r.in_range(at, 8) ? (_r.u32(at), _r.u16(at + 4)) : null;
    }

    public int variable_count(int worker) => _r.u16(Workers[worker].Header + 2);

    /// <summary>One instruction as text, without the native-call annotation.</summary>
    public string render(int worker, Instr ins) {
        Opcodes.Info op = Opcodes.Table[ins.Opcode];
        if (ins.Value is not ushort v) return op.Name;
        return op.Operand switch {
            Operand.Label  => $"{op.Name} l{v}",
            Operand.Int    => int_const(worker, v) is int c ? $"{op.Name} c{v,-8} ; {c}" : $"{op.Name} c{v}",
            Operand.Float  => float_const(worker, v) is float f ? $"{op.Name} f{v,-8} ; {RustFloat.debug(f)}" : $"{op.Name} f{v}",
            Operand.Imm    => $"{op.Name} {(short)v}",
            Operand.Var    => $"{op.Name} v{v}",
            Operand.Worker => $"{op.Name} w{v}",
            _              => $"{op.Name} 0x{v:x4}",
        };
    }

    /// <summary>Disassembles one worker; <paramref name="annotate"/> names native ids.</summary>
    public List<Line> disassemble(int worker, Func<ushort, string?>? annotate = null) {
        WorkerInfo w = Workers[worker];
        List<Line> lines = [];
        foreach (Instr ins in decode(w.Start, w.End)) {
            for (int i = 0; i < w.Entries.Length; i++) {
                if (w.Entries[i] == ins.Offset) lines.Add(new Line(ins.Offset, true, $"e{i}:"));
            }
            for (int i = 0; i < w.Labels.Length; i++) {
                if (w.Labels[i] == ins.Offset) lines.Add(new Line(ins.Offset, true, $"l{i}:"));
            }
            string text = render(worker, ins);
            if (Opcodes.Table[ins.Opcode].Operand == Operand.Func
             && ins.Value is ushort id
             && annotate?.Invoke(id) is string note) {
                text = $"{text,-24} ; {note}";
            }
            lines.Add(new Line(ins.Offset, false, "    " + text));
        }
        return lines;
    }

    /// <summary>The whole block in the Rust disassembler's format, after its "; path" line.</summary>
    public string disassemble_all(Func<ushort, string?>? annotate = null) {
        StringBuilder sb = new();
        sb.Append($"; {Workers.Count} workers\n");
        for (int w = 0; w < Workers.Count; w++) {
            sb.Append($"\nworker {w} type {Workers[w].Kind}\n");
            foreach (Line l in disassemble(w, annotate)) sb.Append(l.Text).Append('\n');
        }
        return sb.ToString();
    }
}

/// <summary>Formats an f32 as Rust's <c>{:?}</c> does: shortest round-trip digits, ".0" on whole numbers, exponent form outside 1e-4..1e16.</summary>
public static class RustFloat {
    public static string debug(float f) {
        if (float.IsNaN(f)) return "NaN";
        if (float.IsInfinity(f)) return f > 0 ? "inf" : "-inf";
        uint bits = BitConverter.SingleToUInt32Bits(f);
        bool neg = (bits >> 31) != 0;
        if (f == 0) return neg ? "-0.0" : "0.0";

        (string digits, int point) = shortest(Math.Abs(f));
        int sci = point - 1; // exponent in d.ddd form
        string body;
        if (sci < -4 || sci >= 16) {
            body = digits.Length == 1 ? $"{digits}e{sci}" : $"{digits[0]}.{digits[1..]}e{sci}";
        } else if (point <= 0) {
            body = "0." + new string('0', -point) + digits;
        } else if (point >= digits.Length) {
            body = digits + new string('0', point - digits.Length) + ".0";
        } else {
            body = digits[..point] + "." + digits[point..];
        }
        return neg ? "-" + body : body;
    }

    /// <summary>The shortest digit string d1..dn and the decimal point position (value = 0.d1..dn * 10^point).</summary>
    private static (string digits, int point) shortest(float x) {
        uint bits = BitConverter.SingleToUInt32Bits(x);
        int  exp  = (int)((bits >> 23) & 0xff);
        BigInteger mant = bits & 0x7fffff;
        if (exp == 0) exp = 1; else mant |= 0x800000;
        exp -= 150; // x = mant * 2^exp exactly
        BigInteger num = exp >= 0 ? mant << exp : mant;
        BigInteger den = exp >= 0 ? BigInteger.One : BigInteger.One << -exp;

        // k = floor(log10(x)), corrected exactly.
        int k = (int)Math.Floor(Math.Log10(x));
        while (cmp_pow10(num, den, k) < 0) k--;
        while (cmp_pow10(num, den, k + 1) >= 0) k++;

        for (int p = 1; p <= 9; p++) {
            // q = round_half_up(x * 10^(p-1-k))
            int s = p - 1 - k;
            BigInteger n = num, d = den;
            if (s >= 0) n *= BigInteger.Pow(10, s); else d *= BigInteger.Pow(10, -s);
            BigInteger q = BigInteger.DivRem(n, d, out BigInteger rem);
            if (rem * 2 >= d) q += 1;
            int kk = k;
            if (q == BigInteger.Pow(10, p)) { q /= 10; kk++; }
            string digits = q.ToString(CultureInfo.InvariantCulture);
            float back = float.Parse($"{digits}E{kk - (p - 1)}", CultureInfo.InvariantCulture);
            if (BitConverter.SingleToUInt32Bits(back) == bits) {
                return (digits.TrimEnd('0') is { Length: > 0 } t ? t : "0", kk + 1);
            }
        }
        string r = x.ToString("E8", CultureInfo.InvariantCulture); // unreachable for finite f32
        return (r[0] + r[2..10], k + 1);
    }

    /// <summary>Compares num/den with 10^k.</summary>
    private static int cmp_pow10(BigInteger num, BigInteger den, int k) =>
        k >= 0 ? num.CompareTo(den * BigInteger.Pow(10, k)) : (num * BigInteger.Pow(10, -k)).CompareTo(den);
}
