// SPDX-License-Identifier: MIT

namespace Yuta.DebugRevival.Native;

/// <summary>Delegates for the game functions the mod calls or hooks; docs/research/ has the evidence for each signature.</summary>
public static class D {
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void VoidFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void IntFn(int a);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int  RetIntFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate nint RetPtrFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate nint IntPtrToPtrFn(int a, nint b);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate uint ExecCtrlWorksFn(int from, int to);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate uint ScriptRunFn(nint worker, uint mode, uint callbacks);

    /// <summary>printf-style, 16 argument slots (FFX's debug prints are variadic cdecl).</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public unsafe delegate void PrintfFn(byte* fmt,
        nint va0, nint va1, nint va2,  nint va3,  nint va4,  nint va5,  nint va6,  nint va7,
        nint va8, nint va9, nint va10, nint va11, nint va12, nint va13, nint va14, nint va15);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void IntIntFn(int a, int b);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void IntPtrFn(int a, nint b);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void PtrFn(nint a);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int  IntToIntFn(int a);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int  IntPtrToIntFn(int a, nint b);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int  PtrPtrToIntFn(nint a, nint b);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int  PtrToIntFn(nint a);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void PtrPtrIntFn(nint a, nint b, int c);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int  IntPtrUIntToIntFn(int a, nint b, uint c);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int  WinProcFn(nint win, int msg, int id);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate nint WindowCreateFn(int x, int y, int w, int h, int flags, nint proc);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate nint UShortUIntToPtrFn(ushort a, uint b);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate uint Rect8Fn(uint a1, uint a2, uint a3, uint a4, uint a5, uint a6, uint a7, uint a8);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate uint Rect7Fn(uint a1, uint a2, uint a3, uint a4, uint a5, uint a6, uint a7);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate uint RectBuildFn(nint render, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6, uint a7, uint unlimited);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate nint UIntToPtrFn(uint a);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate uint Int4Fn(int a, int b, int c, int d);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)] public delegate void ThisFn(nint self);
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)] public delegate void ThisUIntUIntFn(nint self, uint a, uint b);
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)] public delegate void ThisIntFn(nint self, int a);
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)] public delegate void ThisPtrFn(nint self, nint a);
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)] public delegate int  ThisIntPtrToIntFn(nint self, int a, nint b);
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)] public delegate nint ThisPtrToPtrFn(nint self, nint a);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate nint PtrPtrToPtrFn(nint a, nint b);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int  PtrIntToIntFn(nint a, int b);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate nint IntPtrPtrToPtrFn(int a, nint b, nint c);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate nint FontWidthFn(nint text, int kind, nint width, int table);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate nint TextureNameFn(int id, int nibbles, int index, int high, nint size);
}

/// <summary>
///     Call handles. They are ref structs, so each property makes a fresh one. Where Fahrenheit's FhCall has the
///     function with the same signature the mod calls that directly; the ones here are missing there or typed differently.
/// </summary>
public static class Calls {
    private static FhCallHandle<T> h<T>(uint va) where T : Delegate => new(new FhCallLocation("FFX.exe", Mem.off(va)));

    // ---- event-script VM ----
    /// <summary>cdecl (from, to): runs ctrl works [from, to) for one frame.</summary>
    public static FhCallHandle<D.ExecCtrlWorksFn> Atel_ExecCtrlWorks => h<D.ExecCtrlWorksFn>(0x008688f0);
    /// <summary>cdecl (worker, mode, callbacks): runs one worker; mode 1 = one instruction.</summary>
    public static FhCallHandle<D.ScriptRunFn> Script_Run => h<D.ScriptRunFn>(0x008641e0);

