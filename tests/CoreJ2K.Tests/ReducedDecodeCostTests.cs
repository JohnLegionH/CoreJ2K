// Copyright (c) 2026 Legion Builds
// Licensed under the BSD 3-Clause License.

using System;
using System.Linq;
using System.Security.Cryptography;
using Xunit;

namespace CoreJ2K.Tests
{
    /// <summary>
    /// What a decode costs when it returns less than the whole picture: a reduced-resolution decode (a thumbnail, a
    /// texture's lower discard level) must allocate for the picture it returns, and a quality-layer limit must do the
    /// work of the layers it keeps. Allocation is counted in bytes on the decoding thread and work in code-blocks, coding
    /// passes and compressed bytes entropy-decoded; both are deterministic, so nothing here depends on timing. The decoder
    /// runs on the calling thread, so the classes may run in parallel with the rest of the suite.
    /// </summary>
    public class ReducedDecodeCostTests
    {
        /// <summary>
        /// The fixed allowance on top of 1.5 x the returned picture's own buffers: the codestream's header tables, the
        /// packet-header bookkeeping (about 1 KiB per code-block at 30 layers and 5 components, kept for every code-block of
        /// the picture because a reduced decode still reads the packet headers of the resolutions it skips), the
        /// compressed code-block copies and the entropy decoder's per-block state. Measured at 1024 x 1024: at most
        /// 1.9 MiB over 1.5 x the picture, for the 5-component 30-layer bakes at level 3. It grows with the full picture's
        /// code-block count, not with the level; for pictures above 1024 x 1024 it is larger (2048 x 2048: about 4 MiB).
        /// </summary>
        public const long FixedOverheadBytes = 2 << 20;

        /// <summary>The reduced picture's own buffers: one 4-byte sample per pixel per component.</summary>
        private static long PictureBytes(int width, int height, int components, int level)
        {
            long w = width, h = height;
            for (int i = 0; i < level; i++) { w = (w + 1) / 2; h = (h + 1) / 2; }
            return w * h * components * 4;
        }

        public static TheoryData<int, int, int> Cases()
        {
            var d = new TheoryData<int, int, int>();
            foreach (var (comps, extra) in new[] { (3, 0), (3, 7), (3, 18), (5, 18) })
                for (int level = 0; level <= 3; level++)
                    d.Add(comps, extra, level);
            return d;
        }

        [Theory]
        [MemberData(nameof(Cases))]
        public void A_decode_at_a_level_allocates_no_more_than_1_5_times_the_picture_it_returns_plus_a_fixed_overhead(int components, int extraLayers, int level)
        {
            var data = DecodeCost.MakeCodestream(1024, 1024, components, extraLayers);
            var peek = DecodeCost.Peek(data);
            var r = DecodeCost.Measure(data, DecodeCost.Config(peek.DecompositionLevels, level));
            Assert.Equal((1024 >> level, 1024 >> level), (r.Width, r.Height));
            long picture = PictureBytes(1024, 1024, components, level);
            long budget = picture * 3 / 2 + FixedOverheadBytes;
            Assert.True(r.AllocatedBytes <= budget,
                $"level {level}, {components} components, {peek.Layers} layers: allocated {r.AllocatedBytes} bytes; the returned picture's buffers are {picture}, so the budget is {budget}");
        }

        [Fact]
        public void A_lower_level_allocates_less_than_the_level_above_it()
        {
            var data = DecodeCost.MakeCodestream(1024, 1024, 3, 7);
            var dl = DecodeCost.Peek(data).DecompositionLevels;
            var bytes = Enumerable.Range(0, 4).Select(l => DecodeCost.Measure(data, DecodeCost.Config(dl, l)).AllocatedBytes).ToArray();
            Assert.True(bytes[1] * 2 < bytes[0], $"level 1 allocated {bytes[1]}, level 0 {bytes[0]}");
            for (int l = 1; l < 4; l++)
                Assert.True(bytes[l] < bytes[l - 1], $"level {l} allocated {bytes[l]}, level {l - 1} {bytes[l - 1]}");
        }

