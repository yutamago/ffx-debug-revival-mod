// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using System.Threading.Tasks;

using Yuta.DebugRevival.Native;

namespace Yuta.DebugRevival.Features;

/// <summary>
///     Builds the PC files of the two unfinished 2D maps whose background only ships in the PS2 format (bvyt14, bvyt15):
///     one 2048x2048 DXT5 texture per layer, decoded from the PS2 data, and the model and asset list of hiku17's 2D map
///     with the names and layer depths changed. They are written from the player's own game files into the mod's
///     replacement folder (efl/x), which Fahrenheit reads at start-up, so they show from the next start
///     (docs/research/old-backgrounds.md).
/// </summary>
public sealed unsafe class RoomFiles(FhLogger log, DirectoryInfo efl) {
    private const int VERSION = 1;
    private static readonly (string area, string dir)[] ROOMS = [ ("bvyt14", "bvyt/bvyt14"), ("bvyt15", "bvyt/bvyt15") ];

    private const string TEMPLATE = "hiku17", TEMPLATE_DIR = "hiku/hiku17";
    private const string PC_MAP = "/FFX_Data/GameData/PS3Data/map/";
    private const int TEMPLATE_MODEL_SIZE = 51349;
    private static readonly float[] TEMPLATE_DEPTHS = [ 100f, 99f, 98f, 4.25f ];
    private const int DEPTH_COPIES = 8; // instance, bounds and the six vertices of each layer's quad
    private const int TEXTURE_HEADER = 0xa78, SIZE = 2048, SCREEN_W = 512, SCREEN_H = 448;

    private bool _checked;

    private string marker => Path.Join(efl.FullName, $"rooms-v{VERSION}.txt");

    private static string files_dir(string dir) => Path.Join("FFX_Data", "GameData", "PS3Data", "map", dir.Replace('/', Path.DirectorySeparatorChar), "2d");

    /// <summary>Game thread, once the game runs: builds the files when they are missing or from an older version.</summary>
    public void update() {
        if (_checked) return;
        _checked = true;
        if (File.Exists(marker)) return;
        try {
            List<(string area, string dir, byte[] vpa)> rooms = [];
            foreach (var (area, dir) in ROOMS) {
                string vpa = $"/ffx_ps2/ffx/master/jppc/map/{dir}/bin/mapout.vpa";
                if (FieldTools.file_exists(vpa)) rooms.Add((area, dir, read(vpa)));
            }
            byte[] model = read($"{PC_MAP}{TEMPLATE_DIR}/2d/mdl/D3D11/{TEMPLATE}.dae.phyre");
            string list = Encoding.Latin1.GetString(read($"{PC_MAP}{TEMPLATE_DIR}/2d/{TEMPLATE}.ahwin32"));
            byte[][] headers = [ .. Enumerable.Range(0, OldBackgrounds.LAYERS)
                .Select(i => read($"{PC_MAP}{TEMPLATE_DIR}/2d/tex/D3D11/0_{i}_512_448_0.dds.phyre", TEXTURE_HEADER)) ];
            if (rooms.Count == 0 || model.Length != TEMPLATE_MODEL_SIZE) {
                log.Warning("The unfinished rooms' backgrounds can't be built: the game's files are not the expected ones.");
                return;
            }
            Status.done("building the backgrounds of two unfinished rooms...");
            Task.Run(() => {
                try {
                    foreach (var (area, dir, vpa) in rooms) build(area, dir, vpa, model, list, headers);
                    File.WriteAllText(marker, $"Debug Revival built these from your game's files ({string.Join(", ", rooms.Select(r => r.area))}).\n");
                    log.Info("unfinished rooms' backgrounds built");
                    Status.done("two unfinished rooms got their backgrounds: restart the game to see them");
                }
                catch (Exception e) {
                    log.Error($"unfinished rooms' backgrounds: {e}");
                    Status.failed("the unfinished rooms' backgrounds could not be built; see the log");
                }
            });
        }
        catch (Exception e) {
            log.Error($"unfinished rooms' backgrounds: {e}");
        }
    }

