// Copyright (c) 2026 Legion Builds
// Licensed under the BSD 3-Clause License.

using System.Linq;
using Xunit;

namespace CoreJ2K.Tests
{
    /// <summary>
    /// What a decode allocates when it returns less than the whole picture: a reduced-resolution decode (a thumbnail,
    /// a texture's lower discard level) should allocate for the picture it returns, not for the full picture.
    /// Allocation is counted in bytes on the decoding thread (<see cref="System.GC.GetAllocatedBytesForCurrentThread"/>),
    /// which is deterministic, so nothing here depends on timing. These decodes run on the calling thread
    /// (<c>MaxDegreeOfParallelism = 1</c>), so the class may run in parallel with the rest of the suite.
    /// </summary>
    public class ReducedDecodeAllocationTests
    {
        /// <summary>
        /// The fixed allowance on top of 1.5 x the returned picture's own buffers: the codestream's header tables, the
        /// packet-header bookkeeping (kept for every code-block of the picture, because a reduced decode still reads the
        /// packet headers of the resolutions it skips), the compressed code-block copies and the entropy decoder's
        /// per-block state. It grows with the full picture's code-block count, not with the level.
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
    }
}
