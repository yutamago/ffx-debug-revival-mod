// SPDX-License-Identifier: MIT OR Apache-2.0

using Yuta.DebugRevival.Native;

namespace Yuta.DebugRevival.Features;

/// <summary>
///     Field-side debug features:
///     <list type="bullet">
///         <item>noclip: the debug pad's "L1 = walk through walls" (Sg_ControlMain) sets
///               the flag Sg_GetIgnoreID returns, and clears it at the start of every
///               frame; the hook answers 1 instead.</item>
///         <item>game speed: the debug pad's frames-per-frame byte (L1+R1).</item>
///         <item>random encounters on/off (the Basic debug panel's "Battle Enable").</item>
///         <item>map jumps through AtelSetEventJump2, the call the developers' map list used.</item>
///         <item>the ~110 developer saves in savesforviewer/ (the Basic debug panel's "Game Section").</item>
///         <item>the PC port's free camera and photo pause, whose keys ship unbound.</item>
///     </list>
/// </summary>
public sealed unsafe class FieldTools(Guards guards) {
    public volatile bool noclip;

    /// <summary>Bind the free camera to F10 (fly) and F11 (photo pause) while on.</summary>
    public volatile bool bind_camera_keys;

    // Phyre key bindings: F1 = 0x41 (VERIFIED from the default key table), F10 = 0x4a, F11 = 0x4b.
    private const uint KEY_F10 = 0x4a;
    private const uint KEY_F11 = 0x4b;
    // Input map pairs (binding, bit) of the free camera's two actions (Input_BuildPadMap).
    private static readonly (int pair, uint bit, uint key)[] CAMERA_PAIRS = [
        (25, 0x40000, KEY_F10), (47, 0x40000, KEY_F10),
        (27, 0x100000, KEY_F11), (49, 0x100000, KEY_F11),
    ];

    public sealed record Snapshot(int EventId, bool JumpAllowed, uint Encounters, sbyte ExecFrames, int FreeCamType, bool PhotoPause, string EventName);

    public volatile Snapshot? snap;
    private int _named_event = -1;
    private string _event_name = "";

    public bool init(FhModule owner) => Calls.Sg_GetIgnoreID.hook(owner, h_ignore_id);

    [UnmanagedCallConv(CallConvs = [ typeof(CallConvCdecl) ])]
    private int h_ignore_id() => noclip ? 1 : Calls.Sg_GetIgnoreID.chain_from(h_ignore_id).fnptr!();

    private static bool jump_allowed() => (Mem.u8(G.AtelCtrlWorks) & 0x80) != 0;

    /// <summary>Game thread, after each update.</summary>
    public void update() {
        int event_id = *Fahrenheit.FFX.Globals.event_id;
        nint fc = *(nint*)Mem.at(G.FreeCamera);
        if (bind_camera_keys) bind_keys();
        snap = new Snapshot(
            event_id, jump_allowed(), Mem.u32(G.BattleEnable), Mem.i8(G.ExecFrames),
            fc != 0 ? *(int*)fc : -1, fc != 0 && *(byte*)(fc + 0x338) != 0,
            event_id == _named_event ? _event_name : "");
    }

    private static void bind_keys() {
        nint im = Mem.at(G.InputManager);
        int count = *(int*)(im + 0x9c);
        nint pairs = *(nint*)(im + 0xa4);
        if (pairs == 0) return;
        foreach (var (pair, bit, key) in CAMERA_PAIRS) {
            if (pair >= count) continue;
            nint e = pairs + pair * 8;
            if (*(uint*)(e + 4) == bit && *(uint*)e == 0) *(uint*)e = key; // rebuilt maps come back unbound
        }
    }

    public void set_encounters(bool on) => GameThread.post("encounters", () => {
        Calls.setBattleEnable.fnptr!(on ? 1 : 0);
        GameThread.last_status = on ? "random encounters on" : "random encounters off";
    });

    public void set_exec_frames(int n) => GameThread.post("game speed", () => Mem.set_u8(G.ExecFrames, (byte)Math.Clamp(n, 1, 8)));

    public void name_current_event() => GameThread.post("event name", () => {
        int id = *Fahrenheit.FFX.Globals.event_id;
        nint p = Calls.AtelGetEventName.fnptr!(id);
        _event_name = Mem.cstr(p, 32);
        _named_event = id;
    });

    public void jump(int map, int entrance) => GameThread.post("map jump", () => {
        if (!jump_allowed()) { GameThread.last_status = "the current event does not allow leaving the map now"; return; }
        Calls.AtelSetEventJump2.fnptr!(map, entrance, 0);
        GameThread.last_status = jump_allowed() ? "the game refused the jump" : $"jumping to map {map}, entrance {entrance}";
    });

    public void load_viewer_save(int n) => GameThread.post("developer save", () => {
        if (!jump_allowed()) { GameThread.last_status = "only where the player can leave the map (it warps)"; return; }
        Mem.set_u32(G.ViewerSaveLast, 0xffffffff); // the loader skips a repeat of the last one
        guards.viewer_save_once = true;
        Calls.TkDebugLoadViewerSave.fnptr!(n);
        guards.viewer_save_once = false;
        GameThread.last_status = $"developer save {n} loaded";
    });

    public void toggle_fly() => GameThread.post("free camera", () => {
        nint fc = *(nint*)Mem.at(G.FreeCamera);
        if (fc == 0) { GameThread.last_status = "no free camera yet"; return; }
        Calls.FreeCamera_setType.fnptr!(fc, *(int*)fc == 0 ? 1 : 0);
    });

    public void toggle_photo_pause() => GameThread.post("photo pause", () => {
        nint fc = *(nint*)Mem.at(G.FreeCamera);
        if (fc == 0) { GameThread.last_status = "no free camera yet"; return; }
        Calls.FreeCamera_setFreePause.fnptr!(fc);
    });
}
