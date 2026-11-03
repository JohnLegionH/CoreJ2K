// Copyright (c) 2026 Legion Builds

using System.Runtime.CompilerServices;

// Expose internals to test assemblies
[assembly: InternalsVisibleTo("CoreJ2K.Tests")]
[assembly: InternalsVisibleTo("CoreJ2K.DecodeCost")]
[assembly: InternalsVisibleTo("CoreJ2K.Windows.Tests")]
[assembly: InternalsVisibleTo("CoreJ2K.Skia.Tests")]