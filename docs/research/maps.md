# Map jumps: what crashes, and entrances

Static analysis of FFX.exe (sha256 `0537b2a1…686d`) and the shipped data; the game was not run.
VERIFIED = read in the code (decompilation, disassembly, or ffx-decomp's parity-tested port of
the function). INFERRED = concluded, not read.

## Result

| Code | Ids | Effect |
|---|---|---|
| `no_name` | 47: 3-15, 24-40, 66, 101, 111, 114, 246, 251, 278, 281, 328, 343-346, 378, 379, 400, 401 | crash (NULL read) |
| `no_file` | 18: 186, 216, 228, 229, 231, 232, 233, 262, 300, 304, 342, 350, 353, 357, 358, 360, 369, 373 | hang (endless loop) |
| `soft_reset` | 1: 399 | no jump; the game resets |
| (none) | the other 336 | no crash found in the load path |

No other load-time crash showed up. The map geometry, collision, textures, models, motion groups,
messages, .ftc and debug files are all either checked by the game, or present for every map that
has a script.

**Correction.** The mod's first check (`script_name_present`) said no for 393-398 (and 399), but those
are the Eternal Calm scenes. Their cdrom.fnd entries are empty, and the game substitutes built-in names
for them, so they load. Today the mod greys out 6 maps that work. With 399 that makes 54 = 47 + 7.
The 26 `NO_SCRIPT` ids are exactly the 18 `no_file` ids plus 8 that are also `no_name`
(40, 101, 111, 246, 251, 379, 400, 401).

## The load path

`AtelSetEventJump2(map, e, 0)` 0x0086ff40 (VERIFIED): `TkSetEventJump` 0x0088ea60 stores the map
as u16 at save +0, the entrance as **u8** at save +0xc, and sets `g_TkEventJumpRequest`. When the
fade has run out, `TkUpdateEventJump` 0x0088d3d0 calls `TkInitScene(map)` 0x0088e070, which calls
`AtelEventSetUp(map)` 0x00872ef0. Note: `AtelSetEventJump2` reads `*g_ScriptContext` before its
debug-flag test, so a NULL context crashes even with the flag on (VERIFIED decomp).

### 1. `no_name`: empty slot 0 of the event module (known cause, confirmed)

- `TkCdReadWithMallocWithMessage(0xc, map*0x12)` 0x0088d2c0. The size comes from
  `CDfileSize_PC` 0x00a428a0, which is 0 for a NULL name (`PcFileName_00a42c00` returns 0 for an
  empty entry). `user_malloc(0)` still returns a 16-byte block (`UserHeap_Alloc` 0x00942b60).
  Then `Cd_QueueRead` 0x0076b970 runs: `CDfileName_PC` returns 0, and there is no NULL check.
- The crash: **`mov al,[ecx+edi]` at 0x0076bcf0**, the name copy into `g_CdChannelNames`
  0x022fb6c0, with edi = 0. That is an access violation reading 0x00000000. If the c101 swap
  key matches (u16 0x0112ca90 == 0x00ccc7c8), it faults earlier, in the `strlen` at 0x0076bc10.
  VERIFIED (asm).
- Exception: when the name is NULL, `Cd_QueueRead` and `TkCdReadWithMallocWithMessage` fall back
  to the Eternal Calm name and size, if `isEternalCalmScene(file/18)` 0x0067ab50. That means
  maps 393-399: `g_EternalCalmFirstMap` 0x00c33468 = 393, read-only. The names come from
  `g_EternalCalmSceneNames` 0x00c334b8, and `AtelEventSetUp` uses `getEternalCalmSceneName`
  0x0067aac0 for the event name. VERIFIED.
- Runtime check: the mod's existing table check, plus `map - *(int*)0x00c33468` in 0..6 counted
  as present.

### 2. `no_file`: named, but the .ebp is not shipped

- `CDfileSize_PC` uses `sceOpen` (module 0xc is not in its CD_EX list). The open fails, it prints
  "MISSING?" and returns 0. The read task 0x00a43740 then opens the file, which fails, and reads 0
  bytes into the buffer, whose first word is 0. `AtelEventSetUp` checks for `"EV"` and otherwise
  runs `jmp $` at 0x008734f8, which loops forever. VERIFIED (code). The game thread hangs; it
  doesn't crash. That `sceRead` on a failed handle is harmless is INFERRED.
