// SPDX-License-Identifier: MIT

using Yuta.DebugRevival.Native;

namespace Yuta.DebugRevival.Features;

/// <summary>
///     Areas whose geometry ships but whose script does not; a jump there froze the game. The game now reads the
///     developers' camera-test map (camtest0) in place of the missing files, and the mod points that script at the area's
///     geometry, puts the player on its floor and turns off the test's camera tricks (docs/research/stand-in-maps.md).
/// </summary>
public sealed unsafe class StandInMaps(FhLogger log) {
    /// <param name="Map">The event id the game has no script for.</param>
    /// <param name="MapIndex">The geometry its name points at (module 0x13 pair).</param>
    /// <param name="FloorOnly">Only the PS2 file ships: the walk mesh, minimap and background data, but nothing the PC
    /// version draws. The background is in an older format; <see cref="OldBackgrounds"/> rewrites it.</param>
    /// <param name="Borrowed">The PS2 map's folder when the map table has no pair for it: the pair at
    /// <paramref name="MapIndex"/>, whose own files are missing, names this one's instead.</param>
    /// <param name="Fixed">A 2D map: its background is painted for one camera, which the game sets from the background
    /// data (rcbgSceneSetMatrix); camtest0's own follow camera would override it for the characters.</param>
    public readonly record struct StandIn(int Map, short MapIndex, string Area, bool FloorOnly = false, string? Borrowed = null,
                                          bool Fixed = true);

    public static readonly StandIn[] ALL = [
        new(262, 397, "hiku17"), new(350, 398, "hiku18", Fixed: false), new(373, 282, "ikai07"),
        new(360, 409, "bvyt15", FloorOnly: true),
        new(216, 21, "bvyt14", FloorOnly: true, Borrowed: "bvyt/bvyt14"),    // pair 21: znkd11
        // hiku12 (304) and lchb19 (300) ship an empty .vpa (a bare MAP1 header), which crashes the map loader.
    ];

    private const int TEMPLATE_MAP = 2; // camtest0
    private const string TEMPLATE_NAME = "/camtest0.ebp";

    // camtest0's code, relative to its code start: worker 0's set-up places the player with
    // pushimm x; pushfc f8; pushimm z; func_facc 0x0013 (setPosition).
    private const int SET_POSITION = 0x24;
    private static readonly byte[] SET_POSITION_CODE = [ 0xae, 87, 0, 0xaf, 8, 0, 0xae, 17, 0, 0xd8, 0x13, 0 ];
    private const int Y_CONSTANT = 8;
    // Worker 0's set-up, after the encounter calls: pushimm 0, -1, 0; func_facc 0x6027 (CamRefSetTarget); pushfc f9,
    // f11, f10; func_facc 0x6004 (CamSetPolar).
    private const int FOLLOW_CAMERA = 0x48;
    private static readonly byte[] FOLLOW_CAMERA_CODE = Convert.FromHexString("ae0000aeffffae0000d82760af0900af0b00af0a00d80460");
    private const byte OP_NOP = 0x00, OP_PUSHIMM = 0xae;
    private const byte OP_JMP = 0xb0, OP_PUSHFC = 0xaf, OP_END = 0x3c;

    private bool _verified;
    private volatile bool _active;
    private int _patched_map = -1;
    private int _report_frame;
    private bool _pinned; // the stick's direction is pinned to a 2D stand-in's camera // counts down after a stand-in loads; at 1, what the game set up goes to the log
    private readonly HashSet<int> _usable = [];
    // (cdrom.fnd entry, the name it gives instead) of the borrowed pairs; NUL-terminated, never freed
    private readonly Dictionary<int, nint> _names = [];

    private D.IntToPtrFn?    _next_file_name;
    private D.IntIntToPtrFn? _next_read;
    private D.IntFn?         _next_setup;

    public bool active => _active;
    /// <summary>Set when <see cref="OldBackgrounds"/> is in place; the floor-only maps crash without it.</summary>
    public bool floor_only_allowed;

    public static StandIn? find(int map) {
        foreach (StandIn s in ALL) if (s.Map == map) return s;
        return null;
    }

    public bool init(FhModule owner) {
        _active = Calls.PcFileName.hook(owner, h_file_name) && Calls.TkCdReadWithMallocWithMessage.hook(owner, h_read)
               && Calls.AtelEventSetUp.hook(owner, h_event_setup);
        return _active;
    }

    /// <summary>Whether a jump to the map uses a stand-in. Game thread.</summary>
    public bool covers(int map) => ready() && _usable.Contains(map);