    // ---- debug prints ----
    /// <summary>The developers' printf-style functions, each a bare <c>ret</c> in FFX.exe: name and VA.</summary>
    public static readonly (string name, uint va)[] DEBUG_PRINTS = [
        ("dbgPrintf", 0x0062f500), ("scePrintf", 0x0062fbf0), ("AtelPs2DebugString", 0x00873c70),
        ("AtelPs2DebugString2", 0x00873c80), ("rcPrint", 0x00927550),
    ];
    public static FhCallHandle<D.PrintfFn> printf_fn(uint va) => h<D.PrintfFn>(va);
    /// <summary>thiscall (this unused, level, message) ret 8: the engine's log output, which every PhyrePrintf reaches.</summary>
    public static FhCallHandle<D.ThisIntPtrToIntFn> Phyre_PTTYCallback_print => h<D.ThisIntPtrToIntFn>(0x004354f0);

    // ---- the PC port's tweak panels ----
    /// <summary>thiscall (DebugMenuManager*, mode 0..10): shows one panel, 10 hides all.</summary>
    public static FhCallHandle<D.ThisIntFn> DebugMenuManager_setMode => h<D.ThisIntFn>(0x006b4d10);
    /// <summary>cdecl () -> PApplication*.</summary>
    public static FhCallHandle<D.RetPtrFn> PApplication_GetApplication => h<D.RetPtrFn>(0x00623b90);
    /// <summary>thiscall (panel, PApplication*): one panel's input handler (no caller in release).</summary>
    public static FhCallHandle<D.ThisPtrFn> panel_handle_input(uint va) => h<D.ThisPtrFn>(va);
    public const uint RESET_ALL_VA = 0x006f1420;
    public const uint VIEWER_SAVE_VA = 0x008b55e0;
    /// <summary>cdecl (): SteamUserStats()->ResetAllStats(true). Guarded: never runs.</summary>
    public static FhCallHandle<D.VoidFn> StatsAndAchievements_ResetAll => h<D.VoidFn>(RESET_ALL_VA);
    /// <summary>cdecl (n): loads developer save n from savesforviewer/ over the game and warps.</summary>
    public static FhCallHandle<D.IntFn> TkDebugLoadViewerSave => h<D.IntFn>(VIEWER_SAVE_VA);
    public const uint MEM_SET_CURRENT_ADDR_VA = 0x0083c4e0;
    /// <summary>cdecl (p): Sg_MemSetRtAddr([0x00c8f6f8]), then tags bits 4..25 of [p - 4]; called after the debug file reads.</summary>
    /// <summary>cdecl () -> 0 jp, 1 us, 2 fr, 3 sp, 4 de, 5 it, 9 kr, 10 ch.</summary>
    public static FhCallHandle<D.RetIntFn> TOGetFFXLang => h<D.RetIntFn>(0x006410e0);
    public static FhCallHandle<D.PtrFn> Sg_MemSetCurrentAddr => h<D.PtrFn>(MEM_SET_CURRENT_ADDR_VA);
    /// <summary>cdecl (a): 0x013019b0 = ((a - 8) >> 2) &amp; 0x7fffff.</summary>
    public static FhCallHandle<D.IntFn> Sg_MemSetRtAddr => h<D.IntFn>(0x0083c560);
    public const uint SG_MEM_RT_BASE = 0x00c8f6f8;

    // ---- battle ----
    public static FhCallHandle<D.VoidFn> void_fn(uint va) => h<D.VoidFn>(va);
    public static FhCallHandle<D.IntFn>  int_fn(uint va)  => h<D.IntFn>(va);
    /// <summary>cdecl () -> picker's label (field id &lt;&lt; 16 | formation id).</summary>
    public static FhCallHandle<D.RetIntFn> MsDebugGetFieldNum => h<D.RetIntFn>(0x007c5f50);
    /// <summary>cdecl (sys_names, out) -> out: "&lt;field&gt;_NN" of the picker's battle.</summary>
    public static FhCallHandle<D.IntPtrToPtrFn> MsGetBattleName => h<D.IntPtrToPtrFn>(0x007c5fc0);
    /// <summary>cdecl (): writes the battle debug status line into the TODaps buffer.</summary>
    public static FhCallHandle<D.VoidFn> MsBattleProcess_DebugStatus => h<D.VoidFn>(0x00792f20);
    /// <summary>cdecl (): clears the TODaps buffer (called from the message-window draws).</summary>
    public static FhCallHandle<D.VoidFn> TODapsDraw => h<D.VoidFn>(0x0089dc10);
    /// <summary>cdecl (): draws the TODaps buffer through the PC font path (no caller in release).</summary>
    public static FhCallHandle<D.VoidFn> TODapsMakePkt => h<D.VoidFn>(0x0089dcb0);

