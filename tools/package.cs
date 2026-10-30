// SPDX-License-Identifier: MIT OR Apache-2.0
//
// Zips the Release build as mods/yuta.debugrevival/, ready to unpack into the Fahrenheit folder.
//
// usage: dotnet run tools/package.cs -- <out.zip>   (after dotnet build -c Release ffx-debug-revival.slnx)

using System.IO.Compression;
using System.Runtime.CompilerServices;

if (args.Length != 1) {
    Console.Error.WriteLine("usage: dotnet run tools/package.cs -- <out.zip>");
    return 1;
}

string root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(this_file())!, ".."));
string mod = Path.Combine(root, "artifacts", "deploy", "rel", "mods", "yuta.debugrevival");
if (!File.Exists(Path.Combine(mod, "yuta.debugrevival.dll"))) {
    Console.Error.WriteLine("build first: dotnet build -c Release ffx-debug-revival.slnx");
    return 1;
}

string out_path = Path.GetFullPath(args[0]);
File.Delete(out_path);
using (ZipArchive zip = ZipFile.Open(out_path, ZipArchiveMode.Create)) {
    // Entry names use '/' on every OS, so the zip unpacks the same on Windows.
    foreach (string file in Directory.EnumerateFiles(mod, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)) {
        zip.CreateEntryFromFile(file, "mods/yuta.debugrevival/" + Path.GetRelativePath(mod, file).Replace('\\', '/'));
    }
    foreach (string doc in (string[])[ "README.md", "LICENSE-MIT", "LICENSE-APACHE", "THIRD-PARTY-NOTICES" ]) {
        zip.CreateEntryFromFile(Path.Combine(root, doc), "mods/yuta.debugrevival/" + doc);
    }
}
Console.WriteLine(out_path);
return 0;

static string this_file([CallerFilePath] string path = "") => path;
