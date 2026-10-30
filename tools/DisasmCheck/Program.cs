// SPDX-License-Identifier: MIT OR Apache-2.0

using System.Globalization;
using System.Text;
using Yuta.DebugRevival.Script;

// usage: DisasmCheck <script file> <archive path> <natives.tsv>
byte[] file = File.ReadAllBytes(args[0]);
NativeNames names = NativeNames.parse(File.ReadAllText(args[2]));

uint u32(int o) => BitConverter.ToUInt32(file, o);
List<uint> offsets = [];
if (file.Length >= 4 && Encoding.ASCII.GetString(file, 0, 4) == "EV01") {
    for (int i = 1; i < 16 && u32(i * 4) != 0xffffffff; i++) offsets.Add(u32(i * 4));
} else if (u32(0) == 8) {
    for (int i = 1; i <= 8; i++) offsets.Add(u32(i * 4));
} else {
    Console.Error.WriteLine("neither a monster nor an event file");
    return 1;
}
List<uint> present = offsets.Where(o => o != 0).ToList();
uint start = present[0];
uint end = present.FirstOrDefault(o => o > start, (uint)file.Length);
byte[] section = file[(int)start..(int)end];

ScriptBlock? block = ScriptBlock.parse(new ArrayBlockReader(section), section.Length, out string error);
if (block is null) {
    Console.Error.WriteLine(error);
    return 1;
}
Console.Out.Write($"; {args[1]}\n{block.disassemble_all(names.annotate)}");
return 0;
