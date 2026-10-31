# Japanese text on maps without text in the game's language

Static analysis of FFX.exe (Steam, 32-bit) and the shipped file list; the game was not run.
VERIFIED = read in the decompilation or disassembly, or checked against the data. INFERRED =
concluded, not read. Feature: `src/Features/JapaneseText.cs`.

## The crash

60 of the 377 event scripts have no `new_<lang>pc/event/obj_ps3/<area>/<map>/<map>.bin` for an
English game (80 counting the `cn_`/`psv_` variants and test maps). `AtelEventSetUp` 0x00872ef0
then finds no text file (`TKMallocAndReadEventMessage` returns 0, kept in 0x01327128) and passes the
.ebp's own message table, `ebp + [ebp+8]` (the .ebp at 0x023cbd70), to `AtelActorSetUp2`. That
table is Japanese. VERIFIED.

The first message measures its glyphs with `TOGetFontWidth` 0x008b9330, which reads codes
0x2c-0x2f from font dictionary 0's width table (`TOGetFontWidthTblAdrs(0)`, pointer 0x01841da4).
Only `TOFTCTransfer` 0x008ac130 writes that pointer, and an English game never transfers
dictionary 0 (`TOInit` 0x008ad4d0 loads dictionary 4, `uspc/menu/base.ftc`), so it reads null +
index. VERIFIED.

## How the PC port draws a glyph

- `TOGetFontInfo` 0x008b7240 (cdecl, text, record) -> next text fills a 0x24-byte record:
  +0 u16 slot, +2 u16 0x14, +4 u16 page (`idx & 1`), +8..+0x14 the cell box (`x = idx % 18 / 2 * 14`,
  `y = idx / 18 * 18`, 14 x 18 cells), +0x18 slant (0 in Japanese, 1.0 in Western languages),
  +0x1c width (`(i8)widths[idx] / 4`), +0x20 18.0. +6 is not written. Codes 0x30-0xff are
  `idx = b - 0x30`, 0x2c-0x2f t is `(b - 0x2b) * 0xd0 + t - 0x30`. The slot is 0x3c00 in Japanese,
  0x3d40 in English. VERIFIED, and matches ffx-decomp's parity-tested port
  (`ffx_system::tolib::to_get_font_info`).
- Japanese-only details: 0x5c-0x5e draw as a fixed box (70..80 x 0..18, width 10, return text + 1);
  a `:` (0x3a) between two 0x2c kanji is 5 wide (`TOJoinsNeighbours_008b5760`: `p[-2] == 0x2c`,
  `p[1] == 0x2c`, `p[-1] <= 99`, `p[2] <= 0x62`); a text starting with a run of 0x47 is one glyph of
  `run * width` with the box inset by 4 on each side (not doubled, since the base dictionary is 0).
  VERIFIED.
- The .ftc image data is unused on PC (`TOTransImageHL` 0x008b0b10 ends in `sceDmaSend`
  0x0062f710, a bare `ret`). A dictionary supplies only widths and its page size (+0xc/+0xe).
  The glyphs come from DDS atlases named by `TOGetTextureName` 0x008acf50 (cdecl, slot i16,
  nibbles u8, index i16, high u16, &size) -> path: slot 0x3c00 gives `menu/base_ftc` (tables
  0x00c59568 font, 0x00c595a8 shadow) only when `TkGetLang() == 0`, else `menu_us/base_ftc`
  (0x00c595e8 / 0x00c59628), which slot 0x3d40 also uses with the same index math and size output.
  The four tables have 16 entries each, alternating `_0_0` and `_0_1`. VERIFIED.
- `TOGetImageWH` 0x008ac400 (cdecl, slot, &w, &h) -> &h: slot 0x3c00 gives dictionary 0's size
  when `TkGetLang() == 0`, else dictionary 4's. VERIFIED.
- `TOGetFontWidth` 0x008b9330 (cdecl, text, kind, &w, dictionary) -> next measures codes 0x30 and
  up with the caller's dictionary (4 in English) and 0x2c-0x2f always with 0. VERIFIED.

