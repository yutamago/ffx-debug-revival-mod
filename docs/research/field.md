# Facts, round 2 (field): free camera, panel size, map list, grid00, debug flag

Research sheet for the Debug Revival mod (yuta.debugrevival). It builds on
`docs/research/overlays.md` sections 1, 2 and 4.1, and **corrects several
of their claims** (summary in section 0).

Conventions:
- VA = absolute address (image base 0x00400000). off = VA - 0x400000 (the
  FhCallLocation offset).
- VERIFIED = I read the code (objdump of FFX.exe, the Ghidra dump, or raw
  bytes). INFERRED = reasoning from names, layout or partial reads.
- "FhCall" = the handle in Fahrenheit alpha12 `src/core/call*.cs` or
  `src/core/ffx/call.cs`. `d_UnknownFn` gives the location only; the mod
  declares its own delegate.
- Frame order (VERIFIED: `Phyre_PFramework_PApplication_frame` 0x00627940,
  `App_Frame` 0x0062e340, `FFXApplication_animate` 0x0042f520, `updateFFX`
  0x00822840):
  1. `PInput::Update` 0x00625770 (every device's update). Fahrenheit's
     ImGui hook sits here.
  2. `PInputMapper_update` 0x0062e040.
  3. `latchButtons`.
  4. vslot 7 `FFXApplication_handleInputs` -> `Input_UpdateAll` ->
     `Virtuos_InputManager_updateInput` 0x00632a30, which builds the pad
     word.
  5. vslot 4 `FFXApplication_animate` -> `updateFFX` -> **`Sg_MainLoop`**
     (Fahrenheit's PreUpdate/PostUpdate wrap this call; it can run more
     than once per frame while `g_VoiceHold`) -> `graphicSceneAnimate` ->
     **`PhyreScene_animate`** 0x00657140 -> `FreeCamera_handleInput`.
  6. Render.

  While the PC Esc menu is up (app +0x3ad), neither `Sg_MainLoop` nor
  `PhyreScene_animate` runs.

---------------------------------------------------------------------------

## 0. Summary

**A. Why the free camera did nothing (three independent causes).**
1. **Cause 1: `FreeCamera_setType(fc, 1)` alone never attaches the camera.**
   (VERIFIED)
   - The scene renders from the free camera's PCamera `fc+0xc` as soon as
     the type is > 0 (`PhyreScene_render`).
   - That camera is only initialised from the scene camera and attached to
     the fly controller `fc+0x2bc` by the game's own toggle path in
     `FreeCamera_handleInput` (or by setFreePause / setFreeCameraMode).
   - So with setType(1) the fly controller moves a matrix nobody reads. The
     view is a default or stale camera.
   - setType also skips the cursor centring, hiding and clipping.
2. **Cause 2: Fahrenheit's ImGui freezes all game input while any ImGui
   window has focus or is hovered.** (VERIFIED code, plus Dear ImGui
   semantics)
   - `imgui.cs h_pinput` skips the whole `PInput::Update` (keyboard, mouse
     and pads all freeze) when `io.WantCaptureKeyboard ||
     io.WantCaptureMouse || FhApi.Input.lock_get()`.
   - Fahrenheit sets `NavEnableKeyboard`, so WantCaptureKeyboard is true
     whenever an ImGui window is focused.
   - After the user clicks the "Toggle fly mode" button, the mod window
     keeps focus, so arrows and mouse never reach the game.
3. **Cause 3: the mod's key-binding patch is a no-op.** (VERIFIED) It
   patches the wrong pairs.
   - The free camera pairs are **30 and 65** (bit 0x40000) and **32 and
     67** (bit 0x100000).
   - They are not 25/47 and 27/49. Pairs 25/27/47/49 hold other bits, so
     the guard `*(e+4) == bit` never matches.
4. **The fix: drive the game's own toggle.**
   - Either OR bit 0x40000 into the input manager's pad word in PreUpdate,
     or bind F10/F11 into pairs 30/65 and 32/67.
   - Then make ImGui let go of focus.
   - Section 1.7 has the exact recipe.

**B.** The panels can be shrunk and moved with three persistent TextUtil
floats, then one `TextUtil_resize` call (section 2.4).
- The floats are scale +0x8c (default 0.5), x +0x84 (default 3.0) and y
  +0x88 (default 0.0).
- Only the TextUtil constructor and setPosition write them, so the values
  survive every later setMode or resize.
- No hook is needed. Mirroring in ImGui is easy as well: the TextUtil line
  array is a plain PString array (section 2.5).

**C.** eventid.bin has **402 event ids**, internal names up to 12 chars.
- Best runtime source: call `initEventList()` once, then read the 16-byte
  table.
- `AtelGetEventName` opens and reads the file on every call and **does no
  bounds check: ids >= 402 copy an arbitrary length into a global
  buffer**.
- There is no per-event human-readable location table. The save screen's
  place name is a separate "save dic" id that the scripts set at runtime
  (section 3.4).
- Recommendation: read at runtime, ship no table.

**D.** "grid00" is not an event.
- `mapChange("grid00")` jumps to **event 0, "event00"**, the event a
  `debug` boot starts in.
- The viewer state is just `ViewerManager` defaults (+0x1c "c001", +0x30
  "grid00"). It only changes when Map Switch is used.
- So the "grid00" gates pass on any map until then.
- 26 event ids have no shipped .ebp script. Never jump to them (section
  4.3).

**E.** Late `FFXSetDebug(1)` + `Sg_DbInitAll()` is safe (VERIFIED code).
- `Sg_DbInitAll` is a cdecl() 64 KB user-heap allocation, re-runnable
  (leaks 64 KB).
- Turning the flag off is safe. Clear 0x013007b1.
- `initEventList` can be called late (cdecl(), runs once).
- Stage0 passes everything after `--` to FFX.exe, so `fhstage0.exe
  <FFX.exe> -- debug` boots debug.
- **That boot starts in event 0 instead of the title, and never enables
  field-map drawing** (`Sg_SetthDispEnable(1)` is skipped). Prefer the
  runtime toggle (section 5).

**Corrections to overlays.md:**
- 4.1 pair indices: 30/65 and 32/67, not 25/27 and 47/49.
- 4.1 "pad 3's sticks": `PInput_GetDevice(3, 0)` is the **touch** device.
  No gamepad input drives the fly camera.
- 4.1 mouse buttons: +0xa0 = left, +0xa1 = right, +0xa2 = middle.
- 1.3: the "grid00" compare is in **CharacterTextureAnimation**'s
  handleInput 0x006b8a20 (and its updateText 0x006bc850). CharacterSwitch
  (0x006b8820) has none.
- 4.1 "FreeCamera_attach sets target+0xb4 = cam+4": that is the **fly
  controller** attaching to the free camera's PCamera, not the scene camera
  (1.4).

---------------------------------------------------------------------------

## 1. A. The free camera

### 1.1 Objects and fields (VERIFIED)

| Item | VA / off | Notes |
|---|---|---|
| `g_FreeCamera` | 0x00ccc830 / 0x8CC830 | ptr. Made on demand by `FreeCamera_CreateInstance_0042f260` (cdecl() -> ptr, FhCall `FUN_0002F260_0002F240`) and by many users (`updateInput`, `getViewProjectionMatrix`, render, `graphicGetFreeCameraType`) |
| fc +0 | s32 type | 0 off, 1 fly |
| fc +4 | s32 | last type seen; drives the tip and icon |
| fc +8 | f32 speed | 60.0 (ctor writes 0x42700000). **The mod may write it** to change the fly speed |
| fc +0xc | PCameraPerspective | the camera the scene renders from while the free camera is on. Its world-matrix pointer is at fc +0xc +0xb4 |
| fc +0x15c / +0x160 | f32 | copies of the scene camera's +0x150/+0x154 |
| fc +0x164 | 2nd PCameraPerspective | the render saves the scene camera into it each frame |
| fc +0x2bc | PWorldMatrixFlyController | vtable 0x00b39348 |
| fc +0x338 | u8 | free (photo) pause |
| fc +0x339 | u8 | last pause state (tip 0x2f3/0x2f4, icon 9, FMV pause) |
| fc +0x33a | u8 | "cursor shown" bookkeeping for ShowCursor balance |
| fc +0x33b | u8 | "we paused the FMV" |
| fc +0x33c | u8 | pause-menu mode (`graphicSetFreeCameraMode`, from Pause_Enter/Pause_Update) |
| `g_PhyreScene` | 0x00ccb9d8 / 0x8CB9D8 | ptr. The scene camera is `*(scene+0xdc)` |
| `g_Virtuos_InputManager` | **0x00ccb170 is the object itself** | `getInstance` 0x00631300 = `mov eax,0xccb170`. +0 pad word (u32), +4 previous word, +0x8c..+0x98 sticks, +0x9c pair count (75), +0xa4 pair array ptr (8 bytes each: binding, bit), +0xa8 "last input was pad" |

### 1.2 FreeCamera_handleInput, step by step (VERIFIED, 0x00680030..0x0068030a)

`FreeCamera_handleInput` 0x00680030 / 0x280030:
- `thiscall(FreeCamera* ecx; f32 dt, PCamera* sceneCam)`, **ret 8**.
- FhCall `FUN_00280030_00096330` (d_UnknownFn).
- Its only caller is PhyreScene_animate at 0x00657490 with (app+0x24,
  scene+0xdc).

Each frame it does:
1. **Pause tip.** If +0x338 != +0x339:
   - on: tip 0x2f3, show icon 9, pause a running FMV and set +0x33b;
   - off: tip 0x2f4, hide icon 9, resume the FMV if we paused it;
   - then +0x339 = +0x338.
2. **Type tip.** If +0 != +4:
   - type != 0: tip 0x2ed and show icon 8;
   - type 0: tip 0x2ee and hide icon 8;
   - then +4 = +0.
3. **Movie wait.** If `g_WaitFramesByTime` 0x0112a008 != 0 (a movie camera
   wait): +0 = 0, ClipCursor(NULL), ShowCursor(1) if hidden. The free
   camera cannot stay on during those waits.
4. **Toggle.** If `inputIsPressButton(0x40000)`: type -= 1, and if that
   goes below 0 the type becomes 1. So 0 -> 1 and 1 -> 0.
   - **Turning on:**
     1. GetClientRect, ClientToScreen and `SetCursorPos(centre)`.
     2. `ShowCursor(0)` with the +0x33a bookkeeping.
     3. `ClipCursor(client rect)`, 4 px inset on the right and bottom
        when `g_Settings+8 == 0` (window mode).
     4. `0x0062bea0(app; w/2, h/2)` (stores the centre).
   - **Turning off:** ClipCursor(NULL), ShowCursor(1).
   - **Then, if type == 1 and +0x338 == 0** (asm 0x0068029d..0x006802bf):
     - `Phyre_PCameraOrthographic_copy_00456430(ecx = fc+0xc; sceneCam)`
       copies the scene camera (its world-matrix pointer included);
     - `FreeCamera_attach_0067fda0(ecx = fc+0x2bc; fc+0xc)`: the fly
       controller adopts that camera's current world matrix and the
       camera's +0xb4 now points at the controller's matrix.

     **This is the attach.** It happens only on this path, on
     `setFreePause` turning on with type 0, or on `setFreeCameraMode(1)`
     with type 0.
5. **Fly update.** If type != 0:
   - `PApplication_updateMouseCamera_0062e3a0(app; fc+0x2bc, dt, speed
     fc+8, 1.0f)`;
   - then `Phyre_PCamera_updateViewMatrices(fc+0x10)` (0x006720c0).

**setType alone** (0x00680410 / 0x280410, `thiscall(fc; int type)` **ret
4**, FhCall `FUN_00280410_00096710`, VERIFIED) writes only +0. Type 0 also
does ClipCursor(NULL) and ShowCursor(1). No copy, no attach, no cursor
capture.

Consequences:
- `PhyreScene_render` copies `fc+0xc` into the scene camera every frame
  while `type > 0 || +0x338 || +0x33c` (asm 0x00668ad3..0x00668b8d,
  VERIFIED).
- So the screen shows whatever `fc+0xc` last held: an old pause camera, or
  a never-initialised default camera.
- The fly controller's matrix is not that camera's, so no input moves the
  view.

`PhyreScene_getViewProjectionMatrix` 0x0065e660 resets `sceneCam+0xb4 =
scene+0xfa04` only while all three of +0, +0x338 and +0x33c are clear
(VERIFIED). Turning everything off restores the game camera by itself. No
detach is needed.

### 1.3 Who calls what in PhyreScene_animate (VERIFIED, 0x00657140)

`PhyreScene_animate`: thiscall(scene) (Ghidra: fastcall), plain ret, eax
0. FhCall none.

In order:
1. `Pause_Update(g_PauseRequest)` when `g_PauseRequest` 0x01340850 != 0.
   **It calls Input_UpdateAll, which rewrites the pad word.**
2. IconPanel.
3. **Maps 0x17 (23 "test20", the title), 0x15c (348 "memochek") and 0x15d
   (349 "loopdemo") skip the whole block.** They jump past the free
   camera.
4. Otherwise:
   1. speed-up (`SpdCtrl_Tick`);
   2. the auto-battle, invincible and encounter keys;
   3. bit 0x80000 (HUD toggle);
   4. **`inputIsPressButton(0x100000)` -> `FreeCamera_setFreePause`**;
   5. **if `g_AsyncControllerOn` 0x0133c930 != 0 and (+0x338 or +0x33c or
      type == 2) -> `setFreePause`** (toggles every frame it holds);
   6. **always `FreeCamera_handleInput(fc; app+0x24, scene+0xdc)`** at
      0x00657490.

There is no g_FFXIsDebug or isDebugMode gate.

Gates on the free camera, all VERIFIED:
- the three maps above;
- the PC Esc menu (animate does not run);
- `g_WaitFramesByTime` (forces type 0);
- `g_PauseEntered` 0x0134084c (updateInput clears bits 0x40000/0x100000
  while the pause is entered).

`graphicGetFreeCameraType` 0x006411e0 returns 2 while +0x338 or +0x33c,
else +0. `Sg_MainLoop` returns early on 2, so the world freezes in photo
pause. Type 1 alone does **not** freeze the world: it keeps running while
you fly. The pad word is restricted, though (1.5), so the player cannot
move.

### 1.4 Helper functions (VERIFIED asm)

| Function | VA / off | Convention | FhCall |
|---|---|---|---|
| `FreeCamera_setFreePause` | 0x00680320 / 0x280320 | thiscall(fc), plain ret. +0x338 ^= 1; `Sg_SetPauseAllSounds(on)`; if now on and type == 0: `Phyre_PCamera_assign_004533b0(ecx=fc+0x10; sceneCam+4)`, fc+0x15c/+0x160 = sceneCam +0x150/+0x154, `attach(ecx=fc+0x2bc; fc+0xc)` | `FUN_00280320_00096620` (d_UnknownFn) |
| `FreeCamera_setFreeCameraMode` (0x006803a0, unnamed in Ghidra) | 0x006803a0 / 0x2803A0 | thiscall(fc; int on) **ret 4**. +0x33c = on != 0; same copy + attach when on and type == 0 | `FUN_002803A0_000966A0` |
| `graphicSetFreeCameraMode_00640e20` | 0x00640e20 / 0x240E20 | cdecl(mode) -> the above (Pause_Enter/Update) | none |
| `Phyre_PCameraOrthographic_copy_00456430` | 0x00456430 / 0x056430 | thiscall(dstCam; srcCam) **ret 4**, eax = dst. Base copy (`assign` 0x004533b0 on +4) plus floats +0x150/+0x154 | none |
| `FreeCamera_attach_0067fda0` (`PWorldMatrixController::attach`) | 0x0067fda0 / 0x27FDA0 | thiscall(controller; PCamera* target) **ret 4** -> 0, or 0x16 when already attached to a different target. ctrl+0x74 = target+0xb4, ctrl+0x78 = target+4, target+0xb4 = ctrl+4, the controller's matrix (+0x34) from the target's old matrix (identity when none). Re-attaching to the same target is fine | none |
| `PApplication_updateMouseCamera_0062e3a0` | 0x0062e3a0 / 0x22E3A0 | thiscall(PApplication*; ctrl*, f32 dt, f32 speed, f32 scale) **ret 0x10** | `FUN_0022E3A0_006BDC80` (d_UnknownFn) |
| `Virtuos_InputManager_isPressButton` | 0x00631360 / 0x231360 | thiscall(im; bit) ret 4: `(+4 & b) == 0 && (+0 & b) != 0` | `FUN_00231360_0003B5E0` |
| `inputIsPressButton` | 0x00630cf0 / 0x230CF0 | cdecl(bit) -> movzx of the above on 0x00ccb170 | none |
| `Input_BuildPadMap` | 0x006315c0 / 0x2315C0 | fastcall(im), plain. Clears the array (`0x00631110` copies an empty one) and pushes 75 pairs | none |
| `Virtuos_InputManager_updateInput` | 0x00632a30 / 0x232A30 | fastcall(im) | none |
| `PhyreScene_animate` | 0x00657140 / 0x257140 | thiscall(scene), plain ret | none |

### 1.5 The pad word and the binding pairs (VERIFIED: asm count of the 75 `mov [eax+4], bit` stores in Input_BuildPadMap)

| Pair | Binding source | Bit |
|---|---|---|
| 0-13 | g_Settings+0x60..+0x94 (keyboard first keys) | d-pad, face, shoulders, Start, Select |
| 14-27 | g_Settings+0x98..+0xcc (pad) | same; shoulders 0x8000004, 0x10000001, 0x2000008, 0x4000002 |
| 28, 29 | +0xd0, +0xd4 (F1, F4) | 0x10000, 0x20000 |
| **30** | **literal 0** | **0x40000 (fly toggle)** |
| 31 | +0x220 | 0x80000 (HUD) |
| **32** | **literal 0** | **0x100000 (photo pause)** |
| 33, 34 | +0xe4, +0xe8 | 0x200000, 0x400000 |
| 35 | +0xec | 0 |
| 36-39 | +0xf0..+0xfc | 0x2000000..0x10000000 |
| 40 | +0x100 (Esc) | 0x1000000 |
| 41-48 | +0x38c..+0x3a8 | 0 (unused) |
| 49-62 | +0x124..+0x158 (keyboard second keys) | as 0-13 |
| 63, 64 | +0x15c, +0x160 | 0x10000, 0x20000 |
| **65** | **literal 0** | **0x40000** |
| 66 | +0x168 | 0x80000 |
| **67** | **literal 0** | **0x100000** |
| 68-74 | +0x170..+0x188 | 0x200000, 0x400000, 0, 0x2000000..0x10000000 |

How `updateInput` reads the pairs (VERIFIED). The pair array offset is
`pairs + 8 * index`.
- **Free camera on** (+0x338 or +0x33c or type != 0), Esc menu closed, not
  paused:
  - only these pairs count: 40 (Esc, 0x140), **30 (0xf0)**, **32
    (0x100)**, 31 (0xf8), 29 (0xe8), 28 (0xe0), **65 (0x208)**, **67
    (0x218)**, 66 (0x210), 64 (0x200), 63 (0x1f8);
  - the sticks (+0x8c..+0x98) are zeroed.
  - **So the toggle keys keep working while flying. Everything else of the
    game is blocked.**
- **Normal:**
  - the pad pairs 14-27 are always read;
  - the keyboard pairs 0-13 and 49-62 only when no pad bit and the left
    stick is centred;
  - **pairs 28-40 and 63-70 are always read.** The free camera pairs are in
    this range.
  - Then: Eternal Calm masking, and with `g_PauseEntered` the bits of pairs
    28-34 / 63-69 cleared; +4 = old +0, +0 = new word.
  - With `g_AsyncControllerOn` (0x0133c930) set, +0/+4 are **not**
    written.

Only `PhyreScene_animate` (0x100000) and `FreeCamera_handleInput`
(0x40000) read these two bits (VERIFIED). The PS2 side takes only the low
16 bits: `Pad_ReadCurrDevice` 0x008898a0 stores u16 (INFERRED from its
ffx.sym note). Injecting them is side-effect free.

Rebuilds reset pairs 30/32/65/67 to binding 0:
- **at boot:** FFXApplication_initApplication 0x0042f7f0 and
  PhyreScene_initScene 0x0065f450;
- **later:** 0x00701680 and 0x00701e60 (no static caller: the key-config
  pages, INFERRED).

Re-apply the patch each frame. The mod's existing guard (`bit` matches and
binding == 0) is right once the indices are fixed.

