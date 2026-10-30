# Debug Revival (`yuta.debugrevival`)

A [Fahrenheit](https://github.com/fahrenheit-crew/fahrenheit) mod for the
Final Fantasy X HD Remaster (Steam). It turns back on the debug tools the
developers left in `FFX.exe`. Press **F9** in game to open its window.

It needs Fahrenheit `1.0.0-alpha12` and the current Steam build of the game.

## Install

Download the zip from the [releases](https://github.com/yutamago/ffx-debug-revival-mod/releases)
and unpack it into your Fahrenheit folder, so the mod ends up in
`mods/yuta.debugrevival/`. Then add a line `yuta.debugrevival` to
`mods/loadorder`. Back up your saves first (`Documents\Square Enix\FINAL
FANTASY X&X-2 HD Remaster`): the cheats and two of the panels change what your
next save writes.

## What's in the window

The **Debug flag** checkbox at the top switches the game into the developers'
debug mode while it runs. With it on:

- L1 walks through walls, and L2 and L1+R1 change the game speed.
- Events never refuse a map jump.
- A New Game starts at story flag 5.
- Outside a battle, L1+R1+L2+R2+X starts the battle picked in the Battle tab.
- The four model-viewer panels work.

| Tab | What it has |
|---|---|
| Scripts | The event-script debugger: every ctrl work and worker, the disassembly with the PC marked, call stack, value stack, registers, queued signals and variables. Pause, continue, run one frame, step one instruction, and click a line to set or clear one of 4 breakpoints. Breakpoints and steps work only in the event's scripts (ctrl work 0). |
| PS2 menu | The developers' own debug menu from the PS2 build, drawn by the game's code: a column of buttons at the right edge of the screen. It is mouse-driven; with **Mouse controls the menu** on, your mouse works it wherever the mod's window isn't. The battle windows open from here too. |
| Panels | The PC port's ten developer panels, drawn by the game in small text at the top left. With **Panel keys** on, the shown panel reads I/K or d-pad up/down to pick a line, J/L or left/right to change it, and Enter or Circle to activate it. Those keys reach the game as well. |
| Battle | The ~40 switches of the developers' BattleConfig window, the debug cheats, the scene picker for starting any battle, and an actor table where HP, MP, overdrive and CTB can be edited. |
| Field | Walk through walls, game speed, random encounters, the free camera, map jumps and the ~110 developer saves. |
| Log | `dbgPrintf` and `scePrintf`, which print nothing in the shipped game. If the log stays empty, Fahrenheit's print control may be taking them first. |

### Free camera

**Fly** closes the window, because the game gets no mouse or keyboard input
while it's open. Move the mouse to look. Up/Down move forward and back and
Left/Right strafe. Right-drag dollies, middle-drag pans and left-drag turns
faster. F9 brings the window back. **Photo pause** freezes the world and mutes
the sound. With the F10/F11 box ticked, those keys toggle the two.

### Map jump

The list holds the developers' internal map names. Greyed-out maps have no
script in the PC version, and jumping there would crash the game. Jumping out
of story order can break progression. **Model viewer** turns the debug flag on
and goes to grid00, the developers' model viewer map.

### Developer saves

`FFX_Data` holds about 110 saves the developers used to jump into each part of
the story (the Basic debug panel's "Game Section"). Loading one replaces the
running game and warps to its map. Your save files stay as they are until you
save.

## Safety

- The Basic debug panel's "Achievements Reset" would call
  `SteamUserStats()->ResetAllStats(true)` and wipe your Steam stats and
  achievements. The mod blocks it entirely.
- The Basic debug panel loads a developer save only when the Field tab allows
  it.
- The PS2 menu items that would hang the game or clear the story flags are
  blocked: CLR FLG, Capture, CacheCLR, MAP Reload, the map and user lists,
  EVENT Reload and Select, MemDumpD and Movie. BattleInfo, BattleConsole,
  BattlePos and the Shake editor are blocked as well, since they can't be
  closed safely outside a battle.
- The cheats, the Game params panel and the Battle character params panel
  change what your next save writes. The mod asks before a cheat that does.
- The developer panels stay locked unless both safety hooks are in place on
  the first frame.

`docs/research/` has the facts behind every address, with the evidence.

## Build

```bash
dotnet build -c Release ffx-debug-revival.slnx   # artifacts/deploy/rel/mods/yuta.debugrevival/
```

`tools/package.sh <out.zip>` zips the build the way a release ships it. Pushing
a tag such as `v0.2.0` makes GitHub build and publish a release; a tag with a
`-` (`v0.2.0-alpha`) makes a prerelease.

`tools/disasm-check.sh` checks the disassembler against ffx-decomp's
`ffx script disasm` on every shipped script. `tools/gen-natives.py`
regenerates `src/Script/natives.tsv`, the native function names in the
disassembly, from `FFX.exe` and ffx-decomp's `symbols/ffx.sym`. Those names
include Fahrenheit's, used with their permission.

## License

MIT OR Apache-2.0, at your option. See `THIRD-PARTY-NOTICES` for the files
taken from the Fahrenheit mod template.
