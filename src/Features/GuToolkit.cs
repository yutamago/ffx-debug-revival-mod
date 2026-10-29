// SPDX-License-Identifier: MIT OR Apache-2.0

using Yuta.DebugRevival.Gs;
using Yuta.DebugRevival.Native;

namespace Yuta.DebugRevival.Features;

/// <summary>
///     The PS2 debug GUI ("Gu" toolkit): the developers' main debug menu
///     (CHR / MAP / SYS / BTL / CAM / EFF / EVENT) and the windows it opens,
///     including the battle windows. FFX.exe still runs all of it, but on PC
///     its drawing goes to a stubbed DMA send, its font is never uploaded and
///     its mouse reads a stubbed USB driver. The mod:
///     <list type="bullet">
///         <item>starts it without the hanging host-clock init (Gu_Init, then the menu window);</item>
///         <item>runs it each frame after Sg_MainDebug (the game does that itself when the debug flag is on);</item>
///         <item>copies each finished packet in a Gu_DrawEnd hook and parses it for the renderer;</item>
///         <item>feeds the Windows mouse into FFXUsbMouseRead2;</item>
///         <item>blocks the items that would hang, corrupt the story flags or warp.</item>
///     </list>
///     Facts: docs/research/gs.md.
/// </summary>
public sealed unsafe class GuToolkit(DebugFlag flag) {
    private const uint PACKET_BUFFER  = 0x00c498dc; // ptr: the 1 MB packet buffer
    private const uint GU_PACKET      = 0x013036c4; // ptr: the packet Gu_DrawEnd closed
    private const uint GU_HIDDEN      = 0x0130375c; // int
    private const uint SCREEN_H       = 0x023cbbe0; // int
    private const uint SCREEN_W       = 0x023cbbe4; // int
    private const uint MOUSE_FX       = 0x01325248; // f32 accumulators: X = fx/2, Y = fy/3
    private const uint MOUSE_FY       = 0x0132524c;
    private const uint CLOCK_MONTH    = 0x023cbc24, CLOCK_DAY = 0x023cbc20, CLOCK_WEEKDAY = 0x023cbc1c;
    private const uint CLOCK_HOUR     = 0x023cbc30, CLOCK_MIN = 0x023cbc2c, CLOCK_SEC = 0x023cbc28;
    private const uint WMAINPROC      = 0x0084f550;

    /// <summary>A parsed frame of the toolkit, for the render thread.</summary>
    public sealed record Frame(long Stamp, int W, int H, GsPrim[] Prims);

    public volatile Frame? frame;
    public volatile string blocked = "";

    // Game-thread state.
    private bool _inited;
    private nint _menu;
    /// <summary>Whether the toolkit is shown (the mod drives it, or the debug flag does).</summary>
    public volatile bool open;

    // Mouse, written by the render thread.
    public volatile bool capture_mouse;
    public volatile float mouse_x, mouse_y;   // logical 512 x 208
    public volatile int mouse_buttons;        // bit 0 left, 1 right, 2 middle
    private int _wheel;
    public void add_wheel(int steps) => Interlocked.Add(ref _wheel, steps);

    // The effect debugger's "CAM/PLY/DIS" line (op_num), sent every frame and drawn nowhere on PC.
    private readonly string[] _op_lines = new string[4];
    private int _op_count;
    public volatile string[] op_lines = [];

    // Next functions in the hook chains.
    private D.VoidFn? _next_main_debug;
    private D.IntFn? _next_draw_end;
    private D.WinProcFn? _next_wmain;
    private D.PtrFn? _next_op_num;

    public bool init(FhModule owner) {
        return Calls.Sg_MainDebug.hook(owner, h_main_debug)
            && Calls.Gu_DrawEnd.hook(owner, h_draw_end)
            && Calls.FFXUsbMouseRead2.hook(owner, h_mouse_read)
            && Calls.WMainProc.hook(owner, h_wmain)
            && Calls.SystemOnHost.hook(owner, h_system_on_host)
            && Calls.Sg_CaptureEnable.hook(owner, h_capture_enable)
            && Calls.op_num.hook(owner, h_op_num);
    }

    // ---------------------------------------------------------------- commands (game thread)

    public void show(bool on) => GameThread.post(on ? "open the debug menu" : "close the debug menu", () => {
        if (on) {
            ensure_init();
            if (_menu == 0) {
                _menu = Calls.Gu_WindowCreate.fnptr!(0x1b0, 0, 0x50, 0x52, 2, Mem.at(WMAINPROC));
            }
            Calls.Gu_Hide.fnptr!(0);
            open = true;
            GameThread.last_status = "debug menu open: the right edge of the screen";
        } else {
            if (_inited) Calls.Gu_Hide.fnptr!(1);
            open = false;
            frame = null;
        }
    });

