// SPDX-License-Identifier: MIT

using Yuta.DebugRevival.Native;

namespace Yuta.DebugRevival.Features;

/// <summary>
///     Two unfinished maps (bvyt14, bvyt15) keep their 2D background in an older format: no padding after counts, a
///     shorter layer header with the palettes inline, and the textures in a YNTM chunk of their own. The shipped loader
///     (rcbgLoadBin) misreads it and crashes. This rewrites such a background into the format the game reads, without
///     the pixels (the PC version draws its own layers), so the game still sets up the layers and the painted camera
///     (docs/research/old-backgrounds.md).
/// </summary>
public sealed unsafe class OldBackgrounds(FhLogger log) {
    private D.PtrFn?      _next_set_data;
    private D.PtrToIntFn? _next_exists;
    private nint _converted; // the last rewrite; the game keeps pointers into it while the map is loaded
    private string[] _generated = []; // the mod's replacement files, as "/FFX_DATA/..." in upper case

    /// <summary>The camera of the last background rewritten, eye and target in world units; null once another map's script is read.</summary>
    public static volatile Camera? last_camera;

    public sealed record Camera(Vector3 Eye, Vector3 Target, Vector3 Up, float Fov);

    /// <param name="efl">The mod's folder of replacement files for FFX (efl/x).</param>
    public bool init(FhModule owner, DirectoryInfo efl) {
        if (efl.Exists) {
            _generated = [ .. efl.GetFiles("*", SearchOption.AllDirectories)
                .Select(f => "/" + Path.GetRelativePath(efl.FullName, f.FullName).Replace('\\', '/').ToUpperInvariant()) ];
        }
        return Calls.Yn_PreBgSetData.hook(owner, h_set_data) && Calls.fiosIsFileExist.hook(owner, h_exists);
    }

    // Fahrenheit serves the mod's files when the game opens them, but the PC map loader first asks fiosIsFileExist,
    // which only knows the archive; files the archive never had would be skipped.
    [UnmanagedCallConv(CallConvs = [ typeof(CallConvCdecl) ])]
    private int h_exists(nint path) {
        _next_exists ??= Calls.fiosIsFileExist.chain_from(h_exists).fnptr!;
        if (_generated.Length > 0 && path != 0) {
            string p = Mem.cstr(path, 0x200).Replace('\\', '/').ToUpperInvariant();
            foreach (string g in _generated) if (p.EndsWith(g, StringComparison.Ordinal)) return 1;
        }
        return _next_exists(path);
    }

    [UnmanagedCallConv(CallConvs = [ typeof(CallConvCdecl) ])]
    private void h_set_data(nint data) {
        _next_set_data ??= Calls.Yn_PreBgSetData.chain_from(h_set_data).fnptr!;
        try {
            if (data != 0 && convert(data) is byte[] bytes) {
                if (_converted != 0) NativeMemory.Free((void*)_converted);
                _converted = (nint)NativeMemory.AlignedAlloc((nuint)bytes.Length, 16);
                bytes.CopyTo(new Span<byte>((void*)_converted, bytes.Length));
                log.Info($"old-format background rewritten ({bytes.Length} bytes)");
                data = _converted;
            }
        }
        catch (Exception e) {
            log.Error($"old background: {e}");
            return; // the original would crash on it
        }
        _next_set_data(data);
    }

    private const int CHUNK = 0x10, MAX_CHUNKS = 64;
    private static readonly uint YNDT = tag("YNDT"), YNSC = tag("YNSC"), YNTM = tag("YNTM"), YNED = tag("YNED");
    private static uint tag(string s) => (uint)(s[0] | s[1] << 8 | s[2] << 16 | s[3] << 24);

