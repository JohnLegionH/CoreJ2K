// Copyright (c) 2026 Legion Builds
// Licensed under the BSD 3-Clause License.

using System;
using System.IO;
using System.Linq;
using CoreJ2K.Tests;

// Decode-cost probe. Usage:
//   CoreJ2K.DecodeCost [--dir DIR] [--synthetic] [--levels N] [--layers a,b,c] [--check] [--repeat]
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
    sets.Add(("syn-1024-rgb-1layer-tiles512", DecodeCost.MakeCodestream(1024, 1024, 3, 0, tileSize: 512)));
    sets.Add(("syn-1024-rgb-19layer-tiles512", DecodeCost.MakeCodestream(1024, 1024, 3, 18, tileSize: 512)));
    sets.Add(("syn-1024-rgb-default", DecodeCost.MakeCodestream(1024, 1024, 3, -1)));
    sets.Add(("syn-1024-rgba-default", DecodeCost.MakeCodestream(1024, 1024, 4, -1)));
    if (o.ContainsKey("big"))
    {
        sets.Add(("syn-2048-rgb-19layer", DecodeCost.MakeCodestream(2048, 2048, 3, 18)));
        sets.Add(("syn-512-rgb-19layer", DecodeCost.MakeCodestream(512, 512, 3, 18)));
        sets.Add(("syn-4096-rgb-3layer", DecodeCost.MakeCodestream(4096, 4096, 3, 2)));
    }
    sets.Add(("syn-1024-rgba5-19layer", DecodeCost.MakeCodestream(1024, 1024, 5, 18)));
}

if (o.ContainsKey("close"))
{
    // Correctness of the reduced levels: the mean absolute difference, per component, between a level and the block
    // average of the full-resolution decode (a low-pass band differs from the block average by a little everywhere; a
    // wrong or missing tile is tens). One line per file and level: CLOSE name WxH layers level mean_abs_diff
    double worst = 0;
    foreach (var (name, data) in sets)
    {
        var h = DecodeCost.Peek(data);
        var full = DecodeCost.Measure(data, DecodeCost.Config(h.DecompositionLevels, 0), true).Pixels!;
        for (int level = 1; level <= Math.Min(maxLevel, h.DecompositionLevels); level++)
        {
            var r = DecodeCost.Measure(data, DecodeCost.Config(h.DecompositionLevels, level), true);
            int block = 1 << level, w = r.Width, hh = r.Height, comps = h.Components;
            double sum = 0; long n = 0;
            for (int c = 0; c < comps; c++)
                for (int y = 0; y < hh; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int avg = 0, cnt = 0;
                        for (int j = 0; j < block && y * block + j < h.Height; j++)
                            for (int i = 0; i < block && x * block + i < h.Width; i++) { avg += full[c * h.Width * h.Height + (y * block + j) * h.Width + x * block + i]; cnt++; }
                        sum += Math.Abs(avg / Math.Max(1, cnt) - r.Pixels![c * w * hh + y * w + x]); n++;
                    }
            double d = sum / n;
            worst = Math.Max(worst, d);
            Console.Out.WriteLine($"CLOSE\t{name}\t{h.Width}x{h.Height}\t{h.Layers}\t{level}\t{d:F2}");
        }
    }
    Console.Out.WriteLine($"CLOSE-SUMMARY\tworst {worst:F2}");
    return;
}

if (o.ContainsKey("stress"))
{
    // Decodes of the same files at reduced levels, 8 threads at once, each result compared with a single-threaded
    // reference. One line: STRESS decodes N different D
    int threads = 8, rounds = 4, n = 0, bad = 0;
    var jobs = sets.SelectMany(s => Enumerable.Range(1, Math.Min(maxLevel, DecodeCost.Peek(s.Data).DecompositionLevels))
        .Select(l => (s.Name, s.Data, Level: l))).ToList();
    string Hash(byte[] d, int dl, int l)
    {
        using var img = CoreJ2K.J2kImage.FromBytes(d, DecodeCost.Config(dl, l));
        var parts = new List<byte>();
        for (int c = 0; c < img.NumberOfComponents; c++) parts.AddRange(img.GetComponentBytes(c));
        return DecodeCost.PixelHash(parts.ToArray());
    }
    var reference = jobs.ToDictionary(j => (j.Name, j.Level), j => Hash(j.Data, DecodeCost.Peek(j.Data).DecompositionLevels, j.Level));
    var lockObj = new object();
    Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, t =>
    {
        for (int r = 0; r < rounds; r++)
            foreach (var j in jobs.Skip(t).Concat(jobs.Take(t)))
            {
                var h = Hash(j.Data, DecodeCost.Peek(j.Data).DecompositionLevels, j.Level);
                lock (lockObj) { n++; if (h != reference[(j.Name, j.Level)]) { bad++; Console.Out.WriteLine($"STRESS-DIFF	{j.Name}	level {j.Level}"); } }
            }
    });
    Console.Out.WriteLine($"STRESS	decodes {n}	different {bad}");
    return;
}

if (o.ContainsKey("repeat"))
{
    // Repeatability: each reduced level decoded, then again after a full-resolution decode of another file has
    // used (and left dirty) the library's reusable buffers. One line per file and level:
    //   REPEAT name WxH layers level same|DIFFERENT
    var dirty = sets.Count > 1 ? sets : new() { ("dirt", DecodeCost.MakeCodestream(1024, 1024, 3, 0, seed: 5)) };
    int di = 0, differ = 0, total = 0;
    foreach (var (name, data) in sets)
    {
        var h = DecodeCost.Peek(data);
        for (int level = 1; level <= Math.Min(maxLevel, h.DecompositionLevels); level++)
        {
            var cfg = DecodeCost.Config(h.DecompositionLevels, level);
            var a = DecodeCost.Measure(data, cfg, true).Pixels!;
            var (_, other) = dirty[di++ % dirty.Count];
            DecodeCost.Measure(other, DecodeCost.Config(DecodeCost.Peek(other).DecompositionLevels, 0));
            var b = DecodeCost.Measure(data, cfg, true).Pixels!;
            total++;
            bool same = a.AsSpan().SequenceEqual(b);
            if (!same) differ++;
            Console.Out.WriteLine($"REPEAT	{name}	{h.Width}x{h.Height}	{h.Layers}	{level}	{(same ? "same" : "DIFFERENT")}");
        }
    }
    Console.Out.WriteLine($"REPEAT-SUMMARY	cases {total}	different {differ}");
    return;
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
