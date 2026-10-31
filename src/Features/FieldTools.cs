// SPDX-License-Identifier: MIT

using Yuta.DebugRevival.Native;

namespace Yuta.DebugRevival.Features;

/// <summary>
///     Field debug features: the debug pad's noclip and speed-up, random encounters, map jumps, the developer saves
///     and the PC port's free camera (docs/research/overlays.md 4.1, field.md).
/// </summary>
public sealed unsafe class FieldTools(Guards guards, DebugFlag flag, Dictionary<int, int> entrances) {
    public volatile bool noclip;
    public volatile bool bind_camera_keys;

    // (input map pair, action bit, Phyre key binding) of the free camera's two actions, which ship bound to nothing.
    private const uint FLY_BIT = 0x40000;
    private static readonly (int pair, uint bit, uint key)[] CAMERA_PAIRS = [ (30, FLY_BIT, 0x4a /* F10 */), (32, 0x100000, 0x4b /* F11 */) ];

    private const int TITLE_MAP = 0x17;
    private static readonly HashSet<int> NO_FREE_CAMERA_MAPS = [ TITLE_MAP, 0x15c, 0x15d ];

    public const int MODEL_VIEWER_MAP = 0;
    public const int VIEWER_SAVE_COUNT = 0x71;

    /// <summary>What a jump to a map would do instead of loading it (docs/research/maps.md).</summary>
    public enum JumpRisk { None, Crash, Freeze, Reset }

    public sealed record Snapshot(int EventId, bool JumpAllowed, bool FlagOn, uint Encounters, sbyte ExecFrames,
                                  int FreeCamType, bool PhotoPause, float FlySpeed, string Place);

    /// <param name="Entrances">How many entrances the map's script lists; 0 when unknown or none.</param>
    public sealed record MapEntry(int Id, string Name, JumpRisk Risk, int Entrances);

    public volatile Snapshot? snap;
    public volatile MapEntry[]? maps;
    private readonly Dictionary<int, JumpRisk> _risks = [];
    private string _place = "";
    private long _frame;
    private int _fly_check = -1;   // the camera type before a requested toggle, compared two frames later
    private long _fly_check_frame;

    private D.RetIntFn? _next_ignore_id;

    public bool init(FhModule owner) =>
        Calls.Sg_GetIgnoreID.hook(owner, h_ignore_id) && Calls.debugLoadForViewer.hook(owner, h_load_for_viewer);

    // The developers' loader opens the save from disk with fopen, but the PC version ships the saves inside
    // FFX_Data.vbf, so it always failed. This reads them through the game's file layer, which knows the VBF.
    [UnmanagedCallConv(CallConvs = [ typeof(CallConvCdecl) ])]
    private int h_load_for_viewer(int n, nint dst, uint size) {
        byte[] path = Encoding.ASCII.GetBytes($"/FFX_Data/GameData/PS3Data/savesforviewer/{n}\0");
        byte* name = stackalloc byte[0x100];
        fixed (byte* p = path) Calls.fiosUnifyFilename.fnptr!((nint)p, (nint)name, 0x100);
        if ((Calls.fiosIsFileExist.fnptr!((nint)name) & 0xff) == 0) return 1;
        nint file = Calls.fiosOpen.fnptr!((nint)name, 1);
        if (file == 0) return 1;
        Calls.fiosRead.fnptr!(file, dst, size);
        Calls.fiosClose.fnptr!(file);
        return 0;
    }

    [UnmanagedCallConv(CallConvs = [ typeof(CallConvCdecl) ])]
    private int h_ignore_id() {
        if (noclip) return 1;
        _next_ignore_id ??= Calls.Sg_GetIgnoreID.chain_from(h_ignore_id).fnptr!;
        return _next_ignore_id();
    }

    private const uint CD_LIB = 0x02310c40;
    private const uint ETERNAL_CALM_FIRST = 0x00c33468, SOFT_RESET_MAP = 0x00c33480;
    private const int ETERNAL_CALM_COUNT = 7;

    /// <summary>The map's script file name in cdrom.fnd, 0 when its entry is empty, -1 in HDD mode (no names).</summary>
    private static nint script_name(int map) {
        const int EVENT_MODULE = 0xc, FILES_PER_MAP = 0x12;
        nint names = *(nint*)Mem.at(CD_LIB + 0x30);
        if (names == 0) return -1;
        nint firsts = *(nint*)Mem.at(CD_LIB + 0x2c);
        if (firsts == 0 || map < 0) return 0;
        int loc = *(short*)(firsts + EVENT_MODULE * 2) + map * FILES_PER_MAP;
        if (loc < 0 || loc >= *(int*)names) return 0;
        uint start = *(uint*)(names + 4 + 4 * loc), end = *(uint*)(names + 8 + 4 * loc);
        return (int)(end - start) > 0 ? names + (nint)start : 0;
    }