    // Chunks after the YNDT header: 16-byte header (tag, size in 16-byte rows), then the data.
    private static List<(uint tag, nint at, int size)>? chunks(nint data) {
        if (*(uint*)data != YNDT) return null;
        List<(uint, nint, int)> list = [];
        nint p = data + CHUNK;
        for (int i = 0; i < MAX_CHUNKS; i++) {
            uint t = *(uint*)p;
            int size = *(int*)(p + 4) * CHUNK;
            if (size < 0 || size > 0x1000000) return null;
            list.Add((t, p + CHUNK, size));
            if (t == YNED) return list;
            p += CHUNK + size;
        }
        return null;
    }

    /// <summary>The background in the shipped format, or null when it already is (no YNTM chunk).</summary>
    public static byte[]? convert(nint data) {
        var list = chunks(data);
        if (list is null || !list.Exists(c => c.tag == YNTM)) return null;
        using MemoryStream out_ = new();
        BinaryWriter w = new(out_);
        w.Write(YNDT); w.Write(new byte[12]);
        foreach (var (t, at, size) in list) {
            if (t == YNTM) continue;
            byte[] body = t == YNSC ? convert_scene(new ReadOnlySpan<byte>((void*)at, size)) : new ReadOnlySpan<byte>((void*)at, size).ToArray();
            w.Write(t); w.Write(body.Length / CHUNK); w.Write(new byte[8]);
            w.Write(body);
        }
        return out_.ToArray();
    }

    // Old layer: 0x50-byte header (+0 flags, +4 w, +8 h, +0xc x, +0x10 y, +0x14 depth, +0x24/+0x28 texture u/v, +0x34
    // texture), the palette transfer (BITBLTBUF, TRXPOS, TRXREG, count, entry size 0x410), then count palettes of 0x400
    // bytes, without the transfers' tags.
    private const int OLD_HEADER = 0x50, OLD_TRANSFER = 0x20, PALETTE = 0x400, TAIL = 0x38;
    private const float SCENE_SCALE = 10; // rcbgSceneInit's +0xf4, which rcbgSetViewMatrix multiplies eye and target by

    /// <summary>The model the PC files are built from (hiku17's) has this many layers; the background gets as many.</summary>
    public const int LAYERS = 4;

    /// <summary>Each old layer's flags and depth, in file order.</summary>
    public static List<(uint flags, float depth)> old_layers(ReadOnlySpan<byte> scene) {
        int count = BitConverter.ToInt32(scene);
        if (count is <= 0 or > 64) throw new InvalidDataException($"layer count {count}");
        List<(uint, float)> list = [];
        int p = 4;
        for (int i = 0; i < count; i++) {
            list.Add((BitConverter.ToUInt32(scene.Slice(p)), BitConverter.ToSingle(scene.Slice(p + 0x14))));
            int palettes = BitConverter.ToInt32(scene.Slice(p + OLD_HEADER + 0x18));
            if (palettes is < 0 or > 16) throw new InvalidDataException($"layer {i}: {palettes} palettes");
            p += OLD_HEADER + OLD_TRANSFER + PALETTE * palettes;
        }
        return list;
    }

    /// <summary>
    ///     Which old layers go into each of the <see cref="LAYERS"/> new ones: far to near, the neighbours with the
    ///     smallest depth gap merge until there are few enough. A group takes its farthest member's depth, so characters
    ///     between merged layers stay in front. Missing groups are empty and come last, so layer i is group i.
    /// </summary>
    public static List<(float depth, int[] members)> groups(List<(uint flags, float depth)> layers) {
        List<List<int>> gs = [ .. Enumerable.Range(0, layers.Count).OrderByDescending(i => layers[i].depth).Select(i => new List<int> { i }) ];
        while (gs.Count > LAYERS) {
            int best = 0;
            float gap = float.MaxValue;
            for (int i = 0; i + 1 < gs.Count; i++) {
                float d = layers[gs[i][^1]].depth - layers[gs[i + 1][0]].depth;
                if (d < gap) { gap = d; best = i; }
            }
            gs[best].AddRange(gs[best + 1]);
            gs.RemoveAt(best + 1);
        }
        List<(float, int[])> result = [ .. gs.Select(g => (layers[g[0]].depth, g.ToArray())) ];
        while (result.Count < LAYERS) result.Add((layers[gs[0][0]].depth, []));
        return result;
    }


