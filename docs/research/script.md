# Facts: event-script (ATEL) VM and its debugger

For the yuta.debugrevival mod: a mod-side script debugger and the original on-screen debugger pages.
- Addresses are VAs with image base 0x00400000. "off" = FhCallLocation offset = VA - 0x400000.
- VERIFIED = code seen (dump, objdump of FFX.exe, or our ported Rust with parity tests). INFERRED = not traced end to end.

Sources:
- symbols/ffx.sym and docs/formats/script.md ("The VM in memory", "The interpreter", "Signals");
- crates/ffx-script/src/vm/{mod.rs, exec.rs, bridge/run.rs, bridge/signal.rs, bridge/helpers.rs};
- crates/ffx-script/src/{opcode.rs, script.rs, text.rs, natives.rs}; crates/ffx-cli/src/main.rs;
- a Ghidra dump of FFX.exe (ffx-decomp's tools/ghidra/dump.sh); objdump of FFX.exe;
- Fahrenheit alpha12 src/core/atel/*.cs, src/core/ffx/gstate.cs, src/core/call_1.g.cs, src/core/ffx/call*.cs.

Note on FhCall handles: Fahrenheit has two generated handle families, both `d_UnknownFn` unless stated.
- Shared (`Fahrenheit.FhCall`, src/core/call_1.g.cs): `FUN_00XXXXXX_00YYYYYY`, where the second number is the FFX-2 offset.
- FFX-only (`Fahrenheit.FFX.FhCall`, src/core/ffx/call_N.g.cs): lowercase `FUN_00xxxxxx`.
- With `d_UnknownFn` the mod must declare its own delegate (cdecl unless stated).

---------------------------------------------------------------------------------------------------

## 1. Runtime data model

### 1.1 Globals

| VA (off) | Type | Meaning | Status |
|---|---|---|---|
| `0x01325ba0` (`0xf25ba0`) | ctrl work[7], `0x238` each | `g_AtelCtrlWorks`. `AtelGetCtrlWork(i)` `0x0086afa0` returns `0x01325ba0 + i*0x238`. Fahrenheit: `Fahrenheit.FFX.Globals.Atel.controllers` (`AtelWorkerController*`, same address). | VERIFIED |
| `0x01326b28` (`0xf26b28`) | `AtelCtrlWork*` | `g_ScriptContext`, the current ctrl work. It sits right after the array. Fahrenheit: `Globals.Atel.current_controller`. `AtelGetCurCtrlWork` `0x0086aff0` has an FhCall handle `Fahrenheit.FFX.FhCall.AtelGetCurCtrlWork` (`d_AtelGetCurCtrlWork`: `nint()`). | VERIFIED |
| `0x01326b2c` | u32 | Index of the worker being run in `Atel_RunCtrlWorkers` (`ctx+4`) | VERIFIED (port) |
| `0x01326b30` | u32 | Index of the ctrl work being run (`ctx+8`). `Atel_ExecCtrlWorks` writes it. | VERIFIED (asm `0x0086892b`) |
| `0x013270dc` / `0x013270e0` / `0x013270e4` | u16 / u16 / u32 | `AtelGetBasicWork` cache: ctrl id, index, worker | VERIFIED |
| `0x013270d8` | u32 | `g_AtelError` (zeroed on PS2 error paths) | VERIFIED |
| `0x01326bd8` | f32 | `g_GameSpeed`, a factor on each worker's speed | VERIFIED |
| `0x00c52740` | `Mutex*` | `g_ScriptRunMutex`. -1 until the first `Script_Run`, then a `new(4)` that holds a `CreateMutexW` handle. | VERIFIED |
| `0x00c52704` | u32[14] | `g_AtelConstants` (opcode `75`): `0x5a000000+4k` (k=0..3) are i32 storage-5 location words of I0..I3; `0x6a000010+4k` (k=0..9) are f32 words of F0..F9. That is, "push a reference to internal register n". | VERIFIED (bytes read) |
| `0x01328558` | `entry*[16]` | `g_NativeTables`: per group (`id>>12`) a table of 16-byte native entries | VERIFIED |

About `Globals.Atel.current_worker` (`0xF270E4`): Fahrenheit reads it with `ptr_at`, so it treats the address `0x013270e4` itself as the worker. In fact it holds a pointer to the last `AtelGetBasicWork` result.
- **Bug in Fahrenheit; do not use it.** VERIFIED (`FhUtil.ptr_at` = base+offset).
- Use `*(u32*)0x013270e4` at most, and only for "last looked up worker", not for "running worker".

### 1.2 Ctrl work (`0x238` bytes, 7 of them)

Ctrl work roles:
- **0:** the event (field). VERIFIED.
- **2 and 3:** battle (75 and 6 workers in the battle snapshot). VERIFIED.
- **6:** the menu's (`AtelMenuExec` runs 6..7). VERIFIED.
- **1, 4, 5:** INFERRED (other battle/system uses).

| Off | Type | Meaning | Status |
|---|---|---|---|
| `+0x00` | u8 | Flags; bit 0 = in use (`AtelGetEventActorTotal`, `AtelRemoveCtrlWork`); bit 3 = event started (decides whether the `+0x4c` callback runs workers or the init) | VERIFIED |
| `+0x02` | u8 | Flags (bit 4 cleared each pass) | VERIFIED (port) |
| `+0x03` | u8 | Bits 0-1: `Script_Run`'s run mode. 1 = skip every worker; 2 = only workers with `+0x36 & 0x8000`. Setup writes `0x30`. | VERIFIED |
| `+0x08` | **u8** | **Debugger state.** 0 = run; 1 = step (passed to `Script_Run` as `mode`, see 3.3); 2 = run one frame; 3 = stopped (the ctrl work's whole `+0x4c` update is skipped). After the update, `Atel_ExecCtrlWorks` turns 1 and 2 into 3. `Atel_RunCtrlWorkers` sets 3 on a breakpoint hit. `AtelRequireCtrlWork` sets 0. Nothing in release writes 1 or 2. | VERIFIED (asm `0x00868920`-`0x0086895e`; port) |
| `+0x0a` | u16 | The player's worker (`0xffff` = none) | VERIFIED |
| `+0x0c` | u16 | Workers with a full work area, the "actors"; only these run. Fahrenheit: `runnable_script_count`. | VERIFIED |
| `+0x10` | u16 | All workers, including the `0x30`-byte subroutine-only ones | VERIFIED |
| `+0x14`, `+0x16`, `+0x18` | u16 | Counts of the `0x5b8`, `0x558` and `0x2e8` classes. Fahrenheit: `count_0x5b8`/`_0x558`/`_0x2e8`. | VERIFIED; agrees |
| `+0x1a` | u16 | This ctrl work's number | VERIFIED |
| `+0x1c` | u32 | First worker. Fahrenheit: `_workers`. | VERIFIED; agrees |
| `+0x2c`, `+0x30` | u32 | Bases of variable storage 0 (in the field: the save data's script area) and storage 1 | VERIFIED |
| `+0x34`, `+0x38` | u32 | Message tables. Fahrenheit: `text_chunk`, `en_text_chunk`. | agrees |
| `+0x48` | u32 | The script block. Ctrl work 0: the event's. Battle ctrl works: 0, and each worker has its own block at worker `+4`. docs/formats/script.md calls it "a map record"; that is stale. `AtelRequireCtrlWork` stores the block here. | VERIFIED |
| `+0x4c` | fn | Per-frame update callback `cdecl(ctrl)`: `AtelExecCtrlEvent` `0x00867740` (event), `Atel_CtrlExec` `0x00867710` (default), `MsAtelUpdate0/1/2` (battle). | VERIFIED |
| `+0x50` | fn | Start callback (default `Atel_CtrlStart` `0x008683e0`) | VERIFIED |
| `+0x54` | fn | Storage-3 address callback `(worker, offset)` | VERIFIED |
| `+0x58`, `+0x5c` | fn | `cdecl(worker, level)`, called before and after each `Script_Run` whose `callbacks` argument is non-zero. **Called without a null check.** | VERIFIED (asm `0x008642e8`) |
| `+0x60` | fn | Gate for `REQ` forms `47`-`49`, `4c`-`4e`, `51`-`53` | VERIFIED |
| **`+0x64`** | 4 × 16 B | **Breakpoint table**, layout below | VERIFIED |
| `+0x1e4`, `+0x1e6`, `+0x1e8` | u8 / u8 / u16 | Talk choice: open flag, member, chosen worker. Fahrenheit names `+0x1e8` `__0x1E8` (last NPC interaction). | VERIFIED |
| `+0x1f4` | u32 | Frame counter while bit 3 is set | VERIFIED |
| `+0x1f8`, `+0x1fc` | u32 | Battle: table of blocks and table of per-block "x8" words (debug data pointers, 0 in release; INFERRED). Fahrenheit: `script_chunk`, `__0x1FC`. | VERIFIED (sym) |

Breakpoint entry, `ctrl + 0x64 + slot*0x10` (slot 0..3):

| Off | Type | Meaning |
|---|---|---|
| `+0x0` | i32 | id; `>= 0` = set, -1 = free. `AtelRequireCtrlWork` writes -1 to all four. |
| `+0x4` | u32 | tag (a user value) |
| `+0x8` | u8* | absolute address of the patched byte; compared with the level's PC |
| `+0xc` | - | unused |
| `+0xe` | u8 | the original byte that `0x40` replaced |
| `+0xf` | u8 | "hit/armed": set on the first stop, cleared when the replaced instruction runs |

### 1.3 Worker (`AtelBasicWorker` in Fahrenheit)

Workers lie back to back from `ctrl+0x1c` in five size classes, in this order:
1. `actors - n558 - n2e8 - n5b8` workers of `0xb58`;
2. `n558` of `0x558`;
3. `n5b8` of `0x5b8`;
4. `n2e8` of `0x2e8`;
5. the rest, up to `+0x10`, of `0x30`.

This is `AtelGetBasicWork` `0x0086a830` (VERIFIED, Rust `ffx_script::vm::worker_offset`). Fahrenheit's `AtelWorkerController.worker(idx)` computes the same.
- The `0x30` workers have no stack and no levels. They are subroutine namespaces reached only by `JSR` (opcode `33`).

| Off | Type | Meaning | Fahrenheit | Status |
|---|---|---|---|---|
| `+0x00` | u32 | Worker header (in the block); its first byte = worker type | `script_header` | agrees |
| `+0x04` | u32 | Script block | `script_chunk` | agrees |
| `+0x08` | u32 | Debug data (`.bdb`) pointer; 0 in release | - | INFERRED (fork, see 6) |
| `+0x0c..+0x28` | u32[8] | Source page chains (debug only) | `+0xC event_actor_total` u16 | conflict; INFERRED both ways, irrelevant in release |
| `+0x2c` | u16 | Entry point being run | `entry_point_idx` | agrees |
| `+0x2e` | u16 | Worker index | `worker_idx` | agrees |
| `+0x32` | u8 | Level (request priority) running, 0..8 | `current_thread_priority` | agrees |
| `+0x34` | u16 | Flags, below | `__0x34` | VERIFIED |
| `+0x36` | u16 | Flags 2, below | `__0x36` | VERIFIED |
| `+0x3c` | | Camera handle cache | | VERIFIED |
| `+0x40` | u32 | Bound party character | | VERIFIED |
| `+0x44` | ptr | Target of opcode `79` (REQCHG): 8 u16, filled from `TkGetVibrateDefaultCurve` the first time | `request_table` (ushort*) | name conflict; our "vibration" label is INFERRED |
| `+0x48` | i32[4] | Integer registers I0-I3 (opcodes `59`-`5c`, `67`-`6a`). Storage-5 variables also start here. | `reg_int` | agrees |
| `+0x58` | f32[10] | Float registers F0-F9 (`5d`-`66`, `6b`-`74`) | `reg_float` | agrees |
| `+0x80` | `sig*` | Signal queue head | `pending_signal_queue` | agrees |
| `+0x88` | `sig*` | Free signal nodes | `free_signals` | agrees |
| `+0x90` | `sig*` | Signal being run | `current_signal` | agrees |
| `+0x94` | f32 | Speed | `__0x94` | VERIFIED |
| `+0x98` | f32 | Speed accumulator (`+= speed * g_GameSpeed`) | `__0x98` | VERIFIED |
| `+0x9c` | u32 | Field character | (commented out) | VERIFIED |
| `+0xc4` | stack | Value stack, below | `stack` | agrees on offsets |
| `+0x12c` | level[9] | Levels, `0x4c` each. Types 5 and 6 have 2; index clamped by `Script_WorkerLevel` `0x00872650`. | `threads`, declared `[18]` with comment `[9]` | 9 VERIFIED; Fahrenheit over-declares |
| `+0x284` / `+0x558` | | Position block of types 5/6 / of the others (type 4 has none) | | VERIFIED |

Worker `+0x34` flags (VERIFIED, `Script_Run`):
- `0x2`: runnable (`Script_Run` skips the worker when clear).
- `0x4`: runs during battle. When clear, and `+0x36 & 0x40` is clear, and `MsBattleCheck()` is true, the worker is skipped.
- `0x8`: the last run yielded cleanly (empty stack).
- `0x10`: init done (`Atel_CtrlStart`).
- Bits 10-12 are the run state:
  - `0x400`: running or in a native call;
  - `0x800`: waiting on a request (`REQSW`/`REQEW`/`PREQ*`);
  - `0xc00`: entry ended (`RET*`);
  - `0x1000`: halted (`DRET`);
  - `0x1400`: runaway (more than 65536 instructions in one run).
- A breakpoint hit clears bits 10-12 (`& 0xe3ff`).

Worker `+0x36` flags:
- `0x10`/`0x20`: camera-native bookkeeping;
- `0x40`: runs in battle;
- `0x180`: the worker runs in the passes without input;
- `0x600`: pending levels;
- `0x1000`: cleared by `clear`;
- `0x8000`: runs in run mode 2.

### 1.4 Level (`AtelWorkThread` in Fahrenheit; `0x4c` bytes; worker `+0x12c + n*0x4c`)

| Off | Type | Meaning | Fahrenheit | Status |
|---|---|---|---|---|
| `+0x00` | u32[3(4)] | `JSR` return offsets (code-relative) | `script_offset_stack[4]` | agrees |
| `+0x10` | u16[3(4)] | `JSR` callee worker ids | `script_idx_stack[4]` | agrees |
| **`+0x18`** | **u8*** | **PC, an absolute address.** Code offset = `PC - (block + u32[block+0x30])`, where block = worker `+4`. | `pc`; `pc_of()` does the same | agrees |
| `+0x1d` | i8 | `JSR` call depth (max 3) | `stack_depth` | agrees |
| `+0x1e` | u8 | Native call state: 0 = not started, 1 = being polled | `__0x1E` | VERIFIED |
| `+0x1f` | u8 | Wait state: 0 = none; 1 = waits for a reply (`REQSW`/`REQEW`/`PREQSW`/`PREQEW`); 2 = waits while a request for entry `+0x2c` is queued on worker `+0x2e` (opcodes `77`/`78`) | `wait_state` | VERIFIED |
| `+0x20`, `+0x24` | u32 | Registers A and B (`POPX`/`PUSHX`, `POPY`/`PUSHY`) | `reg_x`, `reg_y` | agrees |
| `+0x28` | f32 | Float accumulator (`POPA`/`PUSHA`, `CALLPOPA`) | `reg_a` | agrees |
| `+0x2c..+0x3f` | 0x14 B | Native-call scratch (the "args" pointer handed to natives); request waits keep entry/worker/level at `+0x2c`/`+0x2e`/`+0x30` | `thread_local_storage` | agrees |
| `+0x3c`, `+0x3e`, `+0x40` | u16 | Binary header index, header table index, ctrl work index the level runs from | `+0x40 ctrl_idx` | agrees |
| `+0x44`, `+0x48` | ptr | Move and turn blocks | `motion`, `rotation` | agrees |

### 1.5 Value stack (worker `+0xc4`)

Layout:
- `+0x00` i32 depth;
- `+0x04` u32 values[19];
- `+0x54` u8 kinds[19] (1 = int, 2 = float).

Behaviour:
- **19 slots** (`AtelPushStackInteger` `0x0086e3f0`: `cmp edx, 0x13`). VERIFIED.
- Fahrenheit's `AtelStack.MAX_SIZE = 20`. Wrong by one; the offsets agree.
- A push onto a full stack is dropped. A pop from an empty stack gives 0.

### 1.6 Signal node (stride `0x18`; Fahrenheit `AtelSignal` says `Size = 0x16`)

| Off | Type | Ours | Fahrenheit |
|---|---|---|---|
| `+0x00`, `+0x04` | ptr | next; prev (points at the previous node's or the head's `next` field) | `ptr_next`, `ptr_prev` |
| `+0x08` | u16 | Entry to run (in a reply: the entry that ran) | `entry_point` |
| `+0x0a`, `+0x0c` | u16 | Sender, receiver (`0xffff` = none) | `idx_work_src`, `idx_work_tgt` |
| `+0x0e` | u8 | Bits 0-3 = level; bits 4-6 = state (1 queued, 2 running); bit 7 = new | `priority` (bits 0-3), `process_status` (bits 4-7) |
| `+0x0f` | u8 | Kind: 0 `REQ`, 1 `REQSW` (sender waits for start), 2 `REQEW` (waits for end), 3 reply | `state` enum IGNORE/RUN/COMPLETE/ACKNOWLEDGED. **Disagrees**; ours comes from ported code with parity tests. |
| `+0x10`, `+0x12`, `+0x14` | u16 | Binary header index, table index, ctrl work. Stack garbage in practice. | `__0x10`, `__0x12`, `idx_work_ctrl` |

- The node stride of `0x18` is VERIFIED by `Atel_FreeLineSignals`: `t + 0x40 + i*0x18`.
- The queue is ordered by level (highest first), with replies last.

### 1.7 Script block and worker header (in memory = file layout)

Block header:
- `+0x00` code length;
- `+0x20` storage-6 offset;
- `+0x2c` expansion header;
- **`+0x30` code start**;
- `+0x34` u16 worker count;
- `+0x36` u16 actors;
- **`+0x38` u32[n] worker header offsets.** Fahrenheit's `script_header_offsets` is typed `ushort*`; that is wrong, `AtelGetIndexWork` reads u32.

Worker header:

| Off | Meaning |
|---|---|
| `+0x00` | u16 type |
| `+0x02` | var count |
| `+0x04` | int const count |
| `+0x06` | float const count |
| `+0x08` | entry count |
| `+0x0a` | label count |
| `+0x14` | var table (8 B each) |
| `+0x18` | int consts (i32) |
| `+0x1c` | float consts (f32) |
| `+0x20` | entry table (u32 code offsets) |
| `+0x24` | label table (u32 code offsets) |
| `+0x28` / `+0x2c` / `+0x30` | storage 2 / 3 / 4 bases |

All header offsets are relative to the block. VERIFIED.

### 1.8 Variables

A descriptor is `{u32 word, u16 length, u16 range}`:
- type = bits 28-31: u8, i8, u16, i16, u32, i32, f32 = 0..6;
- storage = bits 25-27;
- offset = bits 0-23.

| Storage | Area |
|---|---|
| 0 | ctrl `+0x2c` (save data in the field) |
| 1 | ctrl `+0x30` |
| 2 / 3 / 4 | `block + header[+0x28/+0x2c/+0x30]` (3 goes through ctrl `+0x54` when that is set) |
| 5 | worker `+0x48` |
| 6 | `block + block[+0x20]` |
| 7 | none |

`AtelGetRealVarAdrs` `0x0086c2e0` does `switch(word >> 25 & 7)`. VERIFIED.
- **Fahrenheit's `AtelScriptVar.location = properties & 0x0F` reads bits 24-27, so it gives 2×storage.** Their enum order matches our storage numbers, but their extraction is wrong. Their `element_count` is a u32; ours is u16 length + u16 range.
- Storage 0, 1 and 3 resolve through `g_ScriptContext`. A mod reading a worker of ctrl k must use ctrl k's `+0x2c`/`+0x30`, not the current ctrl.
- Index access is clamped to `0..len-1`.

### 1.9 Enumerating every live worker each frame (VERIFIED layout; read on the game thread, see 3.5)

```
for c in 0..7:
  ctrl = 0x01325ba0 + c*0x238
  if (u8[ctrl] & 1) == 0 or u32[ctrl+0x1c] == 0: continue
  actors = u16[ctrl+0x0c]; n558=u16[+0x16]; n5b8=u16[+0x14]; n2e8=u16[+0x18]
  for i in 0..actors:                      # only these have stacks/levels; i in actors..u16[+0x10] are 0x30-byte subroutine workers
    w = u32[ctrl+0x1c] + worker_offset(i)  # classes 0xb58,0x558,0x5b8,0x2e8 as in 1.3
    hdr = u32[w]; block = u32[w+4]; type = u8[hdr]
    code = block + u32[block+0x30]
    nlev = (type==5||type==6) ? 2 : 9
    cur = u8[w+0x32]; L = w+0x12c+min(cur,nlev-1)*0x4c
    pc_off = u32[L+0x18] - code
    runnable = u16[w+0x34] & 2; state = u16[w+0x34] & 0x1c00; wait = u8[L+0x1f]
    stack: depth=i32[w+0xc4], values u32[w+0xc8+4k], kinds u8[w+0x118+k]
    signals: for (s = u32[w+0x80]; s; s = u32[s]) ...
```

Do not call `AtelGetBasicWork` for this. It works on the current ctrl only, maps out-of-range indices to worker 0, and overwrites the cache.

---------------------------------------------------------------------------------------------------

## 2. Bytecode encoding (for a C# disassembler)

### 2.1 Instruction format (VERIFIED: `AtelFetchCommand` `0x00869d60`, `crates/ffx-script/src/script.rs::decode`)
- Byte `b`:
  - opcode = `b & 0x7f`;
  - if `b & 0x80`, a **u16 little-endian operand** follows, and the length is 3;
  - otherwise the length is 1.
  - The length formula is `((b | 0x40) >> 6)`.
- **The operand's presence comes only from bit 7 of the byte, not from the opcode.** The tables below say which opcodes normally carry one.
- The interpreter dispatches opcodes 0..`0x7a`. Above `0x7a` it stops with an error.
- Fetch returns `opcode<<24 | operand`.
- Our decoder walks each worker's code range in order. A worker's range runs from its lowest entry to the next worker's lowest entry, or to code length.
- The disassembler reads code statically from the file block. In memory, the same bytes are at `block + block[+0x30]`.

### 2.2 Operands (VERIFIED: `Script_Run` ports, `text.rs::render`)

| Kind (ours) | Original kind (table `0x00c54600`) | Meaning | Printed as |
|---|---|---|---|
| Label | 7 | Index into the **worker's** label table. At runtime `Script_Jump` uses the label table of the worker owning the code at this call depth (inside a `JSR`: the callee's). Target = `code + labels[i]`. | `l<i>` |
| Int | 2 | Index into the int constant table (header `+0x18`, i32) | `c<i>`, padded to 8, `; <value>` |
| Imm | 1 | Signed 16-bit immediate | signed decimal |
| Float | 5 | Index into the float constant table (header `+0x1c`, f32) | `f<i>`, padded to 8, `; <value>` (Rust `{:?}`, e.g. `1.0`) |
| Var | 3 | Index into the var table (header `+0x14`) | `v<i>` |
| Func | 4 | Native id: group = `id>>12`, index = `id & 0xfff` | `0x%04x` (+ annotation) |
| Worker | 8 | Worker index (`JSR` calls that worker's **entry 0**, in the caller's level, at most 3 deep) | `w<i>` |
| Raw | 0 / 6 (TAG) | Other | `0x%04x` |

### 2.3 Opcode table

Columns:
- "ours" = `crates/ffx-script/src/opcode.rs` names (what `ffx script disasm` prints).
- "orig" = the original names in FFX.exe's table at **`0x00c54600`**: 16-byte records `{char* name, u32 a, u32 b, u16 len, u16 kind}` for opcodes `0x00`-`0x7a`. Dumped from the exe. VERIFIED.
  - Fahrenheit's `AtelOp` enum uses the same names, with bit 7 set on the opcodes that take an operand.
- len = the table's normal length. 0 = pseudo-op that never runs. 3 = has a u16 operand.

| op | ours | orig | len | operand |
|---|---|---|---|---|
| 00 | nop | NOP | 0 | - |
| 01 | lor | OPLOR | 1 | - |
| 02 | land | OPLAND | 1 | - |
| 03 | or | OPOR | 1 | - |
| 04 | xor | OPEOR | 1 | - |
| 05 | and | OPAND | 1 | - |
| 06 | eq | OPEQ | 1 | - |
| 07 | ne | OPNE | 1 | - |
| 08 | gtu | OPGTU | 1 | - |
| 09 | ltu | OPLSU | 1 | - |
| 0a | gt | OPGT | 1 | - |
| 0b | lt | OPLS | 1 | - |
| 0c | geu | OPGTEU | 1 | - |
| 0d | leu | OPLSEU | 1 | - |
| 0e | ge | OPGTE | 1 | - |
| 0f | le | OPLSE | 1 | - |
| 10 | btst | OPBON | 1 | - |
| 11 | btstn | OPBOFF | 1 | - |
| 12 | shl | OPSLL | 1 | - |
| 13 | sar | OPSRL (arithmetic in fact) | 1 | - |
| 14 | add | OPADD | 1 | - |
| 15 | sub | OPSUB | 1 | - |
| 16 | mul | OPMUL | 1 | - |
| 17 | div | OPDIV | 1 | - |
| 18 | mod | OPMOD | 1 | - |
| 19 | not | OPNOT | 1 | - |
| 1a | neg | OPUMINUS | 1 | - |
| 1b | op_1b | OPFIXADRS (invalid: error) | 1 | - |
| 1c | bnot | OPBNOT | 1 | - |
| 1d | nop_1d | LABEL | 0 | (label) |
| 1e | nop_1e | TAG | 0 | (tag) |
| 1f | pushv | PUSHV | 3 | Var |
| 20 | popv | POPV | 3 | Var |
| 21 | popv_1 | POPVL (store, narrow types saturate) | 3 | Var |
| 22 | pushva | PUSHAR | 3 | Var |
| 23 | popva | POPAR | 3 | Var |
| 24 | popva_1 | POPARL | 3 | Var |
| 25 | popfacc | POPA (pop into the float accumulator) | 1 | - |
| 26 | pushfacc | PUSHA | 1 | - |
| 27 | pushvref | PUSHARP | 3 | Var |
| 28 | pusha | PUSHX (register A) | 1 | - |
| 29 | pushb | PUSHY (register B) | 1 | - |
| 2a | popa | POPX | 1 | - |
| 2b | op_2b | REPUSH (push the top again) | 1 | - |
| 2c | popb | POPY | 1 | - |
| 2d | pushc | PUSHI | 3 | Int (const index) |
| 2e | pushimm | PUSHII | 3 | Imm (s16) |
| 2f | pushfc | PUSHF | 3 | Float (const index) |
| 30 | jmp | JMP | 3 | Label |
| 31 | jnz | CJMP (if A != 0) | 3 | Label |
| 32 | jz | NCJMP (if A == 0) | 3 | Label |
| 33 | call | JSR | 3 | Worker |
| 34 | ret | RTS | 1 | - |
| 35 | func | CALL (native; pushes its result) | 3 | Func |
| 36 | req0 | REQ | 1 | - |
| 37 | reqw0 | REQSW | 1 | - |
| 38 | reqe0 | REQEW | 1 | - |
| 39 | op_39 | PREQ | 1 | - |
| 3a | op_3a | PREQSW | 1 | - |
| 3b | op_3b | PREQEW | 1 | - |
| 3c | end | RET | 1 | - |
| 3d | endp | RETN | 1 | - |
| 3e | end_1 | RETT | 1 | - |
| 3f | endp_1 | RETTN | 1 | - |
| 40 | op_40 | **HALT = breakpoint** (opcode.rs's doc "wait" is stale) | 1 | - |
| 41 | op_41 | PUSHN (invalid in VM) | 3 | Worker |
| 42 | op_42 | PUSHT (invalid) | 3 | Tag |
| 43 | op_43 | PUSHVP (invalid) | 3 | Var |
| 44 | op_44 | PUSHFIX (invalid) | 3 | Int |
| 45-49 | req1-req5 | FREQ, TREQ, BREQ, BFREQ, BTREQ | 1 | - |
| 4a-4e | reqw1-reqw5 | FREQSW, TREQSW, BREQSW, BFREQSW, BTREQSW | 1 | - |
| 4f-53 | reqe1-reqe5 | FREQEW, TREQEW, BREQEW, BFREQEW, BTREQEW | 1 | - |
| 54 | op_54 | DRET (halt the worker) | 1 | - |
| 55 | popa_jmp | POPXJMP | 3 | Label |
| 56 | popa_jnz | POPXCJMP | 3 | Label |
| 57 | popa_jz | POPXNCJMP | 3 | Label |
| 58 | func_facc | CALLPOPA (native, result into the float accumulator) | 3 | Func |
| 59-5c | popi0-popi3 | POPI0-POPI3 | 1 | - |
| 5d-66 | popf0-popf9 | POPF0-POPF9 | 1 | - |
| 67-6a | pushi0-pushi3 | PUSHI0-PUSHI3 | 1 | - |
| 6b-74 | pushf0-pushf9 | PUSHF0-PUSHF9 | 1 | - |
| 75 | pushg | PUSHAINTER (push `g_AtelConstants[n]`, a register reference) | 3 | Imm (index 0..13; ours prints Raw `0x%04x`) |
| 76 | nop_76 | SYSTEM (no-op in VM) | 3 | Imm (ours: Raw) |
| 77 | op_77 | REQWAIT | 1 | - |
| 78 | op_78 | PREQWAIT | 1 | - |
| 79 | op_79 | REQCHG | 1 | - |
| 7a | op_7a | ACTREQ | 1 | - |
| 7b-7f | op_7b-op_7f | (none; invalid) | - | - |

Comparisons: `a` is pushed first and `b` is on top. Floats use the same opcodes.

The other record words `a` and `b` are 1/2 for the operators and 0 otherwise. INFERRED: operand/result type classes. A disassembler does not need them.

### 2.4 Output format of `ffx script disasm` (VERIFIED, `text.rs::disassemble_annotated` + `ffx-cli/src/main.rs`)

```
; <archive path>
; <N> workers

worker <w> type <type u16>
e<i>:                      # entry i starts here (all entries at this offset, in index order)
l<j>:                      # then labels
    <mnemonic>             # 4-space indent; no operand
    <mnemonic> l3 | w3 | v3 | -12 | 0x6012
    pushc c3       ; 1234  # "c{idx:<8} ; {value}"
    pushfc f2       ; 1.5
    func 0x70ab              ; Native_SetActorsProperty 0x007a5360   # line padded to 24, then "; name 0xhandler" or "; 0xhandler"
```

- Mnemonics come from `OPCODES[op].name`. The operand style comes from `OPCODES[op].operand` and is only used when bit 7 was set.
- There are no addresses or offsets in the lines.
- A C# debugger should also print `%05x:` code offsets (the original debugger page 13 did) and mark the PC.

### 2.5 Native names

- There are 11 native tables (`natives.rs::GROUP_TABLES`, from `AtelInitTotal`):

  | Group | Table |
  |---|---|
  | 0 | `0x00c50050` |
  | 1 | `0x00c52be0` |
  | 4 | `0x00c88d88` |
  | 5 | `0x00c891f8` |
  | 6 | `0x00c43998` |
  | 7 | `0x00c42628` |
  | 8 | `0x00c5dc90` |
  | 9 | `0x00c5d8c0` |
  | b | `0x00c40e30` |
  | c | `0x00c52dd8` |
  | d | `0x00c85eb0` |

- Each entry is 16 bytes, in slot order: `{start(w, args, stack), poll(w, args) -> bits (1 done, 2 no-yield, 4 run on), retf(w, args, stack) -> ST0, reti(w, args, stack) -> eax}`, all cdecl, null = absent. The slots are dispatched by `Script_CallNative` `0x008777a0`, `Script_PollNative` `0x00877730` and `Script_FinishNative` `0x008777f0`.
  - Fahrenheit: `AtelCallTarget` and the delegates `d_CT_Init/Exec/RetF/RetI`.
- Fahrenheit's per-group id ranges (call_atel.cs; group C is "Debug"):

  | Group | Name | Ids |
  |---|---|---|
  | 0 | Common | 0x000-0x267 |
  | 1 | Math | 0x00-0x1d |
  | 4 | SgEvent | 0x00-0x46 |
  | 5 | ChEvent | 0x00-0x90 |
  | 6 | Camera | 0x00-0x89 |
  | 7 | Battle | 0x000-0x127 |
  | 8 | MapFunc | 0x00-0x6b |
  | 9 | MnFunc | 0x000 |
  | B | MovieFunc | 0x00-0x0f |
  | C | Debug | 0x00-0x5d |
  | D | AbilityMap | 0x000 |

- `natives.rs` holds only the table addresses. **Names do not live in natives.rs.**
  - The CLI maps id → handler: the int-result slot, else float, else start, else poll.
  - It then looks the handler VA up in `symbols/ffx.sym`. The format is `<0xVA> <function|label> <Name> [# comment]`, one per line, sorted by address. Lines with an `fh ...` comment are Fahrenheit's names.
- Counts:
  - The shipped event, monster and battle scripts call 1059 distinct ids, and 1056 of them are named.
  - ffx.sym has 1584 `Native_*` functions. Of the 1586 handler addresses, 1370 are hooked by our Rust port.
  - The comments give push-order arguments.
- Export to JSON/CSV for C#. Recommended row:

  `id(hex u16), group, index, start_va, poll_va, retf_va, reti_va, name, comment`

  To produce it:
  1. Walk each group table over Fahrenheit's ranges, or until a slot falls outside `.text` (`0x00401000`-`0x00b0bc00`), the rule `natives.rs` uses.
  2. Name each row from ffx.sym by the chosen handler.
  - Alternatively, `ffx script natives` already prints `0x<id> <calls> <scripts> 0x<handler> <name> (slots: start poll float int)` for every called native. Parse that.
  - Fahrenheit's own `AtelFn_<Grp>_<idx>_<Init|Exec|RetI|RetF>` handle names cover 1624 slots and can serve as a fallback naming.

---------------------------------------------------------------------------------------------------

## 3. Execution

### 3.1 Functions

| Function | VA (off) | Convention / signature | ret | FhCall |
|---|---|---|---|---|
| `Script_Run` | `0x008641e0` (`0x4641e0`) | cdecl `u32 (Worker* w, u32 mode, u32 callbacks)` | plain `ret` (`0x0086524f`) | `Fahrenheit.FFX.FhCall.FUN_004641e0` (`d_UnknownFn`) |
| `AtelDecodeCommand` | `0x008641c0` (`0x4641c0`) | cdecl `(int index, u32 mode)` = `Script_Run(AtelGetBasicWork(index), mode, 1)` | | `FUN_004641c0` (`d_UnknownFn`) |
| `AtelFetchCommand` | `0x00869d60` (`0x469d60`) | cdecl `u32 (Worker*, Level*)`. **Only caller: `Script_Run` at `0x008643e9`**, once per instruction. | `ret` | `FUN_00469D60_00324450` (`d_UnknownFn`) |
| `AtelFetchCommandMain` | `0x00869da0` (`0x469da0`) | cdecl `u32 (u32 long, u8* pc)` | `ret` | `FUN_00469DA0_00324490` |
| `Atel_ExecCtrlWorks` | `0x008688f0` (`0x4688f0`) | cdecl `u32 (int from, int to)` | `ret` (`0x00868980`) | `FUN_004688F0_00323040` |
| `AtelExec` | `0x008666d0` (`0x4666d0`) | cdecl `()`: `Atel_ExecCtrlWorks(0, 6)`. 13 bytes; the call is at `+4`, so a detour must relocate it. | `ret` | `FUN_004666d0` |
| `AtelMenuExec` | `0x0086dd60` (`0x46dd60`) | cdecl `()`: `Atel_ExecCtrlWorks(6, 7)` | | `FUN_0046dd60` |
| `Atel_EventFrame` | `0x00872bd0` (`0x472bd0`) | cdecl `()`: one event frame; calls `AtelExec`. Caller: `TkUpdateEventJump` `0x0088d3d0`, every frame of `Sg_MainLoop`. | | `FUN_00472bd0` |
| `AtelExecCtrlEvent` | `0x00867740` (`0x467740`) | cdecl `(ctrl)`. The event's `+0x4c`: three `Atel_RunCtrlWorkers` passes `(1,0)`, `(0,0)`, `(0,1)`, or the init. | | `FUN_00467740_00321EB0` |
| `Atel_RunCtrlWorkers` | `0x00867950` (`0x467950`) | cdecl `(ctrl, input, clear)`. Each worker gets `Script_Run(w, u8 ctrl+8, input?1:0)`. Without input, only workers with `+0x36 & 0x180` run. Result bit 15 → `ctrl+8 = 3`. Repeats while the results have bits outside `0xc000` (speed > 1). | | `FUN_00467950` |
| `AtelChangeCurrentCtrl` / `AtelSetCurrentCtrl` | `0x00862d30` / `0x0086f730` | cdecl `old = Change(i)`, then `Set(old)` (as `MsAtelParseSysTag` does) | | `FUN_00462D30_0031D390` / `FUN_0046f730` |
| `Mutex_Lock` / `Mutex_Unlock` | `0x006f2ed0` / `0x006f2f50` | cdecl `(Mutex*)`: `WaitForSingleObject(INFINITE)` / `ReleaseMutex` | | `FUN_002F2ED0_00114A10` / `FUN_002f2f50` |

All VERIFIED: objdump and the dump, plus the ported Rust.

### 3.2 The loop (VERIFIED, objdump + `ffx_script::vm::bridge::run`)

1. **Prologue.**
   - The mutex is created if it is -1 (`0x008641f0`), then `Mutex_Lock(g_ScriptRunMutex)` runs (`0x008641fc`).
   - The skip tests: `+0x34` bit 1; battle bit 2/`+0x36 & 0x40`/`MsBattleCheck`; ctrl `+3` run mode. A skip unlocks and returns 0 (`0x0086523b`).
2. **Setup.**
   - Level = `Script_WorkerLevel(w, u8 w+0x32)`; ebx = worker, edi = level, esi = `w+0xc4` (stack).
   - The speed accumulator is updated. "cont" (`[ebp+8]`) = 1 when `speed*g_GameSpeed >= 1`.
   - Result (`[ebp-4]`) = 1 when the accumulator reached 1.
   - With `callbacks`: `ctrl+0x58(w, level)`.
3. **Wait states** (`0x008642f7`).
   - `+0x1f` = 1: look for a reply (`0x008635c0`); none → leave.
   - `+0x1f` = 2: leave while the request is queued.
4. **Run.** Set `+0x34` to `0x400`. Then loop (`0x00864380`):
   - Count instructions; at 65536 set `0x1400` and `g_AtelError`.
   - If cont == 0: when the stack is empty → end; in mode 1 → end.
   - **Fetch:** `call AtelFetchCommand` at `0x008643e9`.
   - Bounds check `cmp ecx, 0x7a` at `0x0086440b`.
   - **Dispatch** at `0x00864420`: `movzx eax, byte [0x0086536c + op]; jmp [0x00865250 + eax*4]` (`0x00864427`).
   - Handlers:
     - most end at `0x00864512`, the PC step: `PC = code + (PC - code) + len(byte)`, with `len` from the replaced byte when a breakpoint ran;
     - jumps and waits "stay" at `0x00864bf2`/`0x00864bf5`/`0x00864bf8`;
     - natives, `REQ*` waits and ends clear cont.
   - **Mode 1:** after each instruction the result is masked to `0xc000` (bit 14 set if cont) and cont = 0. So exactly one instruction runs per call.
5. **End** (`0x008651c2`).
   - With `callbacks`: `ctrl+0x5c(w, level)`.
   - `+0x34` bit 3 = yielded with an empty stack.
   - `Mutex_Unlock`. Return the result.

### 3.3 Opcode `40` (breakpoint, case at `0x0086442e`; VERIFIED in asm and the port)

1. Clear cont. `w+0x34 &= 0xe3ff`.
2. Scan the **current ctrl work's** (`g_ScriptContext`) table at `+0x64`, 4 entries. A match needs `id >= 0` and `entry+8 == level PC`.
3. **No match:** the PC stays and the instruction does nothing.
   - DANGER: with cont = 0 and a non-empty stack the loop fetches the same `0x40` again. **An unmatched `0x40` in the middle of an expression loops forever** and hangs the game thread.
   - Only a stray `0x40` does this, e.g. when the table entry was cleared without restoring the byte.
4. **Match with `+0xf == 0` and `mode != 1`: "stop here".**
   - `result |= 0x8000`; `entry+0xf = 1`; mode becomes 1; the PC stays.
   - `Script_Run` ends this worker's run right away, whatever the stack depth: the worker yields at the breakpoint.
   - `Atel_RunCtrlWorkers` sees bit 15 and sets **`ctrl+8 = 3`**, which freezes the whole ctrl work from the next frame. The other workers of the current pass still run this frame.
5. **Match with `+0xf == 1` (resuming), or in mode 1 (stepping):**
   - `+0xf = 0`;
   - `AtelFetchCommandMain(saved & 0x80, pc)` decodes the **replaced** instruction (saved byte + operand bytes at `pc+1`), sets cont = 1, then dispatches it (`jbe 0x00864420`);
   - the PC advances by the saved byte's length.
- **Who writes the table.**
  - `AtelRegistBreakPointer` `0x0086e860` (`0x46e860`; `FUN_0046E860_003292D0`, `d_UnknownFn`): cdecl `(int worker_index, int slot 0..3, int code_offset, u32 tag, int id)`, plain `ret`.
    - It clamps the slot, then works on `e = g_ScriptContext + 0x64 + slot*0x10`: `e.id = id`, `e.tag = tag`, `e.hit = 0`.
    - With `id >= 0`: `p = block + block[+0x30] + offset` (block of `AtelGetBasicWork(worker_index)`); `e.ptr = p`; `e.saved = *p`; `*p = 0x40`.
    - With `id < 0`: if the slot was set, the saved byte is restored.
  - **`0x00864150`** (`0x464150`; `FUN_00464150_0031E6A0`) **is AtelClearBreakPointer.** **ffx.sym's name `Msg_CancelChoice` and its comment are wrong.**
    - cdecl `(int worker_index, int slot)`: `e.id = -1`, `tag = 0`, `hit = 0`; if it was set, `*e.ptr = e.saved`.
  - Lookup helpers:
    - `0x00863230 (worker, offset) -> slot | -1` (`FUN_00463230_0031D770`);
    - `0x00863270 (worker, tag, id) -> slot` (`FUN_00463270_0031D7B0`);
    - `0x0086b6d0 () -> first free slot | -1` (`FUN_0046B6D0_00325DE0`).
  - The only caller of set and clear is the dead `AtelDebugCtrl`; release never sets a breakpoint.
  - All of these work on the **current** ctrl work and resolve the worker through `AtelGetBasicWork` (current ctrl).
- A breakpoint in a subroutine worker's code fires for every `JSR` caller.

### 3.4 Hook points and the recommended pause/step/breakpoint design

The game has a separate render thread (Fahrenheit imgui.cs comment). `render_imgui` runs there, and scripts run on the game thread:

```
Sg_MainLoop → TkUpdateEventJump → Atel_EventFrame → AtelExec → Atel_ExecCtrlWorks(0,6)
… → AtelExecMenuCtrl → AtelMenuExec → Atel_ExecCtrlWorks(6,7)
```

**Hook 1: `Atel_ExecCtrlWorks` `0x008688f0`, cdecl `(from, to)`.** This is the frame hook.
- Pre:
  1. Apply the queued UI commands: states, breakpoints.
  2. Set `in_pass = true`.
- Post:
  1. Set `in_pass = false`.
  2. Snapshot the ctrl works, workers, stacks, levels, signals and the code bytes around each PC into a double buffer for `render_imgui`.
- This is the only point where no `Script_Run` is in progress and the VM is consistent.
- `0x00871d70`, which it calls first, is hooked internally by Fahrenheit's runtime (`AtelExec_Internal_871d10`). Hook `0x008688f0` itself, not that one.

**Pause, continue, frame advance.** Write `u8 ctrl+8` (VERIFIED mechanism of the original):
- 3 = paused;
- 0 = continue;
- 2 = run one frame, then paused.

On pause, the ctrl work's whole `+0x4c` update is skipped: workers, signal decoding and the callback queue. Pause only ctrl works 0..5. Pausing 6 freezes menu scripts.

**Breakpoints.** Use the original mechanism. Either:
- call `AtelRegistBreakPointer`/`AtelClearBreakPointer` under `old = AtelChangeCurrentCtrl(c) … AtelSetCurrentCtrl(old)`; or
- write the 16-byte entry and patch the byte directly, exactly as in 3.3. Writing directly is simpler and leaves current-ctrl state and the `AtelGetBasicWork` cache alone.

Do it from the hook's pre-step. The rules:
- Offsets must be instruction starts; take them from the disassembly.
- At most 4 per ctrl work.
- **Never write `0x40` without a matching entry** (see the hang above).
- **Before the block goes away, restore the bytes and free the slots:**
  - on map/event change, `AtelRequireCtrlWork` `0x0086ee10` (`FUN_0046ee10`) resets the table to -1 without restoring;
  - on battle end, `AtelRemoveCtrlWork` `0x0086ed40` (`FUN_0046ED40_003297C0`).

  Hook `AtelRemoveCtrlWork`, or watch `ctrl+0x48` and worker `+4` for changes. Then drop the mod-side entries **without** writing the old byte into memory that may already have been freed.

**Hit detection.** In the post-hook, look for a ctrl with `+8 == 3` that the mod did not set, and an entry with `+0xf == 1`. The worker is the one whose current level's PC equals `entry+8`. "Continue" = write `+8 = 0`: the worker runs the replaced instruction and goes on.

**Step.** Two options:
- **Step every worker of a ctrl by one instruction:** `ctrl+8 = 1`. This works only where the `+0x4c` update passes `ctrl+8` as the mode: the event (`Atel_RunCtrlWorkers`).
  - Battle `MsAtelUpdate*` call `AtelDecodeCommand(k, 0)`, so for them 1 behaves like 2 (one frame).
  - `AtelExecCtrlEvent` makes three passes (the second and third only for workers with `+0x36 & 0x180`), so a worker can step up to 3 times in one frame.
- **Step one worker (recommended): Hook 2 on `Script_Run` `0x008641e0`.**
  - While `in_pass` is true and a step of worker W in ctrl C is armed:
    - set `C+8 = 1` (or 0 for battle ctrl works);
    - for every other worker of C, return 0 without calling the original. That is the same result as the game's own "not runnable" skip path.
    - for W, call the original with `mode = 1`, only once per step command.
  - When `in_pass` is false, always call the original. **DANGEROUS** otherwise: `MsAtelParseSysTag` `0x007979d0` runs a battle worker in an unbounded `do { AtelDecodeCommand; AtelDecodeSignal } while (AtelGetRequestLevel > 6)` loop, so skipping `Script_Run` there hangs the game. `AtelExecCtrlEventInit` `0x008677a0` and `MsAtelRequestExe` `0x00797d50` also call it outside the frame pass.
  - Mode 1 runs one instruction. When the level is in a wait state (`+0x1f != 0`), it runs nothing: show "waiting".
  - A multi-frame native is started and polled once per step.
  - A breakpoint at the PC is stepped over: its replaced instruction runs.
- Single-instruction tracing or conditional breakpoints: hook `AtelFetchCommand` `0x00869d60`. It is called once per executed instruction, with `(worker, level)`; PC = `level+0x18`. It may observe, but must not return a fabricated `0x40`.

**Not recommended:**
- ctrl `+3` run mode 1. `Script_Run` returns 0 for every worker of the current ctrl, including inside the `MsAtelParseSysTag` loop: hang risk.
- Skipping `Script_Run` globally.

### 3.5 Threads and the mutex (VERIFIED)

`g_ScriptRunMutex` (`0x00c52740`):
- It is held only for the duration of each `Script_Run`.
- `Mutex_Lock` has one caller: `Script_Run`.
- It is a Win32 mutex, which is recursive on the same thread, so nested `Script_Run` calls from natives do not deadlock.
- It is not held during signal decoding, the callbacks or the update callbacks.

Taking it from the render thread would only keep the reader out of the middle of an instruction, and it blocks the game thread while held. **Do not read VM memory from `render_imgui`.** Snapshot it in Hook 1's post-step and render the snapshot.

---------------------------------------------------------------------------------------------------

## 4. Original on-screen debugger

### Verdict
- The pad input handler, the draw root and every page are present in FFX.exe. All of it is dead code. Nothing calls it, and the init it needs is gated by `TkIsDebug`, which is a stub that returns 0. VERIFIED.
- **It cannot draw on PC.** Every page writes PS2 GIF sprite quadwords into the debug-font buffer `g_TkDebugFontBuffer` (`0x0133c8cc`).
  - That pointer is in .bss and starts as 0. Nothing that runs in release sets it.
  - The only functions that set it up, swap it or turn it into a DMA chain are unreferenced.
  - No PC code reads the buffer.
  - VERIFIED by static analysis and a full rel32/absolute-pointer scan of .text.
- **DANGEROUS:** calling `AtelDebugDraw` as it is dereferences `*(0)` in `TkGetDebugFontBuffer` → access violation. INFERRED.

### Functions

| VA (off) | What | Convention / args / ret | Callers | FhCall |
|---|---|---|---|---|
| `0x00872c80` (`0x472c80`) `AtelDebugDraw_thunk` | `jmp 0x0087d6c0` | - | none (rel32 and absolute scans) | none |
| `0x0087d6c0` (`0x47d6c0`) `AtelDebugDraw` | per-frame draw root | cdecl `void(void)`, `ret` | only the thunk | none |
| `0x0087bd90` (`0x47bd90`) **`AtelDebugCtrl`** (ffx.sym puts it at `0x0087bdcd`; that is a Ghidra split, the real entry is `0x0087bd90`) | pad input handler. At entry it clears `0x01328aac`; it returns early (after `TkSetDebugDrawFlag(0)`) when `TkGetDebugMenuStatus() == 0`. | cdecl `void(void)` | none | `FUN_0047BD90_00339040` (`d_UnknownFn`) |
| `0x00883070` (`0x483070`) `AtelDebugPages` | page switch (jump table `0x008832b0`, byte map `0x0088330c`) | cdecl `void(void)` | `0x0087d7f5` only | none |
| `0x00883040` (`0x483040`) `AtelDebugDisplay_00883040` | **misnamed in ffx.sym**: a helper that draws one marker triangle, `FUN_00890500(0, 0, x-4, y+400, x+4, y+400, x, y+0x196)` | cdecl `(x, y)` | none | `FUN_00483040_003401A0` |
| `0x0087f390` (`0x47f390`) | left info panel (table `0x00c545cc`) | cdecl `void(void)` | `AtelDebugDraw` | none |
| `0x0087d820` (`0x47d820`) | 3D overlay of trigger lines and boxes (toggle `0x01328a79`) | cdecl `(mode)` | none | `FUN_0047D820_0033AB60` |
| `0x0087f0d0` (`0x47f0d0`) `AtelInitDebugger` | state reset | cdecl `()` → `TkSetStartMapIndex(0x17, 0)`'s eax | only `AtelInitPS2Debug` (tail jmp) | `FUN_0047F0D0_0033C430` |
| `0x0087f1c0` (`0x47f1c0`) `AtelClearDebugger` | clears 5 windows of `0xaa4` bytes from `0x01328af0` | cdecl `() -> 0xa80` | `AtelInitDebugger` | `FUN_0047F1C0_0033C520` |
| `0x008798f0` (`0x4798f0`) `AtelInitPS2Debug` | `user_malloc`: 4 MB into `0x01328598[0]` and `[6]`, plus source buffers; then `jmp AtelInitDebugger` | cdecl `()` | `AtelInitTotalDebug_00873ba0` only, behind `TkIsDebug` | `Fahrenheit.FFX.FhCall.FUN_004798f0` |
| `0x00879e70` (`0x479e70`) `AtelRemovePS2Debug` | frees the above | cdecl `()` | behind `TkIsDebug` | `FUN_00479E70_00337400` |
| `0x00887d10` (`0x487d10`) `TkIsDebug` | `xor eax,eax; ret` (**stubbed**) | cdecl | AtelInitTotal, AtelEventSetUp, … | none |
| `0x00885110` (`0x485110`) `AtelSetDebugDraw` | `u8 [0x01328a78] = arg` | cdecl `(u8)` | none | `FUN_00485110` |

### Globals (block `0x01328a6c`-`0x0132c030`, in .bss; read and written only by dead code; VERIFIED by xrefs)

| VA | Meaning |
|---|---|
| `0x01328a6c` | input sub-state |
| `0x01328a70` | selected ctrl work, 0..6 |
| `0x01328a74` | font colour index |
| `0x01328a78` | **master toggle**, u8. Readers: `AtelDebugDraw` `0x0087d6c7`, `0x0087d820`, `AtelDebugCtrl`. Writers: `AtelDebugCtrl` (pad 1 Start), `AtelInitDebugger`, `AtelSetDebugDraw`. |
| `0x01328a79` | 3D overlay on |
| `0x01328a7a` | page panel on (init 1) |
| `0x01328a7c` / `0x01328a80` | panel x/y |
| `0x01328a84`, `0x01328aa0` | blink counters |
| `0x01328a88` | info sub-page 0..3 |
| `0x01328a8c`-`0x01328a98` | last analog values |
| `0x01328a9c` | show hidden workers |
| `0x01328aa4` | count of the module list at `0x00c53638` |
| `0x01328aac` | 3D overlay mode |
| `0x01328ab0` | 4x4 matrix |
| `0x01328af0 + ctrl*0xaa4` | per-ctrl window (layout below) |

Window layout:
- `+0x00`: group, 0..4: Actor, Code, Var., Info, Sys (`0x00c53414`; `{s16 first, s16 last, char* name}`).
- `+0x0c`: selected worker.
- `+0x10 + 4g`: sub-page within group g.
- `+0x24 + page*0x40`: per-page cursor and scroll state, 42 pages.

Only 5 windows are cleared, but ctrl runs 0..6 (overrun, INFERRED).

### Pages

Names come from `0x00c53448`; drawers come from the jump table. VERIFIED.

| Page | Shows | Drawer |
|---|---|---|
| 0 | variables / watch | `0x00884cc0` |
| 1 | signals | `0x00883e80` |
| 2 | **stack** (ffx.sym's name "Variables" is wrong) | `0x008845a0` |
| 3 | int registers | `0x00882ac0` |
| 4 | float registers | `0x00882990` |
| 5-12 | source files 1-8 | `0x00884290` |
| 13 | bytecode listing | `0x00882bb0` → `0x00882d50` |
| 14-18 | Public 8/16/32/float/1bit hex | `0x008850c0` |
| 19-23 | Local | `0x00884fe0` |
| 24-28 | My | `0x00885040` |
| 29-39 | Game/ Party, GuideMap, F.Ship, Snd3d, Sndfoot, EvSep, Shape, Albhed, Effect, Helpbit, Info | `0x008807b0`, `0x00881be0`, `0x0087fb10`, `0x00881fe0`, `0x008822c0`, `0x0087f810`, `0x00883960`, `0x0087f4a0`, `0x0087f660`, `0x00881e10`, `0x0087fca0` |
| 40-41 | System/ Version, Info | `0x00882880`, `0x008827b0` |

Page 13's line format:
- a breakpoint marker ①..④ (SJIS `0x8740` + slot);
- `>` at the PC;
- `"%05x"` offset and `":"`;
- the mnemonic from `0x00c54600` (`0x00885640` looks it up; its ffx.sym name `GetClearMaskDescription` is a misname);
- the operand from `AtelGetDebugSubCodeString` `0x008856b0`, by kind as in 2.2.

Symbolic names need the `.bdb` (worker `+8`), which is absent.

Info panel table `0x00c545cc`:
- indexed `[worker type 0..7][sub-page 0..3]`;
- each entry is a null-terminated list of `{char* label, cdecl printer(worker), i16 x, i16 y}`, e.g. "PC:" → `AtelDebug_PrintPc`, "SP:" → worker `+0xc4`.

Variable storage tags: GBL, LCL, FIX, MY, DAT, ITR, LC2 for storage 0..6. A mod can reuse all of these tables as data.

### Input (`AtelDebugCtrl`, dead; VERIFIED args, INFERRED button names)

- It reads **pad 1** (the second controller), plus pad 0's held/trig: `TkGetControlerHist`/`ReptHist`/`TrigHist`/`AnalogHist` with pad = 1.
- Bindings:
  - Start (`0x800`) alone: toggle;
  - `0x200`: next ctrl work;
  - `4`/`1`: group;
  - `8`/`2`: sub-page;
  - `0x80` held + left/right: worker;
  - up/down repeat: scroll;
  - `0x80` + `5`: toggle a breakpoint at the cursor (pages 5-13).
- It has no step or continue key.
- **DANGEROUS side combos in the same function:**
  - debug event jumps (`TkSetEventJump`) with music/movie stops;
  - `AtelPopPublicWork` (`0x0086de40` → `0x0087e6f0`), which **writes 0x1000 bytes back into the save data at +0x1ec**;
  - moving the worker's position;
  - `pppFpDispDebug`, `SndDumpPlayFlag`.
- **Do not call it.**

### Drawing path (VERIFIED: does not reach the PC renderer)

- `TkPutDebugFont*` (`0x0088f140`/`0x0088f160`/`0x0088f4c0`/…) → `TkPutDebugFontStringCore_0088f240`, cdecl `(prop, colour, scale, s)`. It writes into `*g_TkDebugFontBuffer`.
- All callers of the font functions are in the debugger range `0x0087a000`-`0x00885000`.
- The buffer is never initialised: the init/swap function `0x0088eed0` (which sets `0x0133c8c4`/`0x0133c8c8` and jumps to `TkDebugFont_SwapBuffer` `0x00890780`) has no references.
- The buffer is never consumed: `TkRegistDebugFontDmaTag` `0x0088f760` (the GIF/DMA chain) has no references.
- The other primitives write into the same buffer:
  - rect/frame/scissor: `0x008903d0`, `0x0088ff70`, `0x0088fd50`;
  - line: `0x0088fb30`;
  - triangle: `0x00890500`;
  - `AtelDebugDrawRect`.
- `Send_DebugFont` `0x008510b0` only loads a TIM.

### Gates

- **`g_FFXIsDebug` (`0x0133c910`) does not gate this debugger.** It is set to 1 for argv[1] "debug"/"DEBUG" (`0x00822490`, from `asyncFFXInitialize`).
  - Its readers are many: `pppFpLoop`, `MsBattleDraw`/`Exe`/`Init`/`Process`, `MsDebugInfoRead`, `MsEnvInit`, `MsBtlReadSetScene`, `MsSetFileName`/`MsGetPlySaveName` (host0: paths), `AtelSetEventJump`/`2`, `yiRectToOMD*`, `Yn_FieldProc`, `rcbgInit`/`Fini`, `rcefMainInit`, `Sg_*` (`MainDebug`/`MainLoop`/`ControlCamera`, …), `Ch_*`, `Light_Set`, `TextureAnimation_Animate`, `isDebugMode`, and others.
  - Setting it globally changes all of these. **Do not set it for this feature.**
- **`g_TkDebugDrawFlag` (`0x01330872`) is write-only in release.**
  - Writers: `TkInitWork`; `TkSetDebugDrawFlag` `0x0088ea20`, called only from dead code.
  - Its only reader, `TkGetDebugDrawFlag` `0x0088d5a0`, has no callers.
  - Setting it does nothing. VERIFIED.
- The real gates on the draw root are the toggle `0x01328a78` and `TkGetDebugMenuStatus()` `0x0088d5b0` (u8 `0x01330870`; `TkInitWork` sets it to 1).

### Recommendation

Build the ImGui debugger on the original's data and the breakpoint mechanism (sections 1-3), and reuse these tables:
- mnemonics `0x00c54600`;
- info panel `0x00c545cc`;
- page names `0x00c53448`;
- storage tags.

Optional "hybrid" revival (risky, INFERRED to work):
1. Call `AtelInitDebugger` once.
   - Side effect: it installs Push/Pop-Public callbacks at `0x01327070`/`0x01327074`. Every `AtelSetEventJump` then memcpys save+0x1ec to `0x0132d570` and prints "TK:Push Public!!".
   - Clear those two pointers again afterwards.
2. Hook these to point at a mod text sink, so nothing touches the null buffer:
   - `TkPutDebugFontStringCore_0088f240`: record the text, the position from `(0x0133c8d4>>4, 0x0133c8d8>>4)` and the colour, then return `s`;
   - `TkGetDebugFontBuffer` `0x0088edc0`: return a scratch buffer;
   - the rect/line/scissor/triangle primitives: no-ops.
3. Set `0x01328a78 = 1`. Drive the state globals yourself and never call `AtelDebugCtrl`.
4. Call `AtelDebugDraw` once per frame on the game thread, in Hook 1's post-step.
5. Render the recorded SJIS strings in `render_imgui` (512x416 PS2 coordinates).

Pages 5-13 and all names stay empty, because there is no `.bdb`.

---------------------------------------------------------------------------------------------------

## 5. `0x0067a9b0`: the SyncManager, not a debugger (VERIFIED)

- **`0x0067a9b0` (`0x27a9b0`).** cdecl `(u16 scenario, a, b, c)` wrapper, `ret`: `mov ecx, [0x00ce9014]` (`g_SyncManager`), then `call 0x006fb380`. FhCall: `Fahrenheit.FFX.FhCall.FUN_0027a9b0` (`d_UnknownFn`).
- **`0x006fb380` (`0x2fb380`).** A thiscall SyncManager method; FhCall `FUN_002FB380_00121A10` (`d_UnknownFn`). What it does:
  1. When the sync data is active (`this+0x1c`), it **spins in 5 ms `SleepThreadInMillisecond` waits until the async load is done (`+0x20`)**.
  2. It looks the 4-tuple up in its parallel arrays (`+0x28`/`+0x30`/`+0x34`/`+0x38`, count `+0x10`).
  3. On a match it sets the cursor `+8 = (+4)[i]`.
  4. It returns 0.
- The SyncManager family (`SyncManager_syncInit` reads "SyncList.txt", `syncGetData`, `syncDataChangeAsync`) is the voice/lip-sync data.
- **Call site.** `Script_OpReq` `0x00867230` is the only caller (`0x008672fc`). It pushes `(g_SaveScenario u16 0x0112d67c, entry, worker, level)`, in our port's argument order. The callee selects the sync record for the event request that is about to start.
- This is not debugger code. Our Rust port keeps it original as `REQ_HOOK`; signal.rs's doc calls it "a debug hook on a sound object", and the "debug" part is a misnomer.
- The only relevance to a debugger: a `REQ` can block the game thread briefly while a sync file loads.

---------------------------------------------------------------------------------------------------

## 6. Source-debug machinery (low priority)

- **`AtelLoadDebugData`** `0x008799b0` (`0x4799b0`; cdecl `(slot, event) -> 1/0`; `FUN_004799B0_00336F00`).
  - It reads `AtelEventGetFilePath2(1, "debug", 0, AtelGetEventName(event), ".bdb")` (strings `0x00b59148`, `0x00b5c588`), at most `0x3fffff` bytes, into `0x01328598[slot]`.
  - Its only caller is `AtelEventSetUp` (`0x0087331c`), behind `TkIsDebug()`. `TkIsDebug` is a stub, so the load never runs.
  - **No `.bdb` and no `debug/` exist in FFX_Data.vbf** (71,979 entries). VERIFIED.
- **`0x01328598` u32[7]**: the loaded `.bdb` image per slot. ffx.sym's `g_AtelEventHeaders` and "the 7 debug buffers" are the same array. `AtelInitPS2Debug` allocates 4 MB for slots 0 and 6 only. Related:
  - `0x01328698[slot*8+i]`: `.src` text buffers (`debug/<dir>/<name>.src`);
  - `0x013285b8`: page-record work buffers;
  - `0x01328778`: sizes.
- **`.bdb` layout** (INFERRED from the accessors through worker `+8`):

  | Off | Contents |
  |---|---|
  | `+0x00..+0x3c` | per native group, string offsets (native names) |
  | `+0x40` | source file table |
  | `+0x44` | variable names |
  | `+0x48` | tag names |
  | `+0x50` | worker names |
  | `+0x54` | per-worker 16-byte records |
  | `+0x58` | per-code-byte line map, 4 B each: u16 line, u16 (bits 0-13 file/column, `0x4000` = 3-byte instruction, `0x8000` = statement start) |
  | `+0x5c` | count |

- **`AtelSetUpSourceDebug`** `0x00885b00` (`0x485b00`; cdecl `(char* text, len, on, rec* out, bytes)`, `ret`; `FUN_00485B00_00342CF0`).
  - It returns at once when `text` or `on` is 0.
  - Otherwise it splits the text into **16-byte page records `{rec* next, i32 lines (<= 64), char* first_line, 0}`**: a new record after every 64 `\n`, at most `bytes/16` records. The scan stops at `len`, NUL or `0x1a`; the last record's `next` = 0.
  - Callers: `AtelActorSetUp2`, `AtelCommonSetUp`.
  - `AtelInitSourceCodeDebugWork` `0x0086d670` stores up to 8 chains at worker `+0x0c + 4k`. Pages 5-12 list them.
- **Feeding your own text:**
  - Possible in principle: run `AtelSetUpSourceDebug` on mod-owned text and records, set worker `+0x0c..+0x28`, and supply a fake `.bdb` at worker `+8` for the line map and names.
  - Only worth it with the text-sink revival of 4. An ImGui debugger can keep its own listing and maps.

---------------------------------------------------------------------------------------------------

## Errata found while researching (for ffx.sym/docs; not fixed here)

- `0x00864150` "Msg_CancelChoice" is **AtelClearBreakPointer** `(worker, slot)`.
- `AtelDebugPage_Variables_008845a0` is the stack page.
- `AtelDebugDisplay_00883040` is a triangle-marker helper.
- `AtelDebugCtrl` starts at `0x0087bd90`, not `0x0087bdcd`.
- `0x00885640` "GetClearMaskDescription" is the debugger's mnemonic lookup.
- opcode.rs's `op_40` doc "wait" should say breakpoint (HALT).
- docs/formats/script.md's ctrl `+0x48` "a map record" should say the script block.
- Fahrenheit:
  - `Globals.Atel.current_worker` uses `ptr_at` instead of a dereference;
  - `AtelStack.MAX_SIZE` is 20, should be 19;
  - `AtelScriptVar.location` uses `& 0x0F`, should be `(word >> 25) & 7`;
  - `AtelScriptChunk.script_header_offsets` is `ushort*`, should be `u32`;
  - `AtelSignal` `Size` is 0x16, the stride is 0x18;
  - `AtelBasicWorker.threads` is `[18]`, should be 9.