        [Fact]
        public void A_layer_limit_equal_to_the_declared_layer_count_decodes_everything()
        {
            var data = DecodeCost.MakeCodestream(512, 512, 3, 7);
            var peek = DecodeCost.Peek(data);
            var all = DecodeCost.Measure(data, DecodeCost.Config(peek.DecompositionLevels, 0), keepPixels: true);
            var limited = DecodeCost.Measure(data, DecodeCost.Config(peek.DecompositionLevels, 0, peek.Layers), keepPixels: true);
            Assert.Equal((all.CodingPasses, all.CodedBytes), (limited.CodingPasses, limited.CodedBytes));
            Assert.Equal(all.Pixels, limited.Pixels);
        }

        [Fact]
        public void A_layer_limit_of_n_decodes_the_first_n_layers_and_does_less_work_the_fewer_it_keeps()
        {
            var data = DecodeCost.MakeCodestream(512, 512, 3, 18);
            var peek = DecodeCost.Peek(data);
            var work = new[] { 1, 2, 4, 8, 12, peek.Layers }
                .Select(n => (n, m: DecodeCost.Measure(data, DecodeCost.Config(peek.DecompositionLevels, 0, n))))
                .ToArray();
            Assert.All(work, w => Assert.True(w.m.CodingPasses > 0 && w.m.CodedBytes > 0, $"a limit of {w.n} layers decoded nothing"));
            for (int i = 1; i < work.Length; i++)
            {
                Assert.True(work[i].m.CodedBytes > work[i - 1].m.CodedBytes,
                    $"{work[i].n} layers decoded {work[i].m.CodedBytes} compressed bytes, {work[i - 1].n} layers {work[i - 1].m.CodedBytes}");
                Assert.True(work[i].m.CodingPasses > work[i - 1].m.CodingPasses,
                    $"{work[i].n} layers decoded {work[i].m.CodingPasses} passes, {work[i - 1].n} layers {work[i - 1].m.CodingPasses}");
            }
        }

        [Fact]
        public void A_layer_limit_and_a_reduced_level_together_do_the_work_of_both()
        {
            var data = DecodeCost.MakeCodestream(1024, 1024, 3, 18);
            var peek = DecodeCost.Peek(data);
            var level0 = DecodeCost.Measure(data, DecodeCost.Config(peek.DecompositionLevels, 0));
            var level2 = DecodeCost.Measure(data, DecodeCost.Config(peek.DecompositionLevels, 2));
            var level2Limited = DecodeCost.Measure(data, DecodeCost.Config(peek.DecompositionLevels, 2, 4));
            Assert.True(level2.CodedBytes < level0.CodedBytes);
            Assert.True(level2Limited.CodedBytes < level2.CodedBytes, $"{level2Limited.CodedBytes} vs {level2.CodedBytes}");
            Assert.True(level2Limited.AllocatedBytes <= PictureBytes(1024, 1024, 3, 2) * 3 / 2 + FixedOverheadBytes);
        }

        /// <summary>
        /// A decode must not depend on what earlier decodes left in the buffers the library reuses: the same reduced
        /// decode, run again after full-resolution decodes of an unrelated picture, gives the same pixels. Many thin
        /// layers leave code-blocks without data at the low levels, whose area nothing writes.
        /// </summary>
        [Theory]
        [InlineData(0, 1)]
        [InlineData(0, 2)]
        [InlineData(7, 1)]
        [InlineData(18, 1)]
        [InlineData(18, 2)]
        [InlineData(18, 3)]
        public void A_reduced_decode_gives_the_same_pixels_after_other_decodes_dirtied_the_buffers(int extraLayers, int level)
        {
            var data = DecodeCost.MakeCodestream(512, 512, 3, extraLayers);
            var noise = DecodeCost.MakeCodestream(512, 512, 3, 0, seed: 99);
            var cfg = DecodeCost.Config(DecodeCost.Peek(data).DecompositionLevels, level);
            var first = DecodeCost.Measure(data, cfg, keepPixels: true).Pixels;
            for (int i = 0; i < 3; i++)
            {
                DecodeCost.Measure(noise, DecodeCost.Config(DecodeCost.Peek(noise).DecompositionLevels, 0));
                Assert.Equal(first, DecodeCost.Measure(data, cfg, keepPixels: true).Pixels);
            }
        }

