# Debug Revival (`yuta.debugrevival`)

A [Fahrenheit](https://github.com/fahrenheit-crew/fahrenheit) mod for the
Final Fantasy X HD Remaster (Steam) that turns the developers' debug tools
left in `FFX.exe` back on. Press **F9** in game for its window.

Fahrenheit `1.0.0-alpha12`, for the current Steam build of the game.

## What it revives

| Tab | What | How |
|---|---|---|
| Scripts | Event-script debugger: every ctrl work and worker, the disassembly with the PC marked, call stack, value stack, registers, queued signals, variables with their values; pause, continue, one frame, step one instruction of one worker, up to 4 breakpoints per ctrl work | The VM's own debugger support, still in `FFX.exe`: the ctrl work's debugger state byte and its breakpoint table (opcode `0x40`). The disassembler prints exactly what ffx-decomp's `ffx script disasm` prints |
| Panels | The PC port's ten developer panels (lighting, post process, basic debug, battle debug, battle character params, game params, and four model-viewer ones) | The game still draws them every frame; only the switch and their input handlers were cut. The mod calls the switch and, if asked, the shown panel's input handler |
| Battle | The ~40 switches of the developers' BattleConfig window (damage variance, always hit or crit, invincibility, free MP, overdrives, AP/gil x100, monster control, skip spell effects, camera...), the debug cheats (all items, max gil, all abilities, max stats, ...), the developers' scene picker to start any battle, an actor inspector with HP/MP/overdrive/CTB editing | Writing the bytes the battle code still reads, and calling the `MsDebug*` functions and `MsBattleLabelExe` the way the debug pad combo did. Optional: the game's own battle status line through its debug text buffer (experimental) |
| Field | Walk through walls and the speed-up from the debug pad, random encounters on/off, map jumps, the ~110 developer saves ("Game Section"), the PC port's free fly camera and photo pause | A hook on `Sg_GetIgnoreID`, the debug pad's frames byte, `setBattleEnable`, `AtelSetEventJump2`, `TkDebugLoadViewerSave`, `FreeCamera_*` |
| Log | `dbgPrintf` and `scePrintf`, compiled to an empty `ret` in the shipped game (459 call sites) | Hooks that format the arguments the game still pushes |

The PS2 debug windows (the Gu toolkit, the rc map tool, the effect
inspector) and the original on-screen script debugger draw through PS2 GS
packets the PC port never sends, and the remote debugger link (Deci2) is
compiled out; the Scripts and Battle tabs rebuild what they showed.
`docs/research/` has the facts behind every address, with the evidence.

## Safety

- The Basic debug panel's "Achievements Reset" line would call
  `SteamUserStats()->ResetAllStats(true)` and wipe your Steam stats and
  achievements. The mod blocks that function entirely.
- Loading a developer save replaces the running game (not your save files,
  until you save). The panel's own "Game Section" load is blocked unless you
  allow it in the Field tab.
- The cheats, the Game params panel and the Battle character params panel
  change the game state your next save writes. The mod asks before running a
  cheat that does.
- Breakpoints and single steps are limited to event scripts (ctrl work 0);
  battle code runs scripts in loops of its own, where a stopped or skipped
  worker could hang the game. Other ctrl works can be paused and advanced a
  frame at a time.
- The developer panels stay locked unless both safety hooks are confirmed in
  place at the first frame.
- The debug log formats the game's arguments itself and reads strings only
  from readable memory, since these prints never ran in the PC build.

## Status

The mod builds, and the disassembler matches ffx-decomp's on all 758 shipped
scripts (`tools/disasm-check.sh`). Its native code was reviewed against
`docs/research/`. It has **not been run in the game yet**.

## Build

```bash
dotnet build -c Debug ffx-debug-revival.slnx   # artifacts/deploy/dbg/mods/yuta.debugrevival/
```

Install as any Fahrenheit mod: copy the folder into Fahrenheit's `mods/` and
add `yuta.debugrevival` to `mods/loadorder`.

`tools/gen-natives.py` regenerates `src/Script/natives.tsv` (native function
names for the disassembly) from `FFX.exe` and ffx-decomp's `symbols/ffx.sym`,
whose names include Fahrenheit's, used with their permission.

## License

MIT OR Apache-2.0, at your option. See `THIRD-PARTY-NOTICES` for the files
taken from the Fahrenheit mod template.
