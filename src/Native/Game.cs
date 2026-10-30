// SPDX-License-Identifier: MIT OR Apache-2.0

namespace Yuta.DebugRevival.Native;

/// <summary>Delegates for the game functions the mod calls or hooks; docs/research/ has the evidence for each signature.</summary>
public static class D {
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void VoidFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void IntFn(int a);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int  RetIntFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate nint RetPtrFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate nint IntPtrToPtrFn(int a, nint b);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void Int3Fn(int a, int b, int c);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate uint ExecCtrlWorksFn(int from, int to);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate uint ScriptRunFn(nint worker, uint mode, uint callbacks);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int  BattleLabelExeFn(uint label, byte encount_type, byte transition);

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
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int  PtrIntToIntFn(nint a, int b);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int  WinProcFn(nint win, int msg, int id);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate nint WindowCreateFn(int x, int y, int w, int h, int flags, nint proc);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate ushort RetUShortFn();
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
}

/// <summary>Call handles. They are ref structs, so each property makes a fresh one.</summary>
public static class Calls {
    private static FhCallHandle<T> h<T>(uint va) where T : Delegate => new(new FhCallLocation("FFX.exe", Mem.off(va)));

    // ---- event-script VM ----
    /// <summary>cdecl (from, to): runs ctrl works [from, to) for one frame.</summary>
    public static FhCallHandle<D.ExecCtrlWorksFn> Atel_ExecCtrlWorks => h<D.ExecCtrlWorksFn>(0x008688f0);
    /// <summary>cdecl (worker, mode, callbacks): runs one worker; mode 1 = one instruction.</summary>
    public static FhCallHandle<D.ScriptRunFn> Script_Run => h<D.ScriptRunFn>(0x008641e0);

    // ---- debug prints (bare `ret` in FFX.exe) ----
    public static FhCallHandle<D.PrintfFn> dbgPrintf => h<D.PrintfFn>(0x0062f500);
    public static FhCallHandle<D.PrintfFn> scePrintf => h<D.PrintfFn>(0x0062fbf0);

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

    // ---- battle ----
    public static FhCallHandle<D.VoidFn> void_fn(uint va) => h<D.VoidFn>(va);
    public static FhCallHandle<D.IntFn>  int_fn(uint va)  => h<D.IntFn>(va);
    /// <summary>cdecl () -> picker's label (field id &lt;&lt; 16 | formation id).</summary>
    public static FhCallHandle<D.RetIntFn> MsDebugGetFieldNum => h<D.RetIntFn>(0x007c5f50);
    /// <summary>cdecl (sys_names, out) -> out: "&lt;field&gt;_NN" of the picker's battle.</summary>
    public static FhCallHandle<D.IntPtrToPtrFn> MsGetBattleName => h<D.IntPtrToPtrFn>(0x007c5fc0);
    /// <summary>cdecl (label, type, transition) -> -1: requests a battle (the debug combo's path).</summary>
    public static FhCallHandle<D.BattleLabelExeFn> MsBattleLabelExe => h<D.BattleLabelExeFn>(0x00781c90);
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
    /// <summary>cdecl () -> the current save-dictionary (place) id.</summary>
    public static FhCallHandle<D.RetUShortFn> AtelGetSaveDic => h<D.RetUShortFn>(0x0086c410);
    /// <summary>cdecl (id, english) -> the place name, game-encoded.</summary>
    public static FhCallHandle<D.UShortUIntToPtrFn> AtelGetSaveDicName => h<D.UShortUIntToPtrFn>(0x0086c430);
    /// <summary>cdecl (map, entrance, fade): warps when the event allows leaving the map.</summary>
    public static FhCallHandle<D.Int3Fn> AtelSetEventJump2 => h<D.Int3Fn>(0x0086ff40);
    /// <summary>cdecl (name, mode) -> fd, 0 when the file doesn't exist; maps host0: names to the PC files.</summary>
    public static FhCallHandle<D.PtrIntToIntFn> sceOpen => h<D.PtrIntToIntFn>(0x0062fa30);
    public static FhCallHandle<D.IntToIntFn> sceClose => h<D.IntToIntFn>(0x0062f610);
    /// <summary>cdecl (on): random encounters on/off (g_BattleEnable).</summary>
    public static FhCallHandle<D.IntFn> setBattleEnable => h<D.IntFn>(0x00782f70);
    /// <summary>cdecl () -> the field's "ignore collision" flag; hooked for noclip.</summary>
    public static FhCallHandle<D.RetIntFn> Sg_GetIgnoreID => h<D.RetIntFn>(0x0082d830);
    /// <summary>thiscall (FreeCamera*): toggles the photo-mode pause.</summary>
    public static FhCallHandle<D.ThisFn> FreeCamera_setFreePause => h<D.ThisFn>(0x00680320);
}

/// <summary>Globals the mod reads or writes (ffx.sym addresses).</summary>
public static class G {
    // event-script VM
    public const uint AtelCtrlWorks   = 0x01325ba0; // 7 x 0x238
    public const int  CtrlWorkSize    = 0x238;
    public const int  CtrlWorkCount   = 7;
    public const uint CurrentCtrlIdx  = 0x01326b30; // u32, the ctrl work Atel_ExecCtrlWorks is running
    public const uint ScriptContext   = 0x01326b28; // AtelCtrlWork*, the current ctrl work (g_ScriptContext)

    // tweak panels
    public const uint DebugMenuManager = 0x00cccab0; // ptr; +4 current mode (10 = none)

    // battle
    public const uint BtlStatus   = 0x0112a8e0; // i8, 0 = no battle
    public const uint BtlExe      = 0x0112a8e2; // u8, 2 = label battle requested
    public const uint BtlChrTop   = 0x011334d4; // ptr to 0x1f actors of 0xf90
    public const int  ActorSize   = 0xf90;
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

    // free camera gates
    public const uint AsyncControllerOn = 0x0133c930; // u8
    public const uint PauseRequest      = 0x01340850; // int
    public const uint PauseEntered      = 0x0134084c; // int
}