    private static byte[] read(string path, int max = int.MaxValue) {
        using FhVbfFileScope file = new(path);
        byte[] buf = new byte[Math.Min(max, (int)file.get_file_size())];
        int n = (int)file.read(buf);
        if (n < buf.Length) throw new IOException($"{path}: read {n} of {buf.Length} bytes");
        return buf;
    }

    private void build(string area, string dir, byte[] vpa, byte[] template_model, string template_list, byte[][] headers) {
        string root = Path.Join(efl.FullName, files_dir(dir));
        Directory.CreateDirectory(Path.Join(root, "tex", "D3D11"));
        Directory.CreateDirectory(Path.Join(root, "mdl", "D3D11"));

        var (scene, layers) = decode(vpa);
        var groups = OldBackgrounds.groups(OldBackgrounds.old_layers(scene));
        for (int g = 0; g < groups.Count; g++) {
            byte[] image = new byte[SCREEN_W * SCREEN_H * 4];
            foreach (int i in groups[g].members) over(image, layers[i]);
            byte[] big = upscale_flipped(image);
            using FileStream f = File.Create(Path.Join(root, "tex", "D3D11", $"0_{g}_512_448_0.dds.phyre"));
            f.Write(headers[g]);
            f.Write(Dxt5.encode(big, SIZE, SIZE));
        }

        // Same-length names, so nothing in the model moves.
        byte[] model = Encoding.Latin1.GetBytes(Encoding.Latin1.GetString(template_model)
            .Replace(TEMPLATE_DIR, dir).Replace(TEMPLATE, area));
        List<int>[] at = [ .. TEMPLATE_DEPTHS.Select(d => find_all(model, d)) ];
        if (at.Any(a => a.Count != DEPTH_COPIES)) throw new InvalidDataException("the template model's layer depths are not where expected");
        for (int g = 0; g < at.Length; g++)
            foreach (int o in at[g]) BinaryPrimitives.WriteSingleLittleEndian(model.AsSpan(o), groups[g].depth);
        File.WriteAllBytes(Path.Join(root, "mdl", "D3D11", $"{area}.dae.phyre"), model);

        // The texture animation and effect textures stay hiku17's; the layers and model are the room's.
        string nl = template_list.Contains("\r\n") ? "\r\n" : "\n";
        string list = string.Join(nl, template_list.Split(nl).Select(l => l.Contains("textureAnimation") || l.Contains("/fp/") ? l
            : l.Replace(TEMPLATE_DIR, dir).Replace(TEMPLATE.ToUpperInvariant(), area.ToUpperInvariant()).Replace(TEMPLATE, area)));
        File.WriteAllText(Path.Join(root, $"{area}.ahwin32"), list, Encoding.Latin1);
    }