        /// <summary>
        /// A picture cut into tiles decodes at a reduced level as the picture it is: the same pixels every time, even
        /// after other decodes dirtied the reusable buffers, and every tile in place. (Each tile used to be sized
        /// and placed as at full resolution, so the tiles after the first fell outside the reduced picture and its pixels
        /// there were whatever the output buffer held; the layer count only changed how often that showed.)
        /// </summary>
        /// <summary>
        /// A reduced preview is often made from the start of a codestream (a partial fetch). The code-blocks the start
        /// lacks write nothing, so what shows there must not be what an earlier decode left in the reused buffers.
        /// </summary>
        [Theory]
        [InlineData(0, 0, 2, 3)]
        [InlineData(18, 0, 2, 3)]
        [InlineData(18, 512, 1, 3)]
        [InlineData(18, 512, 3, 3)]
        [InlineData(0, 0, 3, 40)]
        [InlineData(18, 0, 3, 40)]
        [InlineData(18, 0, 3, 150)]
        [InlineData(18, 0, 2, 100)]
        [InlineData(0, 0, 3, 100)]
        public void A_truncated_codestream_decodes_the_same_at_a_reduced_level_every_time(int extraLayers, int tileSize, int level, int keepOneIn)
        {
            var whole = DecodeCost.MakeCodestream(1024, 1024, 3, extraLayers, tileSize: tileSize);
            var data = whole[..(whole.Length / keepOneIn)];
            var noise = DecodeCost.MakeCodestream(512, 512, 3, 0, seed: 99);
            var cfg = DecodeCost.Config(DecodeCost.Peek(whole).DecompositionLevels, level);
            var first = DecodeCost.Measure(data, cfg, keepPixels: true).Pixels;
            for (int i = 0; i < 3; i++)
            {
                DecodeCost.Measure(noise, DecodeCost.Config(DecodeCost.Peek(noise).DecompositionLevels, 0));
                Assert.Equal(first, DecodeCost.Measure(data, cfg, keepPixels: true).Pixels);
            }
        }

        [Theory]
        [InlineData(0, 1)]
        [InlineData(0, 2)]
        [InlineData(0, 3)]
        [InlineData(18, 1)]
        [InlineData(18, 2)]
        [InlineData(18, 3)]
        public void A_tiled_picture_decodes_the_same_at_a_reduced_level_every_time(int extraLayers, int level)
        {
            var data = DecodeCost.MakeCodestream(1024, 1024, 3, extraLayers, tileSize: 512);
            var noise = DecodeCost.MakeCodestream(512, 512, 3, 0, seed: 99);
            var cfg = DecodeCost.Config(DecodeCost.Peek(data).DecompositionLevels, level);
            var first = DecodeCost.Measure(data, cfg, keepPixels: true).Pixels;
            for (int i = 0; i < 3; i++)
            {
                DecodeCost.Measure(noise, DecodeCost.Config(DecodeCost.Peek(noise).DecompositionLevels, 0));
                Assert.Equal(first, DecodeCost.Measure(data, cfg, keepPixels: true).Pixels);
            }
        }

