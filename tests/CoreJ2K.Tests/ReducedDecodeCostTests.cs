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
        /// packet and code-block bookkeeping, the compressed code-block copies and the entropy decoder's per-block state.
        /// None of these grow with the picture's pixel count at the sizes used here (1024 x 1024 at most, 20 layers).
        /// </summary>
        public const long FixedOverheadBytes = 1 << 20;

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
            for (int l = 1; l < 4; l++)
                Assert.True(bytes[l] * 2 < bytes[l - 1], $"level {l} allocated {bytes[l]}, level {l - 1} {bytes[l - 1]}");
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