Key bindings: binding >= 0x1c is Phyre key v-0x1c. **F9 = 0x49, F10 =
0x4a, F11 = 0x4b, F12 = 0x4c** (VERIFIED from `g_PhyreKeyToDik` 0x00c30e90:
keys 45..48 = DIK 0x43, 0x44, 0x57, 0x58).
- The game's WndProc swallows VK_F10 (platform.md), so F10 does not open a
  system menu.
- Fahrenheit uses F7, and the mod's window uses F9.

### 1.6 What drives the fly controller (VERIFIED, updateMouseCamera 0x0062e3a0)

Inputs, all from `app+0x2b8` (the PInputMapper):
- +0xa8/+0xac: cursor position (ints);
- +0xb0/+0xb4: per-frame deltas dx, dy (screen pixels; dy positive = down);
- **+0xa0 = left button, +0xa1 = right button, +0xa2 = middle button.**
  These come from the mapper mappings +0xc/+0x14/+0x10 (kinds 0xb/0xd/0xc).
  The mouse getButton 0x006289d0 reads byte `this + id + 0x10d`: 0xb ->
  +0x118 (DIMOUSESTATE rgbButtons[0], left), 0xc -> +0x119 (rgbButtons[2],
  middle), 0xd -> +0x11a (rgbButtons[1], right);
