# Facts: debug display machinery and the PC tweak overlays (FFX.exe, Steam HD)

Research sheet for the Debug Revival mod (yuta.debugrevival). Scope: the
DebugMenuManager and the *TweakUtil panels, every g_FFXIsDebug reader, the
debug text/primitive paths, the debug free camera and op/VFX debug, and the
Gu_/Gui_ toolkit with the rc map tool. The battle windows (Ms_Gui_*) and
battle cheats only get pointers here; the battle sheet covers them.

Conventions:
- VA = absolute (image base 0x00400000). "off" = FhCallLocation offset = VA - 0x400000.
- VERIFIED = I saw the code (a Ghidra dump of FFX.exe (ffx-decomp's tools/ghidra/dump.sh), objdump of FFX.exe, or
  raw bytes). INFERRED = reasoning from names, layout or partial reads.
- "FhCall" = the handle in `fahrenheit/src/core/call.cs` / `call_1.g.cs`. A
  `d_UnknownFn` handle gives only the location; declare your own delegate.
- Helper scripts: small local wrappers around objdump, the dump and a .text reference scan (not kept).

---------------------------------------------------------------------------

## 0. Summary (read this first)

1. **The ten tweak panels are complete in the release build and render every
   frame, but nothing can turn them on.** (VERIFIED) All ten utilities are
   created (graphicInitialize) and initialised (PhyreScene_bootInit). Their
   `render` is called every frame by PhyreScene_render. The **only switch,
   DebugMenuManager_handleInput 0x006b4a90 (F9 or Select+Square), and every
   per-panel handleInput have no caller and no pointer anywhere in the
   image.** To revive them:
   - show a panel: call `DebugMenuManager_setMode(g_DebugMenuManager; mode)`
     (0x006b4d10);
   - make it respond: call that panel's dead `handleInput(this; PApplication*)`
     once a frame on the game thread.
   They draw through `TextUtil`, i.e. a Phyre PBitmapFontText with the tuffy
   font and the PhyreText shader, both in the VBF as d3d11 assets. So they
   should appear on the PC renderer (INFERRED, see 1.6).
2. **Dangerous panel items** (details in 1.4):
   - BasicDebug item 0xc **resets all Steam stats AND achievements**.
   - BasicDebug item 9 + Enter **replaces the in-memory save with a developer
     "viewer save" and warps**.
   - BasicDebug item 0xb disables the save CRC check.
   - GameParamConfig: all items write save data (items, gil, abilities, SP,
     blitzball, names, play time).
   - BtlChrParamConfig: writes battle actor stats and g_PlySave/affection
     **every frame** while the cursor is below line 0.
   - BtlDebug item 4 + Enter forces a battle.
   - MapSwitch + Enter does a map jump.
3. **g_FFXIsDebug (0x0133c910) must NOT be set globally without
   preparation.** (VERIFIED) Sg_MainDebug then calls Sg_DbDraw every frame.
   That writes through the debug line buffer pointer 0x013032d0, which is
   NULL in release because Sg_DbInitAll only allocates at boot with the flag
   set. **The game crashes on the first frame.** The flag also has gameplay
   effects:
   - Sg_ControlMain in the field: L1 = walk through walls, L2 = alternate
     exec frames, L1+R1 = game speed or single step;
   - AtelSetEventJump/2 honour map jumps they normally refuse;
   - a New Game starts with g_SaveScenario = 5;
   - MsBattleProcess has a debug "start battle" pad combo;
   - the battle sound project base is changed;
   - and more (section 2).

   TkIsDebug (0x00887d10) is `xor eax,eax; ret`: the flag does **not** enable
   any script debug native.
4. **All PS2 debug drawing is dead on PC** (VERIFIED). It goes into GS/GIF
   packets that end in the stubbed sceDmaSend. This covers Sg_Db* lines, the
   TkDebugFont, the Gu_ toolkit, the rc map tool, the op/VFX digit display
   and TkDrawDebug (an empty `ret`). The TODaps buffer is the exception:
   TO* drawing does reach the PC via graphicFontDraw, but its packet builder
   TODapsMakePkt has no caller (revivable with one hook, 3.3). The dbgPrintf
   stub (`ret`, 459 callers) is the best cheap log source (3.6).
5. **The PC free camera (g_FreeCamera) is fully live, only its two keys are
   hard-unbound.** Patching one binding word revives it (4.1). The PS2 Gu
   debug GUI must be rebuilt in ImGui; it has no init caller, no input, no
   PC drawing, and its init hangs (5).

---------------------------------------------------------------------------

## 1. DebugMenuManager and the *TweakUtil panels

### 1.1 DebugMenuManager

| Item | VA / off | Conv / signature | Evidence / FhCall |
|---|---|---|---|
| `g_DebugMenuManager` | 0x00cccab0 / 0x8CCAB0 | ptr to 8-byte object: +0 vtable 0x00b48630 (one slot: scalar deleting dtor 0x006b4920), **+4 current mode (int)**, 10 = none | VERIFIED (CreateInstance writes +4 = 10) |
| `DebugMenuManager_CreateInstance` | 0x006b4950 / 0x2B4950 | cdecl() void; operator_new(8) once | VERIFIED; called by graphicInitialize 0x006419d0 (unconditional); FhCall none |
| `DebugMenuManager_Destroy` | 0x006b4990 / 0x2B4990 | cdecl() | VERIFIED |
| `DebugMenuManager_GetInstance` | 0x006b49b0 / 0x2B49B0 | cdecl() -> eax = g_DebugMenuManager | VERIFIED; FhCall none |
| `DebugMenuManager_disableTweaks_006b49c0` | 0x006b49c0 / 0x2B49C0 | cdecl(), ecx unused | VERIFIED; calls setEnabled(0) on the first **eight** utils only (not BtlChrParam, not GameParam); caller PhyreScene_softResetScene 0x0066fad0 (which itself has no static caller); FhCall none |
| `DebugMenuManager_hideAll` (0x006b4a70, unnamed) | 0x006b4a70 / 0x2B4A70 | thiscall(dmm), plain ret: `push 0xa; call setMode; ret` | VERIFIED bytes; caller only GameParam handleInput (dead) |
| `DebugMenuManager_getCurrentDebugMenuType` | 0x006b4a80 / 0x2B4A80 | thiscall(dmm) -> eax = [ecx+4], plain ret | VERIFIED bytes (`8b 41 04 c3`); reader TextureAnimation_Animate (mode 5) |
| **`DebugMenuManager_handleInput`** (0x006b4a90, unnamed) | 0x006b4a90 / 0x2B4A90 | thiscall(dmm; PApplication* in) ret 4 | VERIFIED; **0 callers, 0 abs refs (full .text scan)**; FhCall `FUN_002B4A90_000C80E0` (d_UnknownFn) |
| **`DebugMenuManager_setMode`** (0x006b4d10, unnamed) | 0x006b4d10 / 0x2B4D10 | **thiscall(dmm; int mode) ret 4** | VERIFIED disassembly; +4 = mode % 11; setEnabled(0) on all ten, then setEnabled(1) on the one for the mode; FhCall none |

handleInput's combo (VERIFIED code, key meaning INFERRED from the
docs/formats/platform.md key table, Phyre key = binding - 0x1c, pad button
= binding - 0xb). It only acts while none of pad ids 0xf/0x10/0x11/0x12
(L1/R1/L2/R2) are down, and then when
- binding 0x49 (Phyre key 45 = **F9**) is pressed (`PInputMapper` consume,
  0x00626a00), or
- pad ids 0x13 (Select) and 0xb (Square) are held. It consumes them.

It steps the mode (`(+4 + 1) % 11`) and runs the same switch as setMode.