    // ---- the PS2 debug GUI (Gu toolkit); docs/research/gs.md ----
    public static FhCallHandle<D.VoidFn>         Gu_Init             => h<D.VoidFn>(0x008497a0);
    public static FhCallHandle<D.IntPtrFn>       Gu_Main             => h<D.IntPtrFn>(0x00849850);
    public static FhCallHandle<D.IntFn>          Gu_DrawEnd          => h<D.IntFn>(0x008479d0);
    public static FhCallHandle<D.VoidFn>         Gu_VSyncMain        => h<D.VoidFn>(0x0084a460);
    public static FhCallHandle<D.WindowCreateFn> Gu_WindowCreate     => h<D.WindowCreateFn>(0x0084a920);
    public static FhCallHandle<D.IntToIntFn>     Gu_Hide             => h<D.IntToIntFn>(0x00849780);
    public static FhCallHandle<D.WinProcFn>      WMainProc           => h<D.WinProcFn>(0x0084f550);
    public static FhCallHandle<D.IntIntFn>       Sg_SetResource      => h<D.IntIntFn>(0x0083fe10);
    public static FhCallHandle<D.VoidFn>         SgGu_LightTableInit => h<D.VoidFn>(0x0084ea00);
    public static FhCallHandle<D.IntFn>          Gui_MasakiInit      => h<D.IntFn>(0x007c86e0);
    public static FhCallHandle<D.VoidFn>         Sg_MainDebug        => h<D.VoidFn>(0x008207f0);
    public static FhCallHandle<D.IntPtrToIntFn>  FFXUsbMouseRead2    => h<D.IntPtrToIntFn>(0x00855bc0);
    public static FhCallHandle<D.PtrPtrToIntFn>  SystemOnHost        => h<D.PtrPtrToIntFn>(0x0084ede0);
    public static FhCallHandle<D.VoidFn>         Sg_CaptureEnable    => h<D.VoidFn>(0x0083a470);
    public static FhCallHandle<D.PtrFn>          op_num              => h<D.PtrFn>(0x007e63a0);

    // ---- the debug flag ----
    public static FhCallHandle<D.VoidFn> Sg_DbInitAll  => h<D.VoidFn>(0x00841440);
    public static FhCallHandle<D.VoidFn> initEventList => h<D.VoidFn>(0x00907f50);

    // ---- the field map's part selection ----
    /// <summary>cdecl (a1..a7, unlimited-if-debug) -> yiRectToOMDBuild(&amp;g_YiRender, a1..a7, debug ? a8 : 0).</summary>
    public static FhCallHandle<D.Rect8Fn> yiRectToOMD => h<D.Rect8Fn>(0x00909d30);
    /// <summary>cdecl (a1..a7) -> yiRectToOMDBuild(&amp;g_YiRender, a1..a7, debug ? 1 : 0).</summary>
    public static FhCallHandle<D.Rect7Fn> yiRectToOMD_old => h<D.Rect7Fn>(0x00909dd0);
    public static FhCallHandle<D.RectBuildFn> yiRectToOMDBuild => h<D.RectBuildFn>(0x00912270);
    public const uint g_YiRender = 0x01876e00;