- +0x80: keyboard 0 (the PInputDevice keyboard; its latched array is at
  +0x18, so +0x18 + Phyre key).

Latched keyboard bytes: **+0x57 = key 63 = Left arrow, +0x58 = key 64 =
Right arrow, +0x59 = key 65 = Up arrow, +0x5a = key 66 = Down arrow**
(VERIFIED: the DIK table gives 63 cb, 64 cd, 65 c8, 66 d0).

Controller vtable 0x00b39348 slots (VERIFIED from the bytes and bodies):
- +0x1c = `moveForward(d)` 0x005d7190 (translation -= row2 * d, forward);
- +0x20 = `strafe(a, b)` 0x005d72b0 (translation += row0 * a + row1 * b,
  right/up);
- +0x24 = `rotate(yaw, pitch)` 0x005d7450 (radians; atan2/asin of the
  forward axis, wrapped);
- +0x0c = 0x005d7440 (`ret`, a no-op "stop").

Controls (w, h = the client size app +0x26c/+0x270; speed = fc+8 = 60;
dt = frame seconds):

| Input | Effect | Code |
|---|---|---|
| Mouse move, **no button needed** | look: rotate(-64·dx/w·dt, -64·dy/h·dt) | VERIFIED |
| Cursor within 4 px of the client edge (0 px fullscreen) | constant extra turn toward that edge (edge scrolling). That is why the game clips the cursor | VERIFIED asm 0x0062e472.. |
| Left drag | extra rotate(-2·dx/w, -2·dy/h) (not dt-scaled) | VERIFIED |
| Right drag | dolly: moveForward((2·dx/w + 2·dy/h)·speed). Drag right/down = forward (direction INFERRED) | VERIFIED formula |
| Middle drag | pan: strafe((1/proj+0x44)·(w/h)·(2dx/w)·speed, (1/proj+0x44)·(-2dy/h)·speed) | VERIFIED formula |
| **Up / Down arrow** | moveForward(±dt·speed): forward / back | VERIFIED |
| **Right / Left arrow** | strafe(±dt·speed, 0): right / left | VERIFIED |
| Touch (`PInput_GetDevice(3,0)`, the touch device) | 1 finger (+0x15c): rotate by +0x74/+0x78; pinch (+0x15e): moveForward(+0xcc·speed); 2-finger swipe (+0x15f): strafe | VERIFIED code, gesture names INFERRED |

