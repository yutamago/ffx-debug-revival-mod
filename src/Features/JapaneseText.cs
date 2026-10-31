// SPDX-License-Identifier: MIT

using Yuta.DebugRevival.Native;

namespace Yuta.DebugRevival.Features;

/// <summary>
///     Maps without text in the game's language show the Japanese text built into their script, which a non-Japanese
///     game has no font for: the first message crashed. The first such map loads the Japanese base font, and its text
///     is drawn with it (docs/research/font.md).
/// </summary>
public sealed unsafe class JapaneseText(FhLogger log) {
    private const string FONT_PATH = "/ffx_ps2/ffx/master/new_jppc/menu/base.ftc";
    private const int    MAX_FONT_SIZE = 0x10000;

    private const int JP_SLOT = 0x3c00, US_SLOT = 0x3d40;
    private const int ATLAS_COUNT = 16;
    private const uint JP_FONT = 0x00c59568, JP_SHADOW = 0x00c595a8, US_FONT = 0x00c595e8, US_SHADOW = 0x00c59628;

    // The width-table buffers TOFTCTransfer gives dictionaries 1-3 (per-event kanji, battle, help).
    private static readonly uint[] SPARE_WIDTHS = [ 0x018656c0, 0x01865840, 0x018659c0 ];

    // '？' of the base font, drawn for kanji whose dictionary only the Japanese game loads.
    private const int  PLACEHOLDER = 31;
    private const byte PLACEHOLDER_CODE = 0x30 + PLACEHOLDER;

    private bool _tried;
    private volatile bool _active;
    private nint _placeholder_text;

    private D.IntFn?            _next_setup;
    private D.PtrPtrToPtrFn?    _next_font_info;
    private D.FontWidthFn?      _next_font_width;
    private D.TextureNameFn?    _next_texture_name;
    private D.IntPtrPtrToPtrFn? _next_image_wh;

    // The map hook goes in last, so nothing is loaded unless the drawing hooks are all in place.
    public bool init(FhModule owner) =>
        Calls.TOGetFontInfo.hook(owner, h_font_info) && Calls.TOGetFontWidth.hook(owner, h_font_width)
     && Calls.TOGetTextureName.hook(owner, h_texture_name) && Calls.TOGetImageWH.hook(owner, h_image_wh)
     && Calls.AtelEventSetUp.hook(owner, h_event_setup);

    [UnmanagedCallConv(CallConvs = [ typeof(CallConvCdecl) ])]
    private void h_event_setup(int map) {
        _next_setup ??= Calls.AtelEventSetUp.chain_from(h_event_setup).fnptr!;
        _next_setup(map);
        if (_tried || !uses_builtin_text()) return;
        _tried = true;
        try {
            setup();
        }
        catch (Exception e) {
            log.Error($"Japanese text: {e}");
        }
    }

    // After the set-up: the game found no text file for its language and took the .ebp's own table.
    private static bool uses_builtin_text() {
        if (Mem.u32(G.EventMessages) != 0) return false;
        nint ebp = *(nint*)Mem.at(G.EventEbp);
        return ebp != 0 && *(uint*)(ebp + 8) != 0 && Calls.TOGetFFXLang.fnptr!() != 0;
    }

    private void setup() {
        // Kanji of the dictionaries only a Japanese game loads would read a null width table; they now measure 0.
        for (int d = 1; d <= SPARE_WIDTHS.Length; d++) {
            uint ptr = G.FontDics + (uint)d * 16 + 4;
            if (Mem.u32(ptr) == 0) Mem.set_u32(ptr, SPARE_WIDTHS[d - 1]);
        }
        if (!load_font()) {
            Status.failed("this map's text could not be loaded; the game may crash when a message opens");
            return;
        }
        if (!load_atlases()) {
            Status.failed("this map's Japanese letters could not be loaded; its messages will look garbled");
            return;
        }
        _placeholder_text = (nint)NativeMemory.AllocZeroed(2);
        *(byte*)_placeholder_text = PLACEHOLDER_CODE;
        _active = true;
        log.Info("Japanese text: loaded the Japanese base font for a map without text in the game's language.");
        Status.done("this map has no text in your language, so it shows its Japanese text");
    }

