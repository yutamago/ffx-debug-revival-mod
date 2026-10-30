// SPDX-License-Identifier: MIT OR Apache-2.0

// Shared with tools/GsCheck, which has no global usings of its own.
using System.Buffers.Binary;

namespace Yuta.DebugRevival.Gs;

/// <summary>A vertex in PS2 screen pixels (0..W x 0..H), with its colour and texel coordinates.</summary>
public readonly record struct GsVertex(float X, float Y, byte R, byte G, byte B, byte A, float U, float V);

public enum GsPrimKind : byte { Line, Triangle, Sprite }

/// <summary>One primitive as the GS would rasterise it.</summary>
public sealed record GsPrim(
    GsPrimKind Kind,
    GsVertex[] V,
    bool Gouraud,
    bool Textured,
    bool Decal,      // texture as-is (the mouse cursor); otherwise modulated by the vertex colour
    bool Blend,      // alpha blending on (ABE)
    byte FixAlpha,   // ALPHA FIX when the blend is "darken by FIX/128" (the window shadow), else 0
    int ScissorX0, int ScissorY0, int ScissorX1, int ScissorY1);

/// <summary>
///     Parses the GIF stream of a PS2 debug-GUI packet: a DMA "end" tag
///     whose low 16 bits are the quadword count, then GIF tags with their
///     data (PACKED, REGLIST or IMAGE). Only what FFX's debug drawers use is
///     interpreted: PRIM, RGBAQ, UV, XYZF2/XYZ2 (and the no-kick XYZF3/XYZ3),
///     TEX0, SCISSOR and ALPHA. EOP does not end the stream (FFX's text tags
///     set it mid-packet); the quadword count does. Facts:
///     docs/research/gs.md.
/// </summary>
public static class GifParser {
    private sealed class State {
        public int  prim;
        public byte r, g, b, a = 0x80;
        public float u, v;
        public bool decal;
        public byte fix_alpha;
        public bool darken;
        public int sx0, sy0, sx1 = 0x1fd, sy1 = 0x19e;
        public readonly List<GsVertex> queue = [];
    }

