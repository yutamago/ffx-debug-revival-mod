// SPDX-License-Identifier: MIT OR Apache-2.0

namespace Yuta.DebugRevival.Native;

/// <summary>
///     Raw access to FFX.exe's memory. Addresses are the absolute virtual
///     addresses of ffx-decomp's symbols/ffx.sym (image base 0x00400000);
///     <see cref="at"/> rebases them onto the loaded module.
/// </summary>
public static unsafe class Mem {
    private const nint IMAGE_BASE = 0x00400000;

    private static nint _base;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? name);

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

    /// <summary>The loaded FFX.exe's base, or the image base when it cannot be found.</summary>
    public static nint module_base {
        get {
            if (_base == 0) {
                nint b = GetModuleHandleW("FFX.exe");
                _base = b != 0 ? b : IMAGE_BASE;
            }
            return _base;
        }
    }

    /// <summary>Converts an ffx.sym address to the live address.</summary>
    public static nint at(uint va) => module_base + ((nint)va - IMAGE_BASE);

    /// <summary>The FhCallLocation offset of an ffx.sym address.</summary>
    public static nint off(uint va) => (nint)va - IMAGE_BASE;

    public static byte   u8 (uint va) => *(byte*)at(va);
    public static sbyte  i8 (uint va) => *(sbyte*)at(va);
    public static ushort u16(uint va) => *(ushort*)at(va);
    public static uint   u32(uint va) => *(uint*)at(va);
    public static int    i32(uint va) => *(int*)at(va);
    public static float  f32(uint va) => *(float*)at(va);

    public static void set_u8 (uint va, byte v)  => *(byte*)at(va) = v;
    public static void set_u32(uint va, uint v)  => *(uint*)at(va) = v;

    /// <summary>Whether <paramref name="length"/> bytes from <paramref name="p"/> are committed and readable.</summary>
    public static bool readable(nint p, nint length) {
        if (p == 0 || length < 0) return false;
        nint end = p + length;
        while (p < end) {
            if (VirtualQuery(p, out MEMORY_BASIC_INFORMATION mbi, (nuint)sizeof(MEMORY_BASIC_INFORMATION)) == 0) return false;
            if (mbi.State != MEM_COMMIT || (mbi.Protect & (PAGE_NOACCESS | PAGE_GUARD)) != 0 || mbi.Protect == 0) return false;
            nint region_end = mbi.BaseAddress + (nint)mbi.RegionSize;
            if (region_end <= p) return false;
            p = region_end;
        }
        return true;
    }

    /// <summary>The end of the committed, readable run of regions that holds <paramref name="p"/> (0 when unreadable).</summary>
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

    /// <summary>A NUL-terminated ASCII string at a live address, at most <paramref name="max"/> bytes.</summary>
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