- **No gamepad input drives the fly camera** (VERIFIED: no pad device is
  read here, and updateInput zeroes the sticks).
- There is no key for straight up/down. Use middle drag, or the mod can
  call strafe(0, v) itself on the game thread through the vtable.

Fahrenheit and input (VERIFIED, `src/runtime/imgui.cs`):
- `h_pinput` hooks `Phyre_PFramework_PInput_Update` (0x00625770, stdcall
  ()). It **returns 0 without updating any device** while
  `FhApi.Input.lock_get() || io.WantCaptureKeyboard ||
  io.WantCaptureMouse`.
- What freezes: DirectInput keyboard, mouse and pads, all of them. The
  mapper then sees zero deltas and the last button states. A key held at
  that moment stays "held".
- `h_wndproc` passes window messages to ImGui first. The game's keyboard
  and mouse are DirectInput (DISCL_NONEXCLUSIVE | DISCL_FOREGROUND), so
  WndProc filtering does not matter. The ImGui overlay is in the game's own
  window, so foreground is kept.
- `ConfigFlags |= NavEnableKeyboard | NavEnableGamepad`. In Dear ImGui that
  makes `WantCaptureKeyboard` true while any ImGui window is focused
  (NavActive). `WantCaptureMouse` is true while hovering an ImGui window
  (INFERRED from ImGui semantics).
- The game centres the cursor when fly turns on. If the mod window covers
  the screen centre, the hidden cursor hovers it and input freezes again.

### 1.7 Recipe: free camera that really works

**Preferred: let the game's own toggle run** (it does copy + attach +
cursor capture + tips).

1. Fly on/off, from PreUpdate (game thread, after updateInput and before
   PhyreScene_animate):
   ```
   if (*(byte*)0x0133c930 == 0                // async controller off (else +0/+4 are not rewritten -> repeats)
       && *(int*)0x01340850 == 0              // no pause request (Pause_Update would overwrite the word first)
       && *(int*)0x0134084c == 0              // not in the pause
       && map not in {0x17, 0x15c, 0x15d}) {  // the block is skipped there
       uint* im = (uint*)0x00ccb170;
       im[0] |= 0x40000;  im[1] &= ~0x40000u; // one fresh press; updateInput rebuilds both next frame
   }
   ```
   - Do this once per request, not every PreUpdate (PreUpdate can fire
     twice in a frame under `g_VoiceHold`; one injection is enough).
   - Next PreUpdate, check `*(int*)fc` changed. If not, report it.
   - The same bit with type 1 turns fly off.
2. Photo pause: calling `FreeCamera_setFreePause(fc)` (thiscall, plain ret)
   from PreUpdate is exactly what the key does. Alternatively inject bit
   0x100000 the same way.
   - Pause first, then fly: the pause attaches; the fly toggle then skips
     the copy (+0x338 set) and flies from the frozen view.
   - Fly first, then pause: the world freezes where you are.
   - All off restores the game camera automatically (1.2).
3. Or bind real keys: each frame, if `*(int*)(im+0x9c) >= 68`, for (pair,
   bit) in **(30, 0x40000), (65, 0x40000), (32, 0x100000), (67,
   0x100000)**: `e = *(nint*)(im+0xa4) + 8*pair; if (*(uint*)(e+4) == bit
   && *(uint*)e == 0) *(uint*)e = key;` with F10 = 0x4a and F11 = 0x4b. Use
   one key per action; pairs 65 and 67 are spares.
4. **Release ImGui focus** right after the button press, or the user's
   arrows and mouse never arrive. Options:
   - `ImGui.SetWindowFocus(null)` after the click;
   - `ImGuiWindowFlags.NoNavInputs` on the main window, so focus no longer
     sets WantCaptureKeyboard;
   - auto-collapse or close the window while fly is on (F9 reopens it);
   - keep it away from the screen centre;
   - tell the user "click the game, outside the window, to fly".
5. Fallback without the bit (cursor not captured; the mouse look and edge
   scrolling then misbehave):
   - when `*(byte*)(fc+0x338) == 0`, call
     `Phyre_PCameraOrthographic_copy_00456430(fc+0xc, *(nint*)(*(nint*)0x00ccb9d8 + 0xdc))`
     (thiscall ret 4; skip if the scene camera is 0) and
     `FreeCamera_attach_0067fda0(fc+0x2bc, fc+0xc)` (thiscall ret 4);
   - then `setType(fc, 1)`;
   - off: `setType(fc, 0)`.
   - Not recommended: an unclipped cursor at or outside the client edge
     makes the camera spin (1.6).

The controls to show the user:
- mouse = look;
- Up/Down = forward/back;
- Left/Right = strafe;
- right drag = dolly;
- middle drag = pan;
- left drag = faster look.

The speed is f32 `fc+8` (60 by default). The game shows tips 0x2ed/0x2ee
and icon 8 for fly, and 0x2f3/0x2f4 and icon 9 for the pause.

Risks:
- Photo pause mutes all sound (`Sg_SetPauseAllSounds`) and freezes the
  shader GlobalTime.
