# Facts: battle debug, cheats, scene picker, save/load debug (FFX.exe, Steam HD)

Research sheet for the "Debug Revival" Fahrenheit mod (yuta.debugrevival).
Sources: `symbols/ffx.sym`, `docs/formats/battle.md`, `docs/formats/save.md`,
`docs/harness.md`, a Ghidra dump of FFX.exe (ffx-decomp's tools/ghidra/dump.sh), objdump of FFX.exe,
a byte scan of FFX.exe for absolute pointers and rel32 call/jmp to each
function, and the Fahrenheit alpha12 source (facts only).

Conventions: VA = absolute address (image base 0x00400000); "off" = VA -
0x400000 (what `FhCallLocation("FFX.exe", off)` takes). VERIFIED = I read the
code (decompile and/or disassembly); INFERRED = deduced, not checked line by
line. "No callers" means: no rel32 call/jmp and no absolute pointer anywhere in
FFX.exe (byte scan), not only "none in Ghidra's calls.tsv".

Fahrenheit's `Btl` struct is at off `0xD2A8D0` = VA `0x0112a8d0`
(`FFX.Globals.btl`, `core/ffx/gstate.cs`); its `BtlDebugFlags debug` is
`Btl+0x28` = VA `0x0112a8f8` (`core/ffx/battle/btl.cs`). Their field names are
given in the table where they exist.

FhCall in general (VERIFIED): almost every function below only has an
auto-generated handle `FhCall.FUN_00XXXXXX` (named by the *offset*, e.g.
`FUN_003849d0` for VA 0x007849d0; some are "fused" FFX/FFX-2 entries named
`FUN_<ffxoff>_<ffx2off>`, e.g. `FUN_00384C20_0020BE80`), typed
`Fahrenheit.FhCall.d_UnknownFn` = `[UnmanagedFunctionPointer(Cdecl)] void ()`.
That delegate is correct for the zero-argument cdecl functions; for the ones
with arguments the mod must declare its own delegate and
`new FhCallHandle<T>(new FhCallLocation("FFX.exe", off))`. Typed handles that
exist: `FFX.FhCall.MsBattleLabelExe` (`d_MsBattleLabelExe(uint encounter_id,
byte arg2, byte screen_transition)` returns int), `FFX.FhCall.MsBattleExe`
(`d_MsBattleExe(uint arg1, int field_idx, int group_idx, int formation_idx)`),
`FFX.FhCall.MsGetChr` (`Chr* d_MsGetChr(int)`), `FFX.FhCall.AtelSetEventJump2`
(`d_AtelSetEventJump2(int room, int entrance, int do_fade)`), internal
`FhCall.SaveDataManager_debugSave_Internal_6f0650` (see section 5) and internal
`FhCall.Sg_MainLoop`.

---------------------------------------------------------------------------

## 0. Threading and the per-frame hook (read first)

- VERIFIED (docs/harness.md "Frames and threads", Fahrenheit
  `runtime/imgui.cs`): `FhModule.render_imgui()` is called from Fahrenheit's
  `IDXGISwapChain::Present` hook. FFX presents on **PhyreEngine's render
  thread** (`Renderer_PresentFlip` 0x00593760, run by
  `Phyre_PRendering_PRenderThread_processRenderCommands`), while the **main
  thread already runs the next update**. So ImGui code runs concurrently with
  game logic.
- Rule for the mod: from `render_imgui` only *read* for display and *queue*
  requests. Execute every game call and every multi-field write on the game
  thread.
- Game-thread hook already provided by Fahrenheit (VERIFIED,
  `runtime/events/game_loop.cs`): it hooks `Sg_MainLoop` (VA 0x00820ae0, off
  0x420AE0, `cdecl void(float delta)`) and raises
  `FhApi.Events.Common.GameLoop.PreUpdate` / `.PostUpdate`
  (`FhEvent<UpdateLoopEventArgs>`, `delta`) before/after each iteration.
  Subscribe and drain the queue there. `Sg_MainLoop` calls `MsBattleProcess`
  (0x00790c10) directly every frame (calls.tsv, VERIFIED), so PreUpdate runs
  just before the battle step and PostUpdate just after it.
- Caveat (VERIFIED from docs/harness.md): `FFXApplication_animate` skips
  `Sg_MainLoop` while the PC (ESC/booster) menu is up, so queued actions wait
  until it closes. `Sg_MainLoop` itself skips the world update while
  `g_PauseOn` (sym comment).
- Single-byte toggles (section 1) are plain byte stores that are read where
  they are used; a byte store from the render thread is atomic and would work
  (INFERRED), but deferring them to PreUpdate costs nothing and avoids a
  half-frame where two related bytes disagree (e.g. NoDamageParty +
  NoDamageMonster). Recommended: defer everything.
- "In battle" test: `MsBattleGetStatus()` (0x007816f0, `movsx eax, byte
  [0x0112a8e0]`; VERIFIED) != 0, i.e. i8 `g_BtlStatus` at 0x0112a8e0
  (Fahrenheit `Btl.battle_state`, +0x10). 0 = no battle. 1 = the running
  battle (CTB/commands); other values are load/end states (8/10 after
  `MsBtlReadSetScene`, 0x21 the end, ...). Actor memory: `g_BtlChrTop`
  (0x011334d4) is the pointer to the actor array, non-null only while battle
  memory exists (`MsBtlChrGetMem` from `MsBattleExe` writes it; INFERRED that
  it is cleared by `MsBtlChrFreeMem`).

---------------------------------------------------------------------------

## 1. Battle debug toggles (transcribe into the toggle list)

None of the readers below test `g_FFXIsDebug`: every byte takes effect in the
release build when written (VERIFIED for each reader listed). All are written
`v & 1` by the original menu code: use 0/1 only (one is an i8 used
arithmetically, see notes). "Label" is the original Battle Config window text
(`Ms_Gui_BattleConfigProc` 0x007c6e20, VERIFIED); "env" is the `btlenv.txt`
key (table 0x00c443f0, reader 0x007ce450); "707d" is the battle-script native
0x707d `BtlSetFlag` case (0x007a81f0).

| VA | off | Fh `Btl.debug` | Label (button id) | env / 707d | Type | Effect (reader, VERIFIED unless noted) | When it takes effect / notes |
|---|---|---|---|---|---|---|---|
| 0x0112a8e1 | 0xD2A8E1 | - | CTB pause (0x53) | [pause] | u8 | `MsBattleProcess_Order` 0x00790fb0 returns 0 at once: the CTB never advances, nobody gets a turn | immediate; the battle just waits; turn off to resume |
| 0x0112a8f5 | 0xD2A8F5 | - | Auto Execute (0x5d) | [exe] | u8 | `MsBattleExeClear` 0x00781530 keeps a pending `g_BtlExe` (map jumps don't cancel it); `MsBattleInit` re-arms the btlenv [scene] battle | only meaningful with the dead btlenv path; skip |
| 0x0112a8f6 | 0xD2A8F6 | - | Console "PCmaster/CDmaster/local" (0x5e) | [env] | u8 0..2 | `MsEnvInit` 0x00782770, **only with g_FFXIsDebug**: 1/2 call `PcFileTblSet("host0:/ffx/proj/battle/<cc>/cddata/cdrom_cd.fnd" / `_lc.fnd`, 0xd)` (replaces the battle directory's file table) | **DANGEROUS, do not expose**: the VBF holds only `jp/` copies of these files; a wrong table breaks every battle file read |
| 0x0112a8f8 | 0xD2A8F8 | invincible_mon | Mon Invincible (0x5b) | [mtkmon] / 0xc (2 = both) | u8 | `DebugCanTakeDamage` 0x0078d3a0 -> 0 for monsters: `MsSubHP`, `MsSubMP` skip the loss, `MsCalcDamage` keeps "will die" clear | immediate. KO/petrify statuses are separate code (INFERRED still land) |
| 0x0112a8f9 | 0xD2A8F9 | invincible_ply | Ply Invincible (0x8b) | [mtkply] / 1 (2 = both) | u8 | same for party actors | immediate |
| 0x0112a8fa | 0xD2A8FA | mon_control | Mon Input (0x8c) | [teki] / 3 | u8 | `MsBattleProcess_InputCue` 0x00792a90 and `Native_BtlUseCommand` 0x007a44b0: an actor whose turn would be AI-run gets `TOSetBtlActiveChr` instead, i.e. the player picks monster commands | immediate; Fh "mon_control" |
| 0x0112a8fb | 0xD2A8FB | - | CTB Same Order (0x8e) | [sp0] | u8 | `MsBattleProcess_Order`: each tick an actor's CTB counter `+0x65c` above 1 drops straight to 1 | immediate; turns come round in actor order (INFERRED reading) |
| 0x0112a8fc | 0xD2A8FC | free_camera | Mouse Camera (0x60) | [camera] | u8 | `MsBattleProcess` passes it every frame to `MsDebugCameraSleep` 0x007921c0 (gate `g_GsDefDrawEnv2` 0x00b5ec4f is 1 in the image, so it always runs; Ghidra hides that test), which sets the "off" byte (+5, and +5±8, +0x15) of every camera request slot (0x011381ad.., stride 0xda0) = v: the automatic battle camera stops; `MsEffectStart` saves it to 0x0112a8fd and clears it for an effect, `MsEffectFree` restores it | immediate; also set by battle-script native 0x7011 (`Native_7011_BtlSetDebugCameraSleep`), so scripts can overwrite the mod's value. The PS2 "mouse" control itself is in the dead Camera window |
| 0x0112a900 | 0xD2A900 | no_magic_effects | Mag Not Eff (0x89) | [mag0] | u8 | `MsEffectRead` 0x00787f20 loads no effect (+0xdfd = 0): spells/abilities resolve without their visual effect; `MsInsertChrCommand` kind 0 aeon command falls back (+0x1e = 1); battle end skips its closing event effect (`MsBattleProcess_Status`) | next command. Good "skip animations" toggle (INFERRED safe: the developers' own switch). Was named `g_BtlAeonPlain2` in the dump; setter `MsSetDebugMagic` 0x00782d40 has no callers |
| 0x0112a901 | 0xD2A901 | no_mp_cost | Mag Free (0x8a) | [mp0] / 4 | u8 | `MsCheckUseCommand` 0x0078c690: MP cost 0 (as for monsters) | immediate |
| 0x0112a902 | 0xD2A902 | - | Mag Num 0 (0x98) | [btmag0] | u8 | `MsEffectRead`: every command plays effect file 0 (`magic = flag ? 0 : n`); `MsBattleProcess_Status` event effect 0 | test switch; not useful (visual garbage / test effect) |
| 0x0112a903 | 0xD2A903 | - | (no button) | [evmag0] | u8 | `MsEventEffectRead` 0x00788c60: event effects use file 0 | not useful |
| 0x0112a904 | 0xD2A904 | - | Sum Not Eff (0x9a) | [summon] | u8 | `MsInsertChrCommand` 0x007ae1a0: aeon (summon) command kind 0 without its special value (+0x1e = 1) | INFERRED "summon without effect" |
| 0x0112a905 | 0xD2A905 | - | Full Set (0x9c) | [full] | u8 | `MsBattleInit` 0x00781700 tail: runs FullItem, FullCommand, FullMember, FullGill | **no effect in a normal session**: `MsBattleInit` runs at boot (`Sg_MainInit`) and only again in the debug restarts. Use the functions of section 2 instead |
| 0x0112a906 | 0xD2A906 | - | Dmg Status Off (0x9b) | [stat] | u8 | `DmgCalc` 0x0078e630 skips `DmgCalc_InflictStatus` + `_Extra`: no status is inflicted (either side) | immediate |
| 0x0112a907 | 0xD2A907 | - | Exchg WillDie (0xc5) | [willdie] | u8 | `MsGetChrStatInfo` property 0x1b `STAT_WILL_DIE` counts actors whose +0xdec is clear instead of set (battle-script AI sees the inverse) | immediate; developer test, low value |
| 0x0112a908 | 0xD2A908 | no_variance | Dmg Random Off (0x92) | [rnd] / 6 | **i8** | `DmgCalc` passes `1 - v` as `MsCalcDamageCommand`'s vary flag | immediate. **Must be 0 or 1**: other values give vary = 1 - v (e.g. 2 -> -1) |
| 0x0112a909 | 0xD2A909 | never_crit | Dmg Crit Off (0x93) | [crit] / 7 | u8 | `DmgCalc` skips `DmgCalc_Crit` | immediate |
| 0x0112a90a | 0xD2A90A | always_hit | Dmg Prob Off (0x94) | [abs] / 8 | u8 | `DmgCalc_CheckHit` 0x0078a890 never misses; `DmgCalc_InflictStatus` every status chance passes; `Battle_Steal`/`StealGil` always succeed; `DmgCalc_Bribe` always succeeds (even with 0 gil, 2nd clause); `MsGetItem` 0x007990d0: every drop/equipment drop with a non-zero chance drops | immediate (battle-end rewards use it at the end) |
| 0x0112a90b | 0xD2A90B | - | Print Info (0x95) | [print] / 9 | u8 | `MsSoundStreamPlay` 0x007a0820: `dbgPrintf("# SHOUT %d:%d SEQ %d\n")` | log only (shows in Fahrenheit's log only if it captures `dbgPrintf` 0x0062f500) |
| 0x0112a90c | 0xD2A90C | always_available_overdrive | **LimitBreak On** (0xc2) | [limit] / 5 | u8 | the command's overdrive-gauge cost (+0x26) is ignored: `MsCheckUseCommand` (gauge +0x5bc >= max +0x5bd test passes), `MsCheckLeftWindow` 0x0078f700, `MsGetChrStatInfo` property 0x11a, `MsCalcMagic` 0x0078ab20 (no gauge spent) | immediate. Our sym name "g_BtlDebugNoCost" means the *overdrive* cost, not MP |
| 0x0112a90d | 0xD2A90D | always_crit | Dmg Crit On (0xd2) | [criton] / 0xd | u8 | `DmgCalc_Crit` 0x00789690 always doubles (+flag 0x100) | immediate; ignored while Crit Off is set (Crit not called) |
| 0x0112a90e | 0xD2A90E | always_1_dmg | (no button) | 707d 0xe | u8 | `DmgCalc`: every HP and MP result = 1 | immediate. Overwrites heals too: a heal (negative) becomes 1 damage (INFERRED from the code: the value is replaced, sign included) |
| 0x0112a90f | 0xD2A90F | always_9999_dmg | (no button) | 707d 0xf | u8 | `DmgCalc`: HP/MP result = 10000, then the normal cap -> 9999 (99999 with Break Damage Limit) | immediate; same heal caveat |
| 0x0112a910 | 0xD2A910 | always_99999_dmg | (no button) | 707d 0x10 | u8 | `DmgCalc`: result = 100000, capped to 9999/99999 | immediate; checked after the 10000 one (it wins) |
| 0x0112a911 | 0xD2A911 | always_rare_steal | (no button) | 707d 0x11 | u8 | `Battle_Steal` 0x0078b760 and `MsGetItem` drops take the rare item | immediate |
| 0x0112a912 | 0xD2A912 | ap_x100 | (no button) | 707d 0x12 | u8 | `MsGetItem`: monster AP x100 | at the battle end |
| 0x0112a913 | 0xD2A913 | gil_x100 | (no button) | 707d 0x13 | u8 | `MsGetItem`: monster gil x100 | at the battle end |
| 0x0112a914 | 0xD2A914 | never_overkill | Over Kill Off (0xd1) | [overkill] / 0x14 | u8 | `MsDamageCheckDeath` 0x0078c740 never registers an overkill (no overkill number, no overkill drops) | immediate |
| 0x0112a915 | 0xD2A915 | permanent_sensor | Every Look (0xc4) | [look] / 0x18 | u8 | `MsMenuCheckChrLook` 0x0078f990 -> 1, `MsMenuCheckLook` 0x0078f9e0 -> -1: Sensor/Scan info always shown | immediate |
| 0x0112a916 | 0xD2A916 | never_charge_overdrive | LimitBreak Off (199) | [limitoff] | u8 | `MsLimitUp` 0x007b1590 returns its input: the gauge never fills | immediate |
| 0x0112a917 | 0xD2A917 | - | Test Hit Eff (0xcc) | [testeff] | u8 | `MsSetChrEffect_0079eb10`: effects started with the "global" argument get owner 0 instead of the actor (+0x4c) | developer test; skip |
| 0x0112a918 | 0xD2A918 | - | DVD slow start (200) | [slow] | u8 | `MsBtlReadManage` 0x00783020: each read step first waits `g_BtlReadWait` (0x0112a919, 0x3c after each camera request) frames | battle loads become ~1 s slower per step; skip |
| 0x0112a91a | 0xD2A91A | - | ThrowAway Parm (0xca) | [throw] | u8 | `MsBattleProcess` battle end: `MsSetSaveData()` is skipped, the battle's results (HP/MP, AP etc. of the actors) are not written back to the game state | **progress-losing**; mark "test battles, results discarded" |
| 0x0112a91b | 0xD2A91B | - | Map Circle (0xd4) | [map] | u8 | `MsBattleGetMapCircleOn` 0x007816a0, read by `yiMapReadMaster` / `yiObjReadCallback` | when a battle map loads (INFERRED: a debug circle object) |
| 0x0112a91c | 0xD2A91C | - | DVD stop mag (0xc9) | [slowmag] | u8 | `MsEffectStart` 0x00788480 state 3: an effect does not start while set | **stalls the battle** at the next command until cleared ("effect hold") |
| 0x0112a91d | 0xD2A91D | - | Ply HP1 (0xd8) | [plyhp1] | u8 | `MsSetRamChr` 0x0079b4f0: a party actor set up while set gets HP 1; the config button also calls `MsDebugWeakParty(v)` at once | byte alone: next set-up only; for "now" call 0x00784c80(v) (section 2) |
| 0x0112a91e | 0xD2A91E | - | Mon HP1 (0xd9) | [monhp1] | u8 | same for monsters (new monsters incl. reinforcements); button calls `MsDebugWeakMonsters(v)` | as above, 0x00784bc0(v) |
| 0x0112a91f | 0xD2A91F | never_hit | Dmg Hit Miss (0xda) | [miss] | u8 | `DmgCalc_CheckHit` always misses, `DmgCalc_InflictStatus`/`_Extra` never land, `MsCalcEscape` 0x0078a780 always fails | immediate (both sides) |
| 0x0112a920 | 0xD2A920 | - | Weapon (0xdb) | [wep] | u8 | `MsSetSaveStartGame` 0x00786b00: the new-game equipment list goes past kind 0x22 | new game only |
| 0x0112a921 | 0xD2A921 | - | Magic Item (0xdd) | [item] | u8 | **no reader** besides the config/env code (data_refs) | no effect; omit |
| 0x0112a922 | 0xD2A922 | - | Skip Command (0xc3) | [skip] / 0x17 | u8 | `MsMenuGetChrSkipCommand` 0x0078fb80 returns 0xff (all 8 top-level command bits "skipped") | INFERRED risky: may hide every command; test before exposing |
| 0x0112a924 | 0xD2A924 | - | Camera Prio (0xe3) | [camp] | u8 | `MsBattleExe` passes it to `MsCameraPriorityMask` 0x007be100 at battle start (the button calls it at once): non-zero = `MsCameraPriority` changes nothing | byte alone: next battle; "now" = also call `MsCameraPriorityMask(v)` (cdecl(int)) |
| 0x0112a925 | 0xD2A925 | - | Camera Local (0xe5) | [localcam] | u8 | **no reader** besides config/env | omit |
| 0x0112a926 | 0xD2A926 | - | Neck Off (0xe6) | [neckoff] | u8 | `MsSetHeadTarget` 0x007b1e80 does nothing (no head tracking) | immediate |
| 0x0112a927 | 0xD2A927 | - | Focus Off (0xe8) | [focus] | u8 | `MsBattleProcess_ChrDamage_007911e0`: no `MsScreenFocusResetAll` after an effect camera ends | immediate; cosmetic |
| 0x0113338c | 0xD3338C | - | BtlDebugTweak "player never die" | - | **u32** | `MsSubHP` 0x0078e230: actors with model id (+4) 1..7 return before any HP change (never damaged); every other actor loses 99999 extra (one-hit kill) | immediate. Setter `enable_playerNeverdie` 0x0078e620 (cdecl(u32)). INFERRED: 1..7 are the seven main characters' model ids (monsters' ids are 0x1000+) |
| 0x0112a923 | 0xD2A923 | - | - | 707d 0x19 | u8 | `g_BtlFirstAttackSet`: `MsBtlReadManage` uses it for the opening (first strike/ambush), 0xff none; `MsBattleInit` sets 0xff | scripts set it; not a debug toggle (listed for completeness) |

Related writers of these bytes besides the mod (VERIFIED, data_refs): the
battle-script natives 0x707d/0x707e (set/get flags; 0x707d case 0 = FullItem,
10 = SetAllSP + FullGill, 0x15 = FullCommand, 0x16 = FullSummon), native 0x7011
(camera sleep), and the PC tweak panel functions `enableMonInput` 0x007c86b0,
`enableMonsterInvincible` 0x007c86c0, `FUN_007c86d0` (party invincible),
`enable_playerNeverdie` 0x0078e620. The env reader 0x007ce450 is dead (only
reached from the dead `MsDebugInfoRead`, section 6).

Suggested ImGui grouping: Damage (Random Off, Crit On/Off, Prob Off = always
hit, Hit Miss, Status Off, Overkill Off, Dmg 1 / 9999 / 99999), Survival (Ply
Invincible, Mon Invincible, Player never die + one-hit kills), Resources (MP
free, Overdrive always ready, Overdrive frozen), Rewards (Rare steal, AP x100,
Gil x100), Flow (CTB pause, CTB same order, Mon input, Skip anims = Mag Not Eff,
Effect hold, Sensor), Camera (free camera sleep, priority mask, neck, focus),
Dev-only (ThrowAway Parm, DVD slow start, Mag Num 0, Test Hit Eff, Map Circle).

---------------------------------------------------------------------------

## 2. Cheat/action functions (mod buttons)

All are cdecl (caller cleans; plain `ret`, VERIFIED from objdump), no `this`.
Run them from the PreUpdate/PostUpdate queue, never from `render_imgui`.
Save layout used below: inventory `g_SaveItemIds` 0x0113095c (0x70 x u16) and
`g_SaveItemCounts` 0x01130b5c (0x70 x u8); gil `g_SaveGil` 0x011307d8 (u32);
party records `g_PlySave` from 0x0113205c, 18 x 0x94 bytes (Fahrenheit
`PlySave` field order: +4 base_hp, +8 base_mp, +0xc..+0x13 base stats, +0x1c hp,
+0x20 mp, +0x24 max_hp, +0x28 max_mp, +0x2c flags, +0x3b slv_available, +0x3e
abi_map (6 words), +0x60 limit_mode_counters (20 words) — offsets computed
from their struct, INFERRED); shared command bits `g_SaveSharedCommands`
0x011307fc; equipment `g_Equipment` 0x01130f2c (200 x 0x16).

| VA | off | Name (ours) | Signature (VERIFIED) | What it does (VERIFIED) | Preconditions / side effects | FhCall |
|---|---|---|---|---|---|---|
| 0x007849d0 | 0x3849D0 | MsDebugFullItem | `void()` (tail `jmp 0x00a44ea0`) | inventory slot i = item 0x2000+i count 99 for all 0x70 (existing matches set to 99; with `TOGetFFXLang() == 0xb` item 0x2065 becomes 0x2000 x10); `TkMenuItemRefresh(0)`; 0x00a44ea0 refreshes the sphere-grid use-item list if open | any time on the game thread; overwrites the inventory order (every slot becomes its own item). The battle's item menu cache may not refresh until reopened (INFERRED) | `FUN_003849d0` d_UnknownFn (usable) |
| 0x007849c0 | 0x3849C0 | MsDebugFullGill | `void()` | `g_SaveGil = 999999999` | none | `FUN_003849c0` |
| 0x00784830 | 0x384830 | MsDebugAddGill | `void(int n)` ([ebp+8]) | gil += n, capped 999999899 (no lower clamp: negative n can go below 0) | no callers in the exe | `FUN_00384830_0020B910` (needs own delegate) |
| 0x00784860 | 0x384860 | MsDebugAddItem | `void(int n)` | every slot i = item 0x2000+i, count = clamp(count+n, 0, 99) | no callers; overwrites the inventory ids | `FUN_00384860` (own delegate) |
| 0x007848b0 | 0x3848B0 | MsDebugFullCommand | `void()` | **synchronous file read**: `GetCD()` vtable +0x10(0xd) dir, +0x14(1) size, `Sg_MallocA(size, 0x10, 5, "ply_data")`, +0(1, buf, 0, 0) read, +8(0) wait; for chars 0-17 except 7: OR the ply_data record (0x12, or 0x13 from 8) words +0x3e..+0x48 into the char's abi_map; sets the first 0x83 bits of `g_SaveSharedCommands`; `Sg_FreeA`; in battle (`MsGetChrTop` && `MsBattleCheck`) re-applies abilities to actors 0..0x1e (`MsSetRamChrAbility`) | **game thread only, not while a battle/map load is reading** (it switches the CD object's directory and blocks): safe moments = field with control, or battle status 1 between commands (INFERRED). Learns every ability: irreversible on that save if saved. No achievement call itself | `FUN_003848b0` |
| 0x00784ac0 | 0x384AC0 | MsDebugFullMember | `void()` (tail `jmp MsSaveEncountCheck`) | `MsSetSavePlyJoin(i, 1)` for i 0..0x11 (all 7 + Seymour + the aeons), then `MsSaveEncountCheck` | **story-breaking**: joins characters/aeons ahead of the story; flag as dangerous for a real save | `FUN_00384ac0` |
| 0x00784b60 | 0x384B60 | MsDebugFullSummon | `void()` | `MsSetSavePlyJoin(i, 1)` for i 8..0x11 (aeons only) | story-breaking as above; only caller native 0x707d case 0x16 | `FUN_00384b60` |
| 0x00784ae0 | 0x384AE0 | MsDebugSetAllSP_00784ae0 | `void()` | every PlySave +0x3b (sphere levels available) = 0x62 (98) | none | `FUN_00384ae0` |
| 0x00784b00 | 0x384B00 | MsDebugFullStats_00784b00 | `void()` | 18 PlySave records: base_hp/hp/max_hp = 99999, base_mp/mp/max_mp = 9999, the 8 base stats = 0xff | out of battle state only (actors are set up from these at battle start) | `FUN_00384b00` |
| 0x00784b80 | 0x384B80 | MsDebugWeakAll_00784b80 | `void()` | if `MsGetChrTop()`: actors 0..0x1e HP (+0x5d0) = 1, `MsSetChrWeak(i, -1)` | battle only (no-op when `g_BtlChrTop` is 0); party too | `FUN_00384b80` |
| 0x00784bc0 | 0x384BC0 | MsDebugWeakMonsters_00784bc0 | `void(int on)` | if actors exist: actors 0x14..0x1b HP = on ? 1 : max_hp (+0x594); `MsSetChrWeak` | battle only. Restores full HP when on = 0 | `FUN_00384bc0` (own delegate) |
| 0x00784c80 | 0x384C80 | MsDebugWeakParty_00784c80 | `void(int on)` | same for actors 0..0x11 | battle only | `FUN_00384c80` (own delegate) |
| 0x00784c20 | 0x384C20 | MsDebugAllItemsOne_00784c20 | `void()` | slot i = item 0x2000+i count 1, all 0x70 | **destructive** (counts lost) | `FUN_00384C20_0020BE80` |
| 0x00784cd0 | 0x384CD0 | MsDebugClearItems_00784cd0 | `void()` | every slot id 0xff count 0 | **destructive**: empties the inventory | `FUN_00384CD0_0020BF20` |
| 0x00784c40 | 0x384C40 | MsDebugAbilityOne_00784c40 ("LM1") | `void()` | records 0..6, 20 u16 from +0x60: every non-zero word = 1 | INFERRED from Fahrenheit's PlySave layout: these are the overdrive-mode counters, so the next qualifying action learns each mode in progress. Config button label "LM1". Rename in the mod |
| 0x00798f10 | 0x398F10 | MsDebugAddWeapon | `int(int list, u8 owner, u16 id)` -> 0 / -1 | list != 0: the next spoils slot via `BattleRewards_NewGear` (spoils at 0x02310ea0); list == 0: the first free `g_Equipment` slot (+2 clear); writes +0 id, +2 = 1, +4 owner, +5 word 0xff00, +8 = 0x01000101, +0xe/+0x12 = 0x00ff00ff (no abilities) | no callers; INFERRED meaning of +8 bytes; use list = 0 out of battle | `FUN_00398f10` (own delegate) |
| 0x006fc970 | 0x2FC970 | FFEscMenu_onDebugFill_006fc970 | `void(int yes, int* choice)` | yes: choice 0 -> FullItem, **1 -> `achievementUnlockAchievement(0x19)`, `(0x18)`, then FullCommand**, 2 -> FullGill | the PC booster's "Parameter" pop-up callback (set by `FFEscMenu_onParameterButtonClicked` 0x006fc7b0; live in release). **DANGEROUS: choice 1 unlocks two Steam achievements.** Never call it; call the Ms functions directly | `FUN_002fc970` |
| 0x007921c0 | 0x3921C0 | MsDebugCameraSleep | `void(int v)` | if v != `g_BtlDebugCameraSleep` (0x011334c8): every camera request slot's off bytes = v & 1, remembers v | already called every frame with 0x0112a8fc; just set that byte | `FUN_003921c0` |
| 0x007be100 | 0x3BE100 | MsCameraPriorityMask | `void(int v)` | `g_CameraPriorityMask` (0x02311424) | with 0x0112a924 | `FUN_003be100` |

Achievements (for the risk notes): `achievementUnlockAchievement` 0x00822380
(cdecl(id): `SaveDataIsSaveEnabled`, then `SaveDataUnlockAchievement`). None of
the Ms* functions above call it (VERIFIED by reading them); only the booster
callback does. INFERRED: having all abilities/items can still satisfy the
game's own achievement checks later (they run from normal play), so the mod
should warn that cheated saves may unlock achievements.

---------------------------------------------------------------------------

## 3. The battle debug windows (Ms_Gui_*) and the status line

### How they were opened (VERIFIED)

`Gui_MasakiInit(n)` (0x007c86e0, off 0x3C86E0, cdecl(int n); `FhCall.
FUN_003C86E0_0025DBA0`) creates window n once (guard words 0x01139ee0..
0x01139f08) with `Gu_WindowCreate(x, y, w, h, 7, proc)`:

| n | Proc | VA | Title | Opened from main menu tab/button |
|---|---|---|---|---|
| 0 | Ms_Gui_InfoProc | 0x007cfcd0 | "Camera Info" | `< CAM >` "Info" (0x1d) |
| 1 | Ms_Gui_BattleInfoProc | 0x007daed0 | "BattleInfo <%s>" | `< BTL >` "Info" (0x1f) |
| 2 | Ms_Gui_ReferencePos | 0x007d9c20 | "Reference" | `< CAM >` "REF" (0x1e) |
| 3 | Ms_Gui_BattleConsoleProc | 0x007c6110 | "BattleConsole" | `< BTL >` "Console" (0x23) |
| 4 | Ms_Gui_CameraProc | 0x007d44a0 | "Camera" | `< CAM >` "CAM" (0x24) |
| 5 | Ms_Gui_BattleProc | 0x007d80a0 | "BattlePos <%s>" | `< BTL >` "Pos" (0x20) |
| 6 | Ms_Gui_BattleConfigProc | 0x007c6e20 | "BattleConfig" | `< BTL >` "Config" (0x34) |
| 7 | Ms_Gui_BattleChr1Proc | 0x007dd890 | "CHR data 1" | `< BTL >` "CHR1" (0x58) |
| 8 | Ms_Gui_BattleChr2Proc | 0x007de790 | "CHR data 2" | `< BTL >` "CHR2" (0x59) |
| 9 | Ms_Gui_ShakeProc | 0x007dfde0 | "Shake Editor" | `< CAM >` "Shake" (0x50) |

(`Ms_Gui_MapDataProc` 0x007d5480 "Camera Point Data" is opened from inside the
Camera window, pointer at 0x007d5315.) Other main-menu entries on those tabs:
`< BTL >` "CHRInfo" (0x42) -> `Gui_ChrInfo`; `< CAM >` "Spline" (0x39) ->
`sasa_menu(0)`, "always" (0x57) toggles `Sg_EnableCalcCam`.

The only caller of `Gui_MasakiInit` is `WMainProc` (0x0084f550), the debug main
menu's window procedure (tabs `< CHR >`, `< MAP >`, `< SYS >`, `< BTL >`,
`< CAM >`, ...). The only reference to `WMainProc` is a `push 0x84f550` inside
the code at 0x0084f100 (`Gu_WindowCreate(0x1b0, 0, 0x50, 0x52, 2, WMainProc)`
after `Sg_Deci2Open`), and **0x0084f100 has no caller and no pointer** in the
exe (byte scan): the menu is unreachable in release (VERIFIED). Whether Gu
windows draw on PC is the Gu-toolkit agent's question. Note also
`MsBattleDraw` (0x00780d00): `call FFXIsDebug; jne 0x007c8ae0` where 0x007c8ae0
is a bare `ret` — the battle-side debug draw was compiled out (VERIFIED). A
mod can call `Gui_MasakiInit(n)` from the game thread to create a window, but
everything else (input, drawing) depends on the Gu toolkit running.

### What each window shows/edits (for an ImGui rebuild)

- **BattleConfig** (6): the 39 toggles of section 1 (two columns, ids above)
  plus buttons SAVE (0x5f: `MsDebugEnvSave`, writes nothing on PC, see 6),
  FULL (0x97: FullItem, FullCommand, FullMember, SetAllSP, FullGill), HP1
  (0x9d: WeakAll), IT0 (0xe7: ClearItems), IT1 (0xcb: AllItemsOne), LM1 (0xe1:
  AbilityOne), FS (0xf9: FullItem + FullStats). On/off text from the table at
  0x00c4468c.
- **BattleConsole** (3): the debug scene picker (section 4): "SCN" field index
  (0x00c44248, buttons `<`/`>` 0x3b/0x3c, step 1/10/100 by which pad button
  repeats), encounter index "e:max" (0x01139e44, 0x3d/0x3e), the battle name
  (`MsGetBattleName`), "g%d/%d FM/BM%3d e%d/%d" (group, map id `& 0x3ff`, BM
  when `0x400`), the 3 party slots (`g_SaveInParty` 0x011307e8, changed with
  `MsExchangeSaveParty` via `MsDebugNextMember_007c60a0`), language and Asia
  mode switches (`FFXSetLang`, `FFXSetAsiaMode`, `MsSetSaveConfigEnglish`),
  the CD mode byte 0x0112a8f6 (do not touch), SAVE (env save), **EXE** (0x3f:
  in status 0 or 1: `opd_exit_from_ovl`, `TOAllClearMessageWindow`, scene
  walk, `MsNowMusicStop`, `MsBattleExe(0, field, group, formation)`), **SREL**
  (0x4d: the same after a full battle-module reload: `MsCameraInit`,
  `MsBattleAllFree`, `MsEtEffectFree`, `MsBattleMemInit`, `MsEnvInit`,
  `MsBattleInit`, `MsEtEffectRead`).
- **BattleInfo** (1): the running battle's ids and states as text: "SCENE%4d:
  %1d:%1d CTB%2d/%2d IN%1d CM%1d", "BTL%2d EV%1d HIT%1d COM%1d RD%1d SE%1d
  M%3d", "ACT%2d:%d %08x:%d ED%2d MG%1d", "PLY%2d P1..P4", "MON%2d M1..M4",
  "SEP/DEF/TG", "V%dR%dF%d" (formation bytes), "BM%3d"/"FM%3d", encounter
  counters "en/EN %3dp:%2d/%2d/%3d" (`g_BtlEncountSteps` 0x0112a9d8, Fh
  walked_dist), per-slot motion/sound "M/S/W/N/P%1d:%03d". Mirror with the
  actor table below and `Btl` fields.
- **Camera Info** (0), **Camera** (4), **Camera Point Data**, **Reference**
  (2), **Shake Editor** (9): the battle camera debugger (camera pos/ref/up as
  "%.4f", screen depth, focus "Focus %d:%d Near Far", window "WIN/DRW X Y",
  camera modes `MsDebugGetCameraMode` 0x007cfb20, kept cameras
  `MsDebugCameraKeep_007c8c50`, spline/point data THETA/PHI/LEN/WEIGHT, shake
  params with output text "camSetScrShake(...)"; the Camera window's save
  writes "host0:%s%s" via `MsDebugOutCamera` 0x007d2430). The state they edit
  is in 0x0113ed18..0x0113f3e0 and the camera records 0x01137b8c (0xda0 each).
  Camera agent's area if any; a faithful ImGui port is large.
- **BattlePos** (5): battle position file viewer/editor ("AREA", "POS",
  X/Y/Z, "D"; reads "host0:.../btl/<name>/..." via `MsDebugBattlePosInit`
  0x007c90f0 -> files absent on PC, prints "not found %s").
- **CHR data 1** (7): per actor motion flags (u16 at +0x4ac, bit labels
  "ATTACK : Run 01 / Run 00+01 / Stay Attack / Return / Pos Check / Miss
  Motion / Weight Inf", "AVOID : Disable / Move", "DIR : Fix / Return",
  "MOVE : Avoid / Back Jump / Disable Move Chest", "RETURN : Wait", "TURN :
  Run / Stay"; also +0x41b, +0x41d, floats +0x4bc..+0x4dc). "PLY SV"/"MON SV"
  save to host0 `.src` files (`MsGetPlySaveName`/`MsSetFileName`; no-op on
  PC, section 6).
- **CHR data 2** (8): per actor motion tuning: "ATTACK : Normal/Near/Motion
  Frame" (+0x40c/+0x40d/+0x40e), "Inc/Dec Speed" (+0x4b9/+0x4ba), "Height",
  "Offset", "Start Dist", "RUN : Speed / First / Acc / Return Speed",
  "MOVE : Back Jump Dist", "ROUND : Return Frame", "HOKAN : Type", "IDLE2 :
  Prob" (+0x4b8), "WEIGHT" (+0x4b0..+0x4dc floats). Same SV buttons.

### Actor struct for an ImGui battle inspector (VERIFIED: MsGetChr asm, docs/formats/battle.md)

`MsGetChr(i)` 0x00794020 (cdecl(int) -> eax; Fh `FFX.FhCall.MsGetChr`):
i & 0xff < 0x1f -> `*(u32*)0x011334d4 + i * 0xf90`; 0x1f..0x5c -> weapon
records `*(u32*)0x0113446c + (i-0x1f)*0x390`; else 0x011334d8 (spare). Party
actors 0..0x11 (0-7 the characters, 8+ aeons), monsters 0x14..0x1b. Read the
base pointer directly (no call needed) and skip when it is 0. Fahrenheit's
`Chr` (core/ffx/battle/chr.cs) matches; field names theirs.

| Off | Type | Field |
|---|---|---|
| +0x000 | ptr | field-character pointer (`actor`) |
| +0x004 | u32 | model_id |
| +0x00e | u16 | chr_id |
| +0x540 | 0x50 | name (ChrName) |
| +0x594 / +0x598 | i32 | max_hp / max_mp |
| +0x5a8..+0x5af | u8 x8 | strength, defense, magic, magic_defense, agility, luck, evasion, accuracy |
| +0x5bb | u8 | overdrive mode |
| +0x5bc / +0x5bd | u8 | overdrive gauge / gauge max |
| +0x5d0 / +0x5d4 | i32 | hp / mp |
| +0x5da..+0x5dd | u8 | element absorb / nullify / halve / weak (bits fire, ice, thunder, water, holy) |
| +0x606 | u16 | statuses (bit1 zombie, 3 poison, 4-7 breaks, 8-11 confuse/berserk/provoke/threaten) |
| +0x608..+0x614 | u8 x13 | sleep, silence, darkness, shell, protect, reflect, NulTide, NulBlaze, NulShock, NulFrost, regen, haste, slow counters (0xff forever) |
| +0x616 | u16 | extra statuses (scan, distills, dodge, guard, ..., auto-life, curse, defend, cover, sentinel, doom) |
| +0x65c | u8 | CTB counter (ctb) |
| +0x6e4 / +0x6e8 / +0x6ec | i32 | current_hp / current_mp / current_ctb (damage-calc working copies) |
| +0xd34 / +0xd38 / +0xd3c | i32 | last damage hp / mp / ctb |
| +0xdc8 | u8 | in battle (takes part) |
| +0xdcc | u8 | KO state (2 = KO'd) |
| +0xdce | u8 | petrified |
| +0xdd7 / +0xdd8 | u8 | in CTB list / in HP list |
| +0xdeb | u8 | steal count |

Editing hp: write +0x5d0 then call `MsSetChrWeak(i, -1)` (as the debug
functions do) so the weak pose/SOS state follows (INFERRED needed). Edits on
the game thread only.

### The battle debug status line (MsBattleProcess_DebugStatus_00792f20)

- VA 0x00792f20, off 0x392F20, `cdecl void()` (VERIFIED: no args, plain ret);
  `FhCall.FUN_00392f20` d_UnknownFn is usable.
- Called only from `MsBattleProcess` in battle (status != 0) when
  `TODebugGetPadPrintFlag()` (0x008abd20) is non-zero. That function is
  `xor eax,eax; ret` (bytes `33 c0 c3` + int3 padding): **never runs in
  release** (VERIFIED). The same flag also gates debug prints in
  `menu_list_step` 0x0076f470, `movie_csc` 0x0077d5e0 and
  `chr_texanim_draw_sub_new` 0x0077f210, so do not patch it to 1.
- What it prints (VERIFIED): "%2d:%2d:%1d W%1d S%1d E%1d" (battle status,
  battle-end status, sound report, SEP wave, CD state `GetCD()+8(1)`, read cue
  level) at (0xbc, 8); the formation name bytes 0x0112c25a at (0x188, 8); "S%08x
  E%08x %02d" (IOP sound status/error); "VS%8x:%4x"; with `g_BtlDirFree`: aeon
  energies + Yojimbo "Y M D R"; the current command "C%02d %04x M%02x"; camera/
  effect state "EV%1d:%1d E%02d:%02x"; the command actor's motion/read state
  "M%1d_%03d", "C A D M", "Rd Ef Ex Sr Ss"; a frame counter.
- Its output goes to the TODaps text buffer (`TODapsSetPos`/`TODapsString`
  0x0089de30/0x0089dec0 -> `g_TODapsBuffer` 0x0133fab0, 0x800 bytes). The
  buffer is turned into glyphs only by `TODapsMakePkt` (0x0089dcb0, off
  0x49DCB0, cdecl void(); `TOAdpFontM_ASCII` per char at 12 px steps inside a
  512x416 scissor), which has **no caller** (VERIFIED). `TODapsDraw`
  (0x0089dc10, off 0x49DC10, cdecl void()) only clears the buffer; it is
  called at the end of `TODrawMessageWindow` (0x008abe1c), by
  `TkMenuMainLoop_008a9ca0`, `TODrawMovieMessage` and `TkMenuCtrlHelp`.
- Revival (INFERRED, untested): hook `TODapsDraw` (first instruction `push
  0x800`, 5 bytes, hookable) and, in battle, call `0x00792f20` then
  `TODapsMakePkt` before chaining to the original. That draws through the
  normal TO font path (`TOAdpFontM_ASCII` + `TOCtrlPkt`), which is how all
  in-game text reaches the PC renderer, at PS2 coordinates. Simpler and
  safer alternative: rebuild the same line in ImGui from the globals above.

---------------------------------------------------------------------------

## 4. Debug scene picker, debug battles, map jump

### Globals (VERIFIED)

| VA | off | Type | Meaning |
|---|---|---|---|
| 0x00c44248 | 0x844248 | i32 | picker: battle **field index** (record index into the btl field list, wraps 0..g_BtlFieldCount-1) |
| 0x01139e44 | 0xD39E44 | i32 | picker: **encounter index** within the field (flat over all groups) |
| 0x01139e48 | 0xD39E48 | i32 | picker: group index (derived) |
| 0x01139e4c | 0xD39E4C | i32 | picker: formation slot within the group (derived) |
| 0x01139e50 / 0x01139e54 | 0xD39E50 | 2 x i32 | `g_BtlDebugField`: field id and formation id (derived) |
| 0x0112c256 / 0x0112c258 / 0x0112c259 | | i16 / u8 / u8 | the battle to run: `g_BtlFieldIndex`, `g_BtlGroupIndex`, `g_BtlSlotIndex` |
| 0x0112a8e2 | 0xD2A8E2 | u8 | `g_BtlExe` battle request (2 = label battle, 1 = random encounter) |
| 0x0112ca2c | 0xD2CA2C | u8 | `g_BtlLabelLock`: 1 blocks `MsBattleLabelExe` (written by 0x006bce00) |

### Functions (all cdecl, VERIFIED)

- `MsDebugSceneWalk_007c5dd0` (off 0x3C5DD0, `void()`, `FUN_003c5dd0`): wraps
  0x00c44248 and 0x01139e44 into range, walks `MsBtlListField`/`Encount`/
  `Group` and fills 0x01139e48, 0x01139e4c, 0x01139e50/54 (and pointers
  0x01139e94/98/9c/a0). Needs the battle lists loaded (`MsBattleInit` at boot:
  always true after start-up, INFERRED).
- `MsDebugGetFieldNum` 0x007c5f50 (`int()`): `[0x01139e50] << 16 + [0x01139e54]`
  = the label (field id << 16 | formation id) for `MsBattleLabelExe`.
- `MsDebugSetScene` 0x007c5f60 (`void(field, group, slot, field_id, formation_id)`,
  5 args, tail jmp to 0x007c5f00 which recomputes 0x01139e44): sets the picker
  from a battle (called by `MsBattleExe`/`MsBattleInit` only with FFXIsDebug).
- `MsGetBattleName` 0x007c5fc0 (`char* (int sys_names, char* out)`): runs the
  scene walk, writes "<8-char field name>_%02d" (or "output"/"system" for
  field 0 with sys_names) into out. Use for the picker's label.
- `MsBattleLabelExe` 0x00781c90 (off 0x381C90; Fh `FFX.FhCall.MsBattleLabelExe`,
  typed): `int (uint label, u8 encount_type, u8 transition)` -> always -1
  (`or eax,-1; ret`). Unless `g_BtlLabelLock == 1` or `MsBattleGetStatus() != 0`,
  `MsFldGetScene(label, ...)` (0x007827f0: field `label >> 16` by id, formation
  id `label & 0xffff` searched through all groups) and on a hit stores field/
  group/slot, `g_BtlEncountType` (0x0112a9d4) = type, 0x0112a9d5 = transition,
  `g_BtlExe = 2`. The battle starts in the same/next `MsBattleProcess` (status
  0 path: `if (g_BtlExe) MsBattleExe(0, field, group, slot)`). The debug combo
  calls `MsBattleLabelExe(MsDebugGetFieldNum(), 0, 0)` (asm 0x00790f5e: `push
  0; push 0; call MsDebugGetFieldNum; push eax; call 0x00781c90`); scripts
  (native 0x7002) pass type 1.
- `enterBattle` 0x007c6d80 (off 0x3C6D80, `void()`, `FUN_003C6D80_0025BF40`):
  `MsBattleExe(0, [0x00c44248], [0x01139e48], [0x01139e4c])` directly (the PC
  BtlDebugTweakUtil panel's "enter"). No state checks.
- `MsBattleExe` 0x00781020 (Fh typed): `void(int mode, field, group, slot)`;
  mode 0 = a full battle start (blur/focus reset, fades, `MsBattleFree`, load).
  With FFXIsDebug and mode 0 it also calls `SsSetProjectBase("/usr/local/ffx/
  <cc>/ps2/sound")` (-> `SqsSetProjectBase` -> an Sqs RPC: unknown effect on
  PC, INFERRED risk of the debug flag).

### Debug pad combos in `MsBattleProcess` (VERIFIED; only with g_FFXIsDebug)

`TkGetControlerHist(0,0,0)` PS2 bits: 1 L2, 2 R2, 4 L1, 8 R1, 0x20 circle,
0x40 cross, 0x80 square, 0x800 start.
- Outside battle (status 0): L1+R1+L2+R2+Cross -> `dbgPrintf("## BATTLE %x
  %x\n")`, `TOAllClearMessageWindow()`, `MsBattleLabelExe(MsDebugGetFieldNum(), 0, 0)`
  = start the picker's battle.
- In battle: Start+Square (when `MsCheckSoftResetOk`) -> `g_BtlStatus = 1`,
  `g_BtlEndForced = 5` (force the end); L1+R1+L2+R2+Cross -> restart this
  battle (`opd_exit_from_ovl`, `MsNowMusicStop`, `MsCameraInit`, and with
  Circle also the full module reload as SREL), `MsBattleExe(0, ...)`.
Enabling g_FFXIsDebug for this is not recommended (it changes ~40 other code
paths, incl. the sound project base above and `AtelSetEventJump`'s gate).

### Mod recipe: "start encounter N" (INFERRED, from the code above)

On the game thread (PreUpdate), only when `g_BtlStatus == 0`, the player has
field control (script context byte 0 bit 0x80, see below), no menu/message is
open and no map change is pending:
1. either set the picker (`[0x00c44248] = field index`, `[0x01139e44] =
   encounter index`, call `MsDebugSceneWalk_007c5dd0()`, label =
   `MsDebugGetFieldNum()`), or build the label yourself (`field_id << 16 |
   formation_id` from btl.bin / Fahrenheit's `Btl` btl_bin pointers);
2. `TOAllClearMessageWindow()` (as the combo does), then
   `MsBattleLabelExe(label, 0, 0)`; returns -1 always, so check
   `g_BtlExe == 2` afterwards to know it took.
This is the same path as the debug combo and as script battles, so the
battle-end callback (`AtelBattleEndCallback`) and the return to the field run
normally. Avoid `enterBattle`/`MsBattleExe` directly (they skip the request
path). Battles whose formation needs story state may soft-lock (INFERRED).

### Field side: map jump (VERIFIED)

- `TkSetStartMapIndex` 0x0088ebf0 only stores (id, index) into
  `g_TkStartMapEvent` 0x0133085c / 0x01330860 (used at boot by
  `AtelInitDebugger_0087f0d0` with (0x17, 0)); it does not jump. Not useful.
- `AtelSetEventJump2` 0x0086ff40 (off 0x46FF40; Fh `FFX.FhCall.AtelSetEventJump2`,
  cdecl(int map, int entrance, int fade_mode)): only acts when script context
  byte 0 (`*g_ScriptContext`) has bit 0x80 (the "may leave" state) or
  `FFXIsDebug()`: `AtelUcOff`, `TkSetEventJump(map, entrance)` (writes save
  +0 map, +2 previous, +0xc entrance; map < 0 = return to the saved map),
  callback 0x01327070, `MsBattleExeClear`, clears bit 0x80, sets ctx+0x210 bit
  1, fade out. `AtelSetEventJump` 0x0086fec0 is the same with fade mode 0
  (used by the debug map selector and `TkDebugLoadViewerSave`). Fahrenheit
  hooks AtelSetEventJump2 for PostReturnToTitle (room 23 entrance 0).
- Debug map-select list (Gu main menu, dead like the rest): `gui_eve_GWM_BUTTONUP`
  0x00854e80 reads `Sg_PcRead("/ffx/proj/event/header/eventid.bin")` — **this
  file is in FFX_Data.vbf** (`ffx_ps2/ffx/proj/event/header/eventid.bin`,
  VERIFIED in the VBF name table): u32 [0] = table bytes, entries of 8 bytes
  (count = [0]/8), entry i +0 = offset of the name string from the file start;
  the selected entry's *index* is the map id passed to `AtelSetEventJump(id, 0)`.
  A mod can read that file (Fahrenheit VBF access) for a map-name list.
- Mod recipe "jump to map N": game thread, field, control bit set:
  `AtelSetEventJump2(map, entrance, 0)`. INFERRED risk: jumping out of story
  order can break progression flags; entrance must exist for the map.

---------------------------------------------------------------------------

## 5. Save/load debug functions

Real PC saves (docs/formats/save.md, VERIFIED there): `%s%s%03d` of
`Documents\SQUARE ENIX\FINAL FANTASY X&X-2 HD Remaster\FINAL FANTASY X\` +
`ffx_` + n; `ffx_000` is the autosave. Files are 0x6900 bytes
(`g_SaveFileBuffer` 0x01597f70).

Path building of the "debug" file functions (VERIFIED):
`FmodFile_getDataDirectory_00679a10(key, buf, 0x100)` ->
`fiosUnifyFilename(key)` -> stream prefix + key. The game's prefix is
`"../../.."` (`Game_InitFileSystem` 0x006795b0), so the file is
`../../../FFX_Data/GameData/PS3Data/saves/...` **relative to the process
working directory**, opened with the CRT `fopen` (loose files, not the VBF).
Under Fahrenheit, `fiosUnifyFilename` and the stream prefix are replaced
(`runtime/fileloader.cs`: empty prefix + `normalize_path`), so the same call
yields a root-relative `/FFX_Data/GameData/PS3Data/saves/...` (INFERRED: on
the current drive's root). In both cases this is **not** the real save
folder, and the directory normally does not exist, so the writers fail
quietly (`crossAssertOutput` "SKS_DEBUG: ...").

| VA | off | Name | Signature (VERIFIED) | What it does | Reached in release? | Verdict |
|---|---|---|---|---|---|---|
| 0x00646df0 | 0x246DF0 | debugSave | `int(int slot, void* buf, size_t n)` -> 0/-1 | `<prefix>/FFX_Data/GameData/PS3Data/saves/%02d.SAV`, `fopen "wb"`, clears +0x64f4, `TkCRC(0x64b8, buf+0x40)` into +0x1a and +0x64f4, fwrite | via `debugSave_Thunk_006492e0` from `TkMenuCtrlSaveLoad` (the PS2 memory-card menu, state 0x25, pad bits L2/L1) | writes a loose file outside the save folder; **do not expose** (file write; Fahrenheit rule) |
| 0x00646b30 | 0x246B30 | debugLoad_00646b30 | `int(int slot, void* buf, size_t n)` | same path `%02d.SAV`, `fopen "rb"`, fread, `SaveDataCheckCrc` | `debugLoad_Thunk_006492b0` from `TkMenuCtrlSaveLoad` | reads only; file absent on PC; useless |
| 0x00646ca0 | 0x246CA0 | debugLoad_00646ca0 | `int(int n, void* buf, size_t sz)` | path `<saves>/%d` (no ext), fread + CRC check | `debugSavedataLoadAll` | see next row |
| 0x006492c0 | 0x2492C0 | debugSavedataLoadAll | `int(int n)` | `debugLoad_00646ca0(n, g_SaveFileBuffer, 0x6900)` | **live**: `HandleSaveDataScreen` 0x0081ff80. In screen state 1, a Square press (pad bit 0x80) sets `g_SaveDebugSlot` 0x00c494cc (init -1) to 0; then for n = 0..199 it loads `<saves>/n` and on success **`SaveDataToSave()` into the player's real slot n** | **DANGEROUS** if those loose files exist (overwrites all 200 real saves). With no files every load fails and nothing is written (INFERRED normal case). Never create those files; never set 0x00c494cc |
| 0x00649040 | 0x249040 | debugLoadForViewer | `int(int n, void* buf, size_t sz)` | `<prefix>/FFX_Data/GameData/PS3Data/savesforviewer/%d`, CRT fopen "rb", fread | only from `TkDebugLoadViewerSave` | the VBF **does contain** `ffx_data/gamedata/ps3data/savesforviewer/1..110` (VERIFIED, name table) but CRT fopen cannot read the VBF, so it fails on a stock install |
| 0x008b55e0 | 0x4B55E0 | TkDebugLoadViewerSave | `void(int n)` | if n != last (0x00c5a038): `debugLoadForViewer(n, g_SaveFileBuffer, 0x6900)`; on 0: copies block +0x40 (0x68c0) to `g_SaveData` (`MsGetSaveEventAddress()`), n 0x97: map 0xf6 + `g_SaveScenario` 0x7c15; `AtelSetEventJump(map, entrance)` from save +0xb8/+0xba | only from `BasicDebugTweakUtil_handleInput` (PC tweak panel; that agent's area) | **Opportunity**: "load developer save N" without touching save files: read VBF `.../savesforviewer/N` yourself into a 0x6900 buffer, then do the same copy + `AtelSetEventJump`. It replaces the in-memory game state (warn: saving afterwards overwrites the player's slot with dev state). Needs control bit 0x80 or FFXIsDebug for the jump |
| 0x006484d0 | 0x2484D0 | SaveDataPreDebugSave | `void(int)` (arg passed to `SaveDataManager_instance`) | `SaveDataManager_preDebugSave` 0x006f2410: memcpy the manager's buffer (+0x350, size +0x354) to the snapshot 0x00ce81f0 | **live**: `TkInitScene` 0x0088e070 on every map change with a save point state (`TkBackupEventJump`, `TkMenuPreSave`, then this) | release autosave snapshot; never call |
| 0x006f0510 | 0x2F0510 | SaveDataManager_debugSave_Internal_6f0650 | `void(size_t n, void* buf)` (Fh internal handle, same signature) | `SaveDataWriteCrc(buf)`, `SetUpDefaultSaveFolder`, `_wfopen("<SaveFolder>ffx_000", "wb")`, fwrite | **live**: the PC autosave writer (`0x006f1c40` <- `AutoSave_DebugSave_00647e10` <- 0x00869f59 when `gNeedAutoSave` 0x023cbebc) | **DANGEROUS: writes the player's real autosave `ffx_000`**. Despite the "debug" name it is the shipping autosave; Fahrenheit hooks it (RT save impl). Never call |
| 0x00647e10 | 0x247E10 | AutoSave_DebugSave_00647e10 | `int(int)` -> 0/-1 | `SaveDataManager_instance`, `0x006f1c40` (skips when the snapshot's +0x18 == 0x1b), which calls the writer above with the snapshot | live (autosave) | **DANGEROUS**, never call |

Fahrenheit rule conflict summary: everything that writes is either the real
autosave (must stay untouched) or a loose-file writer in a wrong location; no
debug save/load function should be exposed as is. The only useful revival is
the viewer-save *load into memory*, reimplemented from the VBF data.

---------------------------------------------------------------------------

## 6. host0: readers and writers in battle (cannot work without dev files)

How `host0:` works on PC (VERIFIED, `sceOpen` 0x0062fa30): the name minus its
first 7 chars is appended to `"/ffx_ps2/"`, unified (`fiosUnifyFilename`),
and opened only if `fiosIsFileExist`; `sceWrite` 0x0062fdc0 is a stub
(`dbgPrintf("Virtuos sceWrite")`, writes nothing). So every PS2 debug *writer*
is a no-op on PC, and readers find only what the VBF holds under
`ffx_ps2/...` (`host0:/ffx/master/...` is the real data path the shipping
kernel loader `getExcelBinSize` uses).

| Function | VA | Path(s) | Status on PC |
|---|---|---|---|
| MsDebugInfoRead | 0x007826b0 (off 0x3826B0, `void()`) | `sprintf("host0:%s", "btlenv.txt")` -> sceOpen drops 7 chars -> `/ffx_ps2/tlenv.txt` (the PS2 string had no `/`); fallback `host0:/home/<Sg_GetUserName>/btlenv.txt` -> `/ffx_ps2/home/<user>/btlenv.txt`; parses the [key] list of section 1 (0x007ce450) into the toggles, [scene] into `g_BtlMemWord`, [country]/[pal]/[asia] language/video | **no callers at all** (byte scan) and FFXIsDebug-gated; files not in the VBF. Dead. Its first act is `0x023113e0 = FFXGetVideoMode()` |
| MsDebugEnvSave | 0x007cd800 (`void(char* path)`) | writes the same keys to `[0x02311400]` (filled only by the dead MsDebugInfoRead, so empty) via sceOpen/sceWrite | sceWrite stub: writes nothing. Called by the Config/Console SAVE buttons only. Do not call (path buffer empty) |
| MsEnvInit | 0x00782770 | `host0:/ffx/proj/battle/<cc>/cddata/cdrom_cd.fnd` / `_lc.fnd` -> `PcFileTblSet(path, 0xd)` | runs at boot and in SREL; only with FFXIsDebug and 0x0112a8f6 in 1..2. The VBF has these files for `jp` only. Leave alone |
| MsGetPlySaveName | 0x007dcec0 (`void(char* out, char* name)`) | `host0:/home/%s/program/mk/%s/mot_%s.src` (user "masaki") or `host0:/home/%s/battle/<cc>/btl/%s/mot_%s.src` (`mot_w_` underwater) | path builder for CHR1/CHR2 "PLY SV" -> `MsPlySave` -> sceWrite stub: no-op |
| MsSetFileName | 0x007dce80 (`void(char* out, u32 mon)`) | `host0:/home/%s/battle/<cc>/mon/_m%03d/mt%03d.src` | "MON SV" -> `MsMonSave`: no-op |
| MsDebugBattlePosInit | 0x007c90f0 (`void(char* out, char* a, char* b)`) | `host0:/home/%s/battle/<cc>/btl/<name>/...` or `host0:/ffx/proj/battle/<cc>/btl/<name>/...` | BattlePos window file read: absent ("not found") |
| Ms_Gui_CameraProc save | 0x007d44a0 | `host0:%s%s` (`MsDebugScenePath_007d70d0` root 0x00c447a0) | `MsFileOut` 0x007c8b50 -> sceWrite stub: no-op |

`Sg_GetUserName` is only set from `argv[1]` when g_FFXIsDebug is set at start
(`Sg_InitArgs` 0x00820310), so `<user>` is empty in release.

---------------------------------------------------------------------------

## 7. g_FFXIsDebug and other gates touched here

- `g_FFXIsDebug` i8 at 0x0133c910 (off 0xF3C910); `FFXIsDebug` 0x00887c50
  (`movsx eax, byte [0x0133c910]; ret`), `isDebugMode` 0x00822550 (AL).
  Written at start-up (0x00820680, 0x00822490) and by 0x00887c80. 21 functions
  call FFXIsDebug and ~30 read the byte directly (data_refs); the battle ones
  (VERIFIED): `MsBattleDraw` (jumps to a bare ret: nothing), `MsBattleExe`
  (sound project base `/usr/local/ffx/<cc>/ps2/sound` -> Sqs RPC, risk),
  `MsBattleInit` (`MsSetSaveParty`, `MsBtlPosInit`, `MsDebugSetScene`),
  `MsDebugInfoRead` (dead), `MsEnvInit` (file table swap if 0x0112a8f6),
  `MsBtlReadSetScene` (result unused), `MsBattleProcess` (pad combos),
  `MsDebugBattlePosInit`, `MsSetFileName`, `MsGetPlySaveName` (user name in
  paths), and `AtelSetEventJump`/`AtelSetEventJump2` (lets map jumps happen
  without the script's permission bit). The full list belongs to the
  debug-flag agent. Nothing in sections 1-2 needs it: **leave it 0**.
- `TODebugGetPadPrintFlag` 0x008abd20: constant 0 (section 3).
- `g_GsDefDrawEnv2` 0x00b5ec4f: .rdata constant 1 (enables the camera-sleep
  call each frame).
- `g_BtlLabelLock` 0x0112ca2c: blocks label battles when 1.
- PC tweak panels (DebugMenuManager agent's area; noted for cross-reference):
  `BtlDebugTweakUtil` ("Battle Debug Information": ro Battle Map Name, Map
  Index = 0x00c44248, Group Number = 0x01139e44, "player never die" =
  0x0113338c, "Enable Mon Input" = 0x0112a8fa, enter = `enterBattle`);
  `BtlChrParamConfigTweakUitl` (Player, Ply/Mon Invincible, Ply/Mon HP1, HP,
  MP, SP, the 8 stats, LoveParam via `getBtlchrParam`/`setBtlchrParam`);
  `GameParamConfigTweakUitl` ("FullItem", "FullCommand", "FullLevel",
  "FullOverdrive", "FullGill", blitzball, Al Bhed dictionary, battle count,
  play time, names, chocobo, PlayGo). Their input handlers (0x006b81d0,
  0x006b7af0, 0x006b8ce0) have no direct references (virtual calls).

## 8. Quick risk list

- DANGEROUS: `FFEscMenu_onDebugFill` choice 1 (Steam achievements 0x18/0x19);
  `SaveDataManager_debugSave_Internal`/`AutoSave_DebugSave` (real autosave);
  `HandleSaveDataScreen`'s `g_SaveDebugSlot` loop (all 200 real slots, if
  loose files exist); `debugSave` (loose file write); 0x0112a8f6 with
  FFXIsDebug (battle file table); g_FFXIsDebug itself.
- Save-state changing (warn in UI, "affects your next save"): FullItem,
  AllItemsOne, ClearItems, AddItem, FullGill/AddGill, FullCommand,
  FullMember, FullSummon (story), SetAllSP, FullStats, AbilityOne (overdrive
  counters), AddWeapon, ThrowAway Parm (discards results), the viewer-save
  load.
- Battle-stalling toggles: CTB pause, DVD stop mag (effect hold), Skip
  Command (untested).
- Thread rule: everything through the PreUpdate/PostUpdate queue.