    // The loader copies an empty name without a NULL check (crash) and loops forever on a missing file (freeze).
    // The Eternal Calm scenes have empty entries but names built into FFX.exe.
    private JumpRisk risk(int map) {
        if (_risks.TryGetValue(map, out JumpRisk known)) return known;
        JumpRisk r;
        if (map == Mem.i32(SOFT_RESET_MAP)) r = JumpRisk.Reset;
        else if ((uint)(map - Mem.i32(ETERNAL_CALM_FIRST)) < ETERNAL_CALM_COUNT) r = JumpRisk.None;
        else r = script_name(map) switch {
            -1 => JumpRisk.None,
            0 => JumpRisk.Crash,
            nint name => file_exists(name) ? JumpRisk.None : JumpRisk.Freeze,
        };
        _risks[map] = r;
        return r;
    }

    // sceOpen's own test ("/ffx_ps2/" + the name past "host0:/", unified, then fiosIsFileExist), without opening the
    // file: opening and closing 400 files at once corrupted the game's heap.
    private static bool file_exists(nint name) {
        string path = "/ffx_ps2/" + Mem.cstr(name + 7, 0x3f0) + "\0";
        byte[] bytes = Encoding.ASCII.GetBytes(path);
        byte* unified = stackalloc byte[0x400];
        fixed (byte* p = bytes) Calls.fiosUnifyFilename.fnptr!((nint)p, (nint)unified, 0x400);
        return (Calls.fiosIsFileExist.fnptr!((nint)unified) & 0xff) != 0;
    }

    /// <summary>Entrance counts per map, from maps.tsv (tools/gen-map-table.cs).</summary>
    public static Dictionary<int, int> parse_entrances(string tsv) {
        Dictionary<int, int> counts = [];
        foreach (string line in tsv.Split('\n')) {
            if (line.Length == 0 || line[0] == '#') continue;
            string[] f = line.TrimEnd('\r').Split('\t');
            if (f.Length >= 2 && int.TryParse(f[0], out int id) && int.TryParse(f[1], out int n)) counts[id] = Math.Max(n, 0);
        }
        return counts;
    }

    private static bool in_field => *(nint*)Mem.at(G.ScriptContext) != 0;

    /// <summary>AtelSetEventJump(2)'s own check: the current ctrl work allows leaving the map (bit 0x80), or the debug flag is on.</summary>
    internal static bool jump_allowed() {
        nint ctx = *(nint*)Mem.at(G.ScriptContext);
        // AtelSetEventJump2 reads the context before it looks at the flag.
        return ctx != 0 && (Mem.u8(G.FFXIsDebug) != 0 || (*(byte*)ctx & 0x80) != 0);
    }

    /// <summary>Game thread, after each update.</summary>
    public void update() {
        _frame++;
        nint fc = *(nint*)Mem.at(G.FreeCamera);
        if (bind_camera_keys) bind_keys();
        if (_fly_check >= 0 && fc != 0 && _frame >= _fly_check_frame) {
            int now = *(int*)fc;
            if (now == _fly_check) Status.refused("fly mode is not available here");
            else Status.done(now != 0 ? "fly mode on (F9 reopens this window)" : "fly mode off");
            _fly_check = -1;
        }
        if (_frame % 30 == 0) _place = read_place();
        snap = new Snapshot(
            *Fahrenheit.FFX.Globals.event_id, jump_allowed(), flag.on, Mem.u32(G.BattleEnable), Mem.i8(G.ExecFrames),
            fc != 0 ? *(int*)fc : -1, fc != 0 && *(byte*)(fc + 0x338) != 0, fc != 0 ? *(float*)(fc + 8) : 0, _place);
    }

    /// <summary>The place name the save screen shows.</summary>
    private static string read_place() {
        try {
            ushort dic = Calls.AtelGetSaveDic.fnptr!();
            nint p = Calls.AtelGetSaveDicName.fnptr!(dic, 0);
            if (p == 0 || !Mem.readable(p, 1)) return "";
            int len = 0;
            while (len < 64 && *(byte*)(p + len) != 0) len++;
            Span<byte> dest = stackalloc byte[256];
            int n = FhEncoding.decode(new ReadOnlySpan<byte>((void*)p, len), dest, flags: FhEncodingFlags.IMPLICIT_END);
            return Encoding.UTF8.GetString(dest[..Math.Clamp(n, 0, dest.Length)]).Trim();
        }
        catch (Exception) {
            return "";
        }
    }

    // The game rebuilds its input map on some screens, unbound again.
    private static void bind_keys() {
        nint im = Mem.at(G.InputManager);
        int count = *(int*)(im + 0x9c);
        nint pairs = *(nint*)(im + 0xa4);
        if (pairs == 0) return;
        foreach (var (pair, bit, key) in CAMERA_PAIRS) {
            if (pair >= count) continue;
            nint e = pairs + pair * 8;
            if (*(uint*)(e + 4) == bit && *(uint*)e == 0) *(uint*)e = key;
        }
    }

