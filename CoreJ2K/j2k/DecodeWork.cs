// Copyright (c) 2026 Legion Builds
// Licensed under the BSD 3-Clause License.

using System;

namespace CoreJ2K.j2k
{
    /// <summary>
    /// Counts the entropy-decoding work done on the current thread: code-blocks that carried data, their coding
    /// passes and their compressed bytes. The counts are deterministic for a given codestream and configuration,
    /// which makes "this option saves work" testable without timing anything. The decoder is single-threaded per
    /// decode, so thread-local counters are the decode's own; reset them before the decode being measured.
    /// </summary>
    internal static class DecodeWork
    {
        [ThreadStatic] private static long s_codeBlocks;
        [ThreadStatic] private static long s_codingPasses;
        [ThreadStatic] private static long s_codedBytes;

        /// <summary>Code-blocks entropy-decoded on this thread since the last <see cref="Reset"/>.</summary>
        public static long CodeBlocks => s_codeBlocks;

        /// <summary>Coding passes decoded on this thread since the last <see cref="Reset"/>.</summary>
        public static long CodingPasses => s_codingPasses;

        /// <summary>Compressed bytes of the code-blocks decoded on this thread since the last <see cref="Reset"/>.</summary>
        public static long CodedBytes => s_codedBytes;

        public static void Reset() { s_codeBlocks = 0; s_codingPasses = 0; s_codedBytes = 0; }

        internal static void Add(int passes, int bytes)
        {
            s_codeBlocks++;
            s_codingPasses += passes;
            s_codedBytes += bytes;
        }
    }
}
