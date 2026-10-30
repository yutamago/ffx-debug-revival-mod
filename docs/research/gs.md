# Facts, round 2: bringing back the PS2 debug GUI's original look (GS/GIF packets to ImGui)

Research sheet for the Debug Revival mod (yuta.debugrevival). Scope:
- the packet format the Gu toolkit (main menu WMainProc, the Ms_Gui_* battle
  windows, the rcui windows) and the other PS2 debug drawers build;
- the debug-font texture;
- starting the toolkit safely;
- mouse input;
- the other producers (Sg_Db*, TkDebugFont, opd_*, the rc overlay);
- threading.

Conventions:
- VA = absolute (image base 0x00400000). off = VA - 0x400000 (what
  `FhCallLocation("FFX.exe", off)` takes).
- VERIFIED = I read the code (Ghidra dump, objdump of FFX.exe, raw bytes),
  or ran the original machine code in an emulator (below). INFERRED = deduced.
- "FhCall" = the Fahrenheit **v1.0.0-alpha12** tag (checked with `git show
  v1.0.0-alpha12:...`; the local HEAD is alpha13). `Fahrenheit.FhCall.X` is in
  `src/core/call_1.g.cs` (fused FFX/FFX-2 locations). `Fahrenheit.FFX.FhCall.X`
  is in `src/core/ffx/call_*.g.cs`. Every handle below is
  `FhCallHandle<Fahrenheit.FhCall.d_UnknownFn>` (`cdecl void()`). That
  delegate is right only for zero-argument cdecl functions; for the rest,
  declare your own delegate on the same location.
- All functions here are cdecl with a plain `ret` (no `ret N`), VERIFIED for
  each function listed in section 7 (objdump of their returns, or the caller's
  `add esp`).

**Emulation (new this round, VERIFIED-by-execution).** I ran the original
FFX.exe code in Unicorn: Gu_Init, Gu_WindowCreate(..., WMainProc),
Gui_MasakiInit(n), Gu_Main, the mouse path. Only these were stubbed:
- `user_malloc` / UserHeap: a bump allocator;
- a few CRT imports: sprintf, strcpy, memcpy, ...

I decoded the packets and rasterised them. The harness is in
a local emulator harness (not kept in the repo):
- `emu.py`, `pe.py`: the harness and PE loader;
- `gif.py`: a reference GIF/A+D parser, the parser the mod needs, in Python;
- `render.py`: the rasteriser;
- `run_*.py`: the scenarios.

Run with `uv run --no-project --with unicorn --with pillow python -I run_menu.py`.

Outputs (look at them; they are the target look):
- `menu.png`: the main menu, CHR tab;
- `menu2.png`: after a click on the tab button, the selector popup;
- `basic.png`: a bare window with a title and a button;
- `../debugfont.png`: the decoded debug font.

Unicorn is GPL: dev-only, never ship it.

---------------------------------------------------------------------------

## 0. Summary

1. **One choke point for all Gu windows: `Gu_DrawEnd` 0x008479d0** (VERIFIED).
   - Every Gu window draws inside one `Gu_Main` call per frame into one
     contiguous packet. This covers the main menu, all Ms_Gui_* battle
     windows, the rcui ("PRE2D") windows, Gui_* windows, selectors, message
     boxes and the cursor.
   - `Gu_DrawEnd(chan)` closes the packet and calls the stub `sceDmaSend`.
   - Hook it and call the original. Then the buffer at `[0x013036c4]`
     (g_GuPacket) holds `(QWC+1)` quadwords:
     - qword 0 is a DMA **end** tag with QWC in its low 16 bits;
     - then plain GIF tags: no VIF codes, no REF/CALL tags, no pointers to
       follow.
   - It runs on the **game thread**, inside `Sg_MainLoop` -> `Sg_MainDebug`,
     late in the frame.
   - Do **not** hook `sceDmaSend` globally: it has 46 call sites, many of them
     live PC paths (minimap `rcPrimDraw2D`, `TODrawKick`, `op_gs1_transfer`,
     `ch_TextureLoad`, `Sg_DrawSetEnvironmentInit`, ...). If you hook it
     anyway, filter on return address 0x00847a2a.
2. **The packet is simple.** Only PACKED GIF tags. Registers through A+D:
   - PRIM, RGBAQ, XYZF2, TEST_1, ALPHA_1, TEX0_1, TEX1_1, SCISSOR_1, TEXFLUSH.

   Two PACKED tag forms carry UV+XYZF2 pairs (text and cursor). Primitives:
   - triangles (flat and gouraud, some blended);
   - lines;
   - sprites (one blended "shadow" sprite per window);
   - textured triangles (two per glyph).

   Coordinates are 12.4 fixed point with the GS window offset (2048 - W/2,
   2048 - H/2), W x H = 512 x 416. **Gu y is drawn doubled** (g_GuHScale
   0x200): the logical layout and mouse space is 512 x 208.
3. **One texture: the 128x128 4-bit debug font** (VERIFIED).
   - It is a PS1-style TIM **embedded in FFX.exe .data at 0x00c4dfb8**. No
     file is involved.
   - It holds the ASCII 0x20-0x7f glyphs (8x8 cells), three mouse cursors
     (16x16), icons and a second small font.
   - On PC nothing ever uploads it (`sceGsExecLoadImage` stub, init 0x00851100
     never called), so the TEX0 in the packets has TBP0 = CBP = 0. Map "any
     TEX0 in a Gu packet" to your decoded font.
   - Fahrenheit alpha12 has **no raw-RGBA texture API**. It loads textures
     from disk (DDS/TGA/JPEG/PNG/WIC) or the game's Phyre textures. Simplest:
     write the decoded font as a TGA/PNG into the mod's folder at runtime,
     then `FhApi.Resources.load_texture_from_disk(new FhTexture(path,
     FhTextureType.TGA))` (section 2.4).
4. **Starting it safely needs no g_FFXIsDebug.** Per frame, on the game
   thread: `Gu_VSyncMain()` (or your own mouse feed), then
   `Gu_Main(0, Sg_PacketGetPtr())`. Recommended right after the original
   `Sg_MainDebug`. One-time init:
   - `Gu_Init()` 0x008497a0;
   - **`Sg_SetResource(0x12, 1)`**: otherwise creating the menu runs
     `op_et_bindeff_off_all`, which frees battle bind effects (VERIFIED by
     emulation);
   - `SgGu_LightTableInit()` 0x0084ea00 (optional);
   - `Gu_WindowCreate(0x1b0, 0, 0x50, 0x52, 2, WMainProc)`.

   **Never call 0x0084f100.** It runs `Sg_ClockFromHost` -> `SystemOnHost`,
   which writes to 0x21303d4c and faults. Hook `SystemOnHost` 0x0084ede0 to
   return -1 as a global guard. Filter `WMainProc`'s dangerous ids
   (section 3.5).
5. **Mouse.**
   - Hook `FFXUsbMouseRead2` 0x00855bc0, cdecl `(int port, void* dst)` -> 0.
     Fill dst: +8 buttons (bit0 L, bit1 R, bit2 M), +0xc dx, +0x10 dy,
     +0x14 wheel.
   - The toolkit is relative. Absolute positioning is exact with
     `dx = 2*x - [0x01325248 f32]`, `dy = 3*y - [0x0132524c f32]`, where
     (x, y) is in **512 x 208** logical units:
     `x = cursor_x * 512 / client_w`, `y = cursor_y * 208 / client_h`.
   - VERIFIED by emulation: a click on the tab button opened the tab
     selector.
6. **Other producers:**
   - **Sg_Db\*:** same GIF style through `Sg_GsEnd`, but nothing fills the
     list (dead).
   - **TkDebugFont:** *not* GIF. It is VIF UNPACK data for a VU1 microprogram,
     and its buffer is never set up on PC (NULL). Hook its string writer
     instead.
   - **opd_num digits:** own buffer at 0x02286140, which on PC overlaps live
     battle data: never enable. `op_num` 0x007e63a0 (a 1-byte ret) is already
     called **every frame** with "CAM:/PLY:/DIS:" text by `pppFpInfPrint`:
     hook it for free text.
   - **rc overlay:** same DMA-end + GIF format through `rcPacketDataDraw`.
     Filter on return address. It is gated by one `call FFXIsDebug` at
     0x0092209a.

---------------------------------------------------------------------------

## 1. The packet format, exactly

### 1.1 Producers and where to catch them

