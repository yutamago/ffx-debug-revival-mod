// SPDX-License-Identifier: MIT

using Yuta.DebugRevival.Features;
using Yuta.DebugRevival.Gs;

namespace Yuta.DebugRevival.Ui;

/// <summary>
///     Draws the PS2 debug GUI's primitives under the mod's window, stretched over the screen as the game stretches
///     its PS2-space UI. Follows the GS: vertex colours are 7-bit, alpha is A/128, the shadow darkens by FIX/128.
/// </summary>
public sealed class GuRenderer(GuToolkit gu, string font_path) {
    private FhTexture? _font;
    private bool _font_failed;
    private int _load_attempts;

    public void draw() {
        if (!gu.open) return;
        ImGuiIOPtr io = ImGui.GetIO();
        Vector2 ds = io.DisplaySize;

        if (gu.capture_mouse) capture_input(io, ds);

        GuToolkit.Frame? f = gu.frame;
        if (f is null || Environment.TickCount64 - f.Stamp > 500) return; // no new frame for half a second
        ImTextureRef? tex = font();
        draw_frame(ImGui.GetBackgroundDrawList(), f, ds, tex);
    }

    // An invisible full-screen window: where the mod's window isn't, the mouse goes to the toolkit and not the game.
    private void capture_input(ImGuiIOPtr io, Vector2 ds) {
        ImGui.SetNextWindowPos(Vector2.Zero);
        ImGui.SetNextWindowSize(ds);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoSavedSettings
                               | ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav
                               | ImGuiWindowFlags.NoMove;
        if (ImGui.Begin("##gu_input", flags)) {
            bool hovered = ImGui.IsWindowHovered();
            Vector2 m = io.MousePos;
            if (ds.X > 0 && ds.Y > 0 && m.X >= 0 && m.Y >= 0) {
                gu.mouse_x = Math.Clamp(m.X * 512f / ds.X, 0, 512);
                gu.mouse_y = Math.Clamp(m.Y * 208f / ds.Y, 0, 208);
            }
            int buttons = 0;
            if (hovered) {
                if (io.MouseDown[0]) buttons |= 1;
                if (io.MouseDown[1]) buttons |= 2;
                if (io.MouseDown[2]) buttons |= 4;
                if (io.MouseWheel != 0) gu.add_wheel(Math.Sign(io.MouseWheel));
            }
            gu.mouse_buttons = buttons;
        }
        ImGui.End();
    }

    private ImTextureRef? font() {
        if (_font_failed) return null;
        try {
            if (_font is null) {
                if (!File.Exists(font_path)) GuToolkit.write_font_tga(font_path);
                _font = new FhTexture(font_path, FhTextureType.TGA);
            }
            if (_font.try_use(out ImTextureRef r, out _)) return r;
            if (!FhApi.Resources.load_texture_from_disk(_font)) {
                // A failed load leaves the handle locked, so retry with a fresh one.
                _font = null;
                if (++_load_attempts > 5) _font_failed = true;
                return null;
            }
            return _font.try_use(out r, out _) ? r : null;
        }
        catch (Exception) {
            _font_failed = true;
            return null;
        }
    }

    private static uint col(byte r, byte g, byte b, float a) =>
        (uint)r | ((uint)g << 8) | ((uint)b << 16) | ((uint)Math.Clamp((int)(a * 255), 0, 255) << 24);

    private static void draw_frame(ImDrawListPtr dl, GuToolkit.Frame f, Vector2 ds, ImTextureRef? tex) {
        float sx = ds.X / f.W, sy = ds.Y / f.H;
        Vector2 at(GsVertex v) => new(v.X * sx, v.Y * sy);
        Vector2 white = ImGui.GetFontTexUvWhitePixel();
        float thick = Math.Max(1f, sy);

        int cx0 = -1, cy0 = -1, cx1 = -1, cy1 = -1;
        bool clip_pushed = false;

        foreach (GsPrim p in f.Prims) {
            if (p.ScissorX0 != cx0 || p.ScissorY0 != cy0 || p.ScissorX1 != cx1 || p.ScissorY1 != cy1) {
                if (clip_pushed) dl.PopClipRect();
                cx0 = p.ScissorX0; cy0 = p.ScissorY0; cx1 = p.ScissorX1; cy1 = p.ScissorY1;
                dl.PushClipRect(new Vector2(cx0 * sx, cy0 * sy), new Vector2((cx1 + 1) * sx, (cy1 + 1) * sy), true);
                clip_pushed = true;
            }

            GsVertex last = p.V[^1];
            float alpha = p.FixAlpha != 0 ? 1f - p.FixAlpha / 128f : p.Blend ? Math.Min((int)last.A, 0x80) / 128f : 1f;

            if (p.Textured) {
                if (tex is not ImTextureRef t) continue;
                uint c = p.Decal ? 0xffffffffu
                       : col((byte)Math.Min(255, 2 * last.R), (byte)Math.Min(255, 2 * last.G), (byte)Math.Min(255, 2 * last.B), 1f);
                Vector2 uv(GsVertex v) => new(v.U / 128f, v.V / 128f);
                if (p.Kind == GsPrimKind.Triangle) {
                    dl.AddImageQuad(t, at(p.V[0]), at(p.V[1]), at(p.V[2]), at(p.V[2]), uv(p.V[0]), uv(p.V[1]), uv(p.V[2]), uv(p.V[2]), c);
                } else if (p.Kind == GsPrimKind.Sprite) {
                    dl.AddImage(t, at(p.V[0]), at(p.V[1]), uv(p.V[0]), uv(p.V[1]), c);
                }
                continue;
            }

            switch (p.Kind) {
                case GsPrimKind.Line:
                    dl.AddLine(at(p.V[0]), at(p.V[1]), col(last.R, last.G, last.B, alpha), thick);
                    break;
                case GsPrimKind.Sprite: {
                    uint c = p.FixAlpha != 0 ? col(0, 0, 0, alpha) : col(last.R, last.G, last.B, alpha);
                    dl.AddRectFilled(at(p.V[0]), at(p.V[1]), c);
                    break;
                }
                case GsPrimKind.Triangle:
                    dl.PrimReserve(3, 3);
                    uint idx = dl.VtxCurrentIdx;
                    dl.PrimWriteIdx((ushort)(idx + 0));
                    dl.PrimWriteIdx((ushort)(idx + 1));
                    dl.PrimWriteIdx((ushort)(idx + 2));
                    for (int k = 0; k < 3; k++) {
                        GsVertex v = p.Gouraud ? p.V[k] : last;
                        dl.PrimWriteVtx(at(p.V[k]), white, col(v.R, v.G, v.B, p.Blend ? Math.Min((int)v.A, 0x80) / 128f : 1f));
                    }
                    break;
            }
        }
        if (clip_pushed) dl.PopClipRect();
    }
}