    /// <summary>The battle windows that draw safely outside a battle (the others read battle-only pointers).</summary>
    public static readonly (int n, string name)[] BATTLE_WINDOWS = [
        (6, "BattleConfig"), (7, "CHR data 1"), (8, "CHR data 2"), (0, "Camera Info"), (2, "Reference"), (4, "Camera"),
    ];

    public void open_battle_window(int n) => GameThread.post("battle window", () => {
        ensure_init();
        Calls.Gu_Hide.fnptr!(0);
        open = true;
        Calls.Gui_MasakiInit.fnptr!(n);
    });

    private void ensure_init() {
        if (_inited) return;
        Calls.Gu_Init.fnptr!();
        // Without this, creating the menu runs op_et_bindeff_off_all and frees the bound battle effects.
        Calls.Sg_SetResource.fnptr!(0x12, 1);
        Calls.SgGu_LightTableInit.fnptr!();
        DateTime now = DateTime.Now;
        Mem.set_u32(CLOCK_MONTH, (uint)now.Month);
        Mem.set_u32(CLOCK_DAY, (uint)now.Day);
        Mem.set_u32(CLOCK_WEEKDAY, (uint)now.DayOfWeek);
        Mem.set_u32(CLOCK_HOUR, (uint)now.Hour);
        Mem.set_u32(CLOCK_MIN, (uint)now.Minute);
        Mem.set_u32(CLOCK_SEC, (uint)now.Second);
        _inited = true;
    }

    // ---------------------------------------------------------------- hooks

    /// <summary>After the game's debug pass: run the toolkit when the game does not (debug flag off).</summary>
    [UnmanagedCallConv(CallConvs = [ typeof(CallConvCdecl) ])]
    private void h_main_debug() {
        _next_main_debug ??= Calls.Sg_MainDebug.chain_from(h_main_debug).fnptr!;
        _next_main_debug();
        try {
            publish_op_lines();
            if (!open || !_inited || flag.on) return;
            Calls.Gu_VSyncMain.fnptr!();
            Calls.Gu_Main.fnptr!(0, *(nint*)Mem.at(PACKET_BUFFER));
        }
        catch (Exception) { open = false; }
    }

    [UnmanagedCallConv(CallConvs = [ typeof(CallConvCdecl) ])]
    private void h_draw_end(int chan) {
        _next_draw_end ??= Calls.Gu_DrawEnd.chain_from(h_draw_end).fnptr!;
        _next_draw_end(chan);
        try {
            if (!open) return;
            if (Mem.i32(GU_HIDDEN) != 0) { frame = null; return; }
            nint p = *(nint*)Mem.at(GU_PACKET);
            if (p == 0) return;
            int qwc = (int)(*(uint*)p & 0xffff);
            int bytes = (qwc + 1) * 16;
            if (bytes > 0x100000) return;
            GsPrim[] prims = [ .. GifParser.parse(new ReadOnlySpan<byte>((void*)p, bytes), Mem.i32(SCREEN_W), Mem.i32(SCREEN_H)) ];
            frame = new Frame(Environment.TickCount64, Mem.i32(SCREEN_W), Mem.i32(SCREEN_H), prims);
        }
        catch (Exception) { frame = null; }
    }

    /// <summary>The toolkit's USB mouse: the Windows cursor, absolute, while the mod captures it; still otherwise.</summary>
    [UnmanagedCallConv(CallConvs = [ typeof(CallConvCdecl) ])]
    private int h_mouse_read(int port, nint dst) {
        if (dst == 0) return 0;
        if (capture_mouse && open) {
            float fx = Mem.f32(MOUSE_FX), fy = Mem.f32(MOUSE_FY);
            *(int*)(dst + 0x08) = mouse_buttons;
            *(int*)(dst + 0x0c) = (int)(2 * mouse_x - fx);
            *(int*)(dst + 0x10) = (int)(3 * mouse_y - fy);
            *(int*)(dst + 0x14) = Interlocked.Exchange(ref _wheel, 0);
        } else {
            // The original never writes dst; a leftover delta would keep moving the cursor.
            *(int*)(dst + 0x08) = 0;
            *(int*)(dst + 0x0c) = 0;
            *(int*)(dst + 0x10) = 0;
            *(int*)(dst + 0x14) = 0;
        }
        return 0;
    }