    // New layer (rcbgLayerLoad 0x0092b1a0), as the shipped 2D maps have it: a 0x90-byte header in 448-line units; one
    // animation (count, padding, one frame word, padding, and a transfer of the 8-bit image: rcbgSceneProcLayer reads
    // it every frame); two bytes and padding; the palette transfer with two palettes. The PC version draws its own
    // layers, so the pixels and palettes are left zero; every layer covers the screen.
    private const int NEW_HEADER = 0x90, IMAGE = 0x38010, NEW_PALETTE = 0x410;
    private static readonly byte[] IMAGE_REGS = Convert.FromHexString("00000000003c0813" + "0000000000000000" + "00020000c0010000");
    private static readonly byte[] PALETTE_REGS = Convert.FromHexString("00000000002e0100" + "0000000000000000" + "1000000010000000");

    private static byte[] convert_scene(ReadOnlySpan<byte> old) {
        var layers = old_layers(old);
        int p = 4;
        for (int i = 0; i < layers.Count; i++) p += OLD_HEADER + OLD_TRANSFER + PALETTE * BitConverter.ToInt32(old.Slice(p + OLD_HEADER + 0x18));
        ReadOnlySpan<byte> tail = old.Slice(p, TAIL);

        using MemoryStream out_ = new();
        BinaryWriter w = new(out_);
        w.Write(LAYERS); w.Write(new byte[12]);
        foreach (var (depth, members) in groups(layers)) {
            byte[] h = new byte[NEW_HEADER];
            BitConverter.TryWriteBytes(h.AsSpan(0x00), members.Length > 0 ? layers[members[0]].flags : 0x8000u);
            BitConverter.TryWriteBytes(h.AsSpan(0x04), 512);
            BitConverter.TryWriteBytes(h.AsSpan(0x08), 448);
            BitConverter.TryWriteBytes(h.AsSpan(0x14), depth);
            BitConverter.TryWriteBytes(h.AsSpan(0x2c), 256);
            BitConverter.TryWriteBytes(h.AsSpan(0x30), 256);
            BitConverter.TryWriteBytes(h.AsSpan(0x38), 0x44);
            BitConverter.TryWriteBytes(h.AsSpan(0x40), 0x80808080u); // colour, 1.0 each
            BitConverter.TryWriteBytes(h.AsSpan(0x44), 1f);          // scale
            BitConverter.TryWriteBytes(h.AsSpan(0x48), 1f);
            w.Write(h);
            w.Write(1); w.Write(new byte[12]);                         // one animation
            w.Write(0); w.Write(new byte[12]);                         // its frame
            w.Write(IMAGE_REGS); w.Write(1); w.Write(IMAGE); w.Write(new byte[IMAGE]);
            w.Write(new byte[16]);                                    // +0xba, +0xbb
            w.Write(PALETTE_REGS); w.Write(2); w.Write(NEW_PALETTE); w.Write(new byte[2 * NEW_PALETTE]);
        }
        // Camera and layer depths are in the same units as the shipped format's: the game scales eye and target by the
        // scene's +0xf4 (10, rcbgSceneInit) and compares depths with the player's view depth x 0.1 (rcbgSceneSetChadist).
        w.Write(tail);
        float[] c = new float[13];
        for (int i = 0; i < c.Length; i++) c[i] = BitConverter.ToSingle(tail.Slice(4 * i));
        float f(int i) => c[i];
        last_camera = new Camera(new Vector3(f(0), f(1), f(2)) * SCENE_SCALE, new Vector3(f(4), f(5), f(6)) * SCENE_SCALE,
                                 new(f(8), f(9), f(10)), f(12));
        while (out_.Length % CHUNK != 0) w.Write((byte)0);
        return out_.ToArray();
    }
}
