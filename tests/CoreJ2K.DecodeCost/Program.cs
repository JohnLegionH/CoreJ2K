// Copyright (c) 2026 Legion Builds
// Licensed under the BSD 3-Clause License.

using System;
using System.IO;
using System.Linq;
using CoreJ2K.Tests;

// Decode-cost probe. Usage:
//   CoreJ2K.DecodeCost [--dir DIR] [--synthetic] [--levels N] [--layers a,b,c] [--check]
// DIR: raw codestreams or JP2 files (*.j2k, *.j2c, *.jp2). Each is decoded at discard levels 0..N (default 3) and,
// at full resolution, with each layer limit given. One line per case (tab separated):
//   CASE name WxH comps layers decomp level maxLayers outWxH alloc_bytes peak_heap_bytes ms pixel_hash code_blocks passes coded_bytes
// --check: also print a hash of the decoded pixels so two builds can be compared.
var o = new System.Collections.Generic.Dictionary<string, string?>();
for (int i = 0; i < args.Length; i++)
    if (args[i].StartsWith("--")) o[args[i][2..]] = i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : null;
int maxLevel = int.Parse(o.GetValueOrDefault("levels") ?? "3");
int[] layerLimits = (o.GetValueOrDefault("layers") ?? "1,4,8").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray();
bool check = o.ContainsKey("check");

var sets = new System.Collections.Generic.List<(string Name, byte[] Data)>();
if (o.GetValueOrDefault("dir") is { } dir)
    foreach (var f in Directory.GetFiles(dir).Where(f => f.EndsWith(".j2k") || f.EndsWith(".j2c") || f.EndsWith(".jp2")).Order())
        sets.Add((Path.GetFileName(f), FromSoc(File.ReadAllBytes(f))));
if (o.ContainsKey("synthetic"))
{
    sets.Add(("syn-1024-rgb-1layer", DecodeCost.MakeCodestream(1024, 1024, 3, 0)));
    sets.Add(("syn-1024-rgb-8layer", DecodeCost.MakeCodestream(1024, 1024, 3, 7)));
    sets.Add(("syn-1024-rgb-19layer", DecodeCost.MakeCodestream(1024, 1024, 3, 18)));
    sets.Add(("syn-1024-rgba5-19layer", DecodeCost.MakeCodestream(1024, 1024, 5, 18)));
}

foreach (var (name, data) in sets)
{
    var h = DecodeCost.Peek(data);
    var cases = Enumerable.Range(0, Math.Min(maxLevel, h.DecompositionLevels) + 1).Select(l => (level: l, layers: 0))
        .Concat(layerLimits.Where(l => l < h.Layers).Select(l => (level: 0, layers: l)));
    foreach (var (level, layers) in cases)
    {
        try
        {
            var r = DecodeCost.Measure(data, DecodeCost.Config(h.DecompositionLevels, level, layers), check);
            string hash = r.Pixels is null ? "-" : DecodeCost.PixelHash(r.Pixels);
            Console.Out.WriteLine($"CASE\t{name}\t{h.Width}x{h.Height}\t{h.Components}\t{h.Layers}\t{h.DecompositionLevels}\t{level}\t{layers}\t{r.Width}x{r.Height}\t{r.AllocatedBytes}\t{r.PeakHeapBytes}\t{r.Milliseconds:F1}\t{hash}\t{r.CodeBlocks}\t{r.CodingPasses}\t{r.CodedBytes}");
        }
        catch (Exception e)
        {
            Console.Out.WriteLine($"CASE\t{name}\t{h.Width}x{h.Height}\t{h.Components}\t{h.Layers}\t{h.DecompositionLevels}\t{level}\t{layers}\tFAILED\t{e.GetType().Name}: {e.Message.Split('\n')[0]}");
        }
    }
}

// Local cache files may carry a short prefix before the codestream; start at the SOC and SIZ markers.
static byte[] FromSoc(byte[] d)
{
    for (int p = 0; p + 3 < d.Length && p < 64; p++)
        if (d[p] == 0xFF && d[p + 1] == 0x4F && d[p + 2] == 0xFF && d[p + 3] == 0x51) return p == 0 ? d : d[p..];
    return d;
}