    private static List<int> find_all(byte[] data, float value) {
        List<int> list = [];
        uint bits = BitConverter.SingleToUInt32Bits(value);
        for (int i = 0; i + 4 <= data.Length; i++) if (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i)) == bits) list.Add(i);
        return list;
    }

    private static uint tag(string s) => (uint)(s[0] | s[1] << 8 | s[2] << 16 | s[3] << 24);
    private static int i32(byte[] b, int o) => BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(o));
    private static int csm1(int i) => (i & 0xe7) | ((i & 0x08) << 1) | ((i & 0x10) >> 1); // PS2 8-bit CLUT order

    /// <summary>The old background's YNSC chunk and each layer as a 512x448 RGBA image (PS2 lines doubled).</summary>
    private static (byte[] scene, List<byte[]> layers) decode(byte[] vpa) {
        int p = i32(vpa, 0x10) + 0x10; // section 0, after the YNDT header
        Dictionary<uint, (int at, int size)> chunks = [];
        for (int n = 0; n < 64; n++) {
            uint t = (uint)i32(vpa, p);
            int size = i32(vpa, p + 4) * 16;
            chunks[t] = (p + 0x10, size);
            if (t == tag("YNED")) break;
            p += 0x10 + size;
        }
        var (tm, _) = chunks[tag("YNTM")];
        var (sc, sc_size) = chunks[tag("YNSC")];
        // YNTM: count, padding, a TEX0 register per texture (0x20 each), then the 8-bit textures one after another.
        int count = i32(vpa, tm);
        List<(int w, int at)> textures = [];
        int q = tm + 0x10 + 0x20 * count;
        for (int i = 0; i < count; i++) {
            ulong tex0 = BinaryPrimitives.ReadUInt64LittleEndian(vpa.AsSpan(tm + 0x10 + 0x20 * i));
            int tw = 1 << (int)((tex0 >> 26) & 15), th = 1 << (int)((tex0 >> 30) & 15);
            textures.Add((tw, q));
            q += tw * th;
        }
        // YNSC: count; per layer a 0x50-byte header, the palette transfer (0x20) and its palettes (0x400 each).
        List<byte[]> layers = [];
        int layer_count = i32(vpa, sc);
        p = sc + 4;
        for (int i = 0; i < layer_count; i++) {
            int w = i32(vpa, p + 4), h = i32(vpa, p + 8), x = i32(vpa, p + 0xc), y = i32(vpa, p + 0x10);
            int u0 = i32(vpa, p + 0x24), v0 = i32(vpa, p + 0x28);
            var (tw, tat) = textures[i32(vpa, p + 0x34)];
            int palettes = i32(vpa, p + 0x50 + 0x18), pal = p + 0x70;
            byte[] img = new byte[SCREEN_W * SCREEN_H * 4];
            for (int v = 0; v < h; v++)
                for (int u = 0; u < w; u++) {
                    int e = pal + 4 * csm1(vpa[tat + (v0 + v) * tw + u0 + u]);
                    byte a = (byte)Math.Min(255, vpa[e + 3] * 2); // PS2 alpha: 0x80 is opaque
                    int xx = x + u;
                    for (int yy = 2 * (y + v); yy <= 2 * (y + v) + 1; yy++) {
                        if (xx < 0 || xx >= SCREEN_W || yy < 0 || yy >= SCREEN_H) continue;
                        int o = (yy * SCREEN_W + xx) * 4;
                        img[o] = vpa[e]; img[o + 1] = vpa[e + 1]; img[o + 2] = vpa[e + 2]; img[o + 3] = a;
                    }
                }
            layers.Add(img);
            p = pal + 0x400 * palettes;
        }
        return (vpa.AsSpan(sc, sc_size).ToArray(), layers);
    }

    private static void over(byte[] dst, byte[] src) {
        for (int o = 0; o < dst.Length; o += 4) {
            int a = src[o + 3];
            if (a == 0) continue;
            int da = dst[o + 3], oa = a + da * (255 - a) / 255;
            for (int c = 0; c < 3; c++) dst[o + c] = (byte)(oa == 0 ? 0 : (src[o + c] * a + dst[o + c] * da * (255 - a) / 255) / oa);
            dst[o + 3] = (byte)oa;
        }
    }

    // Bilinear on premultiplied colour, so transparent texels don't bleed; the rows bottom first, as the PC's
    // textures store them.
    private static byte[] upscale_flipped(byte[] src) {
        byte[] dst = new byte[SIZE * SIZE * 4];
        Span<float> acc = stackalloc float[4];
        for (int y = 0; y < SIZE; y++) {
            float fy = Math.Clamp((y + 0.5f) * SCREEN_H / SIZE - 0.5f, 0, SCREEN_H - 1);
            int y0 = (int)fy, y1 = Math.Min(y0 + 1, SCREEN_H - 1);
            float ty = fy - y0;
            int row = (SIZE - 1 - y) * SIZE;
            for (int x = 0; x < SIZE; x++) {
                float fx = Math.Clamp((x + 0.5f) * SCREEN_W / SIZE - 0.5f, 0, SCREEN_W - 1);
                int x0 = (int)fx, x1 = Math.Min(x0 + 1, SCREEN_W - 1);
                float tx = fx - x0;
                acc.Clear();
                add(src, acc, x0, y0, (1 - tx) * (1 - ty)); add(src, acc, x1, y0, tx * (1 - ty));
                add(src, acc, x0, y1, (1 - tx) * ty);       add(src, acc, x1, y1, tx * ty);
                int d = (row + x) * 4;
                if (acc[3] > 0) for (int c = 0; c < 3; c++) dst[d + c] = (byte)Math.Clamp(acc[c] / acc[3] + 0.5f, 0, 255);
                dst[d + 3] = (byte)Math.Clamp(acc[3] * 255 + 0.5f, 0, 255);
            }
        }
        return dst;
    }

    private static void add(byte[] src, Span<float> acc, int x, int y, float weight) {
        int o = (y * SCREEN_W + x) * 4;
        float a = src[o + 3] / 255f;
        acc[0] += src[o] * a * weight; acc[1] += src[o + 1] * a * weight; acc[2] += src[o + 2] * a * weight; acc[3] += a * weight;
    }
}