    /// <summary>Parses <paramref name="buf"/> (starting at the DMA tag) into primitives.</summary>
    /// <param name="w">GS screen width (FFX: 512).</param>
    /// <param name="h">GS screen height (FFX: 416).</param>
    public static List<GsPrim> parse(ReadOnlySpan<byte> buf, int w, int h, int max_prims = 100_000) {
        List<GsPrim> prims = [];
        if (buf.Length < 16) return prims;
        int qwc = (int)(u32(buf, 0, 0) & 0xffff);
        int end = Math.Min(1 + qwc, buf.Length / 16);
        float ox = 2048 - w / 2f, oy = 2048 - h / 2f;
        State st = new();

        void vertex(uint x, uint y, bool kick) {
            st.queue.Add(new GsVertex(x / 16f - ox, y / 16f - oy, st.r, st.g, st.b, st.a, st.u, st.v));
            int type = st.prim & 7;
            int need = type switch { 0 => 1, 1 or 2 => 2, 3 or 4 or 5 => 3, 6 => 2, _ => int.MaxValue };
            if (st.queue.Count < need) return;
            if (kick && prims.Count < max_prims) emit(type, st.queue.GetRange(st.queue.Count - need, need).ToArray());
            switch (type) {
                case 0: case 1: case 3: case 6: st.queue.Clear(); break;
                case 2: st.queue.RemoveRange(0, st.queue.Count - 1); break;
                case 4: st.queue.RemoveRange(0, st.queue.Count - 2); break;
                case 5: { GsVertex first = st.queue[0], last = st.queue[^1]; st.queue.Clear(); st.queue.Add(first); st.queue.Add(last); break; }
            }
        }

        void emit(int type, GsVertex[] vs) {
            GsPrimKind kind = type switch { 1 or 2 => GsPrimKind.Line, 6 => GsPrimKind.Sprite, _ => GsPrimKind.Triangle };
            if (type == 0) return; // points: nothing in FFX's debug GUI
            bool iip = (st.prim & 0x08) != 0, tme = (st.prim & 0x10) != 0, abe = (st.prim & 0x40) != 0;
            prims.Add(new GsPrim(kind, vs, iip, tme, st.decal, abe, abe && st.darken ? st.fix_alpha : (byte)0,
                                 st.sx0, st.sy0, st.sx1, st.sy1));
        }

        void areg(uint addr, uint lo, uint hi) {
            switch (addr) {
                case 0x00: st.prim = (int)(lo & 0x7ff); st.queue.Clear(); break;
                case 0x01: st.r = (byte)lo; st.g = (byte)(lo >> 8); st.b = (byte)(lo >> 16); st.a = (byte)(lo >> 24); break;
                case 0x03: st.u = (lo & 0x3fff) / 16f; st.v = ((lo >> 16) & 0x3fff) / 16f; break;
                case 0x04: case 0x05: vertex(lo & 0xffff, (lo >> 16) & 0xffff, true); break;
                case 0x0c: case 0x0d: vertex(lo & 0xffff, (lo >> 16) & 0xffff, false); break;
                // TEX0 in these packets is always the debug font (PC bitfield layout); word 1 bit 5 = decal.
                case 0x06: st.decal = (hi & 0x20) != 0; break;
                case 0x40:
                    st.sx0 = (int)(lo & 0x7ff); st.sx1 = (int)((lo >> 16) & 0x7ff);
                    st.sy0 = (int)(hi & 0x7ff); st.sy1 = (int)((hi >> 16) & 0x7ff);
                    break;
                case 0x42:
                    // (Cd - 0) * FIX: A=1 (Cd), B=2 (0), C=2 (FIX), D=2 (0) = 0xa9 -> darken.
                    st.darken = (lo & 0xff) == 0xa9;
                    st.fix_alpha = (byte)hi;
                    break;
            }
        }

        int i = 1;
        while (i < end) {
            uint g0 = u32(buf, i, 0), g1 = u32(buf, i, 1), g2 = u32(buf, i, 2), g3 = u32(buf, i, 3);
            i++;
            int nloop = (int)(g0 & 0x7fff);
            bool pre  = ((g1 >> 14) & 1) != 0;
            int flg   = (int)((g1 >> 26) & 3);
            int nreg  = (int)(g1 >> 28);
            if (nreg == 0) nreg = 16;
            ulong regs = g2 | ((ulong)g3 << 32);
            if (pre) { st.prim = (int)((g1 >> 15) & 0x7ff); st.queue.Clear(); }

            if (flg == 0) {
                for (int l = 0; l < nloop && i < end; l++) {
                    for (int k = 0; k < nreg && i < end; k++) {
                        uint r = (uint)((regs >> (4 * k)) & 0xf);
                        uint d0 = u32(buf, i, 0), d1 = u32(buf, i, 1), d2 = u32(buf, i, 2), d3 = u32(buf, i, 3);
                        i++;
                        switch (r) {
                            case 0xe: areg(d2 & 0xff, d0, d1); break;
                            case 0x0: st.prim = (int)(d0 & 0x7ff); st.queue.Clear(); break;
                            case 0x1: st.r = (byte)d0; st.g = (byte)d1; st.b = (byte)d2; st.a = (byte)d3; break;
                            case 0x3: st.u = (d0 & 0x3fff) / 16f; st.v = (d1 & 0x3fff) / 16f; break;
                            case 0x4: case 0x5: vertex(d0 & 0xffff, d1 & 0xffff, ((d3 >> 15) & 1) == 0); break;
                            case 0xc: case 0xd: vertex(d0 & 0xffff, d1 & 0xffff, false); break;
                        }
                    }
                }
            }
            else if (flg == 1) {
                int n64 = nloop * nreg;
                int words = (n64 + 1) / 2;
                for (int k = 0; k < n64; k++) {
                    int q = i + k / 2;
                    if (q >= end) break;
                    uint lo = u32(buf, q, (k & 1) * 2), hi = u32(buf, q, (k & 1) * 2 + 1);
                    uint r = (uint)((regs >> (4 * (k % nreg))) & 0xf);
                    if (r is 0xe or 0xf) continue;
                    areg(r, lo, hi);
                }
                i += words;
            }
            else {
                i += nloop; // IMAGE: texture upload data
            }
        }
        return prims;
    }

    private static uint u32(ReadOnlySpan<byte> buf, int qword, int word) {
        int at = qword * 16 + word * 4;
        return at + 4 <= buf.Length ? BinaryPrimitives.ReadUInt32LittleEndian(buf[at..]) : 0;
    }
}