    private bool load_font() {
        byte* name = stackalloc byte[0x100];
        fixed (byte* p = Encoding.ASCII.GetBytes(FONT_PATH + "\0")) Calls.fiosUnifyFilename.fnptr!((nint)p, (nint)name, 0x100);
        // A missing file is not caught by the archive layer.
        if ((Calls.fiosIsFileExist.fnptr!((nint)name) & 0xff) == 0) { log.Error($"Japanese text: {FONT_PATH} is missing"); return false; }
        byte[] file;
        using (FhVbfFileScope f = new(new ReadOnlySpan<byte>(name, 0x100))) {
            uint size = f.get_file_size();
            if (size < 0x40 || size > MAX_FONT_SIZE) { log.Error($"Japanese text: {FONT_PATH} has {size} bytes"); return false; }
            file = new byte[size];
            if (f.read(file) != size) { log.Error($"Japanese text: {FONT_PATH} could not be read"); return false; }
        }
        // TOFTCTransfer copies the widths into the game's own buffer, so the file can go.
        int rc;
        fixed (byte* p = file) rc = Calls.TOFTCTransfer.fnptr!((nint)p);
        if (rc != 0 || Mem.u32(G.FontDics + 4) == 0) { log.Error($"Japanese text: TOFTCTransfer returned {rc}"); return false; }
        return true;
    }

    // Both pages of the font and its shadow, the way GraphicAbmap_loadTextureClusters loads the game's own language.
    private bool load_atlases() {
        nint manager = Calls.ClusterManager_GetInstance.fnptr!();
        if (manager == 0) { log.Error("Japanese text: no cluster manager"); return false; }
        foreach (uint table in (ReadOnlySpan<uint>)[ JP_FONT, JP_SHADOW ]) {
            for (uint page = 0; page < 2; page++) {
                nint path = *(nint*)Mem.at(table + page * 4);
                nint cluster = Calls.ClusterManager_loadPCluster.fnptr!(manager, path);
                if (cluster == 0) { log.Error($"Japanese text: could not load {Mem.cstr(path)}"); return false; }
                Calls.PApplication_FixupClusters.fnptr!((nint)(&cluster), 1);
            }
        }
        return true;
    }

    /// <summary>Whether <paramref name="p"/> points into the loaded map's own message table, the one shown when it has no text in the game's language.</summary>
    private bool in_builtin_text(nint p) {
        if (!_active || Mem.u32(G.EventMessages) != 0) return false;
        nint ebp = *(nint*)Mem.at(G.EventEbp);
        if (ebp == 0) return false;
        uint start = *(uint*)(ebp + 8);
        if (start == 0 || p < ebp + (nint)start) return false;
        // The table ends at the next section; [+0x18] is the file size, so there always is one.
        uint end = uint.MaxValue;
        for (int o = 0xc; o <= 0x18; o += 4) {
            uint s = *(uint*)(ebp + o);
            if (s > start && s < end) end = s;
        }
        return end != uint.MaxValue && p < ebp + (nint)end;
    }

    private static float base_width(int idx) => *(sbyte*)(*(nint*)Mem.at(G.FontDics + 4) + idx) * 0.25f;

    // TOGetFontInfo's record for the Japanese base font: 14 x 18 cells, 18 per row, even and odd cells on two pages.
    private static void write_glyph(nint rec, int idx, float width) {
        float x = idx % 18 / 2 * 14, y = idx / 18 * 18;
        *(ushort*)rec = JP_SLOT;
        *(ushort*)(rec + 2) = 0x14;
        *(ushort*)(rec + 4) = (ushort)(idx & 1);
        *(float*)(rec + 0x08) = x;
        *(float*)(rec + 0x0c) = y;
        *(float*)(rec + 0x10) = x + width;
        *(float*)(rec + 0x14) = y + 18;
        *(float*)(rec + 0x18) = 0; // no slant in Japanese
        *(float*)(rec + 0x1c) = width;
        *(float*)(rec + 0x20) = 18;
    }

