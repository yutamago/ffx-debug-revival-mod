// SPDX-License-Identifier: MIT OR Apache-2.0
//
// For every map (event) id: the number of entrances and whether a jump to it crashes or hangs FFX.exe (Steam HD, the
// build in ffx-decomp's symbols/ffx.sym). Needs FFX_GAME_DIR and FFX_CLI (the `ffx` binary of ffx-decomp).
//
// usage: dotnet run tools/gen-map-table.cs > src/Features/maps.tsv
//        dotnet run tools/gen-map-table.cs -- --detail   (adds the map index per language; not for shipping)
//
// Reason codes (docs/research/maps.md has the mechanism of each):
//   no_name    module 0xc slot map*0x12 empty in cdrom.fnd: Cd_QueueRead copies a NULL name (crash)
//   no_file    named, but the .ebp is not in the archive: size 0, "EV" check fails, AtelEventSetUp jmp $ (hang)
//   bad_path   name lacks "/event/obj/" or a later '/': AtelEventSetUp's strstr/strrchr give a NULL (crash)
//   not_ev     the .ebp doesn't start with "EV": AtelEventSetUp jmp $ (hang)
//   map_name   the map's .vpa (module 0x13 slot 2i+1) is there but slot 2i is empty: graphicFieldMapLoad(NULL) (crash)
//   map_range  map index past module 0x13's slots: reads another module's names (undefined)
//   soft_reset not a crash: TkSetEventJump 0x0088ea60 turns a jump to this map (0x00c33480) into a reset request
// Eternal Calm scenes (0x00c33468, 7 maps) take their .ebp name from FFX.exe (0x00c334b8) when cdrom.fnd has none.
// A code with a "@<langs>" suffix applies only to those TkGetLang values.

using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;

string game = Environment.GetEnvironmentVariable("FFX_GAME_DIR") ?? throw new Exception("set FFX_GAME_DIR");
string cli = Environment.GetEnvironmentVariable("FFX_CLI") ?? throw new Exception("set FFX_CLI to the ffx binary of ffx-decomp");
string archive = Path.Combine(game, "data", "FFX_Data.vbf");
const string CDDATA = "ffx_ps2/ffx/proj/battle/jp/cddata/"; // pc_fat_index_filename_set 0x00a42fc0, non-HDD mode
const int EVENT_MODULE = 0xc, MAP_MODULE = 0x13, FILES_PER_EVENT = 0x12;
// TkGetLang values AtelGetMapIndex 0x0086bd90 can use (>= 0x10 becomes 0): the PC languages.
int[] LANGS = [ 0, 1, 2, 3, 4, 5, 9, 10, 11 ];
bool detail = args.Contains("--detail");

byte[] run(params string[] argv) {
    ProcessStartInfo psi = new(cli) { RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (string a in argv) psi.ArgumentList.Add(a);
    using Process p = Process.Start(psi)!;
    using MemoryStream ms = new();
    Task<string> err = p.StandardError.ReadToEndAsync();
    p.StandardOutput.BaseStream.CopyTo(ms);
    p.WaitForExit();
    if (p.ExitCode != 0) throw new Exception($"ffx {string.Join(' ', argv)} failed: {err.Result}");
    return ms.ToArray();
}
byte[] cat(string path) => run("vbf", "cat", archive, path);
uint u32(byte[] b, long o) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan((int)o));
int i32(byte[] b, long o) => BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan((int)o));
short i16(byte[] b, long o) => BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan((int)o));
string cstr(byte[] b, int start, int end) {
    int zero = Array.IndexOf(b, (byte)0, start, end - start);
    return Encoding.Latin1.GetString(b, start, (zero < 0 ? end : zero) - start);
}

// host0:/ffx/master/... -> ffx_ps2/ffx/master/... (the archive keeps the PS2 tree)
string vbf_path(string host_name) => host_name.StartsWith("host0:") ? "ffx_ps2" + host_name["host0:".Length..] : host_name;

// Eternal Calm first map (0x00c33468), its 7 scene names (0x00c334b8), TkSetEventJump's reset map (0x00c33480).
byte[] exe = File.ReadAllBytes(Path.Combine(game, "FFX.exe"));
int pe = i32(exe, 0x3c);
int section_count = BinaryPrimitives.ReadUInt16LittleEndian(exe.AsSpan(pe + 6));
int optional_size = BinaryPrimitives.ReadUInt16LittleEndian(exe.AsSpan(pe + 20));
List<(uint va, uint size, uint raw)> sections = [];
for (int i = 0; i < section_count; i++) {
    int s = pe + 24 + optional_size + 40 * i + 12;
    sections.Add((u32(exe, s), u32(exe, s + 4), u32(exe, s + 8)));
}
int file_offset(uint va) {
    uint rva = va - 0x400000;
    var (v, _, raw) = sections.First(x => rva >= x.va && rva < x.va + x.size);
    return (int)(rva - v + raw);
}
int calm_first = i32(exe, file_offset(0x00c33468));
Dictionary<int, string> calm = [];
for (int i = 0; i < 7; i++) {
    int o = file_offset(u32(exe, file_offset(0x00c334b8) + 4 * i));
    calm[calm_first + i] = cstr(exe, o, exe.Length);
}
int reset_map = i32(exe, file_offset(0x00c33480));