**Mode -> panel** (VERIFIED from setMode's register flow, each setEnabled's
ecx is the matching getter's eax):

| Mode | Panel | Instance global | Getter VA / off | setEnabled VA (thiscall(this; u8 on) ret 4) |
|---|---|---|---|---|
| 0 | CharacterLightingTweakUtil | 0x00ccca88 / 0x8CCA88 | 0x006a6e40 / 0x2A6E40 | 0x006aa5f0 |
| 1 | PostProcessTweakUtil | 0x00ccc89c / 0x8CC89C | 0x00692bf0 / 0x292BF0 | 0x006984f0 (FhCall `FUN_002984F0_000AE1A0`) |
| 2 | CharacterSwitchTweakUtil | 0x00cccac4 / 0x8CCAC4 | 0x006b6330 / 0x2B6330 | 0x006bb670 |
| 3 | MapSwitchTweakUtil | 0x00cccac8 / 0x8CCAC8 | 0x006b6360 / 0x2B6360 | 0x006bb880 |
| 4 | CharacterAnimationTweakUtil | 0x00cccadc / 0x8CCADC | 0x006b6320 / 0x2B6320 | 0x006bb5f0 (FhCall `FUN_002BB5F0_000F2420`) |
| 5 | CharacterTextureAnimationTweakUtil | 0x00cccad4 / 0x8CCAD4 | 0x006b6340 / 0x2B6340 | 0x006bb6d0 (FhCall `FUN_002BB6D0_000F2500`) |
| 6 | BasicDebugTweakUtil | 0x00cccad0 / 0x8CCAD0 | 0x006b62f0 / 0x2B62F0 | 0x006baf60 |
| 7 | BtlDebugTweakUtil | 0x00cccad8 / 0x8CCAD8 | 0x006b6310 / 0x2B6310 | **0x006bb480** (ffx.sym calls it `MapNameText_Show_006bb480`; it is BtlDebug's setEnabled: same +0x110/+0x114/+0x1c4 fields as BtlDebugTweakUtil_initialize/render) |
| 8 | BtlChrParamConfigTweakUitl | 0x00cccae0 / 0x8CCAE0 | 0x006b6300 / 0x2B6300 | 0x006bb3b0 (FhCall `FUN_002BB3B0_000F21F0`) |
| 9 | GameParamConfigTweakUitl | 0x00cccae4 / 0x8CCAE4 | 0x006b6350 / 0x2B6350 | 0x006bb7c0 |
| 10 | none | | | |

The getters are `mov eax,[global]; ret` (cdecl, VERIFIED bytes); none has a
FhCall handle. `g_ViewerManager` 0x00cccac0 (getter 0x006b6370) is used by
several panels: +0x1c viewer character name, +0x30 viewer map name
("grid00" is the model-viewer map), +0x50 "UI enable" byte (VERIFIED reads).

### 1.2 Lifetime: created, initialised, rendered every frame (VERIFIED)

- **Created:** graphicInitialize 0x006419d0 runs DebugMenuManager_CreateInstance
  and the ten create functions unconditionally (0x006a6d50, 0x00692b10,
  0x006b5eb0, 0x006b6040, 0x006b5f30, 0x006b5e30, 0x006b5cb0, 0x006b5db0,
  0x006b5d30, 0x006b5fc0; call sites 0x00641a71..0x00641ae4).
- **Initialised:** PhyreScene_bootInit 0x00657a50 (0x00657b0d..0x00657b90)
  initialises nine of them: CharacterLighting_initialize, CL tweak
  initialize, CharSwitch, MapSwitch (`DebugMapList_init_006b9e20`),
  CharTexAnim, CharAnim, Basic, BtlDebug, BtlChrParam, GameParam. PostProcess
  is initialised by `PostProcessTweakUtil_initWithSceneContext` 0x006938d0
  from PhyreScene_initPostProcess 0x006656e0. Each initialize does
  TextUtil_init (async cluster load). On success it sets its "init ok" byte
  and appends the lines; it ends with TextUtil_setEnabled(0).
- **Rendered:** PhyreScene_render 0x00668930 near its end, every frame on the
  normal render path (after DrawNormalUI, before VideoText_render), in this
  order: CL, PP, MapSwitch, CharSwitch, CharTexAnim, CharAnim, BtlDebug,
  Basic, BtlChrParam, GameParam.
  - Arguments: ctx = scene+0xd0 and fps = f32 PApplication+0x30. CL also
    gets scene+0xdc+4 (or 0).
  - **The only gate is each util's "shown" byte.** There is no
    g_FFXIsDebug / isDebugMode test on the render path.
- **handleInput:** none is called in release (VERIFIED: 0 rel32 and 0
  absolute refs, full .text scan, for 0x006b7630, 0x006b7af0, 0x006b81d0,
  0x006b85c0, 0x006b8820, 0x006b8a20, 0x006b8ce0, 0x006b9160, 0x00693720,
  0x006a8e40, 0x006b4a90). On Switch they were driven per frame; here the
  calls were removed.

### 1.3 Per-panel signatures and fields

The common signatures below were VERIFIED from the disassembly (ret N, ecx
use).
- `render`: thiscall(this; void* ctx, f32 fps) **ret 8**. It does nothing
  unless shown and ctx != 0. It sprintf's the title ("... %.2f fps"), then
  TextUtil_setTitleText and TextUtil_render.
  - CharacterLighting render 0x006a9f00: thiscall(this; ctx, x, f32 fps) **ret 0xc**.
- `handleInput`: thiscall(this; PApplication* in) **ret 4**. `in` =
  Phyre_PFramework_PApplication_GetApplication() 0x00623b90 (cdecl() ->
  eax). VERIFIED: Virtuos_InputManager_updateInput passes that object as
  ecx to Input_KeyHeld; the handlers use [in+0x2b8] devices.
- `setEnabled`: thiscall(this; u8 on) ret 4. shown = on && initOk; on
  enable it calls TextUtil_resize and refreshes the lines.
- `initialize`: thiscall(this) plain ret (VERIFIED Basic 0x006b9360, CL 0x006a91e0).

| Panel | render VA | handleInput VA (dead) | shown byte | init-ok byte | cursor field | Lines |
|---|---|---|---|---|---|---|
| CharacterLighting | 0x006a9f00 | 0x006a8e40 (FhCall `FUN_002A8E40_000CB430`) | +0xc8 | +0 | (selectNeighborItem 0x006aa410) | Light index, Light enable, Light intensity, Light direction pitch, Light direction yaw, Env ambient color, AtelLight0-3, Auto reset intensity |
| PostProcess | 0x006953a0 | 0x00693720 (FhCall `FUN_00293720_000A9170`) | +0x180 | +4 | +0x17c (0..0x15) | Fog near/far/curve/limit/color RGB/enable, Glow upper/lower threshold, gain, spread, enable, HDAO Level, Bright Level, UnSharp Mask threshold/intensity/blur distance/test/enable, SMAA Level, ColorCorrection enable |
| CharacterSwitch | 0x006ba430 | 0x006b8820 (FhCall `FUN_002B8820_000EF390`) | +0xd4 | +0 | +0xd0 | 7 lines "k/c/m/n/s/w/f" + 3-digit number (model prefixes) |
| MapSwitch | 0x006ba610 | 0x006b9160 (FhCall `FUN_002B9160_000EFEA0`) | +0x1f4 | +0 | | ~2-letter map prefixes (strings 0x00b48700..) + "%02d" |
| CharacterAnimation | 0x006ba390 | 0x006b85c0 | +0xc0 | +0x14 | | Motion Mode, Loop, Next, Animation Count |
| CharacterTextureAnimation | 0x006ba4d0 | 0x006b8a20 | +0xd0 | +0x1c | | Debug Output, Material Name, Group ID, Animation ID, Current Frame, AutoPlay |
| BasicDebug | 0x006ba1a0 | 0x006b7630 | +0x100 | +0x28 | +0xfc | 13 lines, table 1.4 |
| BtlDebug | 0x006ba2e0 | 0x006b81d0 | +0x1c4 | +0x110 | +0x1c0 | (ro) Battle Map Name, Map Index, Group Number, player never die, (line 4: enter battle), Enable Mon Input |
| BtlChrParamConfig | 0x006ba240 | 0x006b7af0 | +0xfc | +0x1c | +0xf8 | 17 lines, table 1.4 |
| GameParamConfig | 0x006ba570 | 0x006b8ce0 | +0xe8 | +0x10 | +0xe4 | 15 lines, table 1.4 |

FhCall for the render functions: none, except TextUtil_render 0x006f65b0
(`FUN_002F65B0_00115920`, d_UnknownFn). FhCall for initialize: none.

**Input each handleInput reads** (VERIFIED calls; key names INFERRED from
the platform.md tables):
- Previous line: binding 0x24 (Phyre key 8 = **I**), or pad id 0x17 (d-pad Up), or
  right-stick Y < -0.5 (`PApplication_isKeyDown(3,0)`).
- Next line: binding 0x26 (**K**), pad 0x19 (Down), stick Y > 0.5.
- Decrement: binding 0x25 (**J**) held, pad 0x1a (Left), stick X (axis 2) < -0.5.
- Increment: binding 0x27 (**L**) held, pad 0x18 (Right), stick X > 0.5.
- Activate: binding 0x4d (key 49 = **Enter**) or pad 0xd (Circle).

The pad index argument is 1 for the d-pad and 0 for the rest. The panels use
the shared PApplication input mapper, so these keys and buttons also reach
the game. Some panels add a repeat divider (BtlChrParam counter 0x00cccabc:
every 4th frame for lines < 5; GameParam 0x00cccaec/0x00cccaf0).

Extra gates inside handleInput (VERIFIED):
- **CharacterSwitch, MapSwitch, CharacterAnimation, CharacterTextureAnimation
  return unless `isDebugMode()`** (g_FFXIsDebug). CharacterSwitch also
  compares ViewerManager+0x30 with "grid00".
- CharTexAnim's setEnabled does nothing at all without isDebugMode.
- CharAnim's updateAnimation 0x006bc130 needs isDebugMode.

These four are viewer tools (grid00 model-viewer map).

### 1.4 What each item does

**BasicDebugTweakUtil** (handleInput 0x006b7630, getLineText 0x006b69a0):

| # | Line | Action (VERIFIED code) | Risk |
|---|---|---|---|
| 0 | Character Name: | ro, ViewerManager+0x1c | none |
| 1 | Map Name: | ro | none |
| 2 | Character Polygon Count: | ro, ViewerManager+0x30 | none |
| 3 | Battle Enable: | toggles `g_BattleEnable` 0x00c421cc via setBattleEnable 0x00782f70 (read by MsBattleEncountExe: random encounters on/off) | gameplay only |
| 4 | ClothSystem Enable: | display only (no case in handleInput) | none |
| 5 | UI Enable: | toggles ViewerManager+0x50, then Guide_SetEnabled(same) 0x0091f640 (g_GuideEnabled 0x00c5e478, the minimap) | low |
| 6 | MiniMap Enable: | Guide_SetEnabled toggle | low |
| 7 | Show 4:3 Frame: | 0x01136fc0 cycles 0 off / 1 on (PhyreScene_render draws PostProcessManager_renderDebug4By3Frame 0x006956d0) / 2 "zoom camera" (Cam_Apply 0x007bc980 changes the FOV) | visual only |
| 8 | Thunder Plain Treasure Enable: | `g_Use200ThunderFile` 0x01a85ba4 (SetGlobal_01a85ba4 0x00a43210). PcFileName_00a42c00 then loads **200thunder_kami0400.ebp** instead of kami0400.ebp (the file is in the VBF) | changes the Thunder Plains event script on next load. **Medium**: a debug variant of a real event, story or reward flags may change |
| 9 | Game Section: | value 1..0x71. **Enter (key 49 or pad Circle) calls TkDebugLoadViewerSave 0x008b55e0(n)**: debugLoadForViewer reads `/FFX_Data/GameData/PS3Data/savesforviewer/<n>` (about 110 files shipped in the VBF), **copies it over g_SaveData and jumps to its map** | **DANGEROUS**: replaces the running game's state; saving afterwards overwrites the slot. A useful "chapter select" if wrapped with a confirm |
| 10 | Saveload fake slot : | 1..99 in +0x18; **nothing reads it** (only the getter's callers touch the instance) | none (dead) |
| 11 | Disable CRC check: | `g_SaveData_CrcCheckOff` 0x00ccb9a4 = 0 (Left) / 1 (Right). SaveDataCheckCrc then accepts any save without TkCRC | **Medium**: lets corrupt or edited saves load |
| 12 | Achievements Reset : | toggles +0x14. **When it becomes 1 and g_SteamReady (0x00cc9cf5): AchievementManager_GetInstance; StatsAndAchievements_ResetAll 0x006f1420 = `SteamUserStats()->ResetAllStats(true)`** (vtable +0x54, push 1 = achievements too; VERIFIED bytes) | **DANGEROUS: wipes the user's Steam stats and ALL achievements. Never expose it; block it if the mod ever calls this handleInput** (e.g. patch out or skip line 12) |

BasicDebugTweakUtil_setEnabled 0x006baf60 also reads
`/ffx/proj/event/Section_GoThrough.txt` through Sg_PcRead into a section
vector. The file is not in the VBF, so it reads nothing; no write (VERIFIED
code, INFERRED absence via the VBF list).

**GameParamConfigTweakUitl** (handleInput 0x006b8ce0, getLineText 0x006b72e0;
each frame it also copies getBattleCount and getPlayTime into the panel). Left
and Right both trigger the action:

| # | Line | Action | Risk |
|---|---|---|---|
| 0 | FullItem: | MsDebugFullItem 0x007849d0: every item x99 into g_SaveItemIds/Counts | **save data** |
| 1 | FullGill: | MsDebugFullGill 0x007849c0: g_SaveGil = 999999999 | **save data** |
| 2 | FullCommand: | MsDebugFullCommand 0x007848b0: all abilities from ply_data | **save data** |
| 3 | FullLevel: | MsDebugSetAllSP_00784ae0: every ply_save SP byte (+0x3b) = 0x62 | **save data** |
| 4 | FullOverdrive: | toggles g_BtlLeftWindowReset 0x011333e8 (0x00790420; MsCheckLeftWindow restores +0x5bc from +0x5bd) | battle only |
| 5 | Full Blitz: | 0x007845b0: 0x78 words at 0x0112dc7c = 0xfeffff7f, 0x3c bytes at 0x0112e00e = 5 (blitzball data) | **save data** |
| 6 | Blitz Cheat: | toggles 0x00cccacc; **no reader outside the panel** (dead) | none |
| 7 | Full Albhed Dictionary: | StepSwitch_018763e0 0x00906740 steps 0x018763e0 through -1/0/1. Read by TOConvAlbhedWord (Al Bhed text translation override) | display |
| 8 | Full Arena Localization: | setFullArenaLocalizationEnable 0x00a431f0 toggles g_FullArenaLocalization 0x01a85ba8. PcFileName loads **full_nagi0700.ebp** (in the VBF) | event script swap, **medium** |
| 9 | Battle Count: | ro | none |
| 10 | PLAYTIME: | +-600 via setPlayTime, clamped to 3599999 | **save data** |
| 11 | Change Name: Tidus | Enter: hideAll + FlashPlayCancelSFX (ffx.sym name; probably opens name entry, INFERRED) | low |
| 12 | Reset Name: All Members | Enter: hideAll + 0x00787100: g_SaveChrNames reloaded from ply_save, debugSetRikkuName past scenario 99, 0x008c5370 past 0x608 | **save data** |
| 13 | Chocobo Game Debug Enable: | every 4th press toggles g_RenderFlag 0x00cccab8 (ReturnGlobalSx8_006bc960). PcFileName then loads **dbg_nagi0000.ebp** (in the VBF) instead of nagi0000.ebp | debug event script, **medium** |
| 14 | PlayGo download speed: | ro | none |

**BtlChrParamConfigTweakUitl** (handleInput 0x006b7af0): line 0 selects
actor 0..7 (getBtlchrParam 0x00787230 into the edit buffer +8..). The lines:
- 1 Ply Invincible: g_BtlDebugNoDamageParty via 0x007c86d0.
- 2 Mon Invincible: enableMonsterInvincible 0x007c86c0.
- 3 Ply HP1: MsDebugWeakParty_00784c80.
- 4 Mon HP1: MsDebugWeakMonsters_00784bc0.
- 5-15 edit the buffer: HP 0..99999, MP 0..999, SP 0..99, Strength,
  Defence, Magic, Magic Defence, Agility, Luck, Evasion, Accuracy (bytes).
- 16 LoveParam: save-event +0x2c + 4i (affection) via 0x007870d0/0x007871b0.

**Every handleInput call with the cursor on a line > 0** runs
`setBtlchrParam(actor, buffer)` 0x007873d0. That writes battle actor +0x5d0
HP, +0x5d4 MP and +0x5a8..+0x5af stats (in battle only). It also writes
`0x00787620`: g_PlySave+0x3b (0x01132097 + 0x94 i, the save SP byte).
- Original bug (VERIFIED): +0x5ad gets buffer[0x10] instead of [0x11].
- The buffer is refreshed only on setEnabled or a line-0 change. **Risk:**
  continuous stat overwrite while the cursor rests on line > 0, a stale
  buffer after entering a new battle, and affection/SP are save data. Battle
  sheet covers the semantics.

**BtlDebugTweakUtil** (handleInput 0x006b81d0):
- Lines 1/2 step the battle map index / group (0x007c6df0 / 0x007c6dc0;
  globals 0x00c44248 / 0x01139e48).
- Line 3: enable_playerNeverdie.
- Line 5: enableMonInput (pad-controlled monsters).
- **Line 4 + Enter: enterBattle 0x007c6d80** (forces the chosen battle). Risk:
  forced battle anywhere. Battle sheet.

Each frame it calls MsGetBattleName into the panel.

**MapSwitchTweakUtil** (handleInput 0x006b9160, isDebugMode-gated):
- Enter calls 0x006b6730, then **mapChange 0x00908100** (FhCall
  `FUN_00508100_003B8D20`). That looks the name up in the event list
  (0x025d5888 entries, count 0x01934ec8) and calls AtelSetEventJump
  0x0086fec0(map, 0).
- **The event list is filled only by initEventList 0x00907f50**
  (`/ffx/proj/event/header/eventid.bin`, which is in the VBF). Only
  Sg_MainInit calls it, and only with g_FFXIsDebug at boot. So in a normal
  run mapChange finds nothing.
- AtelSetEventJump also refuses unless the script context bit 7 or
  FFXIsDebug is set.
- Revival: call initEventList (cdecl(), FhCall `FUN_00507F50_003B8B70`) once,
  and do the jump from the game thread at a safe point. Risk: map warp
  (progression).

**CharacterSwitchTweakUtil** (0x006b8820): Enter runs 0x006b66d0, then
characterChange 0x008295e0(name) (FhCall `FUN_004295E0_002DA450`). It swaps
the viewer model; viewer/grid00 only (INFERRED: useless outside the viewer
map).

**CharacterAnimation / CharacterTextureAnimation**: they play viewer motions
on Sg_GetPlayer (Ch_SetSysMotion_ViewerAnimation) or step texture
animations (TextureAnimationInfoChar_*). CharTexAnim's setEnabled toggles
enableGameControlTextureAnimation. Visual only; isDebugMode-gated.

**CharacterLighting / PostProcess**: they adjust the selected value by +-1.0
(0x006ab180 CL, 0x00698ab0 PP). The values are character lighting
intensity/direction/ambient and fog/glow/HDAO/USM/SMAA/colour correction
parameters. Visual only; no file or save writes (VERIFIED callees).

### 1.5 Other dead PC debug handlers found nearby (pointers)

- `0x006dd970` (the DebugMemoryInfo handler of Instance_006dc800's +0x110
  object, no callers). Its F-key combos call dumpers 0x006dd4b0, 0x006dce50,
  0x006dd3b0, 0x006dcfe0. **They fopen("w") files `WIN32_<tag>_<time>.csv`
  in the working dir**, plus "DebugMemoryInfo clearred" (0x00630730).
  File writes: do not revive without a reason.
- `BootText_create_006ddc80` / `VideoText_render_006ddeb0`: another TextUtil
  overlay rendered from PhyreScene_render when its bytes +4/+5 are set
  (pc-ui.md).

### 1.6 Does the panel text reach the PC renderer?

- TextUtil_render 0x006f65b0 (thiscall(tu; ctx) ret 4) runs only when +0x80
  (enabled) and +0x10 (the PBitmapFontText node, set when the clusters
  load) are set. It then does Phyre_PRendering_PRenderer_beginScene(2), the
  Phyre draw calls (0x00a2aa30, 0x005bb2e0, 0x00a2a400) and endScene. This
  is the Phyre D3D11 renderer, not a PS2 stub (VERIFIED callees).
- TextUtil_init loads the clusters `/FFX_Data/GameData/PS3Data/Shaders/GCM/PhyreText.cgfx.phyre`
  and `.../fonts/GCM/tuffy.fgen.phyre` (strings at 0x00b4be18/0x00b4be54,
  VERIFIED).
- The VBF holds `ps3data/shaders/d3d11/phyretext.fx.phyre` and
  `ps3data/fonts/d3d11/tuffy.fgen.phyre`, and the GCM->D3D11 path remap is
  the one every shader cluster uses (ui-draw.md).
- **So the panels should draw, as small text in the top-left area**
  (TextUtil_resize matrix). INFERRED: not seen running. The text is plain
  ASCII; the selected line has a ">" prefix (0x00b48250 ">" vs 0x00b20b30 " ").

### 1.7 Minimal revival recipe (tweak panels)

1. **Show:** on the game thread, `setMode(*(void**)0x00cccab0, mode)` with
   thiscall at 0x006b4d10 (ret 4). Mode 10 hides all.
   - Without the setMode routine, writing a panel's shown byte directly also
     works: render checks only it.
   - Prefer the routine; it does TextUtil_setEnabled, resize and the line
     refresh.
2. **Input:** each frame, on the game thread, call that panel's
   handleInput(thiscall; ecx = instance, arg = GetApplication()). Use a hook
   on PhyreScene_animate 0x00657140 (thiscall(scene) -> 0, no FhCall) or
   Fahrenheit's `FhCall.Sg_MainLoop` (internal, d_Sg_MainLoop(float)) or
   `FUN_00421e80_00206900` (Sg_FrameSync, typed d_FUN_00421e80_00206900(float)).
   - Do **not** call game functions from render_imgui: it runs on the render
     thread (Fahrenheit imgui.cs notes this).
   - Better: drive the panels from ImGui buttons that queue the actions to
     the game thread. That avoids the key conflicts (IJKL/Enter/d-pad also
     drive the game).
3. **Block the dangerous lines:** BasicDebug line 12 always (Steam reset); line
   9 + Enter, line 11, all GameParam and BtlChrParam lines behind
   "this edits your save" confirmations.
4. Viewer panels (2-5) additionally need isDebugMode, i.e. g_FFXIsDebug.
   That is not worth it outside the viewer map; skip them or reimplement in
   ImGui.

---------------------------------------------------------------------------

## 2. g_FFXIsDebug / isDebugMode / FFXIsDebug / TkIsDebug

### 2.1 The flag and its accessors (VERIFIED bytes)

| Item | VA / off | Code | FhCall |
|---|---|---|---|
| `g_FFXIsDebug` | 0x0133c910 / 0xF3C910 | u8 (BSS, 0 in release) | none (no Fahrenheit global) |
| `isDebugMode` | 0x00822550 / 0x422550 | `mov al,[0x133c910]; ret` (only AL defined) | none |
| `FFXIsDebug` | 0x00887c50 / 0x487C50 | `movsx eax,byte [0x133c910]; ret` | none |
| `FFXSetDebug` (0x00887c80, unnamed) | 0x00887c80 / 0x487C80 | cdecl(int on): if byte 0x00b5ec4f (`sceGsSetDefDrawEnv2`'s value, **1 in the image**) then flag = (on != 0); plain ret | `FUN_00487C80_00347130` (d_UnknownFn); 0 callers |
| `TkIsDebug` | 0x00887d10 / 0x487D10 | **`xor eax,eax; ret`: always 0** | none |
| `sceGsSetDefDrawEnv2` | 0x00887c60 | `movsx eax,byte [0x00b5ec4f]; ret` = 1 | |

Writers of g_FFXIsDebug (VERIFIED): 0x00820680 (an unused alternative main:
0), FFXSetDebug, and **0x00822490**. 0x00822490 is called by
asyncFFXInitialize 0x00636410 with (argc = PApplication+0x3a8, argv = +0x3a4):
- `argv[1]` equal to "debug" or "DEBUG" sets 1, else 0. Then Sg_MemSetup,
  Sg_InitArgs, Sg_MainInit.
- PApplication+0x3a4/+0x3a8 come from FFXApplication_initApplication's
  arguments. Those come from WinMain's CommandLineToArgvW (also checked for
  "_ECalm"). **So `FFX.exe debug` boots in debug mode** (INFERRED for the
  last link, FUN_00629a40 not traced).
- That path also sets the user name "debug" (Sg_InitArgs), boots into
  TkInitScene(0) instead of the normal start, and allocates the debug
  buffers. Not recommended: host paths, a different first scene.

### 2.2 Every reader (full .text scan for the absolute address and rel32 calls; VERIFIED list)

"Boot" = runs only during start-up (Sg_MainInit and its callees), so setting
the flag later changes nothing there.

| Function | VA | With the flag set | Runtime impact if set after boot |
|---|---|---|---|
| **Sg_MainDebug** | 0x008207f0 | every frame: **Sg_DbDraw**; then, unless capture hides the GUI and while 0x012fbbb3: Gu_Main(packet) + Sg_SaveResourceCheck; then Sg_Capture | **CRASH**: Sg_DbDraw writes `*[0x013032d0] = 0` and reads `*[0x013032cc]`, both NULL unless Sg_DbInitAll ran with the flag. Fix: call Sg_DbInitAll 0x00841440 (cdecl, FhCall `FUN_00441440_002F7890`) **after** setting the flag. Gu_Main over the empty window list is harmless (section 5). Sg_Capture idles while 0x01301804 = 0 |
| Sg_FrameSync | 0x00821e80 | Gu_VSyncMain each frame (Sg_MouseMain through the stub RPC, Sg_ClockTick) | harmless (INFERRED) |
| Sg_MainLoop | 0x00820ae0 | op_num_vdisp when 0x012fbbb8 is also set | nothing visible (op_num is `ret`) |
| **Sg_ControlMain** | 0x0082d070 | with LR-button mode 0 (default) on pad 0x023c44ac: **L1 held alone sets 0x023c44b4 (Sg_GetIgnoreID: walk through walls)**; L2 sets 0x013007b1 (exec frames from 0x013007b2); L1+R1 with a new press steps 0x00c496d4 (frames per game frame), below 0 single-step 0x013007b3 | **gameplay change in every field** |
| Sg_ControlCamera | 0x0083f4b0 | Select (0x100) + Sg_CaptureGetSelect (0x0130181c, 0 unless set by the Gu capture window) -> Sg_CaptureEnable | idle by default |
| **AtelSetEventJump** | 0x0086fec0 | map jumps proceed even when the script context bit 7 is clear | **unexpected map jumps** (INFERRED severity) |
| **AtelSetEventJump2** | 0x0086ff40 | same | same |
| **MsSetSaveStartGame** | 0x00786b00 | New Game: **g_SaveScenario = 5** | **a new game starts at story flag 5** |
| MsBattleProcess | 0x00790c10 | out of battle, pad L1+R1+L2+R2+Cross starts a debug battle (MsDebugGetFieldNum); a Start-based branch in battle | battle sheet |
| MsBattleExe | 0x00781020 | mode 0: SsSetProjectBase("/usr/local/ffx/<country>/ps2/sound"); MsDebugSetScene | **may break battle sound loading** (INFERRED); battle sheet |
| MsBattleInit / MsEnvInit / MsDebugInfoRead / sa_cd_callback_init | 0x00781700 / 0x00782770 / 0x007826b0 / 0x0076f100 | host0: btlenv/cdrom .fnd reads, MsSetSaveParty | boot |
| MsBtlReadSetScene | 0x00783e20 | result ignored | none |
| MsDebugBattlePosInit / MsSetFileName / MsGetPlySaveName | 0x007c90f0 / 0x007dce80 / 0x007dcec0 | host0:/home/<user>/... paths | battle debug loaders; battle sheet |
| MsBattleDraw | 0x00780d00 | jumps to a bare ret | none |
| TextureAnimation_Animate | 0x00672000 | with no character loading and debug menu type 5: ViewerManager_DrawTexAnimInfo_006bc210 (grid00 only) | viewer only |
| isDebugMode callers | 0x006b85c0, 0x006b8820, 0x006b8a20, 0x006b9160, 0x006bb6d0, 0x006bc130, 0x006bc850 | the viewer tweak panels (1.3) | only matter if those panels run; CharTexAnim setEnabled(0), run by every setMode/disableTweaks, then also calls enableGameControlTextureAnimation(1) (INFERRED harmless) |
| Ch_Allocate | 0x00824f90 | skips a dbgPrintf | none |
| Ch_BlkGetHandle | 0x00825a40 | tags blocks from the debug lists (0x012fbc64/84, empty unless Ch_InitAll ran with the flag); writes "/ffx/proj2/chr/data/<sec>/<name>" into block +0xb8 | harmless (INFERRED) |
| Ch_InitAll | 0x00826ea0 | reads /ffx/proj2/chr/common/*.tbl debug lists | boot |
| Ch_EnableSetTexture | 0x00830330 | Gui_ButtonSetTexture (NULL-checked) | none |
| Light_Set | 0x00836520 | also stores the args in the Gu light-editor table 0x01303c20 (7 words per light) | none |
| 0x008371f0 (motion name) | 0x008371f0 | reads <chr>/inf/motion.tbl via Sg_PcReadD into 0x01301438 slots | file reads only (fail, INFERRED) |
| Sg_PcReadL / **Sg_PcWrite** | 0x0083aea0 / 0x0083afe0 | host0: open; Sg_PcWrite does sceOpen(name, 0x602) + sceWrite | sceWrite is a stub; **sceOpen with create/trunc flags may create or truncate a real file** (INFERRED). Callers only Sg_MemDumpFile and Gu window 0x00853310 (dead) |
| Sg_MemClearStr / Sg_MemGetStr / Sg_MemDumpFile | 0x0083bab0 / 0x0083c070 / 0x0083bb90 | debug name slots at PS2 address 0x05fb0000 + id*32 | ClearStr has no caller; GetStr only from Sg_MemDumpMCB (Gu MemDump); ids are never set (Sg_MemSetStr is `ret`) |
| Sg_Db2DVertex / DbColor / DbLine / DbLineStrip / DbVertex / DbDraw / DbInitAll | 0x00840fc0..0x00841610 | section 3.1 | needs Sg_DbInitAll |
| Sg_InitArgs, Sg_MainInit (3x), Sg_MapInitAll, Sg_DbInitAll | | user name from argv[1], initEventList, TkInitScene(0), dbgPrintf, map table init (and no Sg_SetthDispEnable(1)) | boot |
| pppFpLoop | 0x00729ab0 | dbgPrintf on packet overflow | none |
| yiRectToOMD / yiRectToOMD_old | 0x00909d30 / 0x00909dd0 | pass the "unlimited" 8th arg to yiRectToOMDBuild (otherwise 0) | field map-part selection changes (INFERRED visual / perf) |
| rcbgInit / rcbgFini / rcbgPreAddFpObjects / Yn_FieldProc 0x00922060 / rcefMainInit | 0x00921ce0 / 0x00921aa0 / 0x00922010 / 0x00925210 | sceDevVif0Reset, MsDebugDrawBoard (a 1-byte ret), the rc debug overlay 0x0092cf10 (only with g_RcuiWork), 0x01b0003c = effect main | harmless while rcui is closed |
| 0x0082db20, 0x008702b0, 0x00a7aee0 | | Gu toggle / bit 0x20 of 0x01325ba2 / host irx loader | no callers |

TkIsDebug callers (unaffected, always 0): Native_00f8, 0x014a,
AtelEventSetUp, AtelInitTotalDebug, AtelSetVagStreamVolume, the 0xc0xx debug
natives, TkResetClip, TkRemove, TkInit, TkRefreshHDDMode. **Script-side debug
needs hooks on TkIsDebug (or per native), not the flag.** Script sheet.

### 2.3 Verdict

- **Setting g_FFXIsDebug globally is not safe** (VERIFIED crash path, plus
  the gameplay effects above).
- If the mod ever sets it, the required order is: set the byte (or call
  FFXSetDebug(1)), then call Sg_DbInitAll() once on the game thread, before
  the next Sg_MainDebug. Even then the gameplay effects remain: L1 noclip, L2
  and L1+R1 speed, forced event jumps, New Game scenario 5, the battle combo,
  the battle sound base.
- **Narrower gates are better.** The only features that truly need the
  flag:
  - the viewer tweak panels: hook isDebugMode only for their call sites, or
    patch their `call 0x822550` (0x006b85d4, 0x006b8626, 0x006b8844,
    0x006b8a47, 0x006b9184, 0x006bb6d6, 0x006bc143, 0x006bc86d) to `mov al,1`;
  - mapChange's AtelSetEventJump acceptance: set the script context bit
    instead, or call TkSetEventJump directly (script sheet);
  - Sg_ControlMain's noclip/speed: write 0x023c44b4 / 0x00c496d4 from the
    mod instead.
  - Nothing that draws on PC needs it.

---------------------------------------------------------------------------

## 3. Debug text and primitives

### 3.1 Sg_Db* debug lines (dead on PC)

- Buffer: start pointer 0x013032cc, write pointer 0x013032d0 (both NULL in
  release). Records:
  - u32 0x10001 = line (Sg_DbLine), 0x20001 = line strip (Sg_DbLineStrip);
  - colour (2, r, g, b): Sg_DbColor 0x00841230, Sg_DbColor8 0x00841270;
  - 3D vertex (3, x, y, z): Sg_DbVertex 0x00841610;
  - 2D vertex (u16 4, x, y): Sg_Db2DVertex 0x00840fc0.
  All are no-ops unless g_FFXIsDebug (VERIFIED).
- Shapes: Sg_DbBox 0x00841000, BoxX 0x00841050, Circle 0x008410a0 (13
  vertices, x-z plane, tables 0x01303308/0x013032d4 from
  Sg_DbInitTables_00841100), CircleXY 0x00841170, CircleYZ 0x008411d0, Null
  0x008414b0, Sphere 0x00841550.
- **Producers in release: none reachable.** Ch_DrawIcons 0x0082ec80 (gated
  by g_ChrDrawIcon 0x013007e9) has no caller; Box/Null/Sphere have none
  (VERIFIED rel32/abs scans).
- Flush: Sg_DbDraw 0x008412c0 (cdecl()) from Sg_MainDebug. It walks the
  list, projects points with the camera*screen matrices and emits GS
  PRIM/RGBAQ/XYZF2 through Sg_GsStart/Sg_GsEnd, i.e. sceDmaSend, a stub
  (main.md). Unknown command bytes loop forever logging, so keep the buffer
  well-formed. **Never reaches the PC** (VERIFIED callees).
- Revival: rebuild in ImGui. A mod could hook Sg_DbDraw and parse the list
  into `ImGui.GetBackgroundDrawList()` lines (projection: the same matrices,
  Ch_SysGetScreenMatrixPtr/CameraMatrixPtr). But nothing fills it, so mod
  code would have to call Sg_Db* itself; drawing directly in ImGui is
  simpler. Not worth reviving natively.

### 3.2 TkDebugFont / TkPutDebugFont* (dead on PC)

- Writers (all cdecl):
  - `TkPutDebugFontString` 0x0088f140 (color, flags, s), `...Euc` 0x0088f160,
    `...P` 0x0088f4c0 (proportional), `...PEuc` 0x0088f4e0, `...PR`
    0x0088f5c0, `...PREuc` 0x0088f5f0, `...R`/`...REuc` 0x0088f690/0x0088f6c0
    (no callers);
  - all go to the core 0x0088f240, which builds GS glyph quadwords into
    `g_TkDebugFontBuffer` 0x0133c8cc (double buffer 0x0133c8c4/c8,
    TkDebugFont_SwapBuffer 0x00890780 from 0x0088eed0 each frame);
  - position: `g_TkDebugFontPos` 0x0133c8d4 (x16; TkSetDebugFontLocate
    0x0088faf0), `g_TkDebugFontPos2` 0x0133c8dc (TkSetDebugFontLocate2
    0x008904e0); colour index `g_TkDebugFontColor` 0x01328a74.
- Flush: TkRegistDebugFontDmaTag 0x0088f760 (no caller) would DMA it.
  **TkDrawDebug 0x0088d4e0 = bare `ret`** (called by Sg_MainLoop). VERIFIED:
  the output never reaches any screen.
- Callers: ~130 functions, all in the script-debugger cluster
  0x0087a4a0..0x00884d90 (AtelDebug_*, TkDebugHexDump_00883340, root
  AtelDebugDisplay_00883040 with no static caller). Script sheet.
- `g_TkDebugDrawFlag` 0x01330872: written by TkSetDebugDrawFlag 0x0088ea20
  (script debugger) and TkInitWork; its getter 0x0088d5a0 has **no caller**
  (VERIFIED). Unused.
- `g_TkDebugMenuStatus` 0x01330870 (TkSet 0x0088ea30 / TkGet 0x0088d5b0):
  - set by opu_debug_check and the spline editor 0x0093ee10 (both dead);
  - read by two script-debugger sites (0x0087bdad, 0x0087d6d4).
- Revival: ImGui. The script debugger's page drawers show what each line
  printed (script sheet).

### 3.3 TODaps print buffer (one hook from working on PC)

- `g_TODapsBuffer` 0x0133fab0 (0x800 bytes, appends stop at 0x7c0),
  `g_TODapsUsed` 0x0133faa0 (i16), pen x/y 0x013402b0/0x013402b4 (i16,
  reset to 10/10).
- Writers (cdecl):
  - TODapsString 0x0089dec0 (s);
  - TODapsSetPos 0x0089de30 (x, y) stores `\b` + x/100, x%100, y/100, y%100;
  - TODapsPrintf 0x0089dde0 (fmt, ...) (FhCall `FUN_0049DDE0_002EA500`,
    d_UnknownFn);
  - TODapsInteger 0x0089dc70 (no callers);
  - TODapsInit 0x0089dc40 (TOInit, TOReset1).
- **Producers live in release** (VERIFIED callers):
  - MsBattleProcess_DebugStatus_00792f20, every battle frame while
    g_DebugPadPrintFlag != 0: "status:..:. W S E", the battle name, sound
    IOP status, VS words, summon energies, the commanding actor;
  - TODrawMessage 0x008b5dc0: "MESSAGE FORMAT ERROR ID:%d" for a debug
    window;
  - TOSwapInternationalEnvExecNonBlock 0x008b0500.
- **Flush on PC:** `TODapsDraw` 0x0089dc10 (cdecl(), called by
  TODrawMessageWindow 0x008abd30, 0x008a9ca0, 0x008abf30 and the menu pass
  0x008beff0) only clears the buffer. **`TODapsMakePkt` 0x0089dcb0 (cdecl(),
  plain ret) has no caller** (VERIFIED). MakePkt sets the sub-font
  environment and a 512x416 scissor (0x27f wide in disp mode 4). It then
  draws each char with 0x00902370 -> TOAdpFontM_ASCII 0x008ee240 ->
  **graphicFontDraw** (the PC font path; VERIFIED callees). Codes: `\b` + 4
  digits = position, `\t` = tab of 4, `\n` = +14 px, a-z upper-cased.
- **Revival:** hook TODapsDraw. In the hook call TODapsMakePkt() and then
  the original (which clears). TODapsDraw is called inside the TO
  message-window draw, i.e. inside the UI pass, so graphicFontDraw is legal
  there (INFERRED; not run). Then turn on g_DebugPadPrintFlag (below) for
  the battle status line. Risks: low; read-only, no files.
  - Caveat (INFERRED): TODapsDraw has four call sites per frame. The first
    one that runs consumes the buffer.

### 3.4 g_DebugPadPrintFlag (live in release)

- `g_DebugPadPrintFlag` 0x01841d43 (u8, 0..3), set to 0 by TOInit.
- TODebugAddPadPrintFlag 0x008abd00 (cdecl(): v = (v+1) % 4) is reached
  from:
  - TkBtlEndCtrl 0x008ba660 (battle-end screen, pad 0x100 = Select);
  - TOControlMessageWindowForMenu 0x008ab9a0 (inlined) while g_TOMenuPad
    0x025d09d2 held and trg are both 0x100.

  **So pressing Select in those contexts already cycles it in the shipped
  game** (VERIFIED code).
- TODebugGetPadPrintFlag 0x008abd20 (cdecl() -> eax) readers:
  - MsBattleProcess (the status line above);
  - movie_csc 0x0077d5e0 (MovieDebugLine_0077de90: "FRAME %d/%d", "FUKA %d"
    into a local only, dead output);
  - TODrawMessage (error banner);
  - menu_list_step, 0x00777440, chr_texanim_draw_sub_new, syncStrPc (result
    ignored).
- Only effect visible on PC once 3.3 is revived: the battle debug status
  line. Mod: write the byte (1) instead of relying on Select. FhCall: none
  for either function.

### 3.5 debugPrintString* and other console prints

- `debugPrintString` 0x00649180 / `ToHex` 0x006491b0 / `ToUTF8` 0x00649220:
  fprintf(stderr, "SKS_DEBUG: ..."); **no callers** (VERIFIED).
- Other stderr prints (SaveDataTtyWrite*, Phyre_PTTYCallback_print 0x004354f0
  -> printf) go to the CRT stdout/stderr, which a GUI exe has no console
  for. A mod could redirect them by AllocConsole/freopen or by hooking those
  functions (INFERRED).

### 3.6 dbgPrintf: the cheapest debug log revival

- `dbgPrintf` 0x0062f500 / off 0x22F500 is `ret` (then int3 padding to
  0x0062f50f; VERIFIED bytes), with 459 call sites. Its messages include
  Ch_Allocate, the battle and sound status, "SG:captured file" and the
  FFXDEBUG-prefixed text.
- FhCall: `FhCall.dbgPrintf` (d__Printf, cdecl(byte* fmt, 16 x nint
  varargs)). Also `scePrintf` 0x22FBF0, `AtelPs2DebugString(2)`
  0x473C70/0x473C80, `rcPrint` and Phyre_PhyrePrintf.
- Hooking a 1-byte `ret` needs a hook engine that accepts code padding
  (INFERRED for Fahrenheit's hooker; 16 bytes are available before
  debug_exit_trace 0x0062f510).
- printf2 0x0076dd10 routes FFXDEBUG-prefixed messages (system.md).
- Revival: hook, vsnprintf, show in an ImGui log window. Read-only, no risk.

### 3.7 Sg_MainDebug, Sg_Debug, Sg_SetDebugTEX_0

- Sg_MainDebug 0x008207f0 (cdecl(), FhCall none): Sg_IdDrawMain,
  Counters_Flush(4), then the g_FFXIsDebug block (2.2). The per-frame call
  is at Sg_MainLoop.
- Sg_Debug 0x0083e5c0 (cdecl(key, v)): with key 0x1113 it stores v in
  0x01301a80, else debug_exit_trace(0) (a stub). A PS2 debug-link switch;
  not interesting.
- Sg_SetDebugTEX_0 0x00851160 (cdecl): writes the debug-font TEX0 register
  into a GS packet (Gu_DrawMouseCursor, Gu_PrintBegin, 0x00850e80). Gu-only,
  GS-only; dead on PC.

---------------------------------------------------------------------------

## 4. Debug free camera and op/VFX debug display

### 4.1 The PC port's free camera (`g_FreeCamera`): live, keys unbound

**What it is.** A Virtuos PC-side fly camera. When on, it takes over the
scene camera: FreeCamera_attach sets target+0xb4 = cam+4 (VERIFIED). The 3D
scene then renders from it through the normal D3D11 path (INFERRED for "the
whole scene"); the 2D UI does not move.

Two toggles:
- **Fly mode** (+0 type: 0 off, 1 fly) (VERIFIED):
  - turning it on centres the cursor (GetClientRect/ClientToScreen/
    SetCursorPos), hides it and clips it to the client rect (4 px inset in
    window mode);
  - it shows tip 0x2ed (0x2ee when off) and IconPanel icon 8;
  - each frame: PApplication_updateMouseCamera_0062e3a0(app; fly controller
    +0x2bc, dt, speed +8 = 60.0f, 1.0), then Phyre_PCamera_updateViewMatrices;
  - input (INFERRED): mouse buttons +0xa0/a1/a2 drive pan/rotate/dolly; the
    keyboard (+0x80, bytes +0x57..+0x5a) and pad 3's sticks also drive it.
- **Free pause** (+0x338) (VERIFIED):
  - FreeCamera_setFreePause flips it and calls Sg_SetPauseAllSounds(on). With
    type 0 it copies the scene camera (+0x150/+0x154) and attaches;
  - Pause_Enter uses the same machinery (graphicSetFreeCameraMode(1) sets +0x33c);
  - graphicGetFreeCameraType 0x006411e0 returns 2 while +0x338 or +0x33c is
    set, and Sg_MainLoop (test at 0x00820b37) then skips the whole PS2
    frame. The world freezes while the camera flies: a photo mode.

**Reached in release: every frame** (VERIFIED). PhyreScene_animate 0x00657140:
- inputIsPressButton(0x100000) -> setFreePause;
- g_AsyncControllerOn 0x0133c930 forces it off;
- always FreeCamera_handleInput(g_FreeCamera; dt = app+0x24, scene+0xdc) at
  0x00657490. Inside it, inputIsPressButton(0x40000) cycles the type.

The block is skipped on maps 0x17/0x15c/0x15d.

**Gate: the two keys are hard-unbound** (VERIFIED). Input_BuildPadMap
0x006315c0 builds pairs at g_Virtuos_InputManager 0x00ccb170 (+0x9c count,
+0xa4 pointer to 8-byte [binding, bit] entries):
- pair 25 = [0, 0x40000] and pair 27 = [0, 0x100000] (again 47 and 49);
- the binding is a literal 0, not from g_Settings, so GameSetting.ini cannot
  bind them.

No g_FFXIsDebug / isDebugMode gate. g_WaitFramesByTime 0x0112a008 (movie
camera) forces type 0. Fly mode zeroes the game's sticks (platform.md).

**Revival:**
1. Patch the binding word each frame if it is 0:
   `*(u32*)(*(u32*)(0x00ccb170+0xa4) + 25*8)` (and 27).
   - Binding >= 0x1c is Phyre key v - 0x1c; F1..F12 = keys 37..48, so F6 =
     0x47, F7 = 0x48 (F1 = 0x41 VERIFIED from the default slot).
   - Re-apply after every rebuild (FFXApplication_initApplication,
     PhyreScene_initScene, 0x00701680, 0x00701e60, the key-config pages).
   - Check +0x9c > 27 first.
2. Or OR the bit into the InputManager pad word +0. isPressButton
   0x00631360 tests `(+4 & b) == 0 && (+0 & b) != 0`. This must land after
   updateInput and before PhyreScene_animate.
3. Or call, on the game thread before PhyreScene_animate:
   - FreeCamera_setFreePause 0x00680320 (thiscall(this), ret) or
   - FreeCamera_setType 0x00680410 (thiscall(this; type) ret 4).

**Risks:**
- The cursor is hidden and clipped (the harness turns SetCursorPos/ClipCursor
  into no-ops in background mode, docs/harness.md). ShowCursor balance is
  tracked at +0x33a.
- Free pause mutes all sound and freezes shader GlobalTime. Focus loss does
  not pause.
- No file, save or Steam effects (VERIFIED callees).

| Item | VA / off | Conv | FhCall |
|---|---|---|---|
| FreeCamera_handleInput | 0x00680030 / 0x280030 | thiscall(this; f32 dt, PCamera*) **ret 8** | `FUN_00280030_00096330` d_UnknownFn |
| FreeCamera_setFreePause | 0x00680320 / 0x280320 | thiscall(this), ret | `FUN_00280320_00096620` |
| FreeCamera_setFreeCameraMode | 0x006803a0 / 0x2803A0 | thiscall(this; on) ret 4 | `FUN_002803A0_000966A0` |
| FreeCamera_setType | 0x00680410 / 0x280410 | thiscall(this; type) ret 4 | `FUN_00280410_00096710` |
| FreeCamera_CreateInstance_0042f260 | 0x0042f260 / 0x02F260 | cdecl() -> instance | `FUN_0002F260_0002F240` |
| graphicGetFreeCameraType | 0x006411e0 / 0x2411E0 | cdecl() -> 2 or +0 | `FUN_002411E0_00052220` |
| Virtuos_InputManager_isPressButton | 0x00631360 / 0x231360 | thiscall(im; bit) ret 4 | `FUN_00231360_0003B5E0` |
| Input_BuildPadMap | 0x006315c0 / 0x2315C0 | fastcall(im) | none |
| PhyreScene_animate | 0x00657140 / 0x257140 | thiscall(scene) -> 0 | none |
| g_FreeCamera | 0x00ccc830 / 0x8CC830 | ptr, 0x340-byte object (+0 type, +4 last, +8 f32 speed, +0xc camera, +0x2bc fly ctrl, +0x338 free pause, +0x33a cursor shown, +0x33c pause mode) | |
| g_Virtuos_InputManager | 0x00ccb170 / 0x8CB170 | +0 word, +4 old, +0x9c count, +0xa4 pairs | |

### 4.2 The PS2 field camera control (Sg_ControlCamera): not a free camera

- Sg_MainLoop -> Sg_ControlMain 0x0082d070 (call at 0x00820df0) ->
  Sg_ControlCamera 0x0083f4b0, every frame (INFERRED for "every frame").
- Pad 0's right stick turns g_FreeCamYaw 0x00c4def0 / g_FreeCamPitch
  0x00c4def4; L/R mode changes height/distance; pad 0x400 cycles
  g_FreeCamPreset 0x01301bb0.
- Output only reaches camera 0's stored rect via Sg_CameraPutRects ->
  MsCameraGet(6,0,0) = 0. No standalone orbit camera exists to switch to.
  Not worth reviving; 4.1 covers the need (VERIFIED for the code read).
- Debug path under g_FFXIsDebug: section 2.2 (capture, noclip L1, speed L2
  and L1+R1).
- Sg_ControlMouseCamera 0x0083f6f0 and MsSetMouseCamera 0x00792e70 are bare
  `ret` (VERIFIED).

### 4.3 g_DebugCamera, g_DebugCameraKeepAngleDeg, MsDebugCamera* (battle; short)

- `g_DebugCamera` 0x011370c1 / 0xD370C1 (u8) is misnamed. It is
  g_Screens[0]+0xc1, the camera index screen 0 views, written by scripts in
  normal play (Cam_SetScreenCamera 0x007bc950, FhCall
  `FUN_003BC950_00250800`). Writing it switches the real camera; it is not
  a debug toggle (VERIFIED).
- `g_DebugCameraKeepAngleDeg` 0x0113ed78 (f32): only the Ms_Gui_CameraProc
  sliders write it. Dead in release (INFERRED).
- Live: MsDebugCameraSleep 0x007921c0 each battle frame with
  g_BtlDebugCameraSleepOn 0x0112a8fc (Fahrenheit `Btl.debug.free_camera`,
  Btl+0x2c). Set to 1, it freezes all battle camera requests. Battle sheet.
- Dead (only from Ms_Gui_* procs or the unreferenced 0x007c89d0):
  MsDebugCameraKeep_007c8c50, KeepAdd_007d0ee0, Copy_007d1220,
  MsDebugOutCamera 0x007d2430, MsDebugCameraInit_007d62c0, MsDebugWinAllocAll.

### 4.4 op/VFX debug display (opd_*, g_OpDebugMode): dead on PC, rebuild in ImGui

Globals (block 0x02322780, 0xa4 bytes, zeroed by op_init_ffx_state_007ff960,
VERIFIED):

| Global | VA / off | Meaning |
|---|---|---|
| g_OpDebugMode u8 | 0x02322790 / 0x1F22790 | master op debug flag |
| 0x02322791 u8 | / 0x1F22791 | cleared by op_debug_reset |
| g_OpNumShow u8 | 0x02322794 / 0x1F22794 | number display (nothing writes it; op_init_ffx_state ANDs it) |
| 0x02322795 u8 | | host-file loads (0x007e5c10) |
| 0x02322798 u32 | | effect debugger camera handle |
| 0x023227bf u8 | | effect debugger on |
| 0x02322780 / 81 | | freeze / step |
| 0x023227c7 u8 | / 0x1F227C7 | gates the manipulator-menu wrappers |
| g_OpPadInput | 0x02321548 | op_pad_set copies pads only when nonzero |
| g_OpDebugChrFlags | 0x02321580 | debug object swaps |
| g_OpPads | 0x012a1370 | 0x20 per pad |

- **Writers of g_OpDebugMode:**
  - pppInitPartRender 0x00761d60 (1 at start-up, then cleared by the memset);
  - opd_read_regist 0x00a63b00 (only via opu_debug_check);
  - pppDebugModeOn_00761320 / 0x361320 and opd_debug_on_00a63a40. **Neither
    has a call, jump or pointer** (VERIFIED byte scan).
- **Why nothing shows (VERIFIED):**
  - op_num 0x007e63a0 is a bare `ret`, so pppFpInfPrint ("CAM/PLY/DIS"),
    opd_display, opd_mmenu_display, opd_camera_printf and opd_print_* print
    nothing;
  - opd_num_* uploads digit sprites through sceGsExecLoadImage (`xor
    eax,eax; ret`) and sceDmaSend (`ret`);
  - opd_num_trans 0x00a5df50 would OR into the NULL DMA channel (INFERRED
    crash), but its only entry op_num_trans 0x007e6530 has no caller.
- **Dead entry points** (VERIFIED): opd_display 0x00a629b0, opd_camera_printf
  0x00a62330, op_mmenu_display_007e62e0 (gates g_OpDebugMode and 0x023227c7,
  then jmp opd_mmenu_display 0x00a611b0, FhCall `FUN_006611B0_00769B60`),
  opu_debug_check 0x007fd020, the 0x007e5bd0..0x007e6380 wrappers.
- `op_debug_reset_008005f0` / 0x4005F0 is cdecl() -> 0: `mov byte
  [0x02322791],0; xor eax,eax; ret` (VERIFIED). It is slot +4 of the battle
  effect overlay table 0x00c48d90, a slot the dump never calls (INFERRED
  dead). Harmless.
- opu_debug_check (no caller) was the effect inspector, driven from pad 1
  (R1+R2 toggle): freeze effects, single-step, pick parts in
  g_OpusBattle/g_OpusAlt and nudge +0x30..+0x4c, opd_camera_change, an
  orbit camera via MsCameraSetRect(0x02322798), and a host "effect registry"
  read.
- **Setting g_OpDebugMode = 1 globally is not safe** (VERIFIED readers):
  - opu_jump_etc inverts kind 0xf000 effect-script jumps and changes kinds
    0xa000/0x3000;
  - opu_en_lock substitutes handles;
  - opu_tc_load skips missing texture blocks;
  - opus_camera_limit calls QuickRecoverMenuReserve;
  - op_pad_set starts copying pads;
  - opu_debug_reload frees and reloads debug oef2 objects.

  Battle effects change and nothing visible is gained.
- g_OpNumShow set before op_init_ffx skips loading et_ffx.bin/et_tex.bin
  (breaks effects); set later it has no visible effect.
- **Revival: ImGui, read-only:**
  - the CAM/PLY/DIS line (camera from the inverse of g_pppCameraMatrix,
    player from Sg_GetPlayer/Ch_GetPos);
  - ppp packet and heap use and peaks (g_pppFpPacketUsed/Peak,
    g_pppFpHeapPeak);
  - g_OpusBattle part lists.

  An effect freeze could be new mod code via Ch_SetMotionSpeed(h, 0|0x100)
  over g_EtChrHandles (INFERRED). FhCall: no named handles for any op
  function.

---------------------------------------------------------------------------

## 5. PS2 debug GUI toolkit (Gu_/Gui_/gui_) and the rc map tool

### 5.1 Verdict: rebuild in ImGui

1. **Nothing it draws reaches the PC** (VERIFIED). Every Gu draw writes GIF
   A+D packets (PRIM, RGBAQ, XYZF2, TEST, debug-font TEX0) into the 1 MB
   buffer [0x00c498dc]. Gu_DrawEnd then calls sceDmaSend 0x0062f710 (`ret`).
   The font upload (Send_DebugFont 0x008510b0 -> Load1TIM ->
   sceGsExecLoadImage) is a stub too. No Gu function calls
   graphicDrawUIElement.
2. **No input** (VERIFIED). The only input is the PS2 USB mouse:
   Sg_MouseMain 0x00851920 -> FFXUsbMouseRead2 0x00855bc0 -> FFXSifCallRpc
   0x00a44750, a stub returning 0 that writes nothing. The Gu range
   0x00846000-0x00856000 makes no pad or keyboard calls, except the spline
   editor's TkGetControlerHist.
3. **Nothing creates a window in release** (VERIFIED). Gu_Init 0x008497a0
   and the top-level menu init 0x0084f100 have zero callers and zero pointer
   refs. Every one of the 49 Gu_WindowCreate call sites hangs off WMainProc
   or its children.
4. **The original init hangs** (VERIFIED). 0x0084f100 runs
   Sg_ClockFromHost_0084f080 -> SystemOnHost 0x0084ede0, which builds a
   DECI2 request at 0x21303d40 (outside the image: probable fault) and then
   spins forever on 0x01303d14.

### 5.2 Per-frame path and globals

- Sg_FrameSync -> (g_FFXIsDebug) Gu_VSyncMain 0x0084a460 (Sg_MouseMain +
  Sg_ClockTick_0084ef10).
- Sg_MainDebug -> (g_FFXIsDebug, capture not hiding, 0x012fbbb3) Gu_Main
  0x00849850. It is cdecl(channel, packet), plain ret; FhCall
  `FUN_00449850_002FB8E0` d_UnknownFn. Steps: Sg_MouseButtonMain (raw
  0x01325260 -> edge word 0x023cbc10), Gu_VSync, and unless g_GuHidden:
  Send_DebugFont, Gu_DrawStart, scissor 512x416, Gu_DrawWindowTree(g_GuWindowList)
  (message 0x8011 per window per frame), cursor, Gu_DrawEnd, Gu_WindowFreeClosed.
- Over the empty list this is harmless (INFERRED). The packet buffer is
  shared with rcPrimDraw2D and the ppp draws; each re-opens it per frame.
- Gu_Init 0x008497a0 zeroes 0x01303734 (g_GuWindowList), 0x01303738,
  0x0130373c, 0x01303740 (popup) and 0x0130375c (g_GuHidden); it sets
  0x01303760 (g_GuMouseEnable) = 1 and tail-jumps to 0x008483d0
  (0x01303730 = 0x80). Not in the dump.
- Gu_WindowCreate 0x0084a920: cdecl(x i16, y, w, h, flags, proc) ->
  window* (user_malloc 0x9c); it sends proc(win, 0x8001, 0). FhCall
  `FUN_0044A920_002FC9B0`.

Input model (only if someone renders it):
- mouse RPC result 0x01325280 (+8 buttons, +0xc dx, +0x10 dy, +0x14 wheel);
- g_GuMouseX/Y 0x01325240/44 (0..0x200 / 0..0x1a0), wheel 0x023cbc04;
- button bits: 1 L down, 2 press, 4 release, 8 double; 0x100.. R; 0x10000.. M;
- messages: 0x8005 L held, 0x8007 L press, **0x8006 L release (click)**,
  0x8008 double, 0x8009/0x800b/0x800a R, 0x800d/0x800f/0x800e M, 0x8017
  wheel, 0x8012 selector result, 0x8003 close, 0x8011 per-frame update;
- injection point (INFERRED): hook FFXUsbMouseRead2 0x00855bc0 (cdecl(arg,
  dst) -> 0).

### 5.3 Top-level menu WMainProc 0x0084f550 (cdecl(win, msg, id) -> 0; FhCall none)

Seven tabs (Sg_SelectMainMenu 0x0084f270; tab index 0x01324128). Header:
0x4b LR-button mode (Sg_Get/SetLRButtonMode), 0x4a/0x49 exec frames -/+
(Sg_SetExecFrames 0..99), the debug clock 0x023cbc30/2c/28 as title, KeepSp,
Alpha1st, Pause. What each tab shows and touches, for ImGui parity:

| Tab | Items (id -> action / globals) |
|---|---|
| CHR | Physics (0x00852080: Ch_Cloth*Para*, Ch_ClothReset/SetXZ/SetY, Sg_GlobalSetWind); Specular (0x00852a30: Ch_DrawGet/SetDisableSpec/FourceSpec/ParaEdit, Ch_PacketSetSpecCoefficient/Color/Size); CHR New (create_chrview); CHRInfo; TexOff (Ch_EnableSetTexture); TexHokan; CalcNorm (Ch_GuiSetCalcNormAll); Light (SgGu_LightEdit: ambient + 3 lights, Light_Set table 0x01303c20); BgColor (yiGetBackColor); HideMap; shadow (0x008523e0: Ch_DrawGet/SetShadowColor/Dir/Mode, Sg_SetMovieGomiAvoid); Mouth (0x00851e70: Ch_GuiGet/SetMouthTh, Ch_GuiSetTalk) |
| MAP | 0x27 MapDir (Sg_MapSetDirSelect 0..2); 0x1b USER / 0x9 MAP selectors (Sg_MapSetName + Sg_MapInit on dev paths: **risky**); 0x52 shade (Sg_MapSetEnableShade); 0x29 Reload (Sg_MapInit); 0xc HideMap (0x00821e40); 0xd IDdisp (Sg_SwitchDrawId, 0x013034b4 0..3); 0x44 F.P.off (Sg_ToggleTkAnti); 0x56 IDshd (Ch_SetShadeByLight); 0x4d BgColor; 0x4f FOG (yiGui_setfogparam_proc 0x009162f0: yiRender_setupFogParam/setupparamGS); 0x63 Alpha1st (Sg_SetMapAlphaFast); 0x2b PRE2D (Gui_YanagiInit, 5.4) |
| SYS | 0xf Capture (window 0x00853520 -> Sg_CaptureEnable -> Sg_Capture: **SystemOnHost hang** once a user name exists); 0x16 HIDE (Gu_Hide); 5 RATE (WFukaProc 0x0084f490: Ch_GetFuka ANM/ELM/ENV/DRW/ALL); 0x38 CPU/VU1 (Gui_ProgAvarage); 0x5b MEMORY (Gui_MemGraph 0x00855020: heap bars); 0x53 CacheCLR (Cache_Clear: **risky**); 0x33/0x3d MemDump (Sg_MemDump -> Cache_Dump, dbgPrintf); 0x43 MotDump; 0x66 MotEv (Ch_SetDumpMotEv); 0x35 LgtDump; 0x67 Pause (Sg_SetPause); 0x25 Clock (Gui_ClockInit) |
| BTL | 0x1f/0x23/0x20/0x34/0x58/0x59 -> Gui_MasakiInit(1/3/5/6/7/8) (Ms_Gui_* windows, **battle sheet**); 0x42 CHRInfo |
| CAM | 0x1d Info / 0x1e REF / 0x24 CAM -> Gui_MasakiInit(0/2/4); 0x39 Spline (sasa_menu -> 0x0093ee10 "SPLINE EDIT": TkGetControlerHist, TkSetDebugMenuStatus, MsAtel*CameraID, sa_camera_set); 0x50 Shake (Gui_MasakiInit(9)); 0x57 always (Sg_EnableCalcCam) |
| EFF | 0x5c BINDOFF (op_et_bindeff_off_all 0x007fbc20 / bindeff_all 0x007fb940); 0x5d ChrIcon (Ch_SetDrawIcon: Ch_DrawIcons is dead anyway); 0x5e HideMap; 0x5f Pre2d; 0x60 IDdisp; 0x61 CPU/VU1; 0x62 FP Mem (Yn_FpMemWinOpen -> 0x00933aa0: pppFpGetHeapSize/Used/MaxUseSize, pppFpGetPacketSize/Used/Peak); 0x2a MATRIX (Sg_EnableEffectMatirx) |
| EVENT | 0x2e Reload / 0x2c Select (gui_eve_GWM_BUTTONUP reads /ffx/proj/event/header/eventid.bin, then AtelSetEventJump: **map jump**); 0x41 CHRInfo (Gui_ChrInfo -> 0x008537d0: pos/offset/rot/dir/speed, hide, camera length/ClipZ, motion name/frame/speed/loop/hokan, map IDs via Ch_IdGetMap/MapB/Sound/Effect/Dictionary/Encount/Water/Shade*, hit); 0x2f TkAnti; 0x32 Focus (Gu_FocusProc_00854980: Sg_Get/SetFocus, Sg_ToggleFocus); 0x37 Blur (0x00854420); 0x3c Flash (0x00854590: Sg_Flash, Sg_AccGetAlpha); 0x3b Acc (0x00854270: Sg_Acc*Alpha); **0x51 CLR FLG (AtelInitSaveWork: clears 0x1000 bytes of the live save-event record = story flags: DANGEROUS)**; 0x55 KeepSp (Sg_SetKeepFps 0..2); 0x15 Movie (movie_view -> 0x0093dc00, host files); 0x6a HideChr (Sg_GuiSetDrawChr); 0x6b TexAnim (Sg_GuiSetTextureAnim) |

Gu_DebugMenuCommand_00854c60 is the EVENT/CHR command dispatcher behind
those ids (0x2c gui_eve_GWM_BUTTONUP, 0x2e 0x0086fec0, 0x2f
Sg_ToggleEnableTkAnti, 0x32/0x37/0x3b/0x3c window creates). INFERRED: Blur,
Flash, Acc, Focus and TkAnti drive PS2 GS post-effects; check whether they
do anything on PC before exposing them. File writes are inert: sceWrite
0x0062fdc0 is a stub, and the `host0:EXEC:cmd /c dir` lists go through
sceOpen to the PC file layer, so they come up empty (INFERRED).

### 5.4 rc map tool ("Yanagi", rcui)

- Gui_YanagiInit 0x00930d80 (cdecl(); FhCall `FUN_00530D80_003CFFB0`) toggles
  the tool: rcbgInit (when g_YnMapSystem is 0), then rcuiInit 0x00934390
  (FhCall `FUN_00534390_003D3550`). rcuiInit does rcCalloc(0xc0c) into
  g_RcuiWork 0x0193c0f0 and opens the "PRE2D" window (proc 0x00935000).
- PRE2D buttons open child windows (rcuiWindowCreate 0x00937ef0):
  1. Load (map2d.bin from host, Yn_PreBgSetData, rcbgIDLoad; fails on PC, INFERRED)
  2. **Save (rcbgSaveText -> SystemOnHost: hang)**
  3. Layer (rcuiLayerProc: anime/alpha/norm/Z/XY speed/palette/depth/white per layer)
  4. Camera (rcuiCameraProc: pos/ref/angle/rot)
  5. Scroll
  6. P-Sim (Yn_PSim*)
  7. FP (0x00933cc0: Fp_Restart/End, pppFpIDdispOn/Off, pppFpDispDebug)
  8. Guide Map (0x00934110: rcbgGuideGet/SetTarget)
  9. Chr. Info
  10. Texture
  11. FP Memory
  12. Debug (0x009325c0: Bank/Stage/No, rcbgPreSetSceneNo, "Guide Map Data Convert", rcuiPreMemWinOpen)
  13. Hi-Reso / Lo-Reso

  Numbers are dragged with the mouse (g_RcuiWork +0xbe8/+0xbf0).
- **The rc overlay draws nothing on PC** (VERIFIED):
  - Yn_FieldProc_0091a8c0 -> 0x00922060 -> `if (FFXIsDebug && sceGsSetDefDrawEnv2()) jmp 0x0092cf10`;
  - with g_RcuiWork it registers 0x0092c130/0x0092c930/0x0092cfa0 via
    op_func_before_ot_set and calls rcuiVramWrite;
  - all of it builds rcPacket* that end in rcPacketDataDraw -> sceDmaSend.
- **rcbgGuide* (0x0091ccc0-0x0091ecb0) is the live field minimap, not debug.**
  It draws on PC via rcPrim* -> rcPrimDraw2D 0x00925f80 -> graphicDrawUIElement
  0x0063eee0.
- rcPrim* (cdecl, 512x416 space, only inside the UI pass; INFERRED) is the
  one debug-adjacent 2D API that reaches the PC:

  | Function | VA | Args | FhCall |
  |---|---|---|---|
  | rcPrimBox | 0x00925b60 | (x, y, w, h, colour*, ui) | `FUN_00525B60_003C8AF0` |
  | rcPrimLine | 0x00926c00 | (x0, y0, x1, y1, colour*, ui) | `FUN_00526C00_003C9D40` |
  | rcPrimBatu | 0x009259c0 | (x, y, size, colour, ui) | `FUN_005259C0_003C8950` |
  | rcPrimCircleLine | 0x00925ea0 | (x, y, r, colour, ui) | `FUN_00525EA0_003C8EF0` |
  | rcPrimTri | 0x009270c0 | (x, y, r, angle, colour, ui) | `FUN_005270C0_003CA220` |

### 5.5 Recommendation

- ImGui windows that mirror 5.3/5.4, calling the same Sg_*/Ch_* setters and
  getters from the game thread.
- Omit or hard-gate: CLR FLG, Capture, rc Save/Load, MAP/EVENT jumps,
  CacheCLR.
- A native fallback (Gu_Init + Gu_WindowCreate(0x1b0,0,0x50,0x52,2,WMainProc),
  never 0x0084f100) would need g_FFXIsDebug with all of section 2, a mouse
  hook, and a GIF-packet -> ImGui translator for [0x00c498dc]. Not
  recommended.

---------------------------------------------------------------------------

## 6. Handle checklist for the mod (what to declare)

| Function | VA / off | Delegate to declare | FhCall today |
|---|---|---|---|
| DebugMenuManager_setMode | 0x006b4d10 / 0x2B4D10 | thiscall void(void* dmm, int mode) | none |
| DebugMenuManager_getCurrentDebugMenuType | 0x006b4a80 / 0x2B4A80 | thiscall int(void* dmm) | none |
| tweak getters | 0x006b62f0..0x006b6360, 0x00692bf0, 0x006a6e40 | cdecl void*() (or read the globals) | none |
| tweak handleInput (x10) | table 1.3 | thiscall void(void* util, void* papp) | four d_UnknownFn (1.3) |
| tweak setEnabled (x10) | table 1.1 | thiscall void(void* util, byte on) | three d_UnknownFn |
| PApplication_GetApplication | 0x00623b90 / 0x223B90 | cdecl void*() | none |
| FFXSetDebug | 0x00887c80 / 0x487C80 | cdecl void(int) | `FUN_00487C80_00347130` d_UnknownFn |
| Sg_DbInitAll | 0x00841440 / 0x441440 | cdecl void() | `FUN_00441440_002F7890` d_UnknownFn |
| initEventList | 0x00907f50 / 0x507F50 | cdecl void() | `FUN_00507F50_003B8B70` d_UnknownFn |
| TODapsDraw (hook) | 0x0089dc10 / 0x49DC10 | cdecl void() | none |
| TODapsMakePkt | 0x0089dcb0 / 0x49DCB0 | cdecl void() | none |
| dbgPrintf (hook) | 0x0062f500 / 0x22F500 | d__Printf | `FhCall.dbgPrintf` |
| Sg_MainLoop (per-frame game-thread hook) | 0x00820ae0 / 0x420AE0 | d_Sg_MainLoop(float) | `FhCall.Sg_MainLoop` (internal) |
| Sg_FrameSync (alternative) | 0x00821e80 / 0x421E80 | d_FUN_00421e80_00206900(float) | public, typed |
| PhyreScene_animate (alternative) | 0x00657140 / 0x257140 | thiscall int(void* scene) | none |
| StatsAndAchievements_ResetAll | 0x006f1420 / 0x2F1420 | **never call** | `FUN_002F1420_0010EAA0` |
| TkDebugLoadViewerSave | 0x008b55e0 / 0x4B55E0 | cdecl void(int n), **save-replacing** | none |