    /// <summary>
    ///     Main-menu items that cannot work on PC or would do lasting harm
    ///     (msg 0x8006 = button click, 0x800a = right click, 0x8012 = selector result).
    /// </summary>
    private static readonly Dictionary<(int msg, int id), string> BLOCKED = new() {
        [(0x8006, 0x51)] = "CLR FLG would clear the story flags",
        [(0x8006, 0x2e)] = "EVENT Reload jumps to another event (use the mod's map jump)",
        [(0x8006, 0x2c)] = "EVENT Select reads a developer file and jumps (use the mod's map jump)",
        [(0x8012, 0x2c)] = "the event selector jumps (use the mod's map jump)",
        [(0x8006, 0x0f)] = "Capture needs the developers' host PC and would hang",
        [(0x800a, 0x0f)] = "Capture needs the developers' host PC and would hang",
        [(0x8006, 0x53)] = "CacheCLR frees the models in use",
        [(0x8006, 0x29)] = "MAP Reload reloads the map from developer paths",
        [(0x8006, 0x09)] = "the MAP list comes from the developers' host PC",
        [(0x8006, 0x1b)] = "the USER map list comes from the developers' host PC",
        [(0x8012, 0x16)] = "the MAP list comes from the developers' host PC",
        [(0x8012, 0x1b)] = "the USER map list comes from the developers' host PC",
        [(0x8006, 0x3d)] = "MemDumpD writes a dump file",
        [(0x8006, 0x15)] = "Movie needs the developers' host files",
        [(0x8006, 0x02)] = "CHR New lists the developers' host folders",
        [(0x8006, 0x03)] = "USER lists the developers' host folders",
        [(0x8012, 0x03)] = "USER lists the developers' host folders",
        [(0x8012, 0x0f)] = "Capture needs the developers' host PC and would hang",
        // Battle windows that read battle-only pointers and cannot be closed again safely.
        [(0x8006, 0x1f)] = "BattleInfo reads battle data that only exists in a battle",
        [(0x8006, 0x23)] = "BattleConsole reads battle data that only exists in a battle",
        [(0x8006, 0x20)] = "BattlePos reads battle data that only exists in a battle",
        [(0x8006, 0x50)] = "the Shake editor reads battle data that only exists in a battle",
    };

    [UnmanagedCallConv(CallConvs = [ typeof(CallConvCdecl) ])]
    private int h_wmain(nint win, int msg, int id) {
        if (BLOCKED.TryGetValue((msg, id), out string? why)) {
            blocked = $"blocked: {why}";
            return 0;
        }
        _next_wmain ??= Calls.WMainProc.chain_from(h_wmain).fnptr!;
        return _next_wmain(win, msg, id);
    }

    /// <summary>The PS2 host link: its request goes to an address outside the image and then spins. Always refuse.</summary>
    [UnmanagedCallConv(CallConvs = [ typeof(CallConvCdecl) ])]
    private int h_system_on_host(nint cmd, nint out_) => -1;

    /// <summary>Screen capture to the developers' host PC: never.</summary>
    [UnmanagedCallConv(CallConvs = [ typeof(CallConvCdecl) ])]
    private void h_capture_enable() { blocked = "blocked: screen capture needs the developers' host PC"; }

    [UnmanagedCallConv(CallConvs = [ typeof(CallConvCdecl) ])]
    private void h_op_num(nint text) {
        if (text != 0 && _op_count < _op_lines.Length && Mem.readable(text, 1)) _op_lines[_op_count++] = Mem.cstr(text, 80);
        _next_op_num ??= Calls.op_num.chain_from(h_op_num).fnptr!;
        _next_op_num(text);
    }

    private void publish_op_lines() {
        if (_op_count > 0) op_lines = _op_lines[.._op_count];
        _op_count = 0;
    }

    // ---------------------------------------------------------------- the debug font

    /// <summary>
    ///     Decodes the toolkit's 128x128 4-bit font (a TIM in FFX.exe's data at
    ///     0x00c4dfb8) to RGBA, 4x nearest-neighbour so it stays sharp when
    ///     scaled, and writes it as a TGA for Fahrenheit's texture loader.
    ///     Generated from the player's game at runtime; never shipped.
    /// </summary>
    public static void write_font_tga(string path) {
        const int SIZE = 128, SCALE = 4, OUT = SIZE * SCALE;
        nint tim = Mem.at(0x00c4dfb8);
        byte* pixels = (byte*)(tim + 0x40);
        // Index 0 transparent, 1 white, 2 black outline (the only indices the image uses).
        uint[] palette = [ 0x00000000, 0xffffffff, 0xff000000, 0x00000000 ];
        byte[] tga = new byte[18 + OUT * OUT * 4];
        tga[2] = 2;                                   // uncompressed true colour
        tga[12] = OUT & 0xff; tga[13] = OUT >> 8;
        tga[14] = OUT & 0xff; tga[15] = OUT >> 8;
        tga[16] = 32;
        tga[17] = 0x28;                               // 8 alpha bits, top-left origin
        for (int y = 0; y < OUT; y++) {
            for (int x = 0; x < OUT; x++) {
                int sx = x / SCALE, sy = y / SCALE;
                byte b = pixels[sy * (SIZE / 2) + sx / 2];
                int index = (b >> (4 * (sx & 1))) & 0xf;
                uint argb = index < palette.Length ? palette[index] : 0;
                int o = 18 + (y * OUT + x) * 4;
                tga[o + 0] = (byte)argb;               // B
                tga[o + 1] = (byte)(argb >> 8);        // G
                tga[o + 2] = (byte)(argb >> 16);       // R
                tga[o + 3] = (byte)(argb >> 24);       // A
            }
        }
        File.WriteAllBytes(path, tga);
    }
}