    // Each stand-in only while the template is camtest0, its own script is still missing (a game update could ship it)
    // and its geometry is there; a borrowed pair only while its own .vpa is missing. Checked once the file tables are
    // loaded. Calls the game's PcFileName, so the names come out as the game's own would.
    private bool ready() {
        if (!_active) return false;
        if (_verified) return true;
        nint name = FieldTools.script_name(TEMPLATE_MAP);
        if (name <= 0) return false;
        _verified = true;
        if (!Mem.cstr(name, 0x400).EndsWith(TEMPLATE_NAME, StringComparison.Ordinal)) {
            log.Warning("Unfinished areas stay closed: the camera test map is not the one their stand-in was made for.");
            _active = false;
            return false;
        }
        foreach (StandIn s in ALL) {
            if (s.FloorOnly && !floor_only_allowed) continue;
            nint own = FieldTools.script_name(s.Map);
            if (own > 0 && FieldTools.file_exists(FieldTools.host_path(Mem.cstr(own, 0x400)))) continue;
            int loc = FieldTools.map_file_loc(s.MapIndex);
            if (loc < 0) continue;
            if (s.Borrowed is string dir) {
                string old_vpa = FieldTools.fnd_name(loc + 1);
                string level = $"host0:/ffx/master/jppc/map/{dir}/bin/mapout.txa", vpa = $"host0:/ffx/master/jppc/map/{dir}/bin/mapout.vpa";
                if (old_vpa.Length == 0 || FieldTools.file_exists(FieldTools.host_path(old_vpa))
                 || !FieldTools.file_exists(FieldTools.host_path(vpa))) continue;
                _names[loc] = c_string(level);
                _names[loc + 1] = c_string(vpa);
            }
            else if (!FieldTools.file_exists(FieldTools.host_path(FieldTools.fnd_name(loc + 1)))) continue;
            _usable.Add(s.Map);
        }
        if (_usable.Count < ALL.Length) log.Warning($"{ALL.Length - _usable.Count} unfinished areas stay closed: their files are not the expected ones.");
        return true;
    }

    private static nint c_string(string s) {
        byte[] bytes = Encoding.ASCII.GetBytes(s + "\0");
        nint p = (nint)NativeMemory.Alloc((nuint)bytes.Length);
        bytes.CopyTo(new Span<byte>((void*)p, bytes.Length));
        return p;
    }

    // Every file of a stand-in map (its script and motion groups) is camtest0's; a borrowed pair names its PS2 map.
    [UnmanagedCallConv(CallConvs = [ typeof(CallConvCdecl) ])]
    private nint h_file_name(int loc) {
        _next_file_name ??= Calls.PcFileName.chain_from(h_file_name).fnptr!;
        if (_active && (_verified || ready())) {
            if (_names.TryGetValue(loc, out nint name)) return name;
            foreach (int map in _usable) {
                int first = FieldTools.event_loc(map);
                if (first >= 0 && loc >= first && loc < first + FieldTools.FILES_PER_MAP) {
                    return _next_file_name(FieldTools.event_loc(TEMPLATE_MAP) + (loc - first));
                }
            }
        }
        return _next_file_name(loc);
    }

    [UnmanagedCallConv(CallConvs = [ typeof(CallConvCdecl) ])]
    private nint h_read(int module, int file) {
        _next_read ??= Calls.TkCdReadWithMallocWithMessage.chain_from(h_read).fnptr!;
        nint buf = _next_read(module, file);
        if (module != 0xc || file % FieldTools.FILES_PER_MAP != 0) return buf;
        _patched_map = -1;
        OldBackgrounds.last_camera = null;
        if (_pinned) { Mem.set_u8(G.PlayerDirFixOn, 0); _pinned = false; }
        if (!_active || !_verified || buf == 0) return buf;
        if (find(file / FieldTools.FILES_PER_MAP) is not StandIn s || !_usable.Contains(s.Map)) return buf;
        try {
            if (patch_script(buf, s)) _patched_map = s.Map;
            else log.Error($"stand-in for map {s.Map}: camtest0 is not as expected; it loads unchanged");
        }
        catch (Exception e) {
            log.Error($"stand-in for map {s.Map}: {e}");
        }
        return buf;
    }

