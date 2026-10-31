// SPDX-License-Identifier: MIT

// Shared with tools/DisasmCheck, which has no global usings of its own.
using System.Globalization;

namespace Yuta.DebugRevival.Script;

/// <summary>Native function names for <c>func</c> annotations, from natives.tsv (tools/gen-natives.cs).</summary>
public sealed class NativeNames {
    private readonly Dictionary<ushort, (uint handler, string name)> _by_id = [];

    public static NativeNames parse(string tsv) {
        NativeNames n = new();
        foreach (string line in tsv.Split('\n')) {
            if (line.Length == 0 || line[0] == '#') continue;
            string[] f = line.TrimEnd('\r').Split('\t');
            if (f.Length < 3) continue;
            n._by_id[ushort.Parse(f[0], NumberStyles.HexNumber)]
                = (uint.Parse(f[1], NumberStyles.HexNumber), f[2]);
        }
        return n;
    }

    /// <summary>"Name 0xhandler", or "0xhandler" when unnamed, or null for an unknown id.</summary>
    public string? annotate(ushort id) {
        if (!_by_id.TryGetValue(id, out var e)) return null;
        return e.name.Length > 0 ? $"{e.name} 0x{e.handler:x8}" : $"0x{e.handler:x8}";
    }
}