| Producer | Buffer | Finisher -> stub | Choke point to hook | Thread / when |
|---|---|---|---|---|
| Gu toolkit (all Gu windows + cursor) | `[0x00c498dc]` (g_RcPrim2DPacket, a 1 MB `user_malloc` block from `Sg_InitAll` at boot; `Sg_PacketGetPtr` 0x0083b650 returns it) | `Gu_DrawEnd` 0x008479d0 -> `sceDmaSend(chan, g_GuPacket)` at 0x00847a25 (return 0x00847a2a) | **`Gu_DrawEnd`** (after the original) or `Gu_Main` 0x00849850 (after the original) | game thread, `Sg_MainLoop` -> `Sg_MainDebug` (or the mod's own call) |
| Sg_Db* debug lines | same buffer (`Sg_GsSetPacket(Sg_PacketGetPtr())`) | `Sg_DbDraw` 0x008412c0 -> `Sg_GsEnd` (call 0x00841406) -> `sceDmaSend` at 0x008417f2 (return 0x008417f7) | `Sg_DbDraw` (after the original). **Not `Sg_GsEnd`**: that is also used every frame by `Sg_DrawFilter`, `Sg_DrawSetEnvironmentInit(Movie)` and `sg_AccSetCurrent` (GS environment packets, no drawing) | game thread, `Sg_MainDebug`, before `Gu_Main` |
| rc overlay (rcui map tool) | `g_RcPrim2DPacket` (same buffer) via `rcPacketDataOpenAdr` | `rcPacketDataDraw` 0x00927600 -> `sceDmaSend(sceDmaGetChan(2), *pkt)` | `rcPacketDataDraw`, only when the return address is **0x0092c22d** (rcbgDrawGuidePackets_0092c130), **0x0092cbe5** (0x0092c960) or **0x0092d219** (0x0092cfa0). Ignore 0x00926b97 (`rcPrimDraw2D`: the live minimap, already drawn on PC) and 0x0092028b (`rcbgRestoreReg`: register restore) | game thread, op OT callbacks (`op_func_before_ot_set`) |
| TkDebugFont (script debugger) | `*g_TkDebugFontBuffer` 0x0133c8cc: **NULL in release** | none (`TkRegistDebugFontDmaTag` 0x0088f760 has no caller; `TkDrawDebug` 0x0088d4e0 = `ret`) | hook the string writer `TkPutDebugFontStringCore_0088f240` and **skip the original** (section 5.2) | game thread (script debugger pages) |
| opd_num digits | 0x02286140 (overlaps battle data on PC) | `opd_num` 0x00a5d3c0 -> `sceDmaSend(DAT_022881d0, 0x02286140)` | do not; hook `op_num` 0x007e63a0 (text) | game thread |

`sceDmaSend` 0x0062f710 is `c3` + 15 x `cc`. `sceDmaGetChan` 0x0062f6e0 is
`xor eax,eax; ret`, so the "channel" is always 0 on PC. The Gu channel is
`[0x023cbbf4]` = `sceDmaGetChan(2)` (Sg_MainInit), i.e. the GIF channel (2)
on PS2 (VERIFIED). The mod can ignore it.

### 1.2 Gu packet layout (VERIFIED: code + emulation dump)

The buffer is an array of 16-byte quadwords (qw).

**qword 0, the DMA tag**, written by `Gu_DrawEnd`:
- word0 = `(n+1) | 0x70000000`: QWC = n+1 (bits 0-15), ID = 7 "end"
  (bits 28-30).
- words 1-3 are **never written by Gu**: stale from earlier users of the
  buffer. Ignore them (no ADDR, no VIF codes).
- Total bytes = (QWC+1)*16.
- Typical sizes, measured: the main menu is 2127 qwords = 34 KB; BattleConfig
  is 7723 qwords = 124 KB. The buffer is 1 MB.

**qword 1 onwards:** GIF tags, each followed by its data. The whole run is
processed as one PATH3 transfer.

> **EOP does not end the stream.** The text tags have EOP=1 in the middle of
> the packet. Keep parsing until QWC is used up.

The last tag is always `00008001 10000000 0000000f 00000000` (NLOOP 1, EOP 1,
NREG 1, REGS NOP) plus one zero data qword.

**GIF tag fields** (standard GS layout, all Gu tags are hand-written
constants):
- w0 bits 0-14 NLOOP, bit 15 EOP; bits 16-31 unused and **may be stale**
  (`Gu_Printf` ORs into a stale qword).
- w1 (= bits 32-63):
  - bit 14 PRE;
  - bits 15-25 PRIM;
  - bits 26-27 FLG (0 PACKED, 1 REGLIST, 2/3 IMAGE);
  - bits 28-31 NREG (0 means 16).
- w1 bits 0-13 are unused and **may be stale**.
- w2|w3 = REGS: 4-bit register ids, low nibble first.

**Gu uses only FLG = 0 (PACKED).** Every GIF tag Gu emits:

| Writer | Tag (w0 w1 w2 w3) | Meaning |
|---|---|---|
| `Gu_DrawStart` 0x00847b00 (qword 1), `Gu_SetScissor` 0x00848b40, `Gu_SetTexFlush` 0x00848d10 | `00000001 10000000 0000000e 00000000` | NLOOP 1, NREG 1, A+D: one register write |
| `Gu_PacketBeginTag` 0x00848530 / `Gu_PacketEndTag` 0x00848460 | `000004d2 10000000 0000000e 0` then NLOOP patched to (qwords written - 1) | NLOOP n, NREG 1, A+D: a run of A+D writes (PRIM/RGBAQ/XYZF2/TEST/ALPHA) |
| `Gu_DrawCursor` 0x00847420 (window shadow) | `3039 -> n`, `10234000`, `e` (emu: `00000004 10234000 0000000e 0`) | PRE=1 PRIM 0x046 (sprite + ABE), A+D run |
| window frame 0x00846b10 | `3039 -> n`, `1025c000`, `e` | PRE=1 PRIM 0x04b (triangle, gouraud, ABE), A+D run, later A+D PRIM=1 (lines) |
| title-bar fill 0x00847b50 | `3039 -> n`, `1005c000`, `e` | PRE=1 PRIM 0x00b (triangle, gouraud, opaque), A+D run |
| `Gu_PrintBegin` 0x00849170 | `00000001 40000000 0000eeee 00000000` | NLOOP 1, NREG 4, 4 x A+D: TEST, TEX0_1, TEX1_1, RGBAQ |
| `Gu_Printf` 0x00848880 (text) | `(stale \| 0x8000), (stale & 0x3fff) \| 0xc089c000, 43434343, 00004343`, NLOOP := glyph quads (emu: `00008008 c089c000 43434343 00004343`) | **EOP=1**, PRE=1 PRIM 0x113 (triangle, TME, FST=UV), NREG 12, REGS 3,4 x6 = 6 (UV, XYZF2) pairs = 2 triangles per glyph per loop |
| `Gu_DrawMouseCursor` 0x00847a40 | `00000001 f089c000 34343eee 04343434` | NLOOP 1, PRE=1 PRIM 0x113, NREG 15, REGS e,e,e then (3,4) x6: 3 A+D (TEST, TEX0_1, RGBAQ), then 6 UV/XYZF2 pairs |
| `Gu_DrawEnd` | `00008001 10000000 0000000f 00000000` + zero qword | end marker |

**Data qwords:**
- **A+D (reg 0xE):** w0|w1 = 64-bit register value; w2 low byte = GS
  register address; w3 = 0.
- **PACKED UV (3):** U = w0 & 0x3fff, V = w1 & 0x3fff (10.4 texels).
  **w2/w3 are not written** by `Gu_DrawSprite` (stale): ignore them.
- **PACKED XYZF2 (4):** X = w0 & 0xffff, Y = w1 & 0xffff (12.4),
  Z = (w2 >> 4) & 0xffffff, F = (w3 >> 4) & 0xff, ADC = (w3 >> 15) & 1
  (ADC=1: no drawing kick). Gu writes w2 = w3 = 0.

A+D registers Gu writes, with value layouts (VERIFIED from the shift code):

| Addr | Reg | Layout | Values Gu uses |
|---|---|---|---|
| 0x00 | PRIM | bits 0-2 type (0 point, 1 line, 2 line strip, 3 tri, 4 tri strip, 5 fan, 6 sprite), 3 IIP (gouraud), 4 TME, 5 FGE, 6 ABE, 7 AA1, 8 FST (UV), 9 CTXT, 10 FIX | `Gu_SetPrim(1)` lines, `(2)` line strip, `(6)` sprite (0x0084bc00, 0x0084dac0, 0x00853ec0, 0x00855020, 0x00855290, 0x008554b0, rc tool); frame writer sets PRIM=1 by A+D |
| 0x01 | RGBAQ | R 0-7, G 8-15, B 16-23, A 24-31, Q 32-63 (float) | `Gu_SetRgbaq(r,g,b,a)` = r \| g<<8 \| b<<16 \| a<<24 \| 0x8000<<32 (Q is garbage, unused with FST/no texture) |
| 0x04 | XYZF2 | X 0-15, Y 16-31 (12.4), Z 32-55, F 56-63 | `Gu_SetXyzf2(x, y)`: X = ((4096-W)/2 + originX + x)*16, Y = ((4096-H)/2 + ((HScale*y)>>8) + originY)*16, Z = F = 0 |
| 0x06 | TEX0_1 | **PC (MSVC 32-bit bitfield) layout, not the GS manual's**, see below | debug font only |
| 0x14 | TEX1_1 | LCM bit 0 | 1 |
| 0x3f | TEXFLUSH | - | 0 |
| 0x40 | SCISSOR_1 | X0 0-10, X1 16-26, Y0 32-42, Y1 48-58 (window pixels, no offset, inclusive) | `Gu_SetScissor(x0,x1,y0,y1)`: y bounds scaled by HScale/256; low bounds < 0 -> 0; x1 <= 0x1fd, y1 <= 0x19e. Global = `019e000001fd0000` |
| 0x42 | ALPHA_1 | A 0-1, B 2-3, C 4-5, D 6-7, FIX 32-39 | 0x44 = (Cs-Cd)*As/128 + Cd (normal blend, frames); `0x56_000000a9` = (Cd-0)*FIX/128 + 0: darken by 0x56/128 (window drop shadow) |
| 0x47 | TEST_1 | ATE 0, ATST 1-3, AREF 4-11, AFAIL 12-13, DATE 14, DATM 15, ZTE 16, ZTST 17-18 | DrawStart (1,1,0,0,0,0,1,2); frames (1,1,0..); text `Gu_SetTest(1,5,0x7f,0,0,0,0,1)` = alpha test GEQUAL 0x7f (drop transparent texels); cursor (1,7,...) |

**The TEX0 quirk** (VERIFIED from the mask arithmetic in `Sg_SetDebugTEX_0`
0x00851160). The PC build sets TEX0 through C bitfields, packed by MSVC in
32-bit units. Fields cannot straddle a word, so TH moves to word 1 and bits
30-31 of word 0 and bit 31 of word 1 are untouched padding:
- **word0:** TBP0 0-13 = u16 `[0x01325172]`, TBW 14-19 = (`[0x01325220]` &
  0x1f80) >> 7, PSM 20-25 = 0x14 (PSMT4), TW 26-29 = 7 (128).
- **word1:** TH 0-3 = 7 (128), TCC 4 = 1, TFX 5-6 = 1 (DECAL), CBP 7-20 =
  `[0x01325170]` & 0x3fff.
- `Gu_PrintBegin` then clears word1 bits 5-6, giving TFX = 0 (MODULATE) for
  text.
- Emulated values: text `1d400000 00000017`, cursor `1d400000 00000037`
  (TBP0/CBP/TBW 0 on PC).

So: **do not decode TEX0 with the GS manual's layout.** Any TEX0 in a Gu
packet means "debug font". Text = modulate, cursor = decal (word1 bit 5).

### 1.3 Coordinates (VERIFIED)

- **Screen size:** g_GuScreenSize `[0x023cbbe0]` = H, `[0x023cbbe4]` = W.
  Set every frame by `Sg_SetScreenWH` from the table at 0x00c494ac:
  - 512 x 416 normally;
  - 640 x 448 while `[0x012fb8fb]` (movie mode) is set, but then
    `Sg_MainDebug` is not called.

  Read W/H live when you snapshot.
- **Pixel position** = X/16 - (2048 - W/2), Y/16 - (2048 - H/2), i.e.
  X/16 - 1792 and Y/16 - 1840. This matches the GS XYOFFSET the game sets
  (`Sg_GsXYOFFSET` 0x00842000: OFX = (0x800 - W/2)*16). The Gu packet itself
  never writes XYOFFSET/FRAME/ZBUF.
- **Vertical doubling:** `g_GuHScale` 0x00c4df54 (i32, 8.8) is 0x200 in the
  image. Every window's +0x8e is 0x200 (`Gu_WindowCreate`), and
  `Gu_DrawWindowTree` sets it per window; `Gu_Main` sets 0x200 before the
  cursor. So **every Gu y and height is drawn x2**, while x is x1.
  - The layout space and the hit-tests (`Gu_MouseInRect`, `Gu_WindowMouseIn`)
    use **unscaled** y.
  - The logical space is therefore 512 x 208.
  - Emulation: window (0x40,0x20, 0x80x0x30) is drawn at (64,64)-(192,160);
    cursor at mouse (100, 40) is drawn at (100, 80).
- **To screen:** the game stretches its PS2-space (512 x 416) UI over the
  whole back buffer: x * 3.75, y * 2.596 on 1920x1080
  (`graphicUiElementRemap`, docs/formats/ui-draw.md). Mod:
  `sx = px * DisplaySize.x / 512`, `sy = py * DisplaySize.y / 416`.
- **Glyphs:** 8 x 8 texels, drawn as 8 px wide x 16 px tall (at print size
  0x100). The cursor is 16x16 texels drawn 32 x 32 px.

### 1.4 Colour, alpha, texture semantics (VERIFIED code; GS rules INFERRED from the PS2 GS manual)

- **ABE = 0** (PRIM bit 6 clear): opaque. This holds for lines/sprites from
  `Gu_SetPrim`, the title-bar fill 0x00b, and text/cursor 0x113. The vertex
  alpha (0xff, 0xc0, ...) is ignored.
- **ABE = 1:**
  - frames 0x04b with ALPHA 0x44: normal blend, alpha = min(A, 0x80)/128.
    Gu frames use A = 0x80 = opaque; `g_GuWindowGlobalTrans` 0x01303730
    (0..0x80) fades windows (`Gui_WindowSetGlobalTrans`).
  - shadow sprite 0x046 with ALPHA 0xa9/FIX 0x56: destination x FIX/128.
    In ImGui: black with alpha 1 - 0x56/128 = 0.33.
- **Flat shading (IIP = 0):** use the **last** vertex's colour.
  **Gouraud (IIP = 1):** per-vertex colours.
- **Textured (text):** TFX = MODULATE: out = texel * vertex / 128 (vertex
  colours are 7-bit, 0..0x7f, from `Gu_PrintSetColor` / `%~c`).
  - In ImGui: vertex colour = min(255, 2*c), texture white.
  - TCC = 1 + alpha test GEQUAL 0x7f: CLUT index 0 is dropped.
  - Cursor: DECAL, i.e. texture as-is.
- **Vertex queue (GS semantics):** writing PRIM (A+D 0x00 or PRE=1) resets it.
  - lines: 2 vertices;
  - line strip: keep the last 1;
  - triangle: 3;
  - triangle strip: keep the last 2;
  - fan: keep the first and the last;
  - sprite: 2, opposite corners, colour of the 2nd.

  XYZF3/XYZ3 (A+D 0x0c/0x0d) and PACKED ADC=1 add a vertex without drawing
  (used by `Sg_GsXYZF2L` for clipped Sg_Db points and by rc).
- **Draw order** = packet order (painter's algorithm). Z is always 0; ignore
  TEST's depth part.
- **Scissor:** apply each SCISSOR_1 as a clip rect until the next one. Gu sets
  a client clip per window and restores 0..0x1fd x 0..0x19e after it.

### 1.5 What each Gu draw emits (VERIFIED; the emulated dump confirms)

| Function | VA | Emits |
|---|---|---|
| Gu_DrawStart | 0x00847b00 | buffer qw1 = A+D tag, qw2 = TEST (1,1,0,0,0,0,1,2); g_GuPacket = g_GuPacketData = buffer; count = 2 (qw0 left for the DMA tag) |
| Gu_SetTexFlush, Gu_SetScissor | 0x00848d10, 0x00848b40 | tag + 1 A+D each |
| Gu_DrawWindowTree | 0x0084ac80 | windows back to front (recursion on +0 first): per window `Gu_DrawCursor(rect,6,3)` (shadow sprite), the frame 0x00846b10 (2 gouraud blended triangles, light 0xe0e0e0 top-left to 0x808080/0x80c080; 4 lines 0xc0c0c0/0x404040 bevel), client SCISSOR, title fill 0x00847b50 (opaque gouraud, 0xe0e0e0 / 0x8080ac / 0x303030), title/control text, the window proc's own draws (message 0x8011), controls (`0x00847720`: frame + text), then full-screen SCISSOR |
| Gu_Printf (fmt, ...) | 0x00848880 | `Gu_PrintBegin` (tag eeee: TEST, TEX0, TEX1, RGBAQ) + the text tag; per glyph `Gu_DrawSprite_00846840` = 6 (UV, XYZF2) = 2 triangles; bold draws each glyph twice (+1 px); italic skews the top edge by size*4/256 |
| glyph UVs | 0x00849070 | c in 0x20..0x7f: u = ((c-0x20) & 15) * 8, v = ((c-0x20) >> 4) * 8, du = dv = 8 texels; width size*8>>8 (8), height (HScale*(size*8>>8)>>8)*size>>8 (16 px); advance size*8/256 + 1 (9 px); '\n' = newline (+9 logical, 18 px) |
| Gu_DrawMouseCursor | 0x00847a40 | when `g_GuMouseEnable` 0x01303760: HScale 0x200, the cursor tag, TEST (1,7,...), TEX0 (decal), RGBAQ 0xff00ff00 (unused with decal), sprite (mouseX, mouseY, w 0x20, h 0x10, u 0, v 0x30, du 0x10, dv 0x10); hotspot = top-left |
| Gu_DrawEnd | 0x008479d0 | end marker + DMA tag, FlushCache(0), sceDmaSend, sceGsSyncPath (stubs) |
| custom drawers (selectors, sliders, light editor, MemGraph, ProgAvarage, rc tool) | 0x0084bc00, 0x0084d730, 0x0084dac0, 0x00853ec0, 0x00854f60, 0x00855020, 0x00855290, 0x008554b0, 0x0093xxxx | `Gu_PacketBeginTag(win)` + `Gu_SetTest(1,5,0x7f,0,..)` + `Gu_SetPrim(1/2/6)` + `Gu_SetRgbaq` + `Gu_SetXyzf2` ... + `Gu_PacketEndTag` |

`Gu_PacketBeginTag(win)` sets the origin that `Gu_SetXyzf2` adds: window
+0x14 + border +0x80, and (+0x16 + border +0x82) * HScale >> 8.
`Gu_DrawSprite` (text) does **not** add the origin (`g_GuPrintX/Y` already
include it).

### 1.6 Parser algorithm (what `gif.py` does; enough to write the C#)

```
qwc = u32(buf+0) & 0xffff; i = 1; end = 1 + qwc
state: prim=0, rgba=(0,0,0,0x80), uv=(0,0), queue=[], scissor=full, tex=none
while i < end:
  tag = qw(i++); nloop = w0 & 0x7fff; pre = (w1>>14)&1; prim_t = (w1>>15)&0x7ff
  flg = (w1>>26)&3; nreg = (w1>>28) or 16; regs = nibbles of w2|w3<<32
  if pre: prim = prim_t; queue.clear()
  if flg == 0 (PACKED):  for nloop: for r in regs: d = qw(i++)
      r 0xE: A+D -> reg(d.w2 & 0xff, d.w0 | d.w1<<32)
      r 0x1: rgba = (d.w0&0xff, d.w1&0xff, d.w2&0xff, d.w3&0xff)
      r 0x3: uv = (d.w0&0x3fff, d.w1&0x3fff)/16
      r 0x4/0x5: vertex(d.w0&0xffff, d.w1&0xffff, kick = !(d.w3>>15 & 1))
      r 0xC/0xD: vertex(..., kick=false); r 0x0: prim = d.w0&0x7ff; queue.clear()
      r 0xF: nop; others (ST 2, FOG 0xA, TEX0 6): handle as needed
  elif flg == 1 (REGLIST): nloop*nreg 64-bit register values (raw GS layout),
      2 per qword, odd count padded; skip id 0xE/0xF
  else (IMAGE): i += nloop   (texture upload, skip)
reg(addr, v): 0x00 PRIM (reset queue), 0x01 RGBAQ, 0x03 UV, 0x04/05 XYZF2/XYZ2
  (kick), 0x0C/0D XYZF3/XYZ3 (no kick), 0x06 TEX0 (Gu: font; bit 37 = decal),
  0x40 SCISSOR, 0x42 ALPHA, others ignore
vertex: px = X/16 - (2048 - W/2), py = Y/16 - (2048 - H/2); push (px,py,rgba,uv);
  emit primitive per PRIM type (1.4) with prim's IIP/TME/ABE flags
```

**ImGui output.** For per-vertex colours and textured triangles, use
`ImDrawList.PrimReserve(idx, vtx)` + `PrimWriteIdx/PrimWriteVtx` (or
`AddImageQuad` for glyph quads):
- untextured prims: the font atlas white-pixel UV
  (`ImGui.GetFontTexUvWhitePixel()`);
- textured prims: inside `PushTexture(fontTexRef)` / `PopTexture()`
  (ImGui >= 1.92; alpha12 uses `ImTextureRef`, so 1.92 API, INFERRED);
- `PushClipRect` for SCISSOR (scaled to screen);
- lines: `AddLine` with the screen scale as thickness (about 2-4 px) so they
  match the PS2's 1-pixel lines scaled up.

### 1.7 Test vectors (emulated original code)

Scenario (`run_basic.py`): screen 512x416, `Gu_Init`,
`Gu_WindowCreate(0x40,0x20,0x80,0x30,6,NULL)`, title "TEST",
`Gu_WindowAddButton(w,0x10,"Btn",4,4,0x20,9)`, `Sg_SetMouse(100,40)`,
12 x `Gu_Main(0, buf)`. Dump (`basic.bin`, 268 qwords):

```
q0   7000010b 00000000 00000000 00000000   DMA end, QWC 0x10b
q1   00000001 10000000 0000000e 00000000   tag A+D x1
q2   00050003 00000000 00000047 00000000     TEST_1
q3   00000001 10000000 0000000e 00000000
q4   00000000 00000000 0000003f 00000000     TEXFLUSH
q5   00000001 10000000 0000000e 00000000
q6   01fd0000 019e0000 00000040 00000000     SCISSOR_1 0..0x1fd x 0..0x19e
q7   00000004 10234000 0000000e 00000000   tag PRE PRIM 0x046 (blended sprite), 4 A+D
q8   00000003 00000000 00000047 00000000     TEST
q9   000000a9 00000056 00000042 00000000     ALPHA = darken 0x56
q10  77607460 00000000 00000004 00000000     XYZF2 (70, 70)
q11  7d607c60 00000000 00000004 00000000     XYZF2 (198, 166)   -> shadow sprite
q12  00000016 1025c000 0000000e 00000000   tag PRE PRIM 0x04b, 22 A+D (frame)
...
q58  00000001 40000000 0000eeee 00000000   Gu_PrintBegin tag
q59  000207fb 00000000 00000047 00000000     TEST (alpha test GEQUAL 0x7f)
q60  1d400000 00000017 00000006 00000000     TEX0_1 (PC layout, modulate)
q61  00000001 00000000 00000014 00000000     TEX1_1
q62  ff000000 00008000 00000001 00000000     RGBAQ black
q63  00008008 c089c000 43434343 00004343   text tag: 8 glyph quads (bold "TEST"), EOP
q64  00000200 00000180 00000000 00000000     UV (32,24)   'T'
q65  00007460 00007720 00000000 00000000     XYZF2 (70,66)
...
q250 00000001 f089c000 34343eee 04343434   cursor tag
q251 0002000f 00000000 00000047 00000000     TEST
q252 1d400000 00000037 00000006 00000000     TEX0 (decal)
q253 ff00ff00 00008000 00000001 00000000     RGBAQ
q254 00000000 00000300 00000000 00000000     UV (0,48)
q255 00007640 00007800 00000000 00000000     XYZF2 (100,80)  ... 6 pairs, 32x32 px
q266 00008001 10000000 0000000f 00000000   end marker
q267 00000000 00000000 00000000 00000000
```

The main menu (`run_menu.py`) gives 2127 qwords and 375 primitives:
- 244 textured triangles;
- 84 lines;
- 42 blended frame triangles;
- 4 title triangles;
- 1 shadow.

`menu.png` shows the result.

### 1.8 Hook choice and copy-out (game thread)

- **Recommended: hook `Gu_DrawEnd` 0x008479d0** (cdecl `void(int chan)`).
  - Prologue `55 8b ec 8b 15 c0 36 30 01`: hookable.
  - Call the original. Then `p = *(uint**)0x013036c4`,
    `qwc = p[0] & 0xffff`, copy `(qwc+1)*16` bytes, or parse in place into a
    vertex list.
  - Also store W/H (`0x023cbbe4`/`0x023cbbe0`). Publish the result to the
    render thread with a double buffer and `Interlocked.Exchange`.
- `Gu_Main` returns early without drawing when `g_GuHidden` 0x0130375c is set
  (Gu_Hide / SYS "HIDE"). Clear your snapshot in a `Gu_Main` pre-hook, or
  keep a frame counter, so a hidden GUI stops showing.
- The buffer is also used, earlier in the same frame, by the ppp model draws
  and rcPrimDraw2D.
  - Those consumers copy what they need synchronously: `rcPrimDraw2D` hands
    `graphicDrawUIElement` a stack quad, not a packet pointer (VERIFIED for
    rcPrimDraw2D; INFERRED for ppp).
  - So writing the Gu packet after them is safe, as the original debug build
    did.
  - Copy it inside the hook. Do not keep the pointer.

---------------------------------------------------------------------------

## 2. Textures

### 2.1 The debug font: embedded TIM (VERIFIED)

The source is **FFX.exe .data at 0x00c4dfb8** (file offset 0x00849fb8 in the
Steam build), a PS1 TIM:

| Offset | VA | Content |
|---|---|---|
| +0 | 0x00c4dfb8 | magic 0x10 |
| +4 | 0x00c4dfbc | flags 8 (4-bit, CLUT present) |
| +8 | 0x00c4dfc0 | CLUT block: length 0x2c, x 0, y 0x1e0, w 16, h 1 |
| +0x14 | 0x00c4dfcc | 16 x u16 CLUT (PS1 15-bit BGR, bit 15 STP): `59ca 7fff 0000 7e1f 0 ...` |
| +0x34 | 0x00c4dfec | image block: length 0x200c, x 640, y 0, w 32 (16-bit units), h 128 |
| +0x40 | 0x00c4dff8 | 0x2000 bytes of 4-bit pixels, 128 x 128, **low nibble = left pixel**; ends 0x00c4fff8 |

Decoding, as `Setup1TIM` 0x0083f8e0 does it:
- colour = ((c & 31) << 3, ((c >> 5) & 31) << 3, ((c >> 10) & 31) << 3);
- alpha 0x80 for every entry, then entry 0's alpha forced to 0;
- the never-called init 0x00851100 also writes entry 0 = 0x0000 and
  entry 2 = 0x8000 (0x00c4dfcc/0x00c4dfd0).

Result:
- index 0 = transparent;
- index 1 = white (248,248,248);
- index 2 = opaque black (the outline);
- index 3 = (248,128,248), unused: the image uses indices 0, 1, 2 only.

For ImGui, use RGBA (255,255,255,255) / (0,0,0,255) / transparent.

Layout (`scratch/gs/debugfont.png`, 4x):
- rows v 0..47: ASCII 0x20..0x7f, 16 per row, 8x8 cells;
- v 48..63: three 16x16 arrow cursors at u 0/16/32 (Gu uses u 0);
- v 64..79: 8x8 media/arrow icons;
- v 80..111: a second, smaller font;
- v 112..127: arrows.

Read the bytes from the running process (they are in the image), or from
FFX.exe on disk. Generate the texture at runtime and **never ship it** (game
data).

### 2.2 Where it was meant to go in GS memory (VERIFIED, for completeness)

- **Init** 0x00851100 (no caller, no pointer):
  - `0x0083fed0(0x3c00, 0x2ee0)` sets the VRAM allocators: TBP base
    0x01301be0 = 0x3c00, CBP base 0x01301be4 = 0x2ee0;
  - `Setup1TIM(0x01325170, 0x00c4dfb8)`;
  - clears the printf state 0x01325150..60.
- **Setup1TIM** fills the 0xb4-byte record at 0x01325170:
  - +0 u16 CBP, +2 u16 TBP, +4 u16 TBW-ish, +6 byte bpp 4, +7 CLUT entries;
  - +8 / +0xc heap copies of image and CLUT;
  - +0x10 and +0x60 two `sceGsSetDefLoadImage` packets;
  - +0xb0 u16 pow2 width, +0xb2 pow2 height, +0xb4/+0xb6 pixel w/h.
- **Every frame:** `Send_DebugFont` 0x008510b0 -> `Load1TIM(0x01325170)` ->
  `sceGsExecLoadImage` (both `xor eax,eax; ret`).
- In release 0x01325170 is all zero, so the TEX0 carries TBP0 = CBP = TBW = 0
  (1.2).
- On PS2 the TEX0 would be TBP0 0x3c00, CBP 0x2ee0, PSMT4, 128 x 128, CSM1.
  The mod maps by "it is in a Gu packet" (or PSM 0x14 + TW = TH = 7).

### 2.3 Other textures

None in the Gu path: Gu writes no other TEX0 (VERIFIED: only
`Sg_SetDebugTEX_0` callers 0x00847a40, 0x00849170, 0x00850e80). The rc
overlay's layer drawer 0x0092c960 uses rc VRAM textures (`rcbgTexGetLog2Size`,
`rcbgPalSetNo`) that are uploaded by `rcuiVramWrite` -> `sceDmaSend` and so
never exist on PC. Draw its untextured prims, and outline or skip the textured
ones.

### 2.4 Fahrenheit alpha12: how a mod gets an ImGui texture (VERIFIED, facts only)

- **Public API:** `FhApi.Resources` (`src/core/resources.cs`, class
  `FhResources`):
  - `load_texture_from_disk(FhTexture)`, `load_game_texture_2d/3d/cubemap`,
    `unload_texture`;
  - `FhTexture(string path, FhTextureType type)` with types DDS/TGA/JPEG/PNG/
    WIC/PHYRE;
  - `texture.try_use(out ImTextureRef, out FhTextureMetadata)` gives the
    `ImTextureRef` (TexID = `ID3D11ShaderResourceView*`).
- **No API takes raw pixels.** The runtime implementation
  (`src/runtime/resources.cs`, `FhResourceLoaderModule`) loads with
  DirectXTex (`LoadFromTGAFile`, ...) and `CreateShaderResourceView` on its
  private `ID3D11Device*`.
- **Device access is internal.** `IFhPlatformUser.platform_bind` is
  `internal`; `FhImguiModule` and `FhResourceLoaderModule` hold the device
  privately.
- **Simplest route:** write the decoded 128x128 RGBA font once into the mod's
  directory (`FhModContext.Paths.ModDir` / `ResourcesDir`; `FhModPaths`,
  `src/core/paths.cs`). Uncompressed 32-bit TGA is trivial: an 18-byte header
  and BGRA rows. Then call `load_texture_from_disk` from `render_imgui`
  (needs the device, so call it after the first Present).
- **alpha12 caveat:** if the load fails, `load_texture_from_disk` returns
  without `texture.unlock()`, so the same `FhTexture` stays locked ("texture
  already locked"). Create a new `FhTexture` to retry.
- **Alternative (INFERRED, ImGui DX11 backend internals):**
  `ImGui.GetIO().BackendRendererUserData` points to `ImGui_ImplDX11_Data`,
  whose first field is the `ID3D11Device*`. With it you can create a
  Texture2D + SRV yourself.
- **Crisp pixels (INFERRED):** the DX11 backend samples linearly, so an 8x8
  glyph scaled 3.75x blurs. Either pre-scale the texture by 8 with nearest
  neighbour before saving (1024 x 1024 RGBA, inset UVs half a texel), or set a
  point sampler in an `ImDrawList.AddCallback`. The latter needs the device
  context from `ImGui.GetPlatformIO().Renderer_RenderState`
  (`ImGui_ImplDX11_RenderState`), then
  `ImDrawCallback_ResetRenderState`.

---------------------------------------------------------------------------

## 3. Starting the toolkit safely

### 3.1 What runs it in the original (VERIFIED)

```
Sg_MainLoop 0x00820ae0 (game thread)
  [only if g_PauseOn == 0]  Sg_FrameSync 0x00821e80 ... if g_FFXIsDebug: Gu_VSyncMain 0x0084a460
                               = call Sg_MouseMain 0x00851920; jmp Sg_ClockTick 0x0084ef10
  ... world update, draws, TODrawMessageWindow, TkDrawDebug (ret), op_num_vdisp (flag) ...
  [only if [0x012fb8fb] == 0 (not movie mode)]  Sg_MainDebug 0x008207f0:
      Sg_IdDrawMain; Counters_Flush(4);
      if g_FFXIsDebug:
          Sg_DbDraw                                      (crashes unless Sg_DbInitAll ran with the flag; overlays.md 2.3)
          if !Sg_CaptureGetGuiHide() && [0x012fbbb3]:    capture-hide = [0x01301804] && [0x01301940]
              Gu_Main([0x023cbbf4], Sg_PacketGetPtr()); Sg_SaveResourceCheck()
          if Sg_Capture(): Sg_SetTkAntiXYB(0,1,0x80)   (Sg_Capture idles while [0x01301804] == 0)
  graphicMenuUpdate, FlushCache, sceGsSyncPath, TkSyncV(2), sceGsSwapDBuffDc ...
```

- `[0x012fbbb3]` (u8) is set to 1 at boot by `Sg_MainInit` ->
  `Sg_EnableDrawGui(1)` 0x00820420 (unconditional, VERIFIED). The only other
  writers are dead (opu_debug_check 0x007fd020; 0x00816680 reached only from
  the table at 0x00c491c8; 0x00878b00 behind TkIsDebug = 0). So it is 1.
- `Sg_MainLoop` returns early, before `Sg_MainDebug`, when
  `FUN_006411e0() == 2` (loading). It is skipped entirely while the PC
  (ESC/booster) menu is up (battle.md section 0). During those frames there
  is no Gu packet.

### 3.2 What the toolkit itself needs (beyond whatever the flag agent does)

**g_FFXIsDebug is not needed if the mod drives the toolkit itself**
(VERIFIED: `Gu_Main`, `Gu_VSync`, `Gu_DrawWindowTree`, `WMainProc` and the
Ms_Gui_* procs never test it). Some leaf calls behind menu items do test it
(e.g. `Ch_EnableSetTexture` -> `Gui_ButtonSetTexture`, `Sg_Db*`); those
items then simply do nothing.

Required state:
1. **The packet buffer** `[0x00c498dc]`: allocated at boot by `Sg_InitAll`.
   Always present.
2. **Gu globals** via `Gu_Init` 0x008497a0. It is not in the dump; the bytes
   (VERIFIED):
   ```
   c7 05 38 37 30 01 00 00 00 00   g_GuDragWindow   0x01303738 = 0
   c7 05 3c 37 30 01 00 00 00 00   g_GuResizeWindow 0x0130373c = 0
   c7 05 34 37 30 01 00 00 00 00   g_GuWindowList   0x01303734 = 0
   c7 05 40 37 30 01 00 00 00 00   g_GuPopupWindow  0x01303740 = 0
   c7 05 5c 37 30 01 00 00 00 00   g_GuHidden       0x0130375c = 0
   c7 05 60 37 30 01 01 00 00 00   g_GuMouseEnable  0x01303760 = 1
   e9 ef eb ff ff                  jmp 0x008483d0: g_GuWindowGlobalTrans 0x01303730 = 0x80; ret
   ```
   It has no caller and no pointer; cdecl `void()`; no FhCall handle. Calling
   it after windows exist leaks them; call it once.
3. **Screen size** `[0x023cbbe4]`/`[0x023cbbe0]`: refreshed every frame by
   `Sg_MainLoop` (`FUN_008217d0` -> `Sg_SetScreenWH`).
4. **The resource block** 0x01301bf0:
   - In release `Sg_LoadVramResource` (boot, a `sceGsExecStoreImage` stub)
     finds no 0x29 0xff header, so `Sg_SaveVramResource` clears it
     ("SG:res_dame"): **every Sg_GetResource value is 0** (INFERRED: the boot
     path runs; VERIFIED code).
   - The menu reads:
     - resource 0 (start tab, so CHR);
     - 1 (map dir);
     - 3 (the event name string), 4 (the event id);
     - 0xa / 0xf (user strings: "user" is shown);
     - 0x12 (BINDOFF);
     - 2 / 0x11 (whether 0x0084f100 opens CPU/VU1 and MEMORY).
5. **Light editor table** 0x01303c20: `SgGu_LightTableInit_0084ea00`
   (cdecl `void()`, reads `Sg_LightGetColor`). Only the CHR "Light" window
   needs it; 0x0084f100 calls it after the menu.
6. **The debug clock** (main-menu title "=HH:MM="):
   - words: 0x023cbc24 month, 0x023cbc20 day, 0x023cbc1c weekday,
     0x023cbc30 hour, 0x023cbc2c min, 0x023cbc28 sec;
   - 0x01323d40 frames 0..59;
   - `Sg_ClockTick` 0x0084ef10 (cdecl `void()`) advances it one frame.
   `Sg_ClockFromHost` sets it from the host `date` via `SystemOnHost`
   (fatal). The mod writes those words from `DateTime.Now` instead
   (`sscanf` order VERIFIED at 0x0084f0ab..0x0084f0d5).
7. **What 0x0084f100 additionally does** (VERIFIED disassembly):
   - zeroes 0x023cbc40..48 and 0x023cbc50..58 (CHR selector counts,
     `0x0084ca00`);
   - zeroes 0x01323d48..0x01323d68 (the MAP/USER selector lists;
     `WMainProc` case 9 tests `[0x01323d48] > 0`);
   - `Sg_MapSetDirSelect(Sg_GetResource(1,0))`;
   - the setting-file reader 0x008531a0 ("/ffx/proj/proj/setting/%s.txt"
     from host via 0x0083ae50: skip it);
   - `Gui_ProgAvarage` if resource 2 == 0 and `Gui_MemGraph` if resource
     0x11 == 0.

   All of these are BSS zeros at first open, so skipping them is fine.
   `Sg_Deci2Open` (0x0084eee0 -> stub `sceDeci2Open` = 0) is harmless but
   pointless.

### 3.3 Minimal init sequence: main menu (VERIFIED by emulation; it produced menu.png)

On the game thread, once:
```
Gu_Init()                                   0x008497a0  cdecl void()
Sg_SetResource(0x12, 1)                     0x0083fe10  cdecl void(int id, int v)   <-- BINDOFF guard
SgGu_LightTableInit()                       0x0084ea00  cdecl void()                (optional)
(write the clock words, optional)
win = Gu_WindowCreate(0x1b0, 0, 0x50, 0x52, 2, 0x0084f550)   0x0084a920  cdecl void*(int x,int y,int w,int h,int flags,void* proc)
```
Every frame, on the game thread (recommended: a hook on `Sg_MainDebug`
0x008207f0, after its original; `Fahrenheit.FFX.FhCall.FUN_004207f0` is
d_UnknownFn, which is correct for it):
```
Gu_VSyncMain()          0x0084a460 cdecl void()   (or skip it and drive the mouse yourself, section 4)
Gu_Main(0, *(void**)0x00c498dc)   0x00849850 cdecl void(int chan, void* buffer)
```

**Why the BINDOFF guard** (VERIFIED by emulation trace).
- On 0x8001, `WMainProc` does:
  1. `Gu_ButtonSetToggleValue(win, 0x5c, 1 - Sg_GetResource(0x12, 0))`;
  2. `WMainProc(win, 0x8006, 0x5c)`, which runs `op_et_bindeff_off_all`
     0x007fbc20 when resource 0x12 != 1. That sets `g_OpEtBindOff` = 1 and
     `opu_unit_free_by_id(g_OpusBattle, i + 0x40000400..0x700)` for every
     g_EtChr entry, i.e. **it frees the bound battle effects**.
- With resource 0x12 = 1 it calls `op_et_bindeff_all` 0x007fb940, a no-op
  while `g_OpEtBindOff` == 0.
- Trace: resource 0 calls 0x007fbc20; resource 1 calls 0x007fb940.

The flags argument of `Gu_WindowCreate` (+0x2e):
- 2 = title bar (control 0x8001, top border 0xc instead of 2);
- 2|4 = also a close button (0x8000);
- 8 = resize handle (0x8002);
- 1 = modal (Gu_WindowMouse: client only).

The function then sends `proc(win, 0x8001, 0)` (create). A second window at
the same x/y is shifted by (+0x10, +8).

### 3.4 Opening the battle windows directly

`Gui_MasakiInit(n)` 0x007c86e0 (cdecl `void(int n)`;
`Fahrenheit.FhCall.FUN_003C86E0_0025DBA0`, d_UnknownFn: declare your own
`void(int)`) after `Gu_Init`. It opens window n once; the guard words are
0x01139ee0..0x01139f08 (battle.md section 3). Emulated with zero battle
state:
- **0 (Camera Info), 2 (Reference), 4 (Camera), 6 (BattleConfig), 7 (CHR1),
  8 (CHR2)** create and draw fine (VERIFIED by emulation).
- **1 (BattleInfo), 3 (Console), 5 (BattlePos), 9 (Shake)** fault on battle
  pointers that are NULL outside battle (VERIFIED by emulation):
  - BattleInfo `[0x0112a9c0]+2` at 0x007db67e;
  - Console `MsBtlListEncount` -> 0x0079d190 result +2;
  - BattlePos `MsBtlGetPos` `[0x0112a9b0]` (set by `MsBtlReadSetScene`);
  - Shake `[0x023114c0]+0x18d` at 0x007dffc1.

  **Open these only while `g_BtlStatus` 0x0112a8e0 != 0** and the pointer is
  non-zero. They may also be stale after a battle (INFERRED).

The Ms_Gui procs draw only through Gu (the callees in calls.tsv are Gu/Ms
helpers, sprintf, dbgPrintf), so they land in the same packet.

### 3.5 Dangerous menu items and how to block them

Hook `WMainProc` 0x0084f550 (cdecl `int(void* win, int msg, int id)`;
prologue `55 8b ec 53 57`; `Fahrenheit.FFX.FhCall.FUN_0044f550`,
d_UnknownFn). Return 0 without calling the original for the cases below.
- Message 0x8006 is a button release (click).
- 0x800a is a right-button release.
- 0x8012 is a selector result.

Note that 0x8006 is dispatched to `FUN_0084cf60` (CHR ids) and
`Gu_DebugMenuCommand_00854c60` (EVENT ids) **before** the switch, so the
filter must sit at `WMainProc` entry.

| msg / id | Item | Calls | Why block |
|---|---|---|---|
| 0x8006 / **0x51** | EVENT "CLR FLG" | `AtelInitSaveWork` 0x0086d4e0 | clears 0x1000 bytes of the live save event record (story flags). **DANGEROUS**. Do not hook `AtelInitSaveWork` globally: `TkInitSaveWork` uses it (new game) |
| 0x8006 / **0x2e** | EVENT "Reload" | `AtelSetEventJump(resource4, 0)` 0x0086fec0 | map/event jump (to event 0 by default) |
| 0x8006 / **0x2c** | EVENT "Select" | `gui_eve_GWM_BUTTONUP`: `Sg_PcRead("/ffx/proj/event/header/eventid.bin")`, then the selector result (0x8012/0x2c) -> `AtelSetEventJump` | host file (absent on PC, INFERRED) + jump |
| 0x8012 / **0x2c** | event selector result | `gui_eve_GWM_SELECTOR` -> `AtelSetEventJump` | jump |
| 0x8006 / **0x0f**, 0x800a / **0x0f** | SYS "Capture" (window / right click) | `FUN_00853780` capture window / `Sg_CaptureEnable` 0x0083a470 | then `Sg_Capture` -> `SystemOnHost` (fault). Also hook `Sg_CaptureEnable` to a no-op: safe globally (other caller `Sg_ControlCamera` only with the flag + Select) |
| 0x8006 / **0x53** | SYS "CacheCLR" | `Cache_Clear` 0x00840030 | frees cached models. Not global: `yiResetObj` uses it |
| 0x8006 / **0x29** | MAP "Reload" | `Sg_MapInit` 0x00844d10 | `yiReset` + reload by debug name/dir |
| 0x8006 / **0x09**, **0x1b**; 0x8012 / **0x16**, **0x1b** | MAP/USER selectors | `Sg_MapSetName` + `Sg_MapInit` | same (lists come from host dirs, probably empty: "No items found.") |
| 0x8006 / **0x3d** | SYS "MemDumpD" | `Sg_MemDump(1)` -> `Sg_MemDumpFile` -> `Sg_PcWrite` (sceOpen 0x602 = create/truncate) | may create/truncate a file (INFERRED) |
| 0x8006 / 0x33 | SYS "MemDump" | `Sg_MemDump(0)` -> `Cache_Dump`, dbgPrintf | harmless log (keep) |
| 0x8006 / **0x15** | EVENT "Movie" | `FUN_00941830` movie_view (host files) | unknown/host |
| 0x8006 / 0x2b, 0x5f | MAP/EFF "PRE2D" | `Gui_YanagiInit` (rc tool) | allowed; but rc "Save" -> `rcbgSaveText` -> `SystemOnHost`: covered by the SystemOnHost guard |
| 0x8006 / 0x02, 0x03 (+0x8012 / 3, 0xf) | CHR "CHR New" / "USER" | host directory lists -> `create_chrview` | lists are empty on PC (INFERRED); gate |
| 0x8006 / 0x5c | EFF "BINDOFF" | `op_et_bindeff_off_all` / `_all` | visible effect change in battle; keep but label |

**Global guard (recommended, VERIFIED safe):** hook `SystemOnHost` 0x0084ede0
(cdecl `int(char* cmd, char* out)`; prologue `55 8b ec 56 8b 75 08`;
`Fahrenheit.FhCall.FUN_0044EDE0_00303810`, d_UnknownFn) and return -1 without
calling the original.
- The original writes the DECI2 request through 0x21303d40|... (edi =
  0x01303d40 | 0x20000000), outside the image, so it faults, and then spins on
  0x01303d14.
- Every caller is a debug path: Sg_Capture, Sg_CaptureToFile(Z),
  Sg_ClockFromHost (checks `js`), and the rc save functions (0x00938010..
  0x0093a910).

---------------------------------------------------------------------------

## 4. Mouse input

### 4.1 The read path (VERIFIED disassembly + emulation)

`Sg_MouseMain` 0x00851920 (cdecl `int()`; 0 ok, 1 = "USB mouse error."):
1. `0x00855bf0(0)`: waits on `FFXSifCheckStatRpc` 0x00a44760, a stub
   returning 0.
2. `r = FFXUsbMouseRead2(0, 0x01325280)`; if r != 0: dbgPrintf, return 1.
3. `[0x01325260] = [0x01325288]`: raw buttons.
4. `g_SgMouseWheel [0x023cbc04] += [0x01325294]`. Gu_Main sets it to 0 after
   drawing.
5. `[0x0132525c] = ([0x0132528c] != 0 || [0x01325290] != 0)`: the moved flag,
   read by `Sg_MouseGetMove` 0x00851910. Gu un-hides on movement.
6. f32 `fx [0x01325248] += dx [0x0132528c]`; f32 `fy [0x0132524c] += dy
   [0x01325290]`.
7. `X = ftol(fx * 0.5)`, `Y = ftol(fy / 3.0)`, computed **before** the clamps.
8. Clamp fx to [0, 1024] and fy to [0, 1248]; clamp X to [0, 0x200] and Y to
   [0, 0x1a0].
9. `g_GuMouseX [0x01325240] = X`, `g_GuMouseY [0x01325244] = Y`.
10. Also `[0x023cbc0c] = f32(dx*3/2)` and `[0x023cbc08]`: camera-drag values,
    unused (`Sg_ControlMouseCamera` 0x0083f6f0 is a bare `ret`).

`FFXUsbMouseRead2` 0x00855bc0 (cdecl `int(int port, void* dst)`, plain ret):
- `[0x01325a80] = port`;
- `FFXSifCallRpc(0x01325a40, 4, 0, 0x01325a80, 0x20, dst, 0x20, 0, 0)`;
- returns its eax. `FFXSifCallRpc` 0x00a44750 = `xor eax,eax; ret`, so it
  returns 0 and **dst is never written**.
- Prologue `55 8b ec 8b 45 08 6a 00`.
- `Fahrenheit.FFX.FhCall.FUN_00455bc0`, d_UnknownFn: declare
  `int(int, nint)`.

Layout of the 0x20-byte result dst = 0x01325280:
- +0x00, +0x04: not read;
- **+0x08 u32 buttons:** bit0 left, bit1 right, bit2 middle;
- **+0x0c i32 dx**, **+0x10 i32 dy**, in raw counts; the toolkit moves
  dx/2 and dy/3 pixels;
- **+0x14 i32 wheel** steps (added to g_SgMouseWheel);
- +0x18..+0x1f: not read.

### 4.2 Button edges (`Sg_MouseButtonMain` 0x008517c0, cdecl `u32()`; Gu_Main stores eax into g_SgMouseButtons 0x023cbc10; VERIFIED)

From raw `[0x01325260]` and the previous result (saved in 0x01325250):
- **Left:**
  - bit 0 = down;
  - bit 1 (2) = pressed this frame;
  - bit 2 (4) = released this frame;
  - bit 3 (8) = double click: a press at **exactly** the same
    g_GuMouseX/Y within 50 frames (countdown 0x023cbc00, position
    0x01325254/58).
- **Right:** 0x100 down, 0x200 press, 0x400 release.
- **Middle:** 0x10000 down, 0x20000 press, 0x40000 release.

`Gu_VSync` 0x0084a260 turns these into window messages:
- 0x8005 L held, 0x8007 L press, **0x8006 L release (click)**, 0x8008 double;
- 0x8009/0x800b/0x800a R;
- 0x800d/0x800f/0x800e M;
- 0x8017 wheel.

A press outside every window is captured for `Sg_ControlMouseCamera` (a
`ret`): harmless.

### 4.3 Best hook and absolute positioning (VERIFIED by emulation)

**Hook `FFXUsbMouseRead2`.** On the game thread:
1. `fx = *(float*)0x01325248`, `fy = *(float*)0x0132524c`.
2. Write dst+8 = buttons (bit0 L, bit1 R, bit2 M).
3. dst+0xc = `(int)(2*x - fx)`, dst+0x10 = `(int)(3*y - fy)`.
4. dst+0x14 = wheel steps since the last call.
5. Return 0.

The accumulators always hold integers, so this lands exactly on (x, y). In
the emulated run the mouse reached (468, 27) and a press/release opened the
tab selector (menu2.png); the tab index 0x01324128 changed. Hook this even if
the mod calls `Sg_MouseMain` itself (through `Gu_VSyncMain`), and also when
the flag agent's g_FFXIsDebug makes `Sg_FrameSync` call it. Note that
`Sg_FrameSync` is skipped while `g_PauseOn`.

**Alternative without any hook** (VERIFIED by reading): skip `Sg_MouseMain`
and, before `Gu_Main`:
- `Sg_SetMouse(x, y)` 0x00851ae0 (cdecl `void(int x, int y)`: g_GuMouseX/Y =
  x/y, fx = 2x, fy = 3y; `Fahrenheit.FhCall.FUN_00451AE0_00305970`);
- `[0x01325260]` = raw buttons;
- `[0x0132525c]` = 1 when moved;
- `[0x023cbc04]` += wheel.

This breaks if anything else calls `Sg_MouseMain` that frame: it would copy
the never-written RPC buffer over 0x01325260.

**Coordinate conversion.**
- The mod knows the cursor in back-buffer pixels: `ImGui.GetIO().MousePos`,
  the same space as `DisplaySize`.
- The toolkit's **logical** space is 512 x 208 (1.3): `x = MousePos.x * 512 /
  DisplaySize.x`, `y = MousePos.y * 208 / DisplaySize.y`.
- The cursor sprite then appears with its hotspot exactly under the Windows
  cursor. Hide one of the two cursors (`Gu_MouseSetEnable(0)` 0x00849b80
  hides the Gu cursor).
- Do not use y in 0..416: Sg_MouseMain allows it, but anything below 208 is
  drawn off-screen.

Buttons and wheel come from `io.MouseDown[0..2]` / `io.MouseWheel` on the
render thread. Hand them to the game thread through volatile fields.
- Wheel sign (INFERRED): WMainProc 0x8017 does `tab -= wheel`;
  `Gu_ClientMouse` scrolls 24 per step. Positive = wheel up.
- To keep clicks away from the game, Fahrenheit's `PInput_Update` hook drops
  game input while `io.WantCaptureMouse` (`runtime/imgui.cs` `h_pinput`). An
  invisible full-screen ImGui window while "Gu input mode" is on sets that
  (INFERRED).

### 4.4 How the cursor is drawn

`Gu_DrawMouseCursor` (1.5): sprite from texels (0,48)-(16,64), DECAL, at
(g_GuMouseX, 2*g_GuMouseY), 32 x 32 px, hotspot top-left.
- It is drawn last in the packet (after all windows).
- Only when `g_GuMouseEnable` 0x01303760 != 0 (Gu_Init sets 1).
- Its height uses the **current `g_GuPrintSize`** (0x013036dc, set to 0x100
  by every window's `Gu_FontSetupWindow`). With no window ever drawn it is 0
  and the cursor is invisible (VERIFIED code).

---------------------------------------------------------------------------

## 5. Other packet producers

### 5.1 Sg_Db* debug lines

- **Format** (VERIFIED, `Sg_DbDraw` 0x008412c0):
  - `Sg_GsSetPacket(Sg_PacketGetPtr())`, then `Sg_GsStart`: an A+D tag
    (NLOOP patched by Sg_GsEnd);
  - TEST (1,1,0,...);
  - per record:
    - `1`: A+D PRIM = u16 (Sg_DbLine 0x10001: PRIM 1 lines;
      Sg_DbLineStrip 0x20001: PRIM 2);
    - `2`: A+D RGBAQ (r, g, b, 0x80);
    - `3`: a 3D point: `FFXVu0RotTransPers` with the camera*screen matrix,
      then `Sg_GsXYZF2L(x, y, z)`: A+D **XYZF2 (4) if z >= 0, else XYZF3
      (0xc)**, i.e. points behind the camera do not kick;
    - `4`: a raw 2D point;
  - `Sg_GsEnd` 0x00841770: tag NLOOP, end marker, **qword 0 words 1-3 = 0**,
    DMA end tag, `sceDmaSync`, `sceDmaSend([0x023cbbf4], buf)`.

  Same parser as Gu. The projected X/Y are GS window coordinates (12.4 with
  offset, INFERRED from the screen matrix). No HScale.
- **Gates:** g_FFXIsDebug, plus the buffer 0x013032cc/d0 allocated by
  `Sg_DbInitAll` with the flag.
- **Who fills it:** only `Ch_DrawIcons` 0x0082ec80 (no caller) and the dead
  shapes Sg_DbBox/BoxX/Null/Sphere (VERIFIED: every Sg_Db* caller listed in
  calls.tsv). The menu's "ChrIcon" (EFF 0x5d / CHR 0x47 -> `Ch_SetDrawIcon`)
  only sets a flag for the dead `Ch_DrawIcons`.
- **Verdict:** nothing to show. If the mod draws 3D debug lines it should
  project and draw them in ImGui itself.

### 5.2 TkDebugFont (the script debugger pages): NOT a GIF stream (VERIFIED)

- **Writer** `TkPutDebugFontStringCore_0088f240`: cdecl
  `(int prop, int colour, int scale, byte* s) -> page mask`; prologue
  `55 8b ec 8b 45 14 83 ec 18`; `Fahrenheit.FFX.FhCall.FUN_0048f240`.
  - colour is clamped 0..7, scale 0..0x3ff.
  - Position: `g_TkDebugFontPos` 0x0133c8d4 (x<<4) / 0x0133c8d8 (y<<4), set
    by `TkSetDebugFontLocate(x, y)` 0x0088faf0. Tab origin
    `g_TkDebugFontPos2` 0x0133c8dc/e0 (`TkSetDebugFontLocate2` 0x008904e0).
  - Per glyph it writes a **VIF code `0x6503c000 | 3*i`** (UNPACK V2-16,
    3 vectors, FLG|USN, VU address 3*i) and 3 x (u16, u16):
    - (c & 15) << 8, (c >> 4) << 8: the glyph cell;
    - (x16, y16): the position;
    - (page, 0): Shift-JIS lead byte - 0x80, or - 0xc2 for >= 0xe0.
  - Every 32 glyphs it closes the batch:
    - colour/scale into the last glyph's top u16;
    - then `0x13000000` FLUSHA, `0x17000000` **MSCNT (run the VU1
      microprogram)**, `0x13000000`/0;
    - and a DMA **cnt** tag (0x10000000 | qwc) with VIF `STCYCL 0x01000404`.
  - The frame helper 0x00890500 adds a GIF **REGLIST** tag
    (`8001 84000000 f5151510 0`: PRIM 0x34b, RGBAQ/XYZ2 x3: a gouraud
    blended triangle marker).
  - `TkRegistDebugFontDmaTag` 0x0088f760 would chain:
    - **CALL tags** (ID 5) to 12 font-page upload packets (pointers
      0x023cbfd8..0x023cc004, the system font in VRAM, set up by 0x0088eff0);
    - CALL 0x023cc030/0x023cc02c/0x023cc008(0c);
    - the glyph buffer;
    - an END/RET/NEXT tail.
- **On PC:**
  - The buffers are never set up: `0x0088eed0` (sets 0x0133c8c4/c8/cc) has no
    caller and no pointer (byte scan). So `g_TkDebugFontBuffer` = 0 and **any
    call to a TkPutDebugFont* function dereferences NULL** (crash).
  - There is no VU1 microprogram to turn the glyph records into
    triangles.
- **Revival:** hook `TkPutDebugFontStringCore_0088f240` and **never call the
  original**:
  - record (x = [0x0133c8d4]/16, y = [0x0133c8d8]/16, colour index, prop,
    text);
  - advance x yourself: per char `(prop ? g_FontWidths[c] + 2 : 9)` px,
    13 for two-byte codes, using the u8 table g_FontWidths 0x023cbf6c
    (= `TkGetCharWidth` 0x00890a00);
  - return 0.
  - Also hook 0x00890500 (frame/marker, cdecl 8 args) to skip it; it
    dereferences the buffer too.
  - Draw with ImGui text. The original used the game's system font, not the
    debug font (INFERRED from the page pointers).
- **Who would call it:**
  - the script debugger page drawers 0x0087a4a0..0x00884d90 (script sheet);
  - per frame, `TkDrawDebug` 0x0088d4e0, called by `Sg_MainLoop` just before
    `Sg_MainDebug`. On PC it is a bare `ret` (`c3` + 15 x `cc`: hookable).
    It is the natural per-frame slot (INFERRED that the PS2 build sent the
    TkDebugFont chain there).
- **Correction to facts-script.md:** the page writers emit VIF/VU1 glyph
  records, not GIF sprites.

### 5.3 opd_num / op digits

- **`opd_num`** 0x00a5d3c0: builds digit sprites into 0x02286140 and sends
  them with `sceDmaSend([0x022881d0], 0x02286140)` + `op_gs1_transfer`.
  - Reached only via the `op_num_*` wrappers 0x007e63b0..0x007e65e0, which
    test `g_OpNumShow` 0x02322794 / `g_OpDebugMode` 0x02322790.
  - ffx.sym notes that **0x02286140.. holds loaded battle data on PC**, so
    enabling it would scribble over battle data. **Never enable.**
- **Free text instead** (VERIFIED):
  - `pppFpInfPrint` 0x00729850 is called **every frame** by `pppPartLoop`
    0x00762240 (when `[0x0230fd38]` == 0) from `Sg_MainLoop`.
  - It calls `op_num_xy_set(2, 2)` (wrapper, flag-gated), then
    `op_num(text)` three times with "CAM:%5d,%5d,%5d", "PLY:%5d,%5d,%5d"
    and "DIS:%d".
  - `op_num` 0x007e63a0 is `c3` + 15 x `cc`, cdecl `void(char* text)`, 27
    callers; `Fahrenheit.FFX.FhCall.FUN_003e63a0`.
  - Hook it and show the strings in ImGui. Hook `op_num_xy_set` 0x007e65e0
    (cdecl `(x, y)`; `Fahrenheit.FhCall.FUN_003E65E0_00286490`) for
    positions. Do not call its original, or opd_num_xy_set may run.

### 5.4 rc map tool ("PRE2D")

- **Windows:** `Gui_YanagiInit` 0x00930d80 -> `rcuiInit` ->
  `rcuiWindowCreate` 0x00937ef0 = `Gu_WindowCreate` + `Gu_WindowSetTitle`.
  They are Gu windows: same packet, same hook (VERIFIED).
- **Field overlay:**
  - `Yn_FieldProc` -> 0x00922060 .. 0x009220aa:
    `call FFXIsDebug` **at 0x0092209a (`e8 b1 5b f6 ff`)**, then
    `sceGsSetDefDrawEnv2` (= 1), then `jne 0x0092cf10`.
  - 0x0092cf10 needs g_RcuiWork 0x0193c0f0 != 0. It registers 0x0092c130
    (+0x3c), 0x0092c930 (+0x34) and 0x0092cfa0 (+0x4c) with
    `op_func_before_ot_set`, calls 0x0092c960 (+0x48), and jumps to
    `rcuiVramWrite` (+0xb08).
  - To enable it without the global flag, patch those 5 bytes to
    `b8 01 00 00 00` (mov eax,1). VERIFIED bytes. INFERRED harmless while
    g_RcuiWork == 0.
- **Packets:**
  - `rcPacketDataOpenAdr(pkt, g_RcPrim2DPacket)`: data starts at +0x10;
  - GIF tags (PACKED RGBAQ as 4 words, XYZ2 with **ADC bit w3 bit 15** for
    off-screen points; see `FUN_009278d0`);
  - `rcPacketDataCloseAdr`: tag `00008000 10000000 0 0` (NLOOP 0, EOP),
    then qword 0 word0 = QWC | 0x70000000;
  - `rcPacketDataDraw` -> `sceDmaSend`.

  The generic parser handles them. Filter return addresses as in 1.1.
- **Risk:** rc Save -> `SystemOnHost` (guard it).

### 5.5 Summary: does each go through the Gu choke point?

| Producer | Through `Gu_DrawEnd`? | Hook instead |
|---|---|---|
| Main menu, all its child windows (Light, Fog, MemGraph, ProgAvarage, CHRInfo, Clock, Focus/Blur/Flash/Acc, selectors, message boxes) | yes | - |
| Ms_Gui_* battle windows | yes | - |
| rcui windows | yes | - |
| rc field overlay | no | `rcPacketDataDraw` (return-address filter) |
| Sg_Db* | no (and empty) | `Sg_DbDraw` |
| TkDebugFont | no (VU1, NULL buffer) | `TkPutDebugFontStringCore` (skip the original) |
| op digits | no (dangerous buffer) | `op_num` / `op_num_xy_set` text |

---------------------------------------------------------------------------

## 6. Threading and timing

- **Where it runs:** everything above runs on the **game (main) thread**
  inside `Sg_MainLoop` 0x00820ae0 (Fahrenheit hooks it for
  `FhApi.Events.Common.GameLoop.PreUpdate/PostUpdate`; battle.md section 0).
  The Gu packet is built and "sent" in `Sg_MainDebug`:
  - after the world update, the 3D/2D draws, `TODrawMessageWindow`,
    `TkDrawDebug` and `op_num_vdisp`;
  - before `graphicMenuUpdate`, `FlushCache`, `TkSyncV(2)` and
    `sceGsSwapDBuffDc`.
- **Mouse:** `Sg_MouseMain` runs earlier in the same frame, in `Sg_FrameSync`
  when the flag is set and not paused, or wherever the mod calls it.
- `pppFpInfPrint`'s `op_num` calls run in `pppPartLoop`, inside the draw
  part of `Sg_MainLoop`.
- `render_imgui` runs on PhyreEngine's render thread (`Present` hook) while
  the game thread already runs the next frame. So:
  1. On the game thread, in the `Gu_DrawEnd` hook (or `Sg_MainDebug`
     post-hook): copy or parse the packet, plus W/H, into a frame object.
  2. Publish it with `Interlocked.Exchange`.
  3. On the render thread, draw the latest published frame.

  Keep the last frame when a frame has none (loading frames, PC menu open,
  movie mode). Clear it when the toolkit is closed or `g_GuHidden`.
- All toolkit calls (`Gu_Init`, `Gu_WindowCreate`, `Gui_MasakiInit`,
  `Gu_Main`, `Sg_SetResource`) belong on the game thread. Queue UI requests
  from `render_imgui`.

---------------------------------------------------------------------------

## 7. Function table

Calling convention: cdecl, plain `ret`. Bytes = the first bytes (VERIFIED raw).

| Function | VA / off | Args -> ret | First bytes | FhCall (alpha12) | Use |
|---|---|---|---|---|---|
| Gu_DrawEnd | 0x008479d0 / 0x4479D0 | (int chan) -> void | `55 8b ec 8b 15 c0 36 30 01` | `Fahrenheit.FFX.FhCall.FUN_004479d0` (d_UnknownFn, wrong arity) | **hook**: the packet choke point |
| Gu_Main | 0x00849850 / 0x449850 | (int chan, void* buf) -> void | `55 8b ec e8 68 7f 00 00` (rel32 call: MinHook relocates it) | `Fahrenheit.FhCall.FUN_00449850_002FB8E0` | call per frame |
| Gu_Init | 0x008497a0 / 0x4497A0 | () -> void | `c7 05 38 37 30 01 ...` (10-byte movs) | none | call once |
| Gu_WindowCreate | 0x0084a920 / 0x44A920 | (int x, int y, int w, int h, int flags, void* proc) -> window* | `55 8b ec 8b 0d 34 37 30 01` | `Fahrenheit.FhCall.FUN_0044A920_002FC9B0` | open the menu |
| WMainProc | 0x0084f550 / 0x44F550 | (void* win, int msg, int id) -> int 0 | `55 8b ec 53 57 8b 7d 08` | `Fahrenheit.FFX.FhCall.FUN_0044f550` | proc pointer; **hook**: item filter |
| 0x0084f100 (menu init) | 0x0084f100 / 0x44F100 | () | `e8 db fd ff ff` | none | **never call** |
| Sg_SelectMainMenu | 0x0084f270 / 0x44F270 | (win, page 0..6) | `55 8b ec 53 8b 5d 08` | `Fahrenheit.FhCall.FUN_0044F270_00303C90` | switch tab |
| Gui_MasakiInit | 0x007c86e0 / 0x3C86E0 | (int n 0..9) -> void | `55 8b ec 8b 45 08 83 f8 09` | `Fahrenheit.FhCall.FUN_003C86E0_0025DBA0` | battle windows (3.4) |
| Gui_YanagiInit | 0x00930d80 / 0x530D80 | () (toggle) | `a1 f0 c0 93 01` | `Fahrenheit.FhCall.FUN_00530D80_003CFFB0` | rc tool |
| Sg_SetResource | 0x0083fe10 / 0x43FE10 | (int id, int v) | `55 8b ec 8b 4d 08` | `Fahrenheit.FhCall.FUN_0043FE10_002F2320` | resource 0x12 = 1 |
| Sg_GetResource | 0x0083fbe0 / 0x43FBE0 | (int id, int def) -> u32 | `55 8b ec 8b 4d 08 0f be 04 8d` | `Fahrenheit.FhCall.FUN_0043FBE0_002F2070` | - |
| SgGu_LightTableInit_0084ea00 | 0x0084ea00 / 0x44EA00 | () | `53 8b dc 83 ec 08 83 e4 f0` | none | optional init |
| Gu_VSyncMain | 0x0084a460 / 0x44A460 | () | `e8 bb 74 00 00 e9 a6 4a 00 00` | `Fahrenheit.FFX.FhCall.FUN_0044a460` (d_UnknownFn: correct) | mouse + clock tick |
| Sg_MouseMain | 0x00851920 / 0x451920 | () -> int (0 ok) | `55 8b ec 51 6a 00 e8` | `Fahrenheit.FhCall.FUN_00451920_003057B0` | (called by Gu_VSyncMain) |
| FFXUsbMouseRead2 | 0x00855bc0 / 0x455BC0 | (int port, void* dst) -> int | `55 8b ec 8b 45 08 6a 00 6a 00` | `Fahrenheit.FFX.FhCall.FUN_00455bc0` | **hook**: mouse feed |
| Sg_MouseButtonMain | 0x008517c0 / 0x4517C0 | () -> u32 edges | `8b 0d 10 bc 3c 02 53` | `Fahrenheit.FhCall.FUN_004517C0_00305650` | (Gu_Main calls it) |
| Sg_SetMouse | 0x00851ae0 / 0x451AE0 | (int x, int y) | `55 8b ec 8b 55 08 8b 4d 0c` | `Fahrenheit.FhCall.FUN_00451AE0_00305970` | absolute set (alt.) |
| Sg_ClockTick_0084ef10 | 0x0084ef10 / 0x44EF10 | () -> hours | `a1 40 3d 32 01 40` | `Fahrenheit.FhCall.FUN_0044EF10_00303940` | (Gu_VSyncMain tail) |
| Gu_Hide | 0x00849780 / 0x449780 | (int on) -> old | `55 8b ec 8b 4d 08 a1 5c 37 30 01` | `Fahrenheit.FFX.FhCall.FUN_00449780` | hide/show |
| Gu_MouseSetEnable | 0x00849b80 / 0x449B80 | (int on) | `55 8b ec 8b 45 08 a3 60 37 30 01` | `Fahrenheit.FFX.FhCall.FUN_00449b80` | hide the Gu cursor |
| Sg_PacketGetPtr | 0x0083b650 / 0x43B650 | () -> buffer | `a1 dc 98 c4 00 c3` | `Fahrenheit.FFX.FhCall.FUN_0043b650` | or read [0x00c498dc] |
| Sg_MainDebug | 0x008207f0 / 0x4207F0 | () | `e8 8b 42 02 00` | `Fahrenheit.FFX.FhCall.FUN_004207f0` (correct) | per-frame slot (post-hook) |
| Sg_EnableDrawGui | 0x00820420 / 0x420420 | (int on) -> old | `55 8b ec 8a 4d 08` | `Fahrenheit.FFX.FhCall.FUN_00420420` | (gate byte, already 1) |
| SystemOnHost | 0x0084ede0 / 0x44EDE0 | (char* cmd, char* out) -> int | `55 8b ec 56 8b 75 08 57` | `Fahrenheit.FhCall.FUN_0044EDE0_00303810` | **hook -> -1** |
| Sg_CaptureEnable | 0x0083a470 / 0x43A470 | () | `0f be 0d 1d 18 30 01` | `Fahrenheit.FhCall.FUN_0043A470_002E96C0` | **hook -> no-op** |
| AtelInitSaveWork | 0x0086d4e0 / 0x46D4E0 | () | `57 e8 4a f3 01 00` | `Fahrenheit.FFX.FhCall.FUN_0046d4e0` | block via WMainProc (not globally) |
| Cache_Clear | 0x00840030 / 0x440030 | () | `53 56 57 bf 98 2f 30 01` | `Fahrenheit.FFX.FhCall.FUN_00440030` | block via WMainProc |
| Sg_MapInit | 0x00844d10 / 0x444D10 | () | `55 8b ec 81 ec c4 00 00 00` | `Fahrenheit.FFX.FhCall.FUN_00444d10` | block via WMainProc |
| Sg_DbDraw | 0x008412c0 / 0x4412C0 | () | `55 8b ec 83 ec 64` | `Fahrenheit.FFX.FhCall.FUN_004412c0` | Sg_Db capture (dead) |
| rcPacketDataDraw | 0x00927600 / 0x527600 | (pkt*) -> eax | `55 8b ec 56 6a 02 e8` | `Fahrenheit.FhCall.FUN_00527600_003D7890` | rc overlay capture |
| sceDmaSend | 0x0062f710 / 0x22F710 | (chan, void* tag) | `c3` + 15 x `cc` | `Fahrenheit.FFX.FhCall.FUN_0022f710` | avoid; filter by return address if used |
| TkPutDebugFontStringCore_0088f240 | 0x0088f240 / 0x48F240 | (int prop, int colour, int scale, byte* s) -> u32 | `55 8b ec 8b 45 14 83 ec 18` | `Fahrenheit.FFX.FhCall.FUN_0048f240` | hook, skip the original |
| TkSetDebugFontLocate | 0x0088faf0 / 0x48FAF0 | (x, y) | - | `Fahrenheit.FFX.FhCall.FUN_0048faf0` | (harmless store) |
| 0x00890500 (TkDebugFont frame) | 0x00890500 / 0x490500 | 8 args | `55 8b ec 83 ec 10` | `Fahrenheit.FFX.FhCall.FUN_00490500` | hook, skip |
| TkRegistDebugFontDmaTag | 0x0088f760 / 0x48F760 | (int, int, int) -> ptr | `55 8b ec 83 7d 08 00` | `Fahrenheit.FFX.FhCall.FUN_0048f760` | never call (NULL buffer) |
| TkDrawDebug | 0x0088d4e0 / 0x48D4E0 | () | `c3` + 15 x `cc` | `Fahrenheit.FFX.FhCall.FUN_0048d4e0` | per-frame slot for TkDebugFont |
| op_num | 0x007e63a0 / 0x3E63A0 | (char* text) | `c3` + 15 x `cc` | `Fahrenheit.FFX.FhCall.FUN_003e63a0` | hook: CAM/PLY/DIS text |
| op_num_xy_set | 0x007e65e0 / 0x3E65E0 | (int x, int y) | `55 8b ec 80 3d 94 27 32 02 00` | `Fahrenheit.FhCall.FUN_003E65E0_00286490` | hook: text position |
| Send_DebugFont | 0x008510b0 / 0x4510B0 | () | `68 70 51 32 01 e8` | `Fahrenheit.FFX.FhCall.FUN_004510b0` | (stub path; ignore) |
| Gu_Printf | 0x00848880 / 0x448880 | (fmt, ...) | `55 8b ec 83 ec 18` | `Fahrenheit.FhCall.FUN_00448880_002FFCB0` | - |

Patch site: 0x0092209a (off 0x52209A) `e8 b1 5b f6 ff` -> `b8 01 00 00 00`
enables the rc field overlay without g_FFXIsDebug.

**Hooking 1-byte `ret` stubs** (`sceDmaSend`, `op_num`, `TkDrawDebug`,
`dbgPrintf`): each is followed by 15 bytes of `cc`. MinHook accepts that as
padding, and Fahrenheit already hooks `dbgPrintf` (the same shape).

### Globals

| Global | VA / off | Type | Meaning / writers / readers |
|---|---|---|---|
| g_RcPrim2DPacket | 0x00c498dc / 0x8498DC | ptr | 1 MB packet buffer (Sg_InitAll `user_malloc(0x100000)`; image value 0x01f00000 is overwritten at boot); Gu, Sg_Db, rc, ppp |
| g_GuPacket | 0x013036c4 / 0xF036C4 | ptr | packet base (Gu_DrawStart); qword 0 = DMA tag |
| g_GuPacketData | 0x013036c8 | ptr | where Gu_Set* append (= g_GuPacket; 0x00848f50 can redirect, no caller) |
| g_GuPacketCount | 0x013036c0 | i32 | qwords used |
| g_GuPacketTag / TagStart / Origin | 0x013036cc / d0 / d4 (+d8 y) | | open A+D tag, its index, origin x / y |
| g_GuHScale | 0x00c4df54 | i32 8.8 | 0x200: y x2 |
| g_GuScreenSize | 0x023cbbe0 (H) / 0x023cbbe4 (W) | i32 | 416 / 512 |
| g_GuWindowList | 0x01303734 | ptr | window list, front first |
| g_GuHidden / g_GuMouseEnable | 0x0130375c / 0x01303760 | i32 | Gu_Init 0 / 1 |
| g_GuWindowGlobalTrans | 0x01303730 | i32 0..0x80 | window alpha (0x80) |
| g_GuPrintSize | 0x013036dc | i32 | 0x100 per window |
| g_GuMouseX / Y | 0x01325240 / 0x01325244 | i32 | 0..0x200 / 0..0x1a0 (use 0..208) |
| mouse accumulators fx / fy | 0x01325248 / 0x0132524c | f32 | 0..1024 / 0..1248 (X = fx/2, Y = fy/3) |
| raw buttons | 0x01325260 | u32 | bit0 L, 1 R, 2 M |
| moved flag | 0x0132525c | i32 | Sg_MouseGetMove |
| previous edges / double-click pos | 0x01325250 / 0x01325254, 58 | | |
| RPC result (dst) | 0x01325280 (0x20 bytes) | | +8 buttons, +0xc dx, +0x10 dy, +0x14 wheel |
| g_SgMouseButtons | 0x023cbc10 | u32 | edge bits (4.2) |
| g_SgMouseWheel | 0x023cbc04 | i32 | wheel steps, cleared by Gu_Main |
| double-click countdown | 0x023cbc00 | i32 | 50 frames |
| main-menu tab | 0x01324128 | i32 0..6 | Sg_SelectMainMenu, wheel, right-click |
| main-menu window | 0x01324130 | ptr | Sg_SelectMainMenu |
| debug clock | 0x023cbc24 month, 20 day, 1c weekday, 30 h, 2c min, 28 s; 0x01323d40 frames | i32 | |
| resource block | 0x01301bf0 (0x400 bytes; offsets from the table 0x00c4df00, filled by Sg_LoadVramResource) | | Sg_Get/SetResource; save request 0x01301bec |
| Gu GUI gate | 0x012fbbb3 | u8 | Sg_EnableDrawGui (1 at boot) |
| capture | 0x01301804 / 0x01301940 | | Sg_CaptureGetGuiHide |
| movie mode | 0x012fb8fb | u8 | Sg_MainDebug skipped, 640x448 |
| debug font record | 0x01325170 (0xb4 bytes) | | zero in release |
| debug font TIM | 0x00c4dfb8 | TIM | section 2.1 |
| g_TkDebugFontBuffer | 0x0133c8cc | ptr | NULL in release |
| g_TkDebugFontPos | 0x0133c8d4 / d8 | i32 x16 | text position |
| g_RcuiWork | 0x0193c0f0 | ptr | rc tool open |
| g_OpEtBindOff | (set by 0x007fbc20) | u8 | bind effects off |

---------------------------------------------------------------------------

## 8. Risks, in short

- **Crash:**
  - `SystemOnHost` (Capture, rc Save, Sg_ClockFromHost, 0x0084f100). Guard it.
  - TkPutDebugFont* / AtelDebugDisplay pages / `TkRegistDebugFontDmaTag`
    (NULL buffer).
  - `Sg_DbDraw` with the flag but without `Sg_DbInitAll`.
  - Battle windows 1/3/5/9 outside battle.
- **Save/story corruption:** CLR FLG (`AtelInitSaveWork`).
- **World changes:** EVENT Reload/Select (event jump), MAP Reload/selectors
  (`Sg_MapInit` = yiReset + reload), CacheCLR, BINDOFF and the menu's
  create-time `op_et_bindeff_off_all` (guard with `Sg_SetResource(0x12, 1)`),
  Pause, exec frames.
- **File system:** MemDumpD (`Sg_PcWrite` with create/truncate, INFERRED).
  Setting, event and chr lists read host paths that do not exist (INFERRED
  empty).
- **Memory:** opd_num's buffer overlaps battle data: never enable
  g_OpNumShow / g_OpDebugMode.
- **Gu_Init twice** leaks windows. `Gu_WindowFreeClosed` frees closed windows
  each frame (`user_free`).
- **Steam stats/achievements:** none of the functions read here calls the
  achievement/stat code (INFERRED: not a full call-tree scan; the dangerous
  stat reset is the PC tweak panel's, overlays.md 1.4).
