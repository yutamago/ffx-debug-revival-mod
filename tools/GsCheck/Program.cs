// SPDX-License-Identifier: MIT

using System.Globalization;
using Yuta.DebugRevival.Gs;

// usage: GsCheck <packet.bin> [prims.txt]   (packet = from the DMA tag on, 512x416 screen)
byte[] buf = File.ReadAllBytes(args[0]);
List<GsPrim> prims = GifParser.parse(buf, 512, 416);
var groups = prims.GroupBy(p => $"{p.Kind}{(p.Textured ? " textured" : "")}{(p.Textured && p.Decal ? " decal" : "")}{(p.Blend ? " blended" : "")}{(p.FixAlpha != 0 ? $" darken {p.FixAlpha:x2}" : "")}{(p.Gouraud ? " gouraud" : "")}");
Console.WriteLine($"{prims.Count} primitives");
foreach (var g in groups.OrderByDescending(g => g.Count())) Console.WriteLine($"  {g.Count(),5}  {g.Key}");
if (args.Length > 1) {
    using StreamWriter w = new(args[1]);
    foreach (GsPrim p in prims) {
        w.Write($"{p.Kind} {(p.Textured ? 1 : 0)} {(p.Decal ? 1 : 0)} {(p.Blend ? 1 : 0)} {p.FixAlpha} {p.ScissorX0} {p.ScissorY0} {p.ScissorX1} {p.ScissorY1}");
        foreach (GsVertex v in p.V) w.Write(string.Create(CultureInfo.InvariantCulture, $" {v.X} {v.Y} {v.R} {v.G} {v.B} {v.A} {v.U} {v.V}"));
        w.WriteLine();
    }
}