    /// <summary>cdecl (key) -> block: a character's data block (loaded on first use).</summary>
    public static FhCallHandle<D.UIntToPtrFn> Ch_BlkGetHandle => h<D.UIntToPtrFn>(0x00825a40);
    /// <summary>cdecl (mode, field, group, formation): loads and starts a battle.</summary>
    public static FhCallHandle<D.Int4Fn> MsBattleExe => h<D.Int4Fn>(0x00781020);

    // ---- panel text ----
    /// <summary>thiscall (TextUtil*, w, h): rebuilds the panel text's matrix from its scale/position fields.</summary>
    public static FhCallHandle<D.ThisUIntUIntFn> TextUtil_resize => h<D.ThisUIntUIntFn>(0x006f6670);

    // ---- field ----
    // Fahrenheit's handles, kept here until FieldTools calls them directly.
    /// <summary>cdecl () -> the current save-dictionary (place) id.</summary>
    public static FhCallHandle<FhCall.d_AtelGetSaveDic> AtelGetSaveDic => FhCall.AtelGetSaveDic;
    /// <summary>cdecl (id, english) -> the place name, game-encoded.</summary>
    public static FhCallHandle<D.UShortUIntToPtrFn> AtelGetSaveDicName => h<D.UShortUIntToPtrFn>(0x0086c430);
    /// <summary>cdecl (map, entrance, fade): warps when the event allows leaving the map.</summary>
    public static FhCallHandle<Fahrenheit.FFX.FhCall.d_AtelSetEventJump2> AtelSetEventJump2 => Fahrenheit.FFX.FhCall.AtelSetEventJump2;
    // ---- the PC file layer, which reads FFX_Data.vbf ----
    /// <summary>cdecl (n, dst, size) -> 0 when loaded: fopen of savesforviewer/n, which only exists inside the VBF.</summary>
    public static FhCallHandle<D.IntPtrUIntToIntFn> debugLoadForViewer => h<D.IntPtrUIntToIntFn>(0x00649040);
    public static FhCallHandle<D.PtrPtrIntFn> fiosUnifyFilename => h<D.PtrPtrIntFn>(0x00679820);
    public static FhCallHandle<D.PtrToIntFn> fiosIsFileExist => h<D.PtrToIntFn>(0x006796f0);
    /// <summary>cdecl (on): random encounters on/off (g_BattleEnable).</summary>
    public static FhCallHandle<D.IntFn> setBattleEnable => h<D.IntFn>(0x00782f70);
    /// <summary>cdecl () -> the field's "ignore collision" flag; hooked for noclip.</summary>
    public static FhCallHandle<D.RetIntFn> Sg_GetIgnoreID => h<D.RetIntFn>(0x0082d830);
    /// <summary>thiscall (FreeCamera*): toggles the photo-mode pause.</summary>
    public static FhCallHandle<D.ThisFn> FreeCamera_setFreePause => h<D.ThisFn>(0x00680320);

