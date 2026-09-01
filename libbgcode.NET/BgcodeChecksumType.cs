namespace libbgcode.NET;

/// <summary>
/// The checksum algorithm a file declares in its header, with the specification's wire values.
/// </summary>
/// <remarks>
/// The algorithm decides how many bytes follow every block, so an unknown value is not a skippable
/// curiosity: every block offset after the first would be computed wrongly. <see cref="BgcodeReader.Open"/>
/// refuses a file declaring one.
/// </remarks>
public enum BgcodeChecksumType
{
    /// <summary>No checksum; blocks are followed by nothing.</summary>
    None = 0,

    /// <summary>
    /// CRC-32 (the zlib polynomial), four bytes little-endian after each block, computed over the
    /// block header, parameters and data as stored.
    /// </summary>
    /// <remarks>
    /// What the checksum covers is not in the published specification; it is established from real
    /// PrusaSlicer output, where only the header-through-data span reproduces the stored values.
    /// </remarks>
    Crc32 = 1,
}
