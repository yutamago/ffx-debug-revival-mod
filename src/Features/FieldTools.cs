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
///         <item>map jumps through AtelSetEventJump2, with the developers' event list.</item>
///         <item>the ~110 developer saves in savesforviewer/ (the Basic debug panel's "Game Section").</item>
///         <item>the PC port's free camera and photo pause, whose keys ship unbound.</item>
///     </list>
///     Facts: docs/research/overlays.md 4.1 and field.md.
/// </summary>
public sealed unsafe class FieldTools(Guards guards, DebugFlag flag) {
    public volatile bool noclip;

    /// <summary>Bind the free camera to F10 (fly) and F11 (photo pause) while on.</summary>
    public volatile bool bind_camera_keys;

    // Phyre key bindings (binding = Phyre key + 0x1c): F10 = 0x4a, F11 = 0x4b.
    private const uint KEY_F10 = 0x4a;
    private const uint KEY_F11 = 0x4b;
    // Input map pairs (binding, bit) of the free camera's two actions, bound to a literal 0 in Input_BuildPadMap.
    private static readonly (int pair, uint bit, uint key)[] CAMERA_PAIRS = [ (30, 0x40000, KEY_F10), (32, 0x100000, KEY_F11) ];
    private const uint FLY_BIT = 0x40000;

    /// <summary>Ids in the event list with no script in the game files (field.md 4.3): jumping there fails.</summary>
    public static readonly HashSet<int> NO_SCRIPT = [
        40, 101, 111, 186, 216, 228, 229, 231, 232, 233, 246, 251, 262, 300, 304, 342,
        350, 353, 357, 358, 360, 369, 373, 379, 400, 401,
    ];

    public sealed record Snapshot(int EventId, bool JumpAllowed, bool FlagOn, uint Encounters, sbyte ExecFrames,
                                  int FreeCamType, bool PhotoPause, float FlySpeed, string Place);

    public sealed record MapEntry(int Id, string Name, bool HasScript);

    public volatile Snapshot? snap;
    public volatile MapEntry[]? maps;
    private string _place = "";
    private long _frame;
    private int _fly_check = -1;   // the type before a requested toggle, checked two frames later
    private long _fly_check_frame;

    public bool init(FhModule owner) => Calls.Sg_GetIgnoreID.hook(owner, h_ignore_id);

    private D.RetIntFn? _next_ignore_id;

    [UnmanagedCallConv(CallConvs = [ typeof(CallConvCdecl) ])]
    private int h_ignore_id() {
        if (noclip) return 1;
        _next_ignore_id ??= Calls.Sg_GetIgnoreID.chain_from(h_ignore_id).fnptr!;
        return _next_ignore_id();
    }

    /// <summary>What AtelSetEventJump(2) checks: bit 0x80 of the current ctrl work's first byte ("may leave the map"), or the debug flag.</summary>
    internal static bool jump_allowed() {
        if (Mem.u8(G.FFXIsDebug) != 0) return true;
        nint ctx = *(nint*)Mem.at(G.ScriptContext);
        return ctx != 0 && (*(byte*)ctx & 0x80) != 0;
    }

    /// <summary>Game thread, after each update.</summary>
    public void update() {
        _frame++;
        int event_id = *Fahrenheit.FFX.Globals.event_id;
        nint fc = *(nint*)Mem.at(G.FreeCamera);
        if (bind_camera_keys) bind_keys();
        if (_fly_check >= 0 && fc != 0 && _frame >= _fly_check_frame) {
            int now = *(int*)fc;
            GameThread.last_status = now != _fly_check
                ? (now != 0 ? "fly mode on: move the mouse to look, arrows to move; F9 reopens the window" : "fly mode off")
                : "the game did not switch fly mode here (paused, in a movie, or on the title screen?)";
            _fly_check = -1;
        }
        if (_frame % 30 == 0) _place = read_place();
        snap = new Snapshot(
            event_id, jump_allowed(), flag.on, Mem.u32(G.BattleEnable), Mem.i8(G.ExecFrames),
            fc != 0 ? *(int*)fc : -1, fc != 0 && *(byte*)(fc + 0x338) != 0, fc != 0 ? *(float*)(fc + 8) : 0,
            _place);
    }

