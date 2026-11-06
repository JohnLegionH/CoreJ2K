// Copyright (c) 2026 Legion Builds
// Licensed under the BSD 3-Clause License.

using System;
using System.Diagnostics;
using CoreJ2K;
using CoreJ2K.Configuration;
using CoreJ2K.Util;

namespace CoreJ2K.Tests
{
    /// <summary>What a codestream's main header declares (SIZ and the first COD).</summary>
    public readonly record struct J2kPeek(int Width, int Height, int Components, int Layers, int DecompositionLevels);

    /// <summary>What one decode cost.</summary>
    public readonly record struct DecodeCostResult(
        int Width, int Height, long AllocatedBytes, long PeakHeapBytes, double Milliseconds, byte[]? Pixels);

    /// <summary>
    /// Helpers for tests and the cost probe: read a codestream's declared size and layers, make deterministic
    /// codestreams with the library's own encoder, and measure a decode in allocated bytes (deterministic), peak
    /// managed heap (sampled) and time.
    /// </summary>
    public static class DecodeCost
    {
        /// <summary>Reads SIZ and the first COD marker of a raw codestream (or the codestream inside a JP2 file).</summary>
        public static J2kPeek Peek(byte[] data)
        {
            int p = 0;
            while (p + 3 < data.Length && !(data[p] == 0xFF && data[p + 1] == 0x4F && data[p + 2] == 0xFF && data[p + 3] == 0x51)) p++;
            int w = 0, h = 0, nc = 0, layers = 0, dl = 0;
            for (int i = p + 2; i + 4 < data.Length;)
            {
                if (data[i] != 0xFF) break;
                int marker = data[i + 1], len = (data[i + 2] << 8) | data[i + 3];
                if (marker == 0x51)
                {
                    w = BE32(data, i + 6) - BE32(data, i + 14); h = BE32(data, i + 10) - BE32(data, i + 18);
                    nc = (data[i + 38] << 8) | data[i + 39];
                }
                else if (marker == 0x52)
                {
                    layers = (data[i + 6] << 8) | data[i + 7];
                    dl = data[i + 9];
                    break;
                }
                i += 2 + len;
            }
            return new J2kPeek(w, h, nc, layers, dl);
        }

        /// <summary>The first 8 bytes of the pixels' SHA-256, in hex.</summary>
        public static string PixelHash(byte[] pixels) =>
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(pixels))[..16];

        private static int BE32(byte[] d, int i) => (d[i] << 24) | (d[i + 1] << 16) | (d[i + 2] << 8) | d[i + 3];

        /// <summary>
        /// A deterministic 8-bit test codestream: smooth gradients plus pseudo-random detail, so every resolution level
        /// has content. <paramref name="extraLayers"/> adds that many unoptimised layers after the first
        /// (the layer count declared is then <c>1 + extraLayers</c>); a negative value leaves the encoder's own default layers and rate.
        /// <paramref name="tileSize"/> above 0 cuts the picture into square tiles of that size.
        /// </summary>
        public static byte[] MakeCodestream(int width, int height, int components, int extraLayers, int seed = 1, int tileSize = 0)
        {
            var data = new int[components][];
            var rng = new Random(seed);
            for (int c = 0; c < components; c++)
            {
                data[c] = new int[width * height];
                for (int y = 0; y < height; y++)
                    for (int x = 0; x < width; x++)
                    {
                        int v = ((x * (3 + c)) / 5 + (y * (2 + c)) / 3 + (int)(64 * Math.Sin((x + c * 17) / 37.0) * Math.Cos(y / 29.0))) & 0xFF;
                        v = (v + rng.Next(0, 24)) & 0xFF;
                        data[c][y * width + x] = v - 128;
                    }
            }
            var src = new InterleavedImageSource(width, height, components, 8, new bool[components], data);
            var pl = J2kImage.GetDefaultEncoderParameterList();
            pl["file_format"] = "off";
            if (tileSize > 0) pl["tiles"] = $"{tileSize} {tileSize}";   // a raw codestream, as the grid's textures are
            if (extraLayers >= 0)
            {
                pl["rate"] = "2.0";
                pl["Alayers"] = extraLayers > 0 ? $"0.05 +{extraLayers}" : "0.05";
            }
            return J2kImage.ToBytes(src, null, pl)!;
        }

        /// <summary>The decoder configuration for a discard level (0 = full resolution) and an optional layer limit (0 = none).</summary>
        public static J2KDecoderConfiguration Config(int decompositionLevels, int level, int maxLayers = 0)
        {
            // One thread: the decode then runs on the calling thread, so its allocation is counted on this thread.
            var cfg = new J2KDecoderConfiguration().WithMaxDegreeOfParallelism(1);
            if (level > 0) cfg = cfg.WithResolutionLevel(decompositionLevels - level);
            if (maxLayers > 0) cfg = cfg.WithQuitConditions(q => q.WithMaxLayers(maxLayers));
            return cfg;
        }

        /// <summary>
        /// Decodes <paramref name="data"/> once to warm every static (pools, tables), then again measuring the bytes
        /// allocated by this thread (<see cref="GC.GetAllocatedBytesForCurrentThread"/>; the decoder runs on the calling thread, and other tests' threads do not count), the managed heap's high-water mark
        /// (sampled every millisecond from a second thread, so it is a lower bound) and the time of the second decode.
        /// </summary>
        public static DecodeCostResult Measure(byte[] data, J2KDecoderConfiguration cfg, bool keepPixels = false)
        {
            using (var warm = J2kImage.FromBytes(data, cfg)) { }
            GC.Collect(2, GCCollectionMode.Forced, true);
            GC.WaitForPendingFinalizers();
            long baseHeap = GC.GetTotalMemory(true);
            long peak = baseHeap;
            using var stop = new System.Threading.ManualResetEventSlim(false);
            var sampler = new System.Threading.Thread(() =>
            {
                while (!stop.IsSet)
                {
                    long m = GC.GetTotalMemory(false);
                    if (m > peak) peak = m;
                    System.Threading.Thread.Sleep(1);
                }
            }) { IsBackground = true };
            sampler.Start();
            long before = GC.GetAllocatedBytesForCurrentThread();
            var sw = Stopwatch.StartNew();
            using var img = J2kImage.FromBytes(data, cfg);
            sw.Stop();
            long alloc = GC.GetAllocatedBytesForCurrentThread() - before;
            long m2 = GC.GetTotalMemory(false);
            if (m2 > peak) peak = m2;
            stop.Set();
            sampler.Join();
            byte[]? px = null;
            if (keepPixels)
            {
                var parts = new System.Collections.Generic.List<byte>();
                for (int c = 0; c < img.NumberOfComponents; c++) parts.AddRange(img.GetComponentBytes(c));
                px = parts.ToArray();
            }
            return new DecodeCostResult(img.Width, img.Height, alloc, Math.Max(0, peak - baseHeap), sw.Elapsed.TotalMilliseconds, px);
        }
    }
}