    public void set_encounters(bool on) => GameThread.post("encounters", () => {
        Calls.setBattleEnable.fnptr!(on ? 1 : 0);
        Status.done(on ? "random encounters on" : "random encounters off");
    });

    public void set_exec_frames(int n) => GameThread.post("game speed", () => Mem.set_u8(G.ExecFrames, (byte)Math.Clamp(n, 1, 8)));

    /// <summary>Reads the developers' event list: every map id with its internal name.</summary>
    public void load_maps() => GameThread.post("map list", () => {
        flag.ensure_event_list();
        int count = Math.Clamp(Mem.i32(G.EventCount), 0, 1024);
        nint table = *(nint*)Mem.at(G.EventTable);
        if (table == 0 || count == 0) { Status.failed("the map list could not be read"); return; }
        MapEntry[] list = new MapEntry[count];
        for (int i = 0; i < count; i++) {
            nint e = table + i * 16;
            // A 12-character name runs into the id at +0xc.
            StringBuilder sb = new();
            for (int k = 0; k < 12; k++) {
                byte b = *(byte*)(e + k);
                if (b == 0 || b >= 0x80) break;
                sb.Append((char)b);
            }
            int id = *(int*)(e + 0xc);
            list[i] = new MapEntry(id, sb.ToString(), risk(id), entrances.GetValueOrDefault(id));
        }
        maps = [ .. list.OrderBy(m => m.Id) ];
    });

    /// <param name="allow_reset">The player confirmed that this map resets the game.</param>
    public void jump(int map, int entrance, bool allow_reset = false) => GameThread.post("map jump", () => {
        switch (risk(map)) {
            case JumpRisk.Crash:  Status.refused($"map {map} would crash the game: the PC version has no script for it"); return;
            case JumpRisk.Freeze: Status.refused($"map {map} would freeze the game: its script file is missing"); return;
            case JumpRisk.Reset when !allow_reset: Status.refused($"map {map} returns to the title screen"); return;
        }
        if (!in_field) { Status.refused("map jumps work only in the field"); return; }
        if (!jump_allowed()) { Status.refused("you can't leave this map right now; the debug flag allows it"); return; }
        Calls.AtelSetEventJump2.fnptr!(map, entrance, 0);
        Status.done($"jumping to map {map}, entrance {entrance}");
    });

    /// <summary>The flag goes on first: the viewer panels need it, and it lets the player leave the viewer map.</summary>
    public void go_to_model_viewer(TweakPanels panels) {
        if (!flag.on) flag.set(true, panels);
        jump(MODEL_VIEWER_MAP, 0);
    }

    public void load_viewer_save(int n) => GameThread.post("developer save", () => {
        if (!in_field) { Status.refused("developer saves load only in the field"); return; }
        if (!jump_allowed()) { Status.refused("you can't leave this map right now; the debug flag allows it"); return; }
        Mem.set_u32(G.ViewerSaveLast, 0xffffffff); // the loader skips a repeat of the last one
        guards.viewer_save_once = true;
        Calls.TkDebugLoadViewerSave.fnptr!(n);
        guards.viewer_save_once = false;
        if (Mem.u32(G.ViewerSaveLast) == (uint)n) Status.done($"developer save {n} loaded");
        else Status.failed($"developer save {n} could not be read");
    });

    /// <summary>Presses the free camera's own toggle bit for one frame, so the game attaches the fly controller itself.</summary>
    public void toggle_fly() => GameThread.post("free camera", () => {
        nint fc = *(nint*)Mem.at(G.FreeCamera);
        if (fc == 0) { Status.refused("no free camera yet"); return; }
        if (Mem.u8(G.AsyncControllerOn) != 0 || Mem.i32(G.PauseRequest) != 0 || Mem.i32(G.PauseEntered) != 0
         || NO_FREE_CAMERA_MAPS.Contains(*Fahrenheit.FFX.Globals.event_id)) {
            Status.refused("fly mode is not available here"); return;
        }
        uint* im = (uint*)Mem.at(G.InputManager);
        im[0] |= FLY_BIT;
        im[1] &= ~FLY_BIT;
        _fly_check = *(int*)fc;
        _fly_check_frame = _frame + 2;
    });

    public void toggle_photo_pause() => GameThread.post("photo pause", () => {
        nint fc = *(nint*)Mem.at(G.FreeCamera);
        if (fc == 0) { Status.refused("no free camera yet"); return; }
        Calls.FreeCamera_setFreePause.fnptr!(fc);
    });

    public void set_fly_speed(float v) => GameThread.post("fly speed", () => {
        nint fc = *(nint*)Mem.at(G.FreeCamera);
        if (fc != 0) *(float*)(fc + 8) = Math.Clamp(v, 1f, 1000f);
    });
}
