// SPDX-License-Identifier: MIT

namespace Yuta.DebugRevival.Native;

/// <summary>
///     FFX.exe's memory, addressed by ffx-decomp's symbols/ffx.sym VAs (image base 0x00400000). The accessors are thin
///     wrappers over Fahrenheit's <see cref="FhUtil.ptr_at{T}"/>, which takes the offset from the module base instead.
/// </summary>
public static unsafe class Mem {
    private const nint IMAGE_BASE = 0x00400000;

    [DllImport("kernel32.dll")]
    private static extern nuint VirtualQuery(nint address, out MEMORY_BASIC_INFORMATION info, nuint length);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORY_BASIC_INFORMATION {
        public nint  BaseAddress;
        public nint  AllocationBase;
        public uint  AllocationProtect;
        public nuint RegionSize;
        public uint  State;
        public uint  Protect;
        public uint  Type;
    }

    private const uint MEM_COMMIT = 0x1000;
    private const uint PAGE_NOACCESS = 0x01;
    private const uint PAGE_GUARD = 0x100;

    /// <summary>The module-base offset of an ffx.sym address, as FhUtil and FhCallLocation take it.</summary>
    public static nint off(uint va) => (nint)va - IMAGE_BASE;

    public static nint at(uint va) => (nint)FhUtil.ptr_at<byte>(off(va));

    public static byte   u8 (uint va) => FhUtil.get_at<byte> (off(va));
    public static sbyte  i8 (uint va) => FhUtil.get_at<sbyte>(off(va));
    public static uint   u32(uint va) => FhUtil.get_at<uint> (off(va));
    public static int    i32(uint va) => FhUtil.get_at<int>  (off(va));
    public static float  f32(uint va) => FhUtil.get_at<float>(off(va));

    public static void set_u8 (uint va, byte v) => FhUtil.set_at(off(va), v);
    public static void set_u32(uint va, uint v) => FhUtil.set_at(off(va), v);

    public static bool readable(nint p, nint length) => p != 0 && length > 0 && readable_end(p, length) == p + length;

    /// <summary>The end of the committed, readable run of regions from <paramref name="p"/>, at most <paramref name="limit"/> bytes on; 0 when unreadable.</summary>
    public static nint readable_end(nint p, nint limit) {
        nint cur = p;
        while (cur < p + limit) {
            if (VirtualQuery(cur, out MEMORY_BASIC_INFORMATION mbi, (nuint)sizeof(MEMORY_BASIC_INFORMATION)) == 0) break;
            if (mbi.State != MEM_COMMIT || (mbi.Protect & (PAGE_NOACCESS | PAGE_GUARD)) != 0 || mbi.Protect == 0) break;
            nint region_end = mbi.BaseAddress + (nint)mbi.RegionSize;
            if (region_end <= cur) break;
            cur = region_end;
        }
        return cur > p ? Math.Min(cur, p + limit) : 0;
    }

    /// <summary>A NUL-terminated string at a live address; non-ASCII bytes read as '?'.</summary>
    public static string cstr(nint p, int max = 256) {
        if (p == 0) return "";
        StringBuilder sb = new();
        for (int i = 0; i < max; i++) {
            byte b = *(byte*)(p + i);
            if (b == 0) break;
            sb.Append(b is >= 0x20 and < 0x7f ? (char)b : '?');
        }
        return sb.ToString();
    }
}