- Cursor hidden and clipped while flying (+0x33a keeps ShowCursor
  balanced; use the game's paths).
- No file, save or Steam effects (VERIFIED callees).
- While flying, the pad word is limited (1.5): the game ignores movement
  and confirm.

---------------------------------------------------------------------------

## 2. B. Tweak panel size and position (TextUtil)

### 2.1 TextUtil layout (VERIFIED ctor 0x006f5b50, resize, render, scrollToVisible)

| Off | Meaning |
|---|---|
| +0x0c | asset: the font holder; `n = *(*(+0xc)+0x30)+4` is the font size TextUtil uses |
| +0x10 | the PBitmapFontText node (0 until the clusters load) |
| +0x18 / +0x1c | the built string (char*) / its length (rebuilt in render when dirty) |
| +0x20..+0x5f | 4x4 world matrix (+0x20 x-scale, +0x34 y-scale, **+0x50/+0x54/+0x58 translation x,y,z**) |
| +0x60..+0x68 | per-line step vector (0, line height, 0); +0x64 = line height |
| +0x70 | u8 dirty |
| +0x74 | line count (& 0x7fffffff) |
| +0x78 | ptr to PString words (char* with low tag bit) |
| +0x7c | title PString |
| **+0x80** | u8 enabled (render and resize do nothing without it) |
| **+0x84** | f32 x position (ctor 3.0) |
| **+0x88** | f32 y position (ctor 0.0) |
| **+0x8c** | f32 scale (ctor 0.5) |

Only the ctor and `TextUtil_setPosition` write +0x84/+0x88/+0x8c (VERIFIED
in the TextUtil code). setPosition is only used by BootText/VideoText.

### 2.2 TextUtil_resize (VERIFIED asm 0x006f6670..0x006f6855)

`TextUtil_resize` 0x006f6670 / 0x2F6670:
- `thiscall(TextUtil*; u32 w, u32 h)`, **ret 8**. eax unused by callers.
- FhCall `FUN_002F6670_001159E0` (d_UnknownFn).

It does:
1. `r = f32((w+0.001)/(h+0.001))`.
2. Returns early unless +0x80, +0x10 and +0xc are set.
3. `k = f32(1/(f32(6n)·r)) · scale(+0x8c)`. The matrix is the identity with
   upper 3x3 diag(k, k, 1). Translation:
   - `x = (+0x84 + r)·(-0.32)`;
   - `y = +0x88·0.1 - 0.8`;
   - `z = 1`.
4. That matrix goes to node+0x128 (`toWorldMatrix3x4` 0x00451190) and to
   +0x20.
5. Line height `+0x64 = f32(n·1.3/(h+0.001)·scale)`.
6. Then `scrollToVisible(lineCount)`. `TextUtil_scrollToVisible` 0x006f6860
   is thiscall(tu; int n) ret 4: translation += step·n, then
   toWorldMatrix3x4 again. **So the block's anchor rises by lineCount ·
   lineHeight.**

Geometry: the font mesh builder (0x00a29770, port `m4r_fonttext`) moves the
pen **down** one font line per newline. So:
- the text grows downward from the anchor;
- the anchor is lifted by N·1.3·n·scale/h;
- the actual per-line advance is about `k · fontLineHeight`, which does not
  depend on h.

Two consequences:
- **The lift grows with 1/h, so higher resolutions lift less and long
  panels sink below the bottom.**
- Only CharacterLighting and PostProcess get the real window size (2.3).

The exact on-screen sizes depend on the font's n and line height, read at
runtime: the formulas are VERIFIED, the screen mapping INFERRED (a 2-D pass
with y up). Directions (INFERRED): larger +0x88 moves the panel **up**;
larger +0x84 moves it **left** (factor -0.32); smaller +0x8c shrinks glyphs
and lift together.

### 2.3 Which w/h each panel passes (VERIFIED from each setEnabled and PhyreScene_resize 0x0066c580)

| Mode | Panel | TextUtil at | w/h fields | Updated on window resize? |
|---|---|---|---|---|
| 0 | CharacterLighting | +0x04 | +0xe0/+0xe4 | **yes** (`CharacterLightingTweakUtil_resize` 0x006aa3d0) |
| 1 | PostProcess | +0x08 | +0x188/+0x18c | **yes** (`PhyreScene_resizeTarget_00698060`, thiscall(pp; w, h) ret 8: stores, then TextUtil_resize if shown +0x180) |
| 2 | CharacterSwitch | +0x04 | +0xdc/+0xe0 | no: stays 1280x720 (ctor) |
| 3 | MapSwitch | +0x04 | +0x1fc/+0x200 | no |
| 4 | CharacterAnimation | +0x18 | +0xc8/+0xcc | no |
| 5 | CharacterTextureAnimation | +0x20 | +0xd8/+0xdc | no |
| 6 | BasicDebug | +0x34 | +0x108/+0x10c | no |
| 7 | BtlDebug | +0x114 | +0x1cc/+0x1d0 | no |
| 8 | BtlChrParamConfig | +0x20 | +0x104/+0x108 | no |
| 9 | GameParamConfig | +0x14 | +0xf0/+0xf4 | no |

Each panel's `setEnabled(1)` calls `TextUtil_setEnabled(tu; shown)` and
then, when shown, `TextUtil_resize(tu; w, h)` (VERIFIED).

### 2.4 Recipe: smaller and/or moved panels (no hook)

On the game thread (PreUpdate), once per panel after init, and again only
if you change the values:
```
tu = *(nint*)panelInstance + tuOff;              // table 2.3
*(float*)(tu+0x8c) = 0.30f;                      // scale (0.5 = original)
*(float*)(tu+0x88) = yUp;                        // e.g. +2..+6 (0.1 NDC per unit) to lift the block
*(float*)(tu+0x84) = xLeft;                      // 3.0 = original
if (*(byte*)(panel + shownOff) != 0)            // apply now; otherwise setMode's setEnabled will
    TextUtil_resize(tu, *(uint*)(panel+wOff), *(uint*)(panel+hOff));
```
- The values persist: setMode -> setEnabled -> resize re-reads them, and
  PhyreScene_resize too.
- To make PostProcess/CharacterLighting behave like the others at any
  resolution, also write their w/h fields as (720·w/h, 720) before setMode.
  A window resize overwrites them; re-apply then.
- An auto-fit the mod can compute (INFERRED, check visually):
  - `n = *(int*)(*(nint*)(tu+0xc)+0x30 → +4)`;
  - the font's line height is `*(float or int)(font+8)` with font =
    `*(*(node+0x158)+0x30)`, node = `*(tu+0x10)`;
  - per-line drop ≈ k·L, lift = N·1.3·n·s/h.
  - Pick s and +0x88 so the top stays below 0.95 and the bottom above
    -0.95.
- Optional hook instead: hook `TextUtil_resize` (thiscall, ret 8), and for
  `this` in the ten panel TextUtils call the original with (720·w/h, 720)
  after setting +0x8c. Not needed for the field-write approach.
- Risks: none known. Display only; resize returns early if the font is not
  loaded.

### 2.5 Mirroring the panels in ImGui (VERIFIED: changeLine, appendLine, rebuild code)

- Lines: `count = *(uint*)(tu+0x74) & 0x7fffffff`; line i = `char*
  (w - (w & 1))` with `w = *(uint*)(*(nint*)(tu+0x78) + 4*i)`; title =
  `*(uint*)(tu+0x7c)` the same way.
- The selected line already has the ">" prefix in its text.
- Read on the game thread (PostUpdate) into a snapshot.
  `TextUtil_changeLine` (0x006f6070, thiscall(tu; i, char*) ret 8, FhCall
  `FUN_002F6070_001152F0`) reassigns PStrings, so the render thread must
  not read them live.
- To hide the in-game text but keep the panel alive: after each setMode,
  `TextUtil_setEnabled(tu, 0)` (0x006f68f0, thiscall(tu; u8) ret 4, FhCall
  none) or write `*(byte*)(tu+0x80) = 0`.
  - The panel's own shown byte stays 1, so its render still refreshes the
    title and its handleInput still runs.
  - `TextUtil_render` (0x006f65b0, thiscall(tu; ctx) ret 4, FhCall
    `FUN_002F65B0_00115920`) returns without +0x80.
- Per-panel formatters, if you prefer calling them: all are `thiscall(panel;
  int line, char* out[0x100])` **ret 8** (VERIFIED ret).

  | Formatter | VA | Lines |
  |---|---|---|
  | BasicDebug | 0x006b69a0 | 13 |
  | BtlChrParamConfig | 0x006b6bb0 | 17 |
  | GameParamConfig | 0x006b72e0 | 15 |
  | BtlDebug | 0x006b6ef0 | 6 |
  | CharTexAnim | 0x006b7140 | 6 |
  | CharacterLighting | 0x006a8580 | 11 |

  Reading the TextUtil lines is simpler and covers PostProcess (22 lines)
  and the rest.
- `TextUtil_setTitleText` 0x006f6930: thiscall(tu; char*) ret 4, FhCall
  `FUN_002F6930_00115CE0`.
- `TextUtil_setPosition_006f6910`: thiscall(tu; f32 x, f32 y) ret 8, FhCall
  none (it writes +0x84/+0x88 only).

---------------------------------------------------------------------------

## 3. C. The map / event list

### 3.1 eventid.bin (VERIFIED: file extracted from FFX_Data.vbf and parsed, matching initEventList)

- VBF path `ffx_ps2/ffx/proj/event/header/eventid.bin`; the game opens it
  as `/ffx/proj/event/header/eventid.bin`. 6741 bytes.
- Layout: `count` (offset u32, length u32) pairs from offset 0, then the
  NUL-terminated names. count = first offset / 8 = **402** (ids 0..401).
  The longest name is 12 chars.
- An event id is the map/room number of `AtelSetEventJump2`,
  `g_CurrentMapNo` and Fahrenheit's `event_id`: id 23 = "test20" is the
  title Fahrenheit detects.
- Ids 101 and 111 have empty names.
- The 4-letter prefix is the area code. The ps3data map folder is
  `map/<prefix>/<first 6 chars>/` (e.g. bsil0300 -> map/bsil/bsil03,
  scene-maps.md).

Dev, test and system ids (the rest are area events):

| Group | Ids |
|---|---|
| Boot, viewer and system | 0 event00, 1 startmap0, 2 camtest0, 6/7 sample01/02, 8 sysfp, 9 soundtest, 23 test20 (title), 292 gameover, 348 memochek, 349 loopdemo, 384/385 open0100/0200, 386-388/390 endg0100..0400, 392 matu0000, 393-401 scene1..9 |
| Test events | 3-5 test01-03, 10-15 test10-15, 24-40 test21-37, 66 test38 (a 173 KB script by "nakazato", the largest test), 114 test39, 246 testhikuu, 251 test_yanagi2, 278 test40, 281 testfont, 328 testbattle, 343-346 testpub1-4, 378 zooo0000, 379 testus, 381 isho0000 |

### 3.2 The functions (VERIFIED)

| Function | VA / off | Convention | Notes | FhCall |
|---|---|---|---|---|
| `AtelGetEventName` | 0x00879760 / 0x479760 | cdecl(int id) -> char* 0x01328958 (`mov eax, 0x1328958`) | **Every call opens the file** (`AtelPCOpen` 0x00879cf0, sceLseek/sceRead/sceClose), reads the pair at id·8, then reads `length` bytes into the 0x11c-byte global 0x01328958. **No bounds check: id >= 402 or < 0 reads a junk (offset, length) and can overflow the buffer.** It also dbgPrintf's | `FFX.FhCall.AtelGetEventName` (d_AtelGetEventName(uint) -> char*) |
| `initEventList` | 0x00907f50 / 0x507F50 | cdecl(), plain ret | Once (flag 0x01934ecc): `Sg_PcRead` the file, `user_malloc(count·16)` -> **0x025d5888** (ptr), each 16-byte entry: name `sprintf`'d at +0 (the name is used as the format string; no name has %), id at +0xc; **count -> 0x01934ec8** | `FUN_00507F50_003B8B70` (d_UnknownFn) |
| `mapChange` | 0x00908100 / 0x508100 | cdecl(char* name) | The **last** entry whose name strstr-contains `name` -> `AtelSetEventJump(id, 0)`; if `name` is a substring of "grid00" (also "" or "grid") -> `AtelSetEventJump(0, 0)` | `FUN_00508100_003B8D20` |
| `yiGetMapName` | 0x00907e70 | cdecl(out) | The current map's name from the same table (empty without initEventList) | none |
| `AtelSetEventJump` | 0x0086fec0 / 0x46FEC0 | cdecl(map, entrance) | Refuses unless `*g_ScriptContext & 0x80` or FFXIsDebug | none |
| `AtelSetEventJump2` | 0x0086ff40 / 0x46FF40 | cdecl(map, entrance, fade) | Same gate; fade 0 = default | `FFX.FhCall.AtelSetEventJump2` (hooked by Fahrenheit's game_loop.cs) |

The 16-byte entry quirk: "test_yanagi2" (12 chars) has its NUL overwritten
by the id at +0xc. Read at most 12 chars, or stop at a byte >= 0x80.

`AutoTestManager` (pointer, VERIFIED):
- graphicInitialize -> 0x006bcc40 creates it unconditionally. Its thread
  0x006bd1e0 reads `std::cin` lines.
- "EnableAutoTest" runs `initEventList()` on that thread.
- Other commands ("JumpMap <name>", "SetBattle Enabled/Disabled",
  "Crash" = null write) go to a queue whose executor 0x006bcff0 **has no
  caller or pointer**. Dead. Do not rely on it.

### 3.3 Recommendation

- **Read at runtime, ship no table.**
- The 402 names come from a game file. Names of maps are facts, but a
  generated 402-entry table copied from eventid.bin is extracted game data
  in practice, and the project rule says "no game data in git".
- Runtime always matches the user's version and costs one call.

Recipe:
1. Once, on the game thread: `initEventList()` (cdecl, safe late:
   Sg_PcRead and a 6.4 KB user_malloc, idempotent). This also makes the Map
   Switch panel's `mapChange` work.
2. Then `count = *(int*)0x01934ec8`, `tbl = *(nint*)0x025d5888`, entry i =
   (`cstr(tbl+16i, ≤12)`, `*(int*)(tbl+16i+0xc)`).
3. Cache it in the mod and use it for the jump list. Bound every id to `<
   count`.
4. Alternative without touching game state: call `AtelGetEventName(i)` for
   i < 402, spread over frames. That is 402 file opens; the count must come
   from somewhere, so initEventList is better.

### 3.4 Human-readable location names

- There is no per-event table (VERIFIED code).
- The save screen and menu footer show `AtelGetSaveDicName(AtelGetSaveDic(),
  MsGetSaveConfigEnglish())`:
  - `AtelGetSaveDic` 0x0086c410 is cdecl() -> u16: save event +0xd4 when
    +0xd6, else +0xe, a "save dic" id that the event scripts set at
    runtime. FhCall `FhCall.AtelGetSaveDic` (d_AtelGetSaveDic() ->
    ushort).
  - `AtelGetSaveDicName` 0x0086c430 is cdecl(u16 id, u32 lang) -> byte*
    into the place dictionary (`TOGetSaveDicAdrs`), **FFX-encoded text**
    (decode with `FhEncoding`). FhCall `FhCall.AtelGetSaveDicName`
    (d_AtelGetSaveDicName(ushort, uint) -> byte*).
- **Cheap and useful: show the current place name live** (game thread, both
  calls are reads).
- A per-id table would need each event's script analysed for the place id
  it sets (expensive, INFERRED). Instead, a small hand-written
  prefix -> area table (e.g. bsil = Besaid, luca = Luca, kami = Thunder
  Plains) is pure fact and fine to ship. Fill it from knowledge, not from
  game files.

---------------------------------------------------------------------------

## 4. D. grid00 and the viewer panels

### 4.1 What grid00 is (VERIFIED unless marked)

- The map files exist in the VBF: `ps3data/map/grid/grid00/` (grid00.dae,
  one texture, a 2d variant) and `ffx_ps2/ffx/master/jppc/map/grid/grid00/bin/mapout.vpa`.
- No event is named grid*. **`mapChange("grid00")` jumps to event 0
  ("event00", an 832-byte script by "katano")**, and the debug boot starts
  in event 0 (`Sg_MainInit`: `TkInitScene(0)`).
- So event 0 is the model-viewer scene. INFERRED that it loads map grid00:
  the event -> map number comes from the script through
  `TkGetNowEventJumpMapNo`, not traced.
- `ViewerManager` ctor 0x006b5870 (`g_ViewerManager` 0x00cccac0):
  - +0x1c = "c001" (viewer character), +0x30 = "grid00" (viewer map),
    +0x50 = 1 (UI enable);
  - +0x30 changes only in `0x006b6730` (MapSwitch's Enter; it copies the
    name and calls `mapChange`). It was also reachable from the dead
    AutoTest "JumpMap".
  - **So `strstr(VM+0x30, "grid00")` is true on every map until Map Switch
    is used.**
- `PhyreScene_fieldMapLoad` 0x0065cbf0 sets scene+0xf9c5 = (level name
  does not contain "grid00"). That is a per-map render switch; grid00 gets
  0. Meaning not traced (INFERRED cosmetic).

### 4.2 Getting there

- `AtelSetEventJump2(0, 0, 0)` on the game thread, with the jump allowed
  (ctrl bit 7) or g_FFXIsDebug set.
- **Leaving again:** event00 is tiny and probably never sets the "may leave"
  bit (INFERRED). Keep g_FFXIsDebug on while there (AtelSetEventJump then
  always accepts), or set the bit, or use TkSetEventJump.
- No save point there. Saving from the menu is possible but pointless.
- Nothing else is required: the ViewerManager defaults already say
  c001/grid00.

### 4.3 Do not jump to these ids: no .ebp script in the VBF

Comparison of eventid.bin names with `event/obj/*/<name>/<name>.ebp` under
master/jppc (VERIFIED file list); the failure mode is INFERRED (load
failure, hang or crash):

40 test37, 101 (empty), 111 (empty), 186 bltz0100, 216 bvyt1400,
228/229/231/232/233 lchb0804/0805/0807/0808/0809, 246 testhikuu,
251 test_yanagi2, 262 hiku1700, 300 lchb1900, 304 hiku1200, 342 kami0900,
350 hiku1800, 353/357/358 bltz0003/0007/0008, 360 bvyt1500, 369 znkd1100,
373 ikai0700, 379 testus, 400 scene8, 401 scene9.

The mod's jump list should grey these out. The mod can also check at
runtime: `AtelGetEventName` gives the name, and the mod tests whether the
file exists via the VBF.

### 4.4 The four viewer panels there (VERIFIED code; all need isDebugMode)

- **Character switch (mode 2, handleInput 0x006b8820):**
  - 7 rows "k/c/m/n/s/w/f" + a 3-digit number. Enter -> `0x006b66d0`: if
    different from VM+0x1c, copy it and call `characterChange(name)`
    0x008295e0 (FhCall `FUN_004295E0_002DA450`).
  - characterChange builds the model id `sg_getdbnum(letter) << 12 |
    atoi(digits)` and `Ch_Allocate`s it.
  - It places the model at the player's position and **makes it the
    player** (`Sg_SetPlayer`).
  - Motions: types 1 and 3 load the system motion group plus sys motion
    0x10 (no loop); type 2 tries `/ffx/npcanm/<name>.anm`.
  - The old player actor is not freed. Repeated swaps grow the actor list
    (INFERRED).
  - **With no player, the position comes from uninitialised stack locals**
    (VERIFIED: `Sg_GetPlayerPos` only when a player exists).
  - No grid00 check. Works on any map with the flag.
- **Map switch (mode 3, 0x006b9160):** 42 rows = the prefixes at 0x00b48700
  (azit, bika, bjyt, bltz, bsil, bsmm, bsvr, bsyt, bvyt, cdsp, djyt, dome,
  genk, **grid**, guad, hiku, ikai, kami, kino, klyt, lchb, lmyt, luca,
  maca, mcfr, mcyt, mihn, mmmc, msmm, mtgz, nagi, omeg, ptkl, sins, slik,
  ssbt, stbv, swin, titl, zkrn, znkd, zzzz), each with a 2-digit counter.
  - Enter -> VM+0x30 = prefix + "%02d" -> `mapChange`: the last event whose
    name contains it, entrance 0. "grid00" -> event 0.
  - Needs `initEventList`, else nothing matches (except grid).
  - After the first non-grid jump, VM+0x30 no longer contains "grid00", so
    the texture-animation panel's grid gate fails until "grid00" is picked
    again.
- **Character animation (mode 4, 0x006b85c0):** Motion Mode / Loop / Next /
  Animation Count.
  - `updateAnimation` 0x006bc130 makes the motion id from **VM+0x1c** (not
    from the actual player) and plays it on `Sg_GetPlayer` via
    `ChN_ReadSystemMGRP_ViewerAnimation` / `Ch_SetSysMotion_ViewerAnimation`.
  - It matches only if the player is the VM+0x1c model, i.e. after a
    Character switch (INFERRED).
- **Character texture animation (mode 5, 0x006b8a20):** Debug Output,
  Material Name, Group ID, Animation ID, Current Frame, AutoPlay for
  `TextureAnimationManager_FindAnimation(VM+0x1c)`.
  - setEnabled 0x006bb6d0 only acts with isDebugMode and calls
    `enableGameControlTextureAnimation`.
  - handleInput and updateText need `strstr(VM+0x30, "grid00")`.
  - `TextureAnimation_Animate` draws extra info through
    `ViewerManager_DrawTexAnimInfo_006bc210` when the debug menu mode is 5
    and the flag is set.

Visual and debug only, except that Character switch replaces the player
actor (do not save afterwards) and Map switch warps.

---------------------------------------------------------------------------

## 5. E. The debug flag at runtime

### 5.1 Functions (VERIFIED asm)

| Function | VA / off | Convention | FhCall |
|---|---|---|---|
| `g_FFXIsDebug` | 0x0133c910 / 0xF3C910 | u8 | none |
| `FFXSetDebug` (0x00887c80) | 0x00887c80 / 0x487C80 | cdecl(int on), plain ret. Sets the flag only if byte 0x00b5ec4f != 0 (it is 1) | `FUN_00487C80_00347130` |
| **`Sg_DbInitAll`** | 0x00841440 / 0x441440 | **cdecl(), no args, plain ret** (`ret` at 0x00841465, or tail `jmp 0x00841100`). Only when the flag is set: `user_malloc(0x10000)` (0x00687020 -> UserHeap_Malloc, the game's user heap) -> 0x013032cc and 0x013032d0, then `Sg_DbInitTables_00841100` (fills a 13-entry cos/sin table at 0x01303308/0x013032d4) | `FUN_00441440_002F7890` |
| `Sg_DbDraw` | 0x008412c0 | cdecl(). With the flag: writes `*[0x013032d0] = 0`, walks the list from `[0x013032cc]` (PS2 GS packets, dead on PC), then `d0 = cc` | none |
| `initEventList` | 0x00907f50 / 0x507F50 | cdecl(), once | `FUN_00507F50_003B8B70` |
| `Sg_SetthDispEnable` | 0x00821d50 / 0x421D50 | cdecl(int on), plain ret: byte 0x012fbbb9 = on & 1 | none |
| `Sg_GetthDispEnable` | 0x00820650 | cdecl() -> movsx of 0x012fbbb9 | none |

### 5.2 Calling Sg_DbInitAll late (VERIFIED)

Order in one PreUpdate (game thread, before Sg_MainLoop, so before this
frame's Sg_MainDebug): set the flag (`FFXSetDebug(1)` or write the byte),
then `Sg_DbInitAll()`.

Safety:
- It is only a 64 KB user-heap allocation plus a table. The buffer is never
  freed.
- The fresh buffer is safe: Sg_DbDraw writes the terminator at d0 (= cc)
  before walking.
- **Running it twice** leaks the first 64 KB and resets both pointers.
  Harmless; guard with a "done" bool anyway.
- **It does nothing if the flag is clear**, so the flag must be set first.
- The Sg_Db* writers (0x00841470.. etc.) check the flag first (VERIFIED for
  0x00841470/0x00841490). They are called only from dead debug code, so the
  buffer stays near empty.
- With the flag, Sg_MainDebug also runs `Gu_Main` while 0x012fbbb3 is set,
  `Sg_SaveResourceCheck` and `Sg_Capture`. Per overlays.md 2.2 these are
  harmless while the Gu window list is empty and 0x01301804 = 0.

### 5.3 Turning the flag off again: safe

- Sg_MainDebug simply stops calling Sg_DbDraw. The buffer stays allocated
  and unused.
- The gameplay effects stop: noclip 0x023c44b4 is cleared every frame by
  Sg_ControlMain anyway.
- **Clear `0x013007b1` yourself.** It is "L2 held = alternate exec frames"
  and is only rewritten under the flag (VERIFIED Sg_ControlMain
  0x0082d070), so it sticks at 1 if L2 was held when the flag went off.
- `0x00c496d4` (the frames-per-frame byte the mod's game speed uses) is
  untouched.
- The event list stays built.
- CharTexAnim's setEnabled does nothing without the flag, so turn its panel
  off (setMode 10) before clearing the flag.

### 5.4 Boot-only readers a runtime toggle misses (VERIFIED code, impact INFERRED where noted)

| Reader | With the flag at boot | Matters for a runtime toggle? |
|---|---|---|
| `Sg_InitArgs` 0x00820310 | `Sg_SetUserName(argv[1])` = "debug" (host0:/home/<user> paths) | no |
| `Sg_InitAll` -> `Sg_DbInitAll` | debug line buffer | **yes: call it late (5.2)** |
| `Ch_InitAll` 0x00826ea0 | reads /ffx/proj2/chr/common/*.tbl debug lists (used for tags in Ch_BlkGetHandle) | no (INFERRED) |
| **`Sg_MapInitAll` 0x00844eb0** | copies empty strings into five name slots and **skips `Sg_SetthDispEnable(1)`** | no for runtime (thDisp is already 1). **For a debug boot it means the field BG is never drawn**: thDisp gates yiAnimMain, yiDisp, yiDisp2 and yiDrawScrEffect in Sg_MainLoop. The mod must call `Sg_SetthDispEnable(1)` |
| `Sg_MainInit` 0x00820860 | `initEventList`; first scene `TkInitScene(0)` (event00) instead of `TkSetEventJump(0x17)/TkInitScene(0x17)` (the title) or the "_ECalm" first map; a dbgPrintf | yes for Map Switch: call `initEventList` late. No title in a debug boot |
| MsBattleInit / MsEnvInit / MsDebugInfoRead / sa_cd_callback_init | host0 battle debug reads (btlenv/cdrom .fnd) | battle sheet; the files are not shipped (INFERRED) |

### 5.5 `FFX.exe debug` through Fahrenheit (VERIFIED)

- The argument path:
  1. Game_Main: `CommandLineToArgvW`, then WideCharToMultiByte per argument.
  2. `Phyre_PFramework_PApplication_parseCommandLine` 0x0062adc0 handles
     only '-' and '/' options. INFERRED that it ignores a bare "debug".
  3. `PApplication_Startup` and `initApplication(argv, argc)` store
     +0x3a4/+0x3a8.
  4. `asyncFFXInitialize` -> `0x00822490(argc, argv)`: `argv[1] == "debug"`
     or "DEBUG" sets g_FFXIsDebug = 1, else 0.
- Fahrenheit alpha12 stage0 (`src/stage0/src/main.cpp`, s0_main_process_args):
  - `fhstage0.exe <target> [stage0 args] -- [target args]`;
  - after `--`, the target command line is `"<target>" args`, passed to
    CreateProcessW.
  - So **`fhstage0.exe ..\..\FFX.exe -- debug`** boots debug mode.
  - Without `--` the target gets an empty command line.

`FFX.exe debug` compared with the runtime toggle:

| | `FFX.exe debug` | Runtime toggle (FFXSetDebug + Sg_DbInitAll [+ initEventList]) |
|---|---|---|
| Debug line buffer | yes | yes, via Sg_DbInitAll |
| Event list / Map Switch | yes | via initEventList |
| First scene | **event 0 (viewer), no title screen** | unchanged |
| Field drawing | **off until Sg_SetthDispEnable(1)** | unchanged (on) |
| User name | "debug" | unchanged |
| Battle debug data reads | attempted at boot | not done |
| Gameplay effects (noclip L1, L2/L1+R1 speed, event jumps, New Game scenario 5, battle combo, battle sound base) | from the start | while on |
| Can turn off | yes (5.3), but the boot choices stay | yes |

**Recommendation: use the runtime toggle.** Turn it on only while the viewer
panels or Map Switch are in use.

---------------------------------------------------------------------------

## 6. Handle checklist (new in this round)

| Name | VA / off | Delegate (mod) | FhCall |
|---|---|---|---|
| FreeCamera_handleInput | 0x00680030 / 0x280030 | thiscall(nint fc, float dt, nint cam) ret 8 | FUN_00280030_00096330 |
| FreeCamera_setFreePause | 0x00680320 / 0x280320 | thiscall(nint fc) | FUN_00280320_00096620 |
| FreeCamera_setFreeCameraMode | 0x006803a0 / 0x2803A0 | thiscall(nint fc, int on) ret 4 | FUN_002803A0_000966A0 |
| FreeCamera_setType | 0x00680410 / 0x280410 | thiscall(nint fc, int t) ret 4 | FUN_00280410_00096710 |
| PCameraOrthographic_copy | 0x00456430 / 0x056430 | thiscall(nint dst, nint src) ret 4 -> nint | none |
| FreeCamera_attach | 0x0067fda0 / 0x27FDA0 | thiscall(nint ctrl, nint cam) ret 4 -> int | none |
| TextUtil_resize | 0x006f6670 / 0x2F6670 | thiscall(nint tu, uint w, uint h) ret 8 | FUN_002F6670_001159E0 |
| TextUtil_setEnabled | 0x006f68f0 / 0x2F68F0 | thiscall(nint tu, byte on) ret 4 | none |
| TextUtil_setPosition | 0x006f6910 / 0x2F6910 | thiscall(nint tu, float x, float y) ret 8 | none |
| panel getLineText (6) | 2.5 | thiscall(nint panel, int i, byte* out) ret 8 | none |
| initEventList | 0x00907f50 / 0x507F50 | cdecl() | FUN_00507F50_003B8B70 |
| mapChange | 0x00908100 / 0x508100 | cdecl(byte* name) | FUN_00508100_003B8D20 |
| AtelGetEventName | 0x00879760 / 0x479760 | cdecl(int) -> byte* (bound id < count!) | FFX.FhCall.AtelGetEventName |
| AtelGetSaveDic / AtelGetSaveDicName | 0x0086c410 / 0x0086c430 | cdecl() -> ushort / cdecl(ushort, uint) -> byte* | FhCall.AtelGetSaveDic / AtelGetSaveDicName |
| Sg_DbInitAll | 0x00841440 / 0x441440 | cdecl() | FUN_00441440_002F7890 |
| FFXSetDebug | 0x00887c80 / 0x487C80 | cdecl(int) | FUN_00487C80_00347130 |
| Sg_SetthDispEnable | 0x00821d50 / 0x421D50 | cdecl(int) | none |

Globals: g_FreeCamera 0x00ccc830, g_Virtuos_InputManager (object)
0x00ccb170, g_PhyreScene 0x00ccb9d8, g_AsyncControllerOn 0x0133c930,
g_PauseRequest 0x01340850, g_PauseEntered 0x0134084c, g_WaitFramesByTime
0x0112a008, event table ptr 0x025d5888, count 0x01934ec8, built flag
0x01934ecc, name buffer 0x01328958 (0x11c bytes), g_ViewerManager
0x00cccac0, thDisp 0x012fbbb9, L2 exec flag 0x013007b1.
