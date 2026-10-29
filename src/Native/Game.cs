// SPDX-License-Identifier: MIT OR Apache-2.0

namespace Yuta.DebugRevival.Native;

/// <summary>
///     Delegates for the game functions the mod calls or hooks. Every signature
///     was checked in FFX.exe (ret N, stack and ECX use); the facts sheets in
///     docs/research/ gives the evidence.
/// </summary>
public static class D {
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void VoidFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void IntFn(int a);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int  RetIntFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate nint RetPtrFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate nint IntToPtrFn(int a);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate nint IntPtrToPtrFn(int a, nint b);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void Int3Fn(int a, int b, int c);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate uint ExecCtrlWorksFn(int from, int to);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate uint ScriptRunFn(nint worker, uint mode, uint callbacks);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int  BattleLabelExeFn(uint label, byte encount_type, byte transition);

    /// <summary>printf-style, 16 argument slots (FFX's debug prints are variadic cdecl).</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public unsafe delegate void PrintfFn(byte* fmt,
        nint va0, nint va1, nint va2,  nint va3,  nint va4,  nint va5,  nint va6,  nint va7,
        nint va8, nint va9, nint va10, nint va11, nint va12, nint va13, nint va14, nint va15);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)] public delegate void ThisFn(nint self);
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
    /// <summary>cdecl (): SteamUserStats()->ResetAllStats(true). Guarded: never runs.</summary>
    public static FhCallHandle<D.VoidFn> StatsAndAchievements_ResetAll => h<D.VoidFn>(0x006f1420);
    /// <summary>cdecl (n): loads developer save n from savesforviewer/ over the game and warps.</summary>
    public static FhCallHandle<D.IntFn> TkDebugLoadViewerSave => h<D.IntFn>(0x008b55e0);

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

    // ---- field ----
    /// <summary>cdecl (map, entrance, fade): warps when the event allows leaving the map.</summary>
    public static FhCallHandle<D.Int3Fn> AtelSetEventJump2 => h<D.Int3Fn>(0x0086ff40);
    /// <summary>cdecl (event) -> name; reads the event index file, so call it sparingly.</summary>
    public static FhCallHandle<D.IntToPtrFn> AtelGetEventName => h<D.IntToPtrFn>(0x00879760);
    /// <summary>cdecl (on): random encounters on/off (g_BattleEnable).</summary>
    public static FhCallHandle<D.IntFn> setBattleEnable => h<D.IntFn>(0x00782f70);
    /// <summary>cdecl () -> the field's "ignore collision" flag; hooked for noclip.</summary>
    public static FhCallHandle<D.RetIntFn> Sg_GetIgnoreID => h<D.RetIntFn>(0x0082d830);
    /// <summary>thiscall (FreeCamera*, type): 0 off, 1 fly.</summary>
    public static FhCallHandle<D.ThisIntFn> FreeCamera_setType => h<D.ThisIntFn>(0x00680410);
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
    public const uint PlayerNeverDie = 0x0113338c; // u32
    public const uint PickerField     = 0x00c44248; // i32
    public const uint PickerEncounter = 0x01139e44; // i32
    public const uint PickerGroup     = 0x01139e48; // i32 (derived)
    public const uint PickerSlot      = 0x01139e4c; // i32 (derived)

    // field
    public const uint BattleEnable  = 0x00c421cc; // u32
    public const uint ExecFrames    = 0x00c496d4; // i8, game frames per frame (the debug pad's speed)
    public const uint FreeCamera    = 0x00ccc830; // ptr to the 0x340-byte FreeCamera
    public const uint InputManager  = 0x00ccb170; // Virtuos input manager: +0x9c pair count, +0xa4 pairs
    public const uint ViewerSaveLast = 0x00c5a038; // the last viewer save loaded (TkDebugLoadViewerSave skips a repeat)
}