All five hooked functions start `push ebp; mov ebp, esp` and end in a bare `ret`. VERIFIED
(disassembly).

## What the mod does

After `AtelEventSetUp` returns, when 0x01327128 is 0, `[ebp+8]` is not 0 and `TOGetFFXLang()` is not
0 (Japanese), once per session:

1. Width tables of dictionaries 1-3 (per-event kanji, battle, help) that are still null get the
   buffers `TOFTCTransfer` would give them: 0x018656c0, 0x01865840, 0x018659c0. Those codes then
   measure 0 instead of crashing. VERIFIED (the addresses); the buffers being zero is INFERRED.
2. `ffx_ps2/ffx/master/new_jppc/menu/base.ftc` (1072 bytes, version 200, dictionary 0, 0x3ea widths,
   128 x 1008) is read through the archive and passed to `TOFTCTransfer`, which copies the widths
   to 0x01864e10. That is the file `TOInit` loads for Japanese. VERIFIED (file and header).
3. The four atlases (font and shadow, pages 0 and 1) load with `ClusterManager_loadPCluster`
   0x0069b940 (thiscall, this = `ClusterManager_GetInstance()` 0x0069a1a0 = `[0x00ccca4c]`, path;
   `ret 4`) and `PApplication_FixupClusters` 0x00623580 (cdecl, &cluster, 1), the way
   `GraphicAbmap_loadTextureClusters_00684ef0` loads the game's own language. VERIFIED.

From then on a text pointer counts as the map's own Japanese text when 0x01327128 is 0 and the
pointer lies in `[ebp + [ebp+8], ebp + next larger offset among [ebp+0xc..0x18])`. `[ebp+0x18]` is
the file size (startmap0: 0x780 = 1920 bytes), so there always is an end. For startmap0 the range is
0x580..0x6c0. VERIFIED (headers of startmap0, azit0500, bjyt0800, cdsp0100, test01, slik1100).
The message window passes its cursor into the message text (window + 0x8c, set from + 0x94) to
`TOGetFontInfo`, so those pointers are in the range. VERIFIED (`TODrawMessageWaitNamesp` 0x008dee00). Macro expansions such as party names
use other buffers and stay English.

| Hook | When | Does |
|---|---|---|
| `TOGetFontInfo` 0x008b7240 | text in the range | writes the Japanese record (slot 0x3c00, dictionary 0's widths, slant 0); 0x26-0x2b draw `？` (base glyph 31) |
| `TOGetFontWidth` 0x008b9330 | text in the range | dictionary 0; 0x26-0x2b measure as `？` and return text + 2 |
| `TOGetTextureName` 0x008acf50 | slot 0x3c00 | asks for slot 0x3d40, then returns the same entry of the Japanese table |
| `TOGetImageWH` 0x008ac400 | slot 0x3c00 | dictionary 0's size (128 x 1008) |
| `AtelEventSetUp` 0x00872ef0 | after it returns | the one-time load above |

In a non-Japanese game slot 0x3c00 comes only from the Japanese branches of `TOGetFontInfo` and
`TOGetFontInfoInterMode` 0x008b78b0; the other uses of 0x3c00 are the no-op GS uploads. VERIFIED
(grep of the decompilation). So English menus, help, battles and maps with text are unchanged.
Nothing is undone per map: the range follows the loaded .ebp.

## Not verified (in-game only)

- The atlases bind and draw after the mid-game load, and the cells line up (UV = texel / 128 x 1008).
- `loadPCluster` is safe to call right after `AtelEventSetUp` (the game loads event font clusters
  inside it). About 8.4 MB of textures stay loaded for the session. INFERRED.
- Glyph `？` is base index 31 (report, from `ffxsjistbl_jp.bin`); not rechecked here.
- Spacing: `TOGetFontSpaceInPiexl` 0x008fa570 keeps the English spacing, so the text is slightly
  wider than in the Japanese game. Al Bhed windows (`TOConvAlbhedWord`) copy the text into their own
  buffer, outside the range, and stay on the English path.
