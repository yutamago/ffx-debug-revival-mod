# Stand-in scripts for unfinished areas

Three `no_file` maps ([maps.md](maps.md)) name a script the PC version doesn't ship, but their geometry
ships, PS2 and PC, and no shipped script uses it:

| Map | Missing script | Geometry (module 0x13 pair) | PC level |
|---|---|---|---|
| 262 | hiku1700.ebp | 397, hiku17 | `map/hiku/hiku17/2d/` |
| 350 | hiku1800.ebp | 398, hiku18 | `map/hiku/hiku18/` |
| 373 | ikai0700.ebp | 282, ikai07 | `map/ikai/ikai07/2d/` |

The other 15 `no_file` maps either have no PC level (bvyt14, bvyt15, lchb19, hiku12: PS2 files only),
no geometry at all (kami09, znkd11), or geometry that shipped maps already show (bltz00, bltz01, lchb08).
The 47 `no_name` maps are empty slots with nothing that points at any geometry. The pairing script name
→ geometry is by name (`hiku1700` → `hiku17`), as every shipped script follows it. INFERRED for these
three.

## How

- **Files.** `PcFileName_00a42c00(loc)` 0x00a42c00 turns a cdrom.fnd entry into a path; `CDfileSize_PC`
  and `CDfileName_PC` both go through it (VERIFIED decomp). For the 0x12 entries of a stand-in map, the
  mod passes camtest0's (map 2) instead, so the script and its motion groups load the normal way.
- **Script.** camtest0 is the developers' camera test: worker 0 sets the player model, party and
  control, places the player (`setPosition` 0x0013) and a polar camera. After
  `TkCdReadWithMallocWithMessage(0xc, map*0x12)` 0x0088d2c0 returns (it waits for the read), the mod
  writes the area's geometry into entrance 0 (`AtelGetMapIndex` reads +0; camtest0 has no per-language
  table), and ends worker 0's main loop (it cycles depth masks every 80 frames) and worker 2's set-up
  (camera line areas around camtest0's room) at their first instruction.
- **Spawn.** `yiMapReadMaster` reads the map synchronously inside `AtelEventSetUp`, so the walk mesh
  is installed (`Sg_IdSetPtr`: triangles 0x01301a84, count 0x01301a88, vertices 0x01301a8c, zoom
  0x01301a90; world = mesh / zoom) when it returns. The mod picks the triangle whose centre is nearest
  the mesh's mean centre and writes that point into the `setPosition` operands (x, z as `pushimm`, y in
  worker 0's float constant 8, which only that call uses). INFERRED: the workers' set-up runs in a later
  frame (`Atel_EventFrame`), after the patch.
- **Checks.** Off unless map 2's entry ends in `/camtest0.ebp` and the three scripts are still missing;
  each patch first compares the bytes it changes with camtest0's.

## Not verified

- Whether the areas' guide map, sound and camera come out right; whether random encounters (camtest0
  sets an encounter weight) start battles there.
- The event's name becomes camtest0's (`TOCDGetFileName` goes through the same table), so its text
  and debug data are camtest0's; it has no text.
