namespace libbgcode.NET;

/// <summary>
/// The per-block compression algorithms, with the specification's wire values.
/// </summary>
public enum BgcodeCompression
{
    /// <summary>Uncompressed; the block header carries no compressed size and is 8 bytes.</summary>
    None = 0,

    /// <summary>
    /// Deflate. The payload is zlib-wrapped rather than raw deflate - a detail the specification
    /// does not state, established from real PrusaSlicer output.
    /// </summary>
    Deflate = 1,

    /// <summary>Heatshrink with window size 11 and lookahead size 4.</summary>
    Heatshrink11 = 2,

    /// <summary>Heatshrink with window size 12 and lookahead size 4.</summary>
    Heatshrink12 = 3,
}
