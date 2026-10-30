#!/usr/bin/env python3
# SPDX-License-Identifier: MIT OR Apache-2.0
"""For every map (event) id: the number of entrances and whether a jump to it
crashes or hangs FFX.exe (Steam HD, the build in ffx-decomp's symbols/ffx.sym).

Needs FFX_GAME_DIR and FFX_CLI (the `ffx` binary of ffx-decomp).
usage: python3 -I tools/gen-map-table.py > src/Features/maps.tsv
       python3 -I tools/gen-map-table.py --detail   (adds the map index per language; not for shipping)

Reason codes (docs/research/maps.md has the mechanism of each):
  no_name    module 0xc slot map*0x12 empty in cdrom.fnd: Cd_QueueRead copies a NULL name (crash)
  no_file    named, but the .ebp is not in the archive: size 0, "EV" check fails, AtelEventSetUp jmp $ (hang)
  bad_path   name lacks "/event/obj/" or a later '/': AtelEventSetUp's strstr/strrchr give a NULL (crash)
  map_name   the map's .vpa (module 0x13 slot 2i+1) is there but slot 2i is empty: graphicFieldMapLoad(NULL) (crash)
  map_range  map index past module 0x13's slots: reads another module's names (undefined)
  soft_reset not a crash: TkSetEventJump 0x0088ea60 turns a jump to this map (0x00c33480) into a reset request
Eternal Calm scenes (0x00c33468, 7 maps) take their .ebp name from FFX.exe (0x00c334b8) when cdrom.fnd has none.
A code with a "@<langs>" suffix applies only to those TkGetLang values.
"""
import os
import struct
import subprocess
import sys

GAME = os.environ.get("FFX_GAME_DIR") or sys.exit("set FFX_GAME_DIR")
CLI = os.environ.get("FFX_CLI") or sys.exit("set FFX_CLI to the ffx binary of ffx-decomp")
ARCHIVE = os.path.join(GAME, "data", "FFX_Data.vbf")
CDDATA = "ffx_ps2/ffx/proj/battle/jp/cddata/"   # pc_fat_index_filename_set 0x00a42fc0, non-HDD mode
EVENT_MODULE, MAP_MODULE, FILES_PER_EVENT = 0xc, 0x13, 0x12
# TkGetLang values AtelGetMapIndex 0x0086bd90 can use (>= 0x10 becomes 0): the PC languages.
LANGS = (0, 1, 2, 3, 4, 5, 9, 10, 11)


def exe_constants():
    """Eternal Calm first map (0x00c33468), its 7 scene names (0x00c334b8), TkSetEventJump's reset map (0x00c33480)."""
    exe = open(os.path.join(GAME, "FFX.exe"), "rb").read()
    pe = u32(exe, 0x3c)
    nsec, opt = struct.unpack_from("<H", exe, pe + 6)[0], struct.unpack_from("<H", exe, pe + 20)[0]
    secs = [struct.unpack_from("<12xIII", exe, pe + 24 + opt + 40 * i) for i in range(nsec)]

    def at(va):
        rva = va - 0x400000
        return next(rva - v + raw for v, size, raw in secs if v <= rva < v + size)

    first = struct.unpack_from("<i", exe, at(0x00c33468))[0]
    calm = {}
    for i in range(7):
        o = at(u32(exe, at(0x00c334b8) + 4 * i))
        calm[first + i] = exe[o:exe.index(b"\0", o)].decode("latin-1")
    return calm, struct.unpack_from("<i", exe, at(0x00c33480))[0]


def cat(path):
    return subprocess.run([CLI, "vbf", "cat", ARCHIVE, path], check=True, capture_output=True).stdout


def archive_paths():
    out = subprocess.run([CLI, "vbf", "list", ARCHIVE], check=True, capture_output=True, text=True).stdout
    return {line.split("\t")[2] for line in out.splitlines() if line.count("\t") >= 2}


def u32(b, o):
    return struct.unpack_from("<I", b, o)[0]


def i16(b, o):
    return struct.unpack_from("<h", b, o)[0]


