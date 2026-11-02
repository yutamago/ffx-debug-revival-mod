# Old-format 2D backgrounds (bvyt14, bvyt15)

Two unfinished Bevelle rooms ([stand-in-maps.md](stand-in-maps.md)) ship only their PS2 map,
`ffx_ps2/ffx/master/jppc/map/bvyt/bvyt1{4,5}/bin/mapout.vpa`. Section 0 is a `YNDT` background in
an older format than any shipped map's. The mod rewrites it in memory for the game, and builds the
PC files the PC version draws 2D maps with. Seen in the game: both rooms show their background, the
player walks in front of the far layers and behind the near ones, and the controls match the view.

## The old format

A `YNDT` header and chunks (16-byte header: tag, size in 16-byte rows): `YNPR`, `YNSC`, **`YNTM`**,
`YNED`. Shipped 2D maps keep the textures inside `YNSC` (hiku17: one 0xe251-row `YNSC`).
`rcbgLoadBin` 0x00921d60 has no case for `YNTM` (tag 4 of `g_RcbgChunkTags` 0x00c5e480), and it parses
`YNSC` in the new layout: `rcbgSceneLoad` → `rcbgLayerLoad` → `rcdtParseGetBin` copies from a bad
offset (crash seen on map 216).

`YNSC` (all little-endian, no padding):

| Offset | Content |
|---|---|
| +0 | layer count |
| per layer | 0x50-byte header: +0 flags (0x8000/0x8010), +4 w, +8 h (PS2 lines: half of the PC's 448), +0xc x, +0x10 y, +0x14 depth (f32), +0x24/+0x28 texture u/v, +0x2c/+0x30 256, 256, +0x34 texture index, +0x38 0x44, +0x48 the palette's GS address |
| | palette transfer: BITBLTBUF (DBP = the CBP of the layer's texture), TRXPOS, TRXREG 16×16, count (2), entry size 0x410 |
| | count palettes of 0x400 bytes (256 RGBA, PS2 CSM1 order, alpha 0x80 = opaque), without their GIF tags |
| tail | eye, target, up (16 bytes each), field of view, a word: the same 0x38 bytes `rcbgSceneLoad` reads after the layers |

`YNTM`: count, 12 bytes of padding, a 0x20-byte record per texture whose first word is its GS
`TEX0` (TBP, TBW, PSM 0x13 = PSMT8, TW, TH, CBP), then the 8-bit textures one after another, each
2^TW × 2^TH bytes, unswizzled. bvyt14: 512×256, 256×256, 512×256 (3 layers). bvyt15: five textures,
6 layers. Layers 1 and 5 share texture 1 at different u/v.

Units: the camera and the depths are in the shipped format's units. `rcbgSceneInit` 0x009209e0 sets
the scene's +0xf4 to 10.0, and `rcbgSetViewMatrix` 0x009204b0 scales eye and target by it. Layers
compare with the player's view depth × 0.1 (`rcbgSceneSetChadist` 0x00920790). An earlier ×10 on the
camera put the characters past the far clip (not drawn at all); drawing the walk mesh through the
camera onto the decoded background matches the painted floor with the stored values × 10.

## In memory (`OldBackgrounds`)

A hook on `Yn_PreBgSetData` 0x0091aa60 rewrites a background that has a `YNTM` chunk into the shipped
layout. The PC version draws its own layers, so the rewrite carries no pixels:

- Four layers (the template model's count): the old layers sorted far to near, neighbours with the
  smallest depth gap merged until four remain. A group takes its farthest member's depth, so a
  character between merged layers stays visible. Missing groups are empty and come last, so layer i
  is the model's layer i.
- Each layer as hiku17's are: a 0x90-byte header (full screen 512×448, the depth, colour 0x80808080,
  scale 1, 1); one animation with a zeroed 512×448 image transfer (`rcbgSceneProcLayer` reads the
  animation's transfer every frame; without it the game crashed a frame after loading); two bytes;
  a palette transfer with two zeroed palettes.
- The tail as stored.

## PC files (`RoomFiles`)

A PC 2D map is `map/<dir>/2d/<area>.ahwin32`, the asset list `PhyreScene_convertMapPath` 0x00659290
names from the map's level name (+0x17 past `host0:/ffx/master/jppc/`, `/bin` → `/2d/<area>.ahwin32`).
It lists one texture per layer, the model and two shared shaders. hiku17's model is four
full-screen quads (512×448) with one 2048×2048 DXT5 texture each, at the layer depths (100, 99, 98,
4.25). Each depth appears 8 times in the file: the instance's `m_layerz0`, its bounds, and the quad's
six vertices.

On the first frame of a session, when `efl/x/rooms-v1.txt` is missing, the mod reads the two `.vpa`
files and hiku17's model, asset list and the first 0xa78 bytes of its four textures (the header before
the pixels) from the archive. Then, off the game thread:

- each group of layers composed into a 512×448 image (PS2 lines doubled), scaled to 2048×2048
  (bilinear, premultiplied), rows bottom first as the PC's textures store them, DXT5-encoded behind
  the template header;
- the model with `hiku/hiku17` and `hiku17` replaced by the room's names, which have the same length,
  and the four depths changed;
- the asset list likewise; its texture animation and effect textures stay hiku17's.

Fahrenheit indexes `efl/x` only at start-up, so the rooms show from the next start. The PC map loader
first checks `fiosIsFileExist` 0x006796f0, which only knows the archive, and Fahrenheit doesn't hook
it. The mod's hook answers yes for the files in its `efl/x` folder.

## Not verified

- Whether hiku17's texture animation (`textureAnimation.ags.phyre`) touches the rooms' layers.
- The merge's effect where a character stands between two merged layers (bvyt15: 27.5 and 22).