    // The stick turns by the active camera's yaw (MsCameraGetYaw, g_Camera), which a 2D map's background camera
    // doesn't set; shipped 2D maps' scripts correct it themselves. Pin it to the background camera's yaw.
    private void pin_controls(int map) {
        nint sys = *(nint*)Mem.at(G.YnMapSystem);
        nint m = sys != 0 ? *(nint*)sys : 0;
        nint scene = m != 0 ? *(nint*)(m + 8) : 0;
        if (scene == 0) return;
        float* v = (float*)NativeMemory.AlignedAlloc(48, 16);
        try {
            for (int i = 0; i < 4; i++) { v[i] = *(float*)(scene + 0x78 + 4 * i); v[4 + i] = *(float*)(scene + 0x68 + 4 * i); }
            v[8] = 0;
            Calls.MsVectorRect2Polar.fnptr!((nint)v, (nint)(v + 4), (nint)(v + 8), 0, 0);
            Mem.set_u32(G.PlayerDirFix, BitConverter.SingleToUInt32Bits(v[8]));
            Mem.set_u8(G.PlayerDirFixOn, 1);
            _pinned = true;
            log.Info($"stand-in for map {map}: controls follow the background camera (yaw {v[8]:0.###})");
        }
        finally {
            NativeMemory.AlignedFree(v);
        }
    }

    /// <summary>Game thread, after each update.</summary>
    public void update() {
        if (_report_frame <= 0 || --_report_frame > 0) return;
        try {
            nint chr = Calls.Sg_GetPlayer.fnptr!();
            string player = chr != 0 ? $"({*(float*)(chr + 0xc):0.#}, {*(float*)(chr + 0x10):0.#}, {*(float*)(chr + 0x14):0.#})" : "none";
            nint sys = *(nint*)Mem.at(G.YnMapSystem);
            nint m = sys != 0 ? *(nint*)sys : 0;
            nint scene = m != 0 ? *(nint*)(m + 8) : 0;
            string bg = "no 2D scene";
            if (scene != 0) {
                float* f = (float*)scene;
                int layers = *(int*)(scene + 4);
                nint list = *(nint*)(scene + 8);
                StringBuilder depths = new();
                for (int i = 0; i < Math.Min(layers, 8) && list != 0; i++) depths.Append($" {*(float*)(list + 0xc0 * i + 0x14):0.#}");
                bg = $"eye ({f[0x68 / 4]:0.##}, {f[0x6c / 4]:0.##}, {f[0x70 / 4]:0.##}) target ({f[0x78 / 4]:0.##}, {f[0x7c / 4]:0.##}, {f[0x80 / 4]:0.##}) fov {f[0xa8 / 4]:0.#}, {layers} layers at{depths}";
            }
            log.Info($"stand-in check: player {player}; {bg}");
        }
        catch (Exception e) {
            log.Error($"stand-in check: {e}");
        }
    }

    // The block's offsets; false when camtest0 differs from the one this was written for.
    private static bool locate(nint ebp, out nint block, out nint code, out nint worker0, out nint worker2) {
        block = code = worker0 = worker2 = 0;
        if (*(ushort*)ebp != 0x5645 /* "EV" */) return false;
        block = ebp + *(int*)(ebp + 4);
        code = block + *(int*)(block + 0x30);
        if (*(ushort*)(block + 0x34) != 5) return false;
        worker0 = block + *(int*)(block + 0x38);
        worker2 = block + *(int*)(block + 0x38 + 8);
        return true;
    }

    private static nint entry(nint block, nint code, nint worker, int e) =>
        code + *(int*)(block + *(int*)(worker + 0x20) + 4 * e);

    // Before the game reads it: the area's geometry in entrance 0, and the test's camera loops off. Worker 0's main loop
    // cycles depth masks every 80 frames, and worker 2 confines the camera to camtest0's room.
    private static bool patch_script(nint ebp, StandIn s) {
        if (!locate(ebp, out nint block, out nint code, out nint w0, out nint w2)) return false;
        if (!new ReadOnlySpan<byte>((void*)(code + SET_POSITION), SET_POSITION_CODE.Length).SequenceEqual(SET_POSITION_CODE)) return false;
        nint loop = entry(block, code, w0, 1), camera = entry(block, code, w2, 0);
        if (*(byte*)loop != OP_JMP || *(byte*)camera != OP_PUSHFC) return false;
        nint entrances = block + *(int*)(block + 4);
        if (*(int*)(block + 4) == 0 || *(int*)(block + 0x2c) != 0 && has_language_maps(block)) return false;
        // Worker 1 attaches the camera (e0) and keeps it on the player (e1).
        nint w1 = block + *(int*)(block + 0x38 + 4);
        nint attach = entry(block, code, w1, 0), follow = entry(block, code, w1, 1);
        Span<byte> set_up = new((void*)(code + FOLLOW_CAMERA), FOLLOW_CAMERA_CODE.Length);
        if (!set_up.SequenceEqual(FOLLOW_CAMERA_CODE) || *(byte*)attach != OP_PUSHIMM || *(byte*)follow != OP_PUSHIMM) return false;

        *(short*)entrances = s.MapIndex;
        *(byte*)loop = OP_END;
        *(byte*)camera = OP_END;
        if (s.Fixed) {
            set_up.Fill(OP_NOP);
            *(byte*)attach = OP_END;
            *(byte*)follow = OP_END;
        }
        return true;
    }

