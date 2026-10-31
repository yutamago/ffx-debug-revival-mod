// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Yuta.DebugRevival.Script;

// Compares the mod's disassembler with ffx-decomp's Rust one (`ffx script disasm`) over the shipped scripts.
// usage: dotnet run --project tools/DisasmCheck -c Release -- [max scripts]   (needs FFX_GAME_DIR and FFX_CLI)
string game = Environment.GetEnvironmentVariable("FFX_GAME_DIR") ?? throw new Exception("set FFX_GAME_DIR");
string cli = Environment.GetEnvironmentVariable("FFX_CLI") ?? throw new Exception("set FFX_CLI to the ffx binary of ffx-decomp");
string archive = Path.Combine(game, "data", "FFX_Data.vbf");
int max = args.Length > 0 ? int.Parse(args[0]) : int.MaxValue;
NativeNames names = NativeNames.parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "natives.tsv")));

(bool ok, byte[] stdout) run(params string[] argv) {
    ProcessStartInfo psi = new(cli) { RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (string a in argv) psi.ArgumentList.Add(a);
    using Process p = Process.Start(psi)!;
    using MemoryStream ms = new();
    Task<string> err = p.StandardError.ReadToEndAsync();
    p.StandardOutput.BaseStream.CopyTo(ms);
    p.WaitForExit();
    _ = err.Result;
    return (p.ExitCode == 0, ms.ToArray());
}

// Native names depend on which ffx.sym each side was built from; compare the handler addresses.
Regex native_name = new(@"; [A-Za-z_][A-Za-z0-9_]* (0x[0-9a-f]{8})$", RegexOptions.Multiline);
string normalize(string text) => native_name.Replace(text, "; $1");

List<string> paths = [ .. Encoding.UTF8.GetString(run("script", "list", "--game-dir", game).stdout)
    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
    .Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "")
    .Where(p => p.EndsWith(".ebp") || p.EndsWith(".bin")) ];

int n = 0, bad = 0;
foreach (string path in paths.Take(max)) {
    n++;
    (bool cat_ok, byte[] file) = run("vbf", "cat", archive, path);
    if (!cat_ok) { Console.WriteLine($"skip (cat): {path}"); continue; }
    (bool rust_ok, byte[] rust) = run("script", "disasm", "--game-dir", game, path);
    if (!rust_ok) { Console.WriteLine($"skip (rust failed): {path}"); continue; }

    string expected = normalize(Encoding.UTF8.GetString(rust));
    string actual = normalize(disassemble(file, path));
    if (expected == actual) continue;
    bad++;
    Console.WriteLine($"DIFF: {path}");
    string[] e = expected.Split('\n'), a = actual.Split('\n');
    int shown = 0;
    for (int i = 0; i < Math.Max(e.Length, a.Length) && shown < 3; i++) {
        string el = i < e.Length ? e[i] : "", al = i < a.Length ? a[i] : "";
        if (el == al) continue;
        Console.WriteLine($"  line {i + 1}\n  - {el}\n  + {al}");
        shown++;
    }
}
Console.WriteLine($"{n} scripts, {bad} differ");
return bad == 0 ? 0 : 1;

// The script section of an event (EV01) or monster file, in `ffx script disasm` format.
string disassemble(byte[] file, string path) {
    uint u32(int o) => BitConverter.ToUInt32(file, o);
    List<uint> offsets = [];
    if (file.Length >= 4 && Encoding.ASCII.GetString(file, 0, 4) == "EV01") {
        for (int i = 1; i < 16 && u32(i * 4) != 0xffffffff; i++) offsets.Add(u32(i * 4));
    } else if (file.Length >= 36 && u32(0) == 8) {
        for (int i = 1; i <= 8; i++) offsets.Add(u32(i * 4));
    } else {
        return "neither a monster nor an event file\n";
    }
    List<uint> present = [ .. offsets.Where(o => o != 0) ];
    uint start = present[0];
    uint end = present.FirstOrDefault(o => o > start, (uint)file.Length);
    byte[] section = file[(int)start..(int)end];
    ScriptBlock? block = ScriptBlock.parse(new ArrayBlockReader(section), section.Length, out string error);
    return block is null ? error + "\n" : $"; {path}\n{block.disassemble_all(names.annotate)}";
}