class Names:
    """cdrom.fnd as PcFileName_00a42c00 reads it: count, offsets; entry n from offset n to n+1."""

    def __init__(self):
        self.fnd = cat(CDDATA + "cdrom.fnd")
        fid = cat(CDDATA + "cdrom.fid")
        self.count = u32(self.fnd, 0)
        self.firsts = struct.unpack_from("<%dh" % (len(fid) // 2), fid)

    def name(self, loc):
        if loc < 0 or loc >= self.count:
            return None
        s, e = u32(self.fnd, 4 + 4 * loc), u32(self.fnd, 8 + 4 * loc)
        if (e - s) & 0x80000000 or e == s:
            return None
        return self.fnd[s:e].split(b"\0")[0].decode("latin-1")

    def module_span(self, m):
        first = self.firsts[m]
        later = [f for f in self.firsts if f > first]
        return first, (min(later) if later else self.count)


def vbf_path(host_name):
    # host0:/ffx/master/... -> ffx_ps2/ffx/master/... (the archive keeps the PS2 tree)
    return "ffx_ps2" + host_name[len("host0:"):] if host_name.startswith("host0:") else host_name


def event_ids():
    """eventid.bin as initEventList 0x00907f50 reads it: (offset, length) pairs, count = first offset / 8."""
    b = cat("ffx_ps2/ffx/proj/event/header/eventid.bin")
    return range(u32(b, 0) // 8)


def script_info(ebp):
    """(entrances, {lang: map index}) of an .ebp, as AtelEventSetUp 0x00872ef0 and
    AtelGetMapIndex 0x0086bd90 read it; entrances None without a table."""
    hdr = u32(ebp, 4)
    first, end = u32(ebp, hdr + 4), u32(ebp, hdr + 8)
    if first == 0:
        return None, {k: -1 for k in LANGS}
    entrances = max(0, (end - first)) // 0x20
    base = i16(ebp, hdr + first)
    maps = {k: base for k in LANGS}
    snd = u32(ebp, hdr + 0x2c)
    if snd:
        rec = u32(ebp, hdr + snd + 0x3c)
        if rec and u32(ebp, hdr + rec + 0xc):
            t = u32(ebp, hdr + rec + 0xc)
            maps = {k: struct.unpack_from("<i", ebp, hdr + t + 4 * k)[0] for k in LANGS}
    return entrances, maps


def map_reason(names, files, span, idx):
    """AtelMapLoad 0x0086dca0 -> yiFieldMapRead 0x009085d0 -> yiMapReadMaster 0x009093b0(0x13, 2i, 0x13, 2i+1)."""
    if idx <= 0:
        return ""                                   # AtelMapLoad skips yiFieldMapRead
    first, nxt = span
    if first + 2 * idx + 1 >= nxt:
        return "map_range"
    vpa = names.name(first + 2 * idx + 1)
    if vpa is None or vpa_path_missing(files, vpa):
        return ""                                   # size 0: yiMapReadMaster returns -1, no map
    if names.name(first + 2 * idx) is None:
        return "map_name"
    return ""


def vpa_path_missing(files, name):
    return vbf_path(name) not in files


def main():
    detail = "--detail" in sys.argv[1:]
    names, files = Names(), archive_paths()
    calm, reset_map = exe_constants()
    ev_first, ev_next = names.module_span(EVENT_MODULE)
    map_span = names.module_span(MAP_MODULE)
    ids = set(event_ids()) | set(range((ev_next - ev_first) // FILES_PER_EVENT))
    print("# map_id\tentrances\tcrash_reason  (gen-map-table.py; entrances -1: no table, the player lands on the "
          "default record; empty reason: no known crash)")
    for m in sorted(ids):
        name = names.name(ev_first + m * FILES_PER_EVENT)
        if name is None and m in calm:
            name = calm[m]                          # Cd_QueueRead / AtelEventSetUp use the built-in name
        entrances, reason, extra = -1, "", ""
        if name is None:
            reason = "no_name"
        elif vbf_path(name) not in files:
            reason = "no_file"
        else:
            cut = name.find("/event/obj/")
            if cut < 0 or "/" not in name[cut + 11:]:
                reason = "bad_path"
            ebp = cat(vbf_path(name))
            if ebp[:2] != b"EV":
                reason = reason or "not_ev"
            else:
                n, maps = script_info(ebp)
                entrances = -1 if n is None else n
                by = {}
                for k, idx in maps.items():
                    by.setdefault(map_reason(names, files, map_span, idx), []).append(k)
                bad = {r: ks for r, ks in by.items() if r}
                if bad and not reason:
                    reason = ",".join(r if len(ks) == len(LANGS) else r + "@" + "/".join(map(str, ks))
                                      for r, ks in sorted(bad.items()))
                if detail:
                    extra = "\t" + " ".join("%d:%d" % kv for kv in sorted(maps.items()))
        if m == reset_map and not reason:
            reason = "soft_reset"
        print("%d\t%d\t%s%s" % (m, entrances, reason, extra))


if __name__ == "__main__":
    main()