    // AtelGetMapIndex takes a per-language map index from the sound table when it has one; camtest0 has none.
    private static bool has_language_maps(nint block) {
        nint sound = block + *(int*)(block + 0x2c);
        int rec = *(int*)(sound + 0x3c);
        return rec != 0 && *(int*)(block + rec + 0xc) != 0;
    }

    // After the set-up the area's walk mesh is in place: the player starts on it.
    [UnmanagedCallConv(CallConvs = [ typeof(CallConvCdecl) ])]
    private void h_event_setup(int map) {
        _next_setup ??= Calls.AtelEventSetUp.chain_from(h_event_setup).fnptr!;
        _next_setup(map);
        if (_patched_map != map) return;
        _patched_map = -1;
        try {
            if (floor_point() is not (float x, float y, float z)) {
                Status.failed("this area has no floor; walk through walls (Field tab) or fly the free camera to look around");
                return;
            }
            nint ebp = *(nint*)Mem.at(G.EventEbp);
            if (!locate(ebp, out nint block, out nint code, out nint w0, out _)) return;
            if (x is < short.MinValue or > short.MaxValue || z is < short.MinValue or > short.MaxValue) return;
            byte* p = (byte*)(code + SET_POSITION);
            *(short*)(p + 1) = (short)MathF.Round(x);
            *(short*)(p + 7) = (short)MathF.Round(z);
            *(float*)(block + *(int*)(w0 + 0x1c) + 4 * Y_CONSTANT) = y;
            log.Info($"stand-in for map {map}: player at ({x:0.#}, {y:0.#}, {z:0.#})");
            if (find(map) is StandIn { Fixed: true }) pin_controls(map);
            _report_frame = 60;
        }
        catch (Exception e) {
            log.Error($"stand-in for map {map}: {e}");
        }
    }

    // The middle of the walk-mesh triangle nearest the mesh's middle, in world units. y is 1 less than the floor's, as
    // the ground search (Sg_IdFindTriangle) takes the lowest height that is not below the point's.
    private static (float, float, float)? floor_point() {
        int count = Mem.i32(G.IdCount);
        nint tris = *(nint*)Mem.at(G.IdTriangles), verts = *(nint*)Mem.at(G.IdVertices);
        float zoom = Mem.f32(G.IdZoom);
        if (count <= 0 || count > 0x10000 || tris == 0 || verts == 0 || !(zoom > 0)) return null;
        (float x, float y, float z) centre(int i) {
            float x = 0, y = 0, z = 0;
            for (int k = 0; k < 3; k++) {
                short* v = (short*)(verts + 8 * *(ushort*)(tris + 16 * i + 2 * k));
                x += v[0]; y += v[1]; z += v[2];
            }
            return (x / 3, y / 3, z / 3);
        }
        int best = 0;
        double best_d = double.MaxValue;
        if (OldBackgrounds.last_camera is OldBackgrounds.Camera cam) {
            // A fixed camera: the triangle nearest the middle of the screen.
            Vector3 f = Vector3.Normalize(cam.Target - cam.Eye), r = Vector3.Normalize(Vector3.Cross(f, cam.Up)), u = Vector3.Cross(r, f);
            for (int i = 0; i < count; i++) {
                var c = centre(i);
                Vector3 d = new Vector3(c.x, c.y, c.z) / zoom - cam.Eye;
                float depth = Vector3.Dot(d, f);
                if (depth <= 0) continue;
                float sx = Vector3.Dot(d, r) / depth, sy = Vector3.Dot(d, u) / depth;
                double off = sx * sx + sy * sy;
                if (off < best_d) { best_d = off; best = i; }
            }
        }
        if (best_d == double.MaxValue) {
            double mx = 0, mz = 0;
            for (int i = 0; i < count; i++) { var c = centre(i); mx += c.x; mz += c.z; }
            mx /= count; mz /= count;
            for (int i = 0; i < count; i++) {
                var c = centre(i);
                double d = (c.x - mx) * (c.x - mx) + (c.z - mz) * (c.z - mz);
                if (d < best_d) { best_d = d; best = i; }
            }
        }
        var (bx, by, bz) = centre(best);
        return (bx / zoom, (by - 1) / zoom, bz / zoom);
    }
}