    // ---- text and fonts; docs/research/font.md ----
    /// <summary>cdecl (map): loads the map's .ebp, its text in the game's language and its .ftc, and sets up its scripts.</summary>
    public static FhCallHandle<D.IntFn> AtelEventSetUp => h<D.IntFn>(0x00872ef0);
    /// <summary>cdecl (file) -> 0 when loaded: copies an .ftc's widths into font dictionary [file+8].</summary>
    public static FhCallHandle<D.PtrToIntFn> TOFTCTransfer => h<D.PtrToIntFn>(0x008ac130);
    /// <summary>cdecl (text, out) -> the text after the glyph: fills the 0x24-byte glyph record.</summary>
    public static FhCallHandle<D.PtrPtrToPtrFn> TOGetFontInfo => h<D.PtrPtrToPtrFn>(0x008b7240);
    /// <summary>cdecl (text, kind, &amp;width, dictionary) -> the text after the glyph.</summary>
    public static FhCallHandle<D.FontWidthFn> TOGetFontWidth => h<D.FontWidthFn>(0x008b9330);
    /// <summary>cdecl (slot i16, nibbles u8, index i16, high u16, &amp;size) -> the font atlas's cluster path.</summary>
    public static FhCallHandle<D.TextureNameFn> TOGetTextureName => h<D.TextureNameFn>(0x008acf50);
    /// <summary>cdecl (slot, &amp;w, &amp;h) -> &amp;h: the size of a font page or menu image.</summary>
    public static FhCallHandle<D.IntPtrPtrToPtrFn> TOGetImageWH => h<D.IntPtrPtrToPtrFn>(0x008ac400);
    /// <summary>cdecl () -> [0x00ccca4c].</summary>
    public static FhCallHandle<D.RetPtrFn> ClusterManager_GetInstance => h<D.RetPtrFn>(0x0069a1a0);
    /// <summary>thiscall (ClusterManager*, path) ret 4 -> the cluster, 0 when the load failed; counts references.</summary>
    public static FhCallHandle<D.ThisPtrToPtrFn> ClusterManager_loadPCluster => h<D.ThisPtrToPtrFn>(0x0069b940);
    /// <summary>cdecl (clusters, count).</summary>
    public static FhCallHandle<D.PtrIntToIntFn> PApplication_FixupClusters => h<D.PtrIntToIntFn>(0x00623580);
}

/// <summary>Globals the mod reads or writes (ffx.sym addresses).</summary>
public static class G {
    // event-script VM; the ctrl works themselves are Fahrenheit's Globals.Atel.controllers
    public const int  CtrlWorkCount   = 7;
    public const uint CurrentCtrlIdx  = 0x01326b30; // u32, the ctrl work Atel_ExecCtrlWorks is running
    public const uint ScriptContext   = 0x01326b28; // AtelCtrlWork*, the current ctrl work (g_ScriptContext; Fahrenheit's Globals.Atel.current_controller)

    // tweak panels
    public const uint DebugMenuManager = 0x00cccab0; // ptr; +4 current mode (10 = none)

    // battle
    public const uint PickerField     = 0x00c44248; // i32
    public const uint PickerEncounter = 0x01139e44; // i32

    // field
    public const uint BattleEnable  = 0x00c421cc; // u32
    public const uint ExecFrames    = 0x00c496d4; // i8, game frames per frame (the debug pad's speed)
    public const uint FreeCamera    = 0x00ccc830; // ptr to the 0x340-byte FreeCamera
    public const uint InputManager  = 0x00ccb170; // Virtuos input manager: +0x9c pair count, +0xa4 pairs
    public const uint ViewerSaveLast = 0x00c5a038; // the last viewer save loaded (TkDebugLoadViewerSave skips a repeat)

    // debug flag
    public const uint FFXIsDebug     = 0x0133c910; // u8 g_FFXIsDebug
    public const uint L2ExecFlag     = 0x013007b1; // u8, the debug pad's "L2 held" (only rewritten under the flag)
    public const uint EventTable     = 0x025d5888; // ptr to 16-byte entries {name[12], id} (initEventList)
    public const uint EventCount     = 0x01934ec8; // int

    // event effects (MsEventEffectFree, MagicFile_Load/Stop/Update)
    public const uint EventEffectState   = 0x01133364; // i8, 0 = no event effect
    public const uint MagicLoaded        = 0x00c64cac; // i32 handle, -1 = none
    public const uint MagicPendingUnload = 0x00c64cb0; // i32 handle, -1 = none

    // free camera gates
    public const uint AsyncControllerOn = 0x0133c930; // u8
    public const uint PauseRequest      = 0x01340850; // int
    public const uint PauseEntered      = 0x0134084c; // int

    // text
    public const uint EventEbp      = 0x023cbd70; // ptr, the loaded .ebp; [+8] its own message table, [+0x18] its size
    public const uint EventMessages = 0x01327128; // ptr, the event's text in the game's language, 0 when it has none
    public const uint FontDics      = 0x01841da0; // 6 x 16 bytes: +0 image, +4 width table, +0xc i16 w, +0xe i16 h
}
