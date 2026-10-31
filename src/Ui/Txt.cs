// SPDX-License-Identifier: MIT

namespace Yuta.DebugRevival.Ui;

/// <summary>ImGui's Text functions take a printf format; these show text that may contain '%' as it is.</summary>
internal static class Txt {
    private static string esc(string s) => s.Replace("%", "%%");

    public static void T(string s) => ImGui.TextUnformatted(s);
    public static void C(Vector4 colour, string s) => ImGui.TextColored(colour, esc(s));
    public static void D(string s) => ImGui.TextDisabled(esc(s));
    public static void W(string s) => ImGui.TextWrapped(esc(s));

    /// <summary>A "(?)" on the same line that shows <paramref name="s"/> on hover.</summary>
    public static void Help(string s) {
        ImGui.SameLine();
        ImGui.TextDisabled("(?)");
        if (ImGui.BeginItemTooltip()) {
            ImGui.PushTextWrapPos(ImGui.GetFontSize() * 32);
            ImGui.TextUnformatted(s);
            ImGui.PopTextWrapPos();
            ImGui.EndTooltip();
        }
    }
}
