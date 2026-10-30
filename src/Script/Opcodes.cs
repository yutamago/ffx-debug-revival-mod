// SPDX-License-Identifier: MIT OR Apache-2.0

namespace Yuta.DebugRevival.Script;

/// <summary>How an instruction's 16-bit operand is read.</summary>
public enum Operand : byte {
    None,
    /// <summary>Index into the worker's label table.</summary>
    Label,
    /// <summary>Index into the worker's integer constants.</summary>
    Int,
    /// <summary>Index into the worker's float constants.</summary>
    Float,
    /// <summary>Signed 16-bit immediate.</summary>
    Imm,
    /// <summary>Index into the worker's variable table.</summary>
    Var,
    /// <summary>Native function id: group in the top 4 bits, index below.</summary>
    Func,
    /// <summary>Worker index (<c>call</c> runs that worker's entry 0).</summary>
    Worker,
    /// <summary>Anything else, printed as hex.</summary>
    Raw,
}

/// <summary>
///     The event-script (ATEL) opcodes. A byte's low 7 bits are the opcode; bit 7
///     set means a little-endian u16 operand follows. Names follow our Rust
///     disassembler (<c>ffx script disasm</c>); <see cref="Original"/> is the
///     name in FFX.exe's own table at 0x00c54600.
/// </summary>
public static class Opcodes {
    public readonly record struct Info(string Name, string Original, Operand Operand);

    public static readonly Info[] Table = build();

    private static Info[] build() {
        Info[] t = new Info[0x80];
        void set(int op, string name, string orig, Operand operand = Operand.None) => t[op] = new(name, orig, operand);

        set(0x00, "nop", "NOP");
        set(0x01, "lor", "OPLOR");
        set(0x02, "land", "OPLAND");
        set(0x03, "or", "OPOR");
        set(0x04, "xor", "OPEOR");
        set(0x05, "and", "OPAND");
        set(0x06, "eq", "OPEQ");
        set(0x07, "ne", "OPNE");
        set(0x08, "gtu", "OPGTU");
        set(0x09, "ltu", "OPLSU");
        set(0x0a, "gt", "OPGT");
        set(0x0b, "lt", "OPLS");
        set(0x0c, "geu", "OPGTEU");
        set(0x0d, "leu", "OPLSEU");
        set(0x0e, "ge", "OPGTE");
        set(0x0f, "le", "OPLSE");
        set(0x10, "btst", "OPBON");
        set(0x11, "btstn", "OPBOFF");
        set(0x12, "shl", "OPSLL");
        set(0x13, "sar", "OPSRL");
        set(0x14, "add", "OPADD");
        set(0x15, "sub", "OPSUB");
        set(0x16, "mul", "OPMUL");
        set(0x17, "div", "OPDIV");
        set(0x18, "mod", "OPMOD");
        set(0x19, "not", "OPNOT");
        set(0x1a, "neg", "OPUMINUS");
        set(0x1b, "op_1b", "OPFIXADRS");
        set(0x1c, "bnot", "OPBNOT");
        set(0x1d, "nop_1d", "LABEL", Operand.Raw);
        set(0x1e, "nop_1e", "TAG", Operand.Raw);
        set(0x1f, "pushv", "PUSHV", Operand.Var);
        set(0x20, "popv", "POPV", Operand.Var);
        set(0x21, "popv_1", "POPVL", Operand.Var);
        set(0x22, "pushva", "PUSHAR", Operand.Var);
        set(0x23, "popva", "POPAR", Operand.Var);
        set(0x24, "popva_1", "POPARL", Operand.Var);
        set(0x25, "popfacc", "POPA");
        set(0x26, "pushfacc", "PUSHA");
        set(0x27, "pushvref", "PUSHARP", Operand.Var);
        set(0x28, "pusha", "PUSHX");
        set(0x29, "pushb", "PUSHY");
        set(0x2a, "popa", "POPX");
        set(0x2b, "op_2b", "REPUSH");
        set(0x2c, "popb", "POPY");
        set(0x2d, "pushc", "PUSHI", Operand.Int);
        set(0x2e, "pushimm", "PUSHII", Operand.Imm);
        set(0x2f, "pushfc", "PUSHF", Operand.Float);
        set(0x30, "jmp", "JMP", Operand.Label);
        set(0x31, "jnz", "CJMP", Operand.Label);
        set(0x32, "jz", "NCJMP", Operand.Label);
        set(0x33, "call", "JSR", Operand.Worker);
        set(0x34, "ret", "RTS");
        set(0x35, "func", "CALL", Operand.Func);
        set(0x36, "req0", "REQ");
        set(0x37, "reqw0", "REQSW");
        set(0x38, "reqe0", "REQEW");
        set(0x39, "op_39", "PREQ");
        set(0x3a, "op_3a", "PREQSW");
        set(0x3b, "op_3b", "PREQEW");
        set(0x3c, "end", "RET");
        set(0x3d, "endp", "RETN");
        set(0x3e, "end_1", "RETT");
        set(0x3f, "endp_1", "RETTN");
        set(0x40, "op_40", "HALT");
        set(0x41, "op_41", "PUSHN");
        set(0x42, "op_42", "PUSHT");
        set(0x43, "op_43", "PUSHVP");
        set(0x44, "op_44", "PUSHFIX");
        string[] req = [ "FREQ", "TREQ", "BREQ", "BFREQ", "BTREQ" ];
        for (int i = 0; i < 5; i++) {
            set(0x45 + i, $"req{i + 1}", req[i]);
            set(0x4a + i, $"reqw{i + 1}", req[i] + "SW");
            set(0x4f + i, $"reqe{i + 1}", req[i] + "EW");
        }
        set(0x54, "op_54", "DRET");
        set(0x55, "popa_jmp", "POPXJMP", Operand.Label);
        set(0x56, "popa_jnz", "POPXCJMP", Operand.Label);
        set(0x57, "popa_jz", "POPXNCJMP", Operand.Label);
        set(0x58, "func_facc", "CALLPOPA", Operand.Func);
        for (int i = 0; i < 4; i++) {
            set(0x59 + i, $"popi{i}", $"POPI{i}");
            set(0x67 + i, $"pushi{i}", $"PUSHI{i}");
        }
        for (int i = 0; i < 10; i++) {
            set(0x5d + i, $"popf{i}", $"POPF{i}");
            set(0x6b + i, $"pushf{i}", $"PUSHF{i}");
        }
        set(0x75, "pushg", "PUSHAINTER", Operand.Raw);
        set(0x76, "nop_76", "SYSTEM", Operand.Raw);
        set(0x77, "op_77", "REQWAIT");
        set(0x78, "op_78", "PREQWAIT");
        set(0x79, "op_79", "REQCHG");
        set(0x7a, "op_7a", "ACTREQ");
        for (int op = 0x7b; op < 0x80; op++) {
            set(op, $"op_{op:x2}", "", Operand.Raw);
        }
        return t;
    }
}