/// <summary>A small DXT5 (BC3) encoder: bounding-box colour endpoints, eight-step alpha.</summary>
internal static class Dxt5 {
    public static byte[] encode(byte[] rgba, int w, int h) {
        byte[] out_ = new byte[w * h];
        Parallel.For(0, h / 4, by => {
            Span<byte> px = stackalloc byte[64];
            for (int bx = 0; bx < w / 4; bx++) {
                for (int y = 0; y < 4; y++) rgba.AsSpan(((by * 4 + y) * w + bx * 4) * 4, 16).CopyTo(px.Slice(y * 16));
                block(px, out_.AsSpan((by * (w / 4) + bx) * 16, 16));
            }
        });
        return out_;
    }

    private static void block(ReadOnlySpan<byte> px, Span<byte> o) {
        byte amin = 255, amax = 0;
        for (int i = 0; i < 16; i++) { byte a = px[i * 4 + 3]; amin = Math.Min(amin, a); amax = Math.Max(amax, a); }
        o[0] = amax; o[1] = amin;
        ulong abits = 0;
        if (amax > amin) {
            for (int i = 0; i < 16; i++) {
                int k = (int)MathF.Round((float)(amax - px[i * 4 + 3]) * 7 / (amax - amin)); // 0 = amax .. 7 = amin
                abits |= (ulong)(k == 0 ? 0 : k == 7 ? 1 : k + 1) << (3 * i);
            }
        }
        for (int i = 0; i < 6; i++) o[2 + i] = (byte)(abits >> (8 * i));

        Span<int> mn = [ 255, 255, 255 ], mx = [ 0, 0, 0 ];
        for (int i = 0; i < 16; i++)
            for (int c = 0; c < 3; c++) { mn[c] = Math.Min(mn[c], px[i * 4 + c]); mx[c] = Math.Max(mx[c], px[i * 4 + c]); }
        for (int c = 0; c < 3; c++) { int inset = (mx[c] - mn[c]) / 16; mn[c] += inset; mx[c] -= inset; }
        ushort c0 = rgb565(mx), c1 = rgb565(mn);
        if (c0 < c1) (c0, c1) = (c1, c0);
        o[8] = (byte)c0; o[9] = (byte)(c0 >> 8); o[10] = (byte)c1; o[11] = (byte)(c1 >> 8);
        uint bits = 0;
        if (c0 != c1) {
            Span<int> pal = stackalloc int[12];
            expand(c0, pal[0..3]); expand(c1, pal[3..6]);
            for (int c = 0; c < 3; c++) { pal[6 + c] = (2 * pal[c] + pal[3 + c]) / 3; pal[9 + c] = (pal[c] + 2 * pal[3 + c]) / 3; }
            for (int i = 0; i < 16; i++) {
                int best = 0, bd = int.MaxValue;
                for (int k = 0; k < 4; k++) {
                    int dr = px[i * 4] - pal[3 * k], dg = px[i * 4 + 1] - pal[3 * k + 1], db = px[i * 4 + 2] - pal[3 * k + 2];
                    int d = dr * dr + dg * dg + db * db;
                    if (d < bd) { bd = d; best = k; }
                }
                bits |= (uint)best << (2 * i);
            }
        }
        BinaryPrimitives.WriteUInt32LittleEndian(o.Slice(12), bits);
    }

    private static ushort rgb565(ReadOnlySpan<int> c) => (ushort)((c[0] * 31 + 127) / 255 << 11 | (c[1] * 63 + 127) / 255 << 5 | (c[2] * 31 + 127) / 255);

    private static void expand(ushort c, Span<int> o) {
        int r = c >> 11 & 31, g = c >> 5 & 63, b = c & 31;
        o[0] = r << 3 | r >> 2; o[1] = g << 2 | g >> 4; o[2] = b << 3 | b >> 2;
    }
}
