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
    private D.PtrFn? _next_set_data;
    private nint _converted; // the last rewrite; the game keeps pointers into it while the map is loaded

    public bool init(FhModule owner) => Calls.Yn_PreBgSetData.hook(owner, h_set_data);

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

    // Old layer: 0x50-byte header (+4 w, +8 h, +0xc x, +0x10 y, +0x14 depth, +0x24/+0x28 texture u/v, +0x34 texture),
    // the palette transfer (BITBLTBUF, TRXPOS, TRXREG, count, entry size 0x410), then count palettes of 0x400 bytes.
    // New layer (rcbgLayerLoad 0x0092b1a0): 0x90-byte header in 448-line units, an animation count (+12 padding), two
    // bytes (+14 padding), and a transfer (three registers, count, entry size, data); here without data.
    private const int OLD_HEADER = 0x50, OLD_TRANSFER = 0x20, PALETTE = 0x400;
    private const int NEW_HEADER = 0x90, TAIL = 0x38;

    private static byte[] convert_scene(ReadOnlySpan<byte> old) {
        int count = BitConverter.ToInt32(old);
        if (count is <= 0 or > 64) throw new InvalidDataException($"layer count {count}");
        using MemoryStream out_ = new();
        BinaryWriter w = new(out_);
        w.Write(count); w.Write(new byte[12]);
        int p = 4;
        for (int i = 0; i < count; i++) {
            byte[] h = new byte[NEW_HEADER];
            old.Slice(p, 0x3c).CopyTo(h);
            BitConverter.TryWriteBytes(h.AsSpan(0x08), BitConverter.ToInt32(h, 0x08) * 2); // height
            BitConverter.TryWriteBytes(h.AsSpan(0x10), BitConverter.ToInt32(h, 0x10) * 2); // y
            BitConverter.TryWriteBytes(h.AsSpan(0x40), 0x80808080u);                      // colour, 1.0 each
            BitConverter.TryWriteBytes(h.AsSpan(0x44), 1f);                               // scale
            BitConverter.TryWriteBytes(h.AsSpan(0x48), 1f);
            w.Write(h);
            w.Write(0); w.Write(new byte[12]);    // no animation
            w.Write(new byte[16]);                // +0xba, +0xbb
            w.Write(new byte[0x18]); w.Write(0); w.Write(0); // empty transfer
            int palettes = BitConverter.ToInt32(old.Slice(p + OLD_HEADER + 0x18));
            if (palettes is < 0 or > 16) throw new InvalidDataException($"layer {i}: {palettes} palettes");
            p += OLD_HEADER + OLD_TRANSFER + PALETTE * palettes;
        }
        w.Write(old.Slice(p, TAIL));
        while (out_.Length % CHUNK != 0) w.Write((byte)0);
        return out_.ToArray();
    }
}