    // The Japanese game draws codes 0x5c..0x5e as this fixed 10-wide box.
    private static void write_box(nint rec) {
        write_glyph(rec, 0, 10);
        *(float*)(rec + 0x08) = 70;
        *(float*)(rec + 0x10) = 80;
    }

    // TOJoinsNeighbours in Japanese: a ':' between two 0x2c kanji is 5 wide.
    private static bool joins_neighbours(byte* p) => p[-2] == 0x2c && p[1] == 0x2c && p[-1] <= 99 && p[2] <= 0x62;

    [UnmanagedCallConv(CallConvs = [ typeof(CallConvCdecl) ])]
    private nint h_font_info(nint text, nint rec) {
        _next_font_info ??= Calls.TOGetFontInfo.chain_from(h_font_info).fnptr!;
        if (!in_builtin_text(text)) return _next_font_info(text, rec);
        byte* p = (byte*)text;
        byte c = *p;
        if (c is >= 0x5c and <= 0x5e) { write_box(rec); return text + 1; }
        if (c is >= 0x26 and <= 0x2b) { write_glyph(rec, PLACEHOLDER, base_width(PLACEHOLDER)); return text + 2; }
        if (c < 0x2c) return _next_font_info(text, rec);
        int idx = 0;
        if (c < 0x30) { idx = (c - 0x2b) * 0xd0; p++; }
        idx += *p - 0x30;
        float width = *p == 0x3a && joins_neighbours(p) ? 5 : base_width(idx);
        write_glyph(rec, idx, width);
        if (c != 0x47) return (nint)(p + 1);
        // A run of 0x47 is one glyph as wide as the run, inset by 4 on each side.
        int run = 1;
        while (p[run] == 0x47) run++;
        if (run > 1) {
            *(float*)(rec + 0x1c) = run * width;
            *(float*)(rec + 0x08) += 4;
            *(float*)(rec + 0x10) -= 4;
        }
        return (nint)(p + run);
    }

    // The game measures single-byte codes with the caller's dictionary (the English one); Japanese text needs 0.
    [UnmanagedCallConv(CallConvs = [ typeof(CallConvCdecl) ])]
    private nint h_font_width(nint text, int kind, nint width, int table) {
        _next_font_width ??= Calls.TOGetFontWidth.chain_from(h_font_width).fnptr!;
        if (!in_builtin_text(text)) return _next_font_width(text, kind, width, table);
        if (*(byte*)text is >= 0x26 and <= 0x2b) {
            _next_font_width(_placeholder_text, kind, width, 0);
            return text + 2;
        }
        return _next_font_width(text, kind, width, 0);
    }

    // Slot 0x3c00 names the Japanese atlas only in a Japanese game; the English branch has the same layout.
    [UnmanagedCallConv(CallConvs = [ typeof(CallConvCdecl) ])]
    private nint h_texture_name(int id, int nibbles, int index, int high, nint size) {
        _next_texture_name ??= Calls.TOGetTextureName.chain_from(h_texture_name).fnptr!;
        if (!_active || (short)id != JP_SLOT) return _next_texture_name(id, nibbles, index, high, size);
        nint name = _next_texture_name(US_SLOT, nibbles, index, high, size);
        for (uint i = 0; i < ATLAS_COUNT; i++) {
            if (name == *(nint*)Mem.at(US_FONT + i * 4))   return *(nint*)Mem.at(JP_FONT + i * 4);
            if (name == *(nint*)Mem.at(US_SHADOW + i * 4)) return *(nint*)Mem.at(JP_SHADOW + i * 4);
        }
        return name;
    }

    // Slot 0x3c00 gets dictionary 0's page size (128 x 1008) instead of the English one's.
    [UnmanagedCallConv(CallConvs = [ typeof(CallConvCdecl) ])]
    private nint h_image_wh(int id, nint w, nint h) {
        _next_image_wh ??= Calls.TOGetImageWH.chain_from(h_image_wh).fnptr!;
        if (!_active || id != JP_SLOT) return _next_image_wh(id, w, h);
        *(float*)w = *(short*)Mem.at(G.FontDics + 0xc);
        *(float*)h = *(short*)Mem.at(G.FontDics + 0xe);
        return h;
    }
}