// cdrom.fnd as PcFileName_00a42c00 reads it: count, offsets; entry n from offset n to n+1.
byte[] fnd = cat(CDDATA + "cdrom.fnd");
byte[] fid = cat(CDDATA + "cdrom.fid");
uint name_count = u32(fnd, 0);
short[] firsts = new short[fid.Length / 2];
for (int i = 0; i < firsts.Length; i++) firsts[i] = i16(fid, 2 * i);

string? name_at(long loc) {
    if (loc < 0 || loc >= name_count) return null;
    uint s = u32(fnd, 4 + 4 * loc), e = u32(fnd, 8 + 4 * loc);
    return (e - s) is 0 or >= 0x80000000 ? null : cstr(fnd, (int)s, (int)e);
}

(int first, long next) module_span(int m) {
    int first = firsts[m];
    short[] later = [ .. firsts.Where(f => f > first) ];
    return (first, later.Length > 0 ? later.Min() : name_count);
}

HashSet<string> files = [];
foreach (string line in Encoding.UTF8.GetString(run("vbf", "list", archive)).Split('\n')) {
    string[] f = line.TrimEnd('\r').Split('\t');
    if (f.Length >= 3) files.Add(f[2]);
}

// AtelMapLoad 0x0086dca0 -> yiFieldMapRead 0x009085d0 -> yiMapReadMaster 0x009093b0(0x13, 2i, 0x13, 2i+1).
(int first, long next) map_span = module_span(MAP_MODULE);
string map_reason(int idx) {
    if (idx <= 0) return "";                                  // AtelMapLoad skips yiFieldMapRead
    if (map_span.first + 2L * idx + 1 >= map_span.next) return "map_range";
    string? vpa = name_at(map_span.first + 2L * idx + 1);
    if (vpa is null || !files.Contains(vbf_path(vpa))) return ""; // size 0: yiMapReadMaster returns -1, no map
    return name_at(map_span.first + 2L * idx) is null ? "map_name" : "";
}

// (entrances, map index per language) of an .ebp, as AtelEventSetUp 0x00872ef0 and AtelGetMapIndex 0x0086bd90 read
// it; entrances -1 without a table.
(int entrances, Dictionary<int, int> maps) script_info(byte[] ebp) {
    long hdr = u32(ebp, 4);
    long first = u32(ebp, hdr + 4), end = u32(ebp, hdr + 8);
    if (first == 0) return (-1, LANGS.ToDictionary(k => k, _ => -1));
    int entrances = (int)(Math.Max(0, end - first) / 0x20);
    int map = i16(ebp, hdr + first);
    Dictionary<int, int> maps = LANGS.ToDictionary(k => k, _ => map);
    long sound = u32(ebp, hdr + 0x2c);
    if (sound != 0) {
        long rec = u32(ebp, hdr + sound + 0x3c);
        if (rec != 0 && u32(ebp, hdr + rec + 0xc) != 0) {
            long t = u32(ebp, hdr + rec + 0xc);
            maps = LANGS.ToDictionary(k => k, k => i32(ebp, hdr + t + 4 * k));
        }
    }
    return (entrances, maps);
}

// eventid.bin as initEventList 0x00907f50 reads it: (offset, length) pairs, count = first offset / 8.
byte[] eventid = cat("ffx_ps2/ffx/proj/event/header/eventid.bin");
(int ev_first, long ev_next) = module_span(EVENT_MODULE);
SortedSet<int> ids = [ .. Enumerable.Range(0, (int)(u32(eventid, 0) / 8)),
                       .. Enumerable.Range(0, (int)((ev_next - ev_first) / FILES_PER_EVENT)) ];

TextWriter output = new StreamWriter(Console.OpenStandardOutput()) { NewLine = "\n" };
output.WriteLine("# map_id\tentrances\tcrash_reason  (gen-map-table.cs; entrances -1: no table, the player lands on the "
          + "default record; empty reason: no known crash)");
foreach (int m in ids) {
    string? name = name_at(ev_first + (long)m * FILES_PER_EVENT);
    if (name is null && calm.TryGetValue(m, out string? built_in)) name = built_in; // Cd_QueueRead / AtelEventSetUp use it
    int entrances = -1;
    string reason = "", extra = "";
    if (name is null) reason = "no_name";
    else if (!files.Contains(vbf_path(name))) reason = "no_file";
    else {
        int cut = name.IndexOf("/event/obj/", StringComparison.Ordinal);
        if (cut < 0 || !name[(cut + 11)..].Contains('/')) reason = "bad_path";
        byte[] ebp = cat(vbf_path(name));
        if (ebp.Length < 2 || ebp[0] != 'E' || ebp[1] != 'V') {
            if (reason.Length == 0) reason = "not_ev";
        } else {
            (entrances, Dictionary<int, int> maps) = script_info(ebp);
            // reason -> the languages that hit it, in LANGS order
            SortedDictionary<string, List<int>> bad = new(StringComparer.Ordinal);
            foreach (int k in LANGS) {
                string r = map_reason(maps[k]);
                if (r.Length > 0) (bad.TryGetValue(r, out var ks) ? ks : bad[r] = []).Add(k);
            }
            if (bad.Count > 0 && reason.Length == 0) {
                reason = string.Join(",", bad.Select(b => b.Value.Count == LANGS.Length ? b.Key : $"{b.Key}@{string.Join("/", b.Value)}"));
            }
            if (detail) extra = "\t" + string.Join(" ", maps.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}:{kv.Value}"));
        }
    }
    if (m == reset_map && reason.Length == 0) reason = "soft_reset";
    output.WriteLine($"{m}\t{entrances}\t{reason}{extra}");
}
output.Flush();
