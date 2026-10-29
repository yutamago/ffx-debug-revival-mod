// SPDX-License-Identifier: MIT OR Apache-2.0

namespace Yuta.DebugRevival.Ui;

/// <summary>
///     ImGui's Text family takes a printf format; game strings, log lines and
///     status messages may contain '%'. These helpers show text as it is.
/// </summary>
internal static class Txt {
    private static string esc(string s) => s.Replace("%", "%%");

    public static void T(string s) => ImGui.TextUnformatted(s);
    public static void C(Vector4 colour, string s) => ImGui.TextColored(colour, esc(s));
    public static void D(string s) => ImGui.TextDisabled(esc(s));
    public static void W(string s) => ImGui.TextWrapped(esc(s));
    public static void B(string s) => ImGui.BulletText(esc(s));
}