        [Theory]
        [InlineData(0, 1)]
        [InlineData(0, 2)]
        [InlineData(0, 3)]
        [InlineData(18, 1)]
        [InlineData(18, 3)]
        public void A_tiled_picture_at_a_reduced_level_is_the_full_picture_scaled_down_in_every_tile(int extraLayers, int level)
        {
            var data = DecodeCost.MakeCodestream(1024, 1024, 3, extraLayers, tileSize: 512);
            var dl = DecodeCost.Peek(data).DecompositionLevels;
            var full = DecodeCost.Measure(data, DecodeCost.Config(dl, 0), keepPixels: true).Pixels!;
            var reduced = DecodeCost.Measure(data, DecodeCost.Config(dl, level), keepPixels: true).Pixels!;
            int size = 1024 >> level;
            Assert.Equal(3 * size * size, reduced.Length);
            // The same picture in one tile is the yardstick: a reduced level is the low-pass band, not the block average,
            // so the two differ by a little everywhere. A missing or misplaced tile is a difference of tens.
            var whole = DecodeCost.MakeCodestream(1024, 1024, 3, extraLayers);
            var wholeFull = DecodeCost.Measure(whole, DecodeCost.Config(dl, 0), keepPixels: true).Pixels!;
            var wholeReduced = DecodeCost.Measure(whole, DecodeCost.Config(dl, level), keepPixels: true).Pixels!;
            for (int c = 0; c < 3; c++)
                for (int qy = 0; qy < 2; qy++)
                    for (int qx = 0; qx < 2; qx++)
                    {
                        double tiled = QuadrantMeanDifference(full, reduced, c, qx, qy, level);
                        double control = QuadrantMeanDifference(wholeFull, wholeReduced, c, qx, qy, level);
                        Assert.True(tiled < control + 3,
                            $"level {level}, component {c}, tile ({qx},{qy}): mean difference {tiled:F1} from the full picture scaled down, the same picture in one tile {control:F1}");
                    }
        }

        /// <summary>The mean absolute difference, in one quadrant of one component, between a reduced picture and the block
        /// average of the full picture (both as the planes of a 1024 x 1024 picture, level <paramref name="level"/>).</summary>
        private static double QuadrantMeanDifference(byte[] full, byte[] reduced, int c, int qx, int qy, int level)
        {
            int size = 1024 >> level, block = 1 << level, half = size / 2;
            double sum = 0;
            for (int y = qy * half; y < (qy + 1) * half; y++)
                for (int x = qx * half; x < (qx + 1) * half; x++)
                {
                    int avg = 0;
                    for (int j = 0; j < block; j++)
                        for (int i = 0; i < block; i++)
                            avg += full[c * 1024 * 1024 + (y * block + j) * 1024 + x * block + i];
                    sum += Math.Abs(avg / (block * block) - reduced[c * size * size + y * size + x]);
                }
            return sum / (half * half);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        public void A_tiled_picture_at_a_level_allocates_for_the_picture_it_returns(int level)
        {
            var data = DecodeCost.MakeCodestream(1024, 1024, 3, 18, tileSize: 512);
            var r = DecodeCost.Measure(data, DecodeCost.Config(DecodeCost.Peek(data).DecompositionLevels, level));
            long budget = PictureBytes(1024, 1024, 3, level) * 3 / 2 + FixedOverheadBytes;
            Assert.True(r.AllocatedBytes <= budget, $"level {level}: allocated {r.AllocatedBytes} bytes, budget {budget}");
        }

        /// <summary>
        /// Pixels the decoder gave before the allocation work (CoreJ2K 2.3.3.91, the commit it was built from), on the
        /// codestreams <see cref="DecodeCost.MakeCodestream"/> makes. Level 0 for every layer count; the reduced levels
        /// only for the few-layer stream, whose reduced decode was repeatable.
        /// </summary>
        [Theory]
        [InlineData(3, 0, 0, "2847E738FE34C393")]
        [InlineData(3, 0, 1, "47E09160DBEFC6F9")]
        [InlineData(3, 0, 2, "1188119354CAF72B")]
        [InlineData(3, 0, 3, "00A91E4C9B1BFAC1")]
        [InlineData(3, 7, 0, "7CFA303C1DC3CCBE")]
        [InlineData(3, 18, 0, "55AE6A5A73407067")]
        [InlineData(5, 18, 0, "2A237658F678C46F")]
        public void Pixels_are_the_same_as_before(int components, int extraLayers, int level, string sha256Prefix)
        {
            var data = DecodeCost.MakeCodestream(1024, 1024, components, extraLayers);
            var r = DecodeCost.Measure(data, DecodeCost.Config(DecodeCost.Peek(data).DecompositionLevels, level), keepPixels: true);
            Assert.Equal(sha256Prefix, DecodeCost.PixelHash(r.Pixels!));
        }
    }
}