- Runtime check (reliable, the game's own path): take the name from cdrom.fnd as the mod already
  does (start offset into the table). Then call `sceOpen(name, 1)` 0x0062fa30 (cdecl) and, when
  it returns non-zero, `sceClose(fd)` 0x0062f610. `sceOpen` maps `host0:/…` to `/ffx_ps2/…` and
  checks `fiosIsFileExist`, the same test `CDfileSize_PC` makes (VERIFIED symbol/port). This can
  replace the `NO_SCRIPT` list. Do it once per id and cache it, on the game thread.
  `PcFileName_00a42c00` also works, but it writes a shared static buffer (0x01a85c30) and adds
  `dbg_`/`200thunder_`/`full_` prefixes for three maps under runtime conditions, so read the
  table directly.

### 3. `soft_reset`: map 399

`TkSetEventJump`: when `map == *(int*)0x00c33480` (= 399, read-only), it only sets
`g_AtelResetRequest` 0x01327108 = 1, and `AtelExecSoftWareReset` then resets the game (VERIFIED
port, constant read from the exe). `AtelSetEventJump2` still takes control and fades. It's no
crash, but the jump doesn't go to 399. Runtime check: compare with `*(int*)0x00c33480`.

### 4. Checked and safe (no code)

| Step | Function | Why it can't crash here |
|---|---|---|
| Event name cut | `AtelEventSetUp` (strstr `"/event/obj/"` + 0xb, strrchr `/`) | NULL results would crash, but every slot-0 name and every Eternal Calm name has both (generator check `bad_path`: none). VERIFIED code, data |
| "EV" magic | `AtelEventSetUp` | every shipped .ebp starts with "EV" (generator `not_ev`: none) |
| Messages, .ftc | `TKMallocAndReadEventMessage` 0x0088cac0, `TKMallocAndReadEventFTC` 0x0088c860 | `fiosIsFileExist` fails → 0; the event uses its own table. VERIFIED port |
| Map geometry | `AtelMapLoad` 0x0086dca0 → `yiFieldMapRead` 0x009085d0 → `yiMapReadMaster` 0x009093b0(0x13, 2i, 0x13, 2i+1) | i = `AtelGetMapIndex(hdr, 0)` 0x0086bd90; i ≤ 0 skips the load. Size of .vpa (slot 2i+1) 0 → returns -1 (no map). **Unchecked**: the slot 2i name goes to `graphicFieldMapLoad` 0x00640210, whose `strstr(name,"hiku21")` would crash on NULL (code `map_name`). For every script, in every PC language, i is in range (< 693), the .vpa is shipped, slot 2i is named, and the PC level exists: none hit |
| PC level | `PhyreScene_fieldMapLoadAsync` 0x0065ce90 | `fiosIsFileExist` fails → 0x13, no load. VERIFIED symbol |
| Motion groups (slots 3-17) | `Ch_RomReadEventMGRP` 0x0082a6e0 | size 0 → returns 0 before the read. VERIFIED |
| Models | `Ch_DataRead` 0x00829ef0, `Ch_MgrpRead` 0x0082a4c0 | `Chr_TableIndex` < 0 or size 0 → no read. VERIFIED decomp |
| Debug flag on | `AtelEventSetUp` → `AtelLoadDebugData` 0x008799b0 / `AtelLoadSourceData` 0x00879a70 | `AtelGetEventName` 0x00879760 reads the shipped eventid.bin (id < 402); missing debug/.src files → fd 0 → skipped. VERIFIED port |
| Debug natives in scripts | disassembly of all 397 scripts | none calls the hang native 0x01ed, 0x401e, 0x4038 or the host-file natives; only 0x00f8/0xc028/0xc02a in test01/02/testbattle, all harmless |

Slots 1 and 2 of each map's 0x12 are empty for every map, and the game never reads them on the
jump path (INFERRED from the callers above). The voice and sync changes at the end of
`TkInitScene` (`iVoiceDataChange`, `iSyncDataChange`) were not traced in depth (INFERRED safe:
many maps have no voice data).

## Entrances

- `entrance` is stored as a byte at save +0xc (`TkSetEventJump`). `TkGetNowEventJumpIndex`
  0x0088d670 returns it. VERIFIED.
- `Event_GetEntrance` 0x0086bfe0 / `Event_GetEntranceAt` 0x0086c020 (negative i → current),
  `AtelRequestMapCamera` 0x0086ed80: the script block is `ebp + u32(ebp+4)`. Its entrance table
  is block-relative, from `+4` to `+8`, 0x20 bytes per record (+6 camera, +8 yaw, +0xc x,
  +0x10 y, +0x14 z; +0 is the map index `AtelGetMapIndex` uses for entrance 0). The record is at
  `first + 32*idx`; **when that is ≥ end (signed), entrance 0 is used**. With no table
  (`+4` = 0), they return the static record 0x01325b50. VERIFIED (port).
- So an entrance past the end is safe: the player spawns at entrance 0. Script logic that
  branches on `GetNowEventJumpIndex` (native 0x0010) may still skip its spawn set-up for an
  unknown index (INFERRED, not crash-checked).
- Counts in the TSV: 1-9 entrances for each map with a script. -1 (no table) for 323 and 397,
  which also load no map (`AtelGetMapIndex` -1); the player lands on the default record.
  -1 is also given for every crash id.
- One map index differs by language: 382 (`AtelGetMapIndex` reads the per-language table at
  sound table +0x3c → +0xc). Both indices are fine.

## Generator

`tools/gen-map-table.cs` (a .NET 10 C# script; uses the ffx CLI). It reads cdrom.fid/.fnd and eventid.bin
from the archive, the three constants from FFX.exe, and each .ebp. It writes
`map_id<TAB>entrances<TAB>crash_reason` for every event-list id and every id with an event slot
(402).

```bash
FFX_GAME_DIR=<game dir> FFX_CLI=<ffx-decomp's ffx binary> \
  dotnet run tools/gen-map-table.cs        # rewrites src/Features/maps.tsv, ~13 s
# --detail adds the map index per language (numbers only; for checking, not shipping)
```

Codes: `no_name`, `no_file`, `bad_path`, `not_ev`, `map_name`, `map_range` (a `@langs` suffix when
only some languages hit it), and `soft_reset`. The output holds only ids, counts and codes.

## Not verified

- The fault address was read from the asm, not seen in a run. Whether `no_file` hangs or the
  read layer fails earlier was not run either.
- Script-level behaviour after the load (scripts that expect story state, party members or a
  given entrance) was not analysed beyond the debug-native scan. Such a map can still misbehave
  or soft-lock after a successful load.
- `iVoiceDataChange` / `iSyncDataChange` / sound-bank loads by map were not traced.

## Effects

A jump while an event effect plays can crash the game in a particle draw job
(dump `31102026_003039.dmp`, map 297 reached from the title screen's attract
demo, `loopdemo`, which plays effect 662). VERIFIED in code unless noted:

- A map change force-ends a running effect: `TkInitScene` -> `AtelEventRemove`
  -> `MsEventEffectFree` (it logs "EFFECT SYNC ERROR") -> `MagicFile_Stop`,
  which marks the effect's `magic_NNNN.dll` for unloading.
- In the same frame, `Sg_MainLoop` queues the effect's particles for drawing,
  then `MagicFile_Update` -> `MagicFile_Unload` -> `PhyreFIOS_UnloadPrx` frees
  the DLL, and only then does `updateFFX` hand the queued particles to the job
  threads (`JobSchedule_pppPart_fillBuffer_Kick`). Nothing waits in between.
  In the dump four job threads fault in `fillBuffer_drawMdl` reading
  `[part+0x18]` inside the freed DLL.
- The game's scripts wait for their effect to end (native 0x00de) before they
  leave a map. The debug flag's jump bypass skips that.
- The third argument of `AtelSetEventJump2` only selects the fade.

The mod refuses its map jumps, developer-save loads and battle starts while any
of these holds: the event-effect state (i8 at 0x01133364) is not 0, the loaded
magic handle (i32 at 0x00c64cac) is not -1, or the pending-unload handle (i32 at
0x00c64cb0) is not -1. Not verified: that the forced end leaves the particles
queued for the rest of the frame (`op_ovl_effect_free` was not traced).