    /// <summary>The place name the save screen shows (FFX-encoded text from the place dictionary).</summary>
    private static string read_place() {
        try {
            ushort dic = Calls.AtelGetSaveDic.fnptr!();
            nint p = Calls.AtelGetSaveDicName.fnptr!(dic, 0);
            if (p == 0 || !Mem.readable(p, 1)) return "";
            int len = 0;
            while (len < 64 && *(byte*)(p + len) != 0) len++;
            ReadOnlySpan<byte> src = new((void*)p, len);
            Span<byte> dest = stackalloc byte[256];
            int n = FhEncoding.decode(src, dest, flags: FhEncodingFlags.IMPLICIT_END);
            return Encoding.UTF8.GetString(dest[..Math.Clamp(n, 0, dest.Length)]).Trim();
        }
        catch (Exception) {
            return "";
        }
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

    /// <summary>Reads the developers' event list (builds it first if needed): every map id with its internal name.</summary>
    public void load_maps() => GameThread.post("map list", () => {
        flag.ensure_event_list();
        int count = Math.Clamp(Mem.i32(G.EventCount), 0, 1024);
        nint table = *(nint*)Mem.at(G.EventTable);
        if (table == 0 || count == 0) { GameThread.last_status = "the event list could not be read"; return; }
        MapEntry[] list = new MapEntry[count];
        for (int i = 0; i < count; i++) {
            nint e = table + i * 16;
            // Names are at most 12 bytes; a 12-character name runs into the id at +0xc.
            StringBuilder sb = new();
            for (int k = 0; k < 12; k++) {
                byte b = *(byte*)(e + k);
                if (b == 0 || b >= 0x80) break;
                sb.Append((char)b);
            }
            int id = *(int*)(e + 0xc);
            list[i] = new MapEntry(id, sb.ToString(), !NO_SCRIPT.Contains(id));
        }
        maps = [ .. list.OrderBy(m => m.Id) ];
    });

    public void jump(int map, int entrance) => GameThread.post("map jump", () => {
        if (NO_SCRIPT.Contains(map)) { GameThread.last_status = $"map {map} has no script in the game files"; return; }
        if (!jump_allowed()) { GameThread.last_status = "the current event does not allow leaving the map now (the debug flag lifts that)"; return; }
        Calls.AtelSetEventJump2.fnptr!(map, entrance, 0);
        GameThread.last_status = $"jumping to map {map}, entrance {entrance}";
    });

    /// <summary>The developers' model viewer: event 0 ("event00", the grid00 viewer). Turns the debug flag on first, so the viewer panels work and the map can be left again.</summary>
    public void go_to_model_viewer(TweakPanels panels) {
        if (!flag.on) flag.set(true, panels);
        jump(0, 0);
    }

    public void load_viewer_save(int n) => GameThread.post("developer save", () => {
        if (!jump_allowed()) { GameThread.last_status = "only where the player can leave the map (it warps)"; return; }
        Mem.set_u32(G.ViewerSaveLast, 0xffffffff); // the loader skips a repeat of the last one
        guards.viewer_save_once = true;
        Calls.TkDebugLoadViewerSave.fnptr!(n);
        guards.viewer_save_once = false;
        GameThread.last_status = $"developer save {n} loaded";
    });

    /// <summary>
    ///     Presses the free camera's own toggle: one fresh press of its input bit,
    ///     so FreeCamera_handleInput copies the scene camera, attaches the fly
    ///     controller and captures the cursor, as the developers' key did.
    /// </summary>
    public void toggle_fly() => GameThread.post("free camera", () => {
        nint fc = *(nint*)Mem.at(G.FreeCamera);
        if (fc == 0) { GameThread.last_status = "no free camera yet"; return; }
        int ev = *Fahrenheit.FFX.Globals.event_id;
        if (Mem.u8(G.AsyncControllerOn) != 0 || Mem.i32(G.PauseRequest) != 0 || Mem.i32(G.PauseEntered) != 0
         || ev is 0x17 or 0x15c or 0x15d) {
            GameThread.last_status = "the free camera is not available here (title screen, pause or this map)";
            return;
        }
        uint* im = (uint*)Mem.at(G.InputManager);
        im[0] |= FLY_BIT;
        im[1] &= ~FLY_BIT;
        _fly_check = *(int*)fc;
        _fly_check_frame = _frame + 2;
    });

    public void toggle_photo_pause() => GameThread.post("photo pause", () => {
        nint fc = *(nint*)Mem.at(G.FreeCamera);
        if (fc == 0) { GameThread.last_status = "no free camera yet"; return; }
        Calls.FreeCamera_setFreePause.fnptr!(fc);
    });

    public void set_fly_speed(float v) => GameThread.post("fly speed", () => {
        nint fc = *(nint*)Mem.at(G.FreeCamera);
        if (fc != 0) *(float*)(fc + 8) = Math.Clamp(v, 1f, 1000f);
    });
}
