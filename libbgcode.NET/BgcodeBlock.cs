namespace libbgcode.NET;

/// <summary>
/// One block of a binary G-code file: its header, its parameters, and where its payload sits in
/// the stream.
/// </summary>
/// <remarks>
/// A descriptor rather than the data - the payload is read on demand through
/// <see cref="BgcodeReader.ReadData"/> or <see cref="BgcodeReader.ReadText"/>, so a caller can walk
/// a file's structure without paying for the blocks it does not want.
/// </remarks>
public sealed class BgcodeBlock
{
    internal BgcodeBlock(BgcodeBlockType type,
                         BgcodeCompression compression,
                         uint uncompressedSize,
                         uint dataSize,
                         ushort rawEncoding,
                         BgcodeThumbnailParameters? thumbnail,
                         long headerPosition,
                         long dataPosition)
    {
        Type = type;
        Compression = compression;
        UncompressedSize = uncompressedSize;
        DataSize = dataSize;
        RawEncoding = rawEncoding;
        Thumbnail = thumbnail;
        HeaderPosition = headerPosition;
        DataPosition = dataPosition;
    }

    /// <summary>The block type. An unknown wire value never reaches here - the walk refuses it.</summary>
    public BgcodeBlockType Type { get; }

    /// <summary>
    /// The compression algorithm, as declared. An unknown wire value survives the cast, so the walk
    /// can still step over a block it could not decompress.
    /// </summary>
    public BgcodeCompression Compression { get; }

    /// <summary>The declared size of the payload once decompressed.</summary>
    public uint UncompressedSize { get; }

    /// <summary>
    /// The payload's size as stored: the compressed size, or <see cref="UncompressedSize"/> for an
    /// uncompressed block.
    /// </summary>
    public uint DataSize { get; }

    /// <summary>
    /// The block's first parameter word, whose meaning depends on <see cref="Type"/>. The typed
    /// views are <see cref="MetadataEncoding"/>, <see cref="GCodeEncoding"/> and <see cref="Thumbnail"/>.
    /// </summary>
    public ushort RawEncoding { get; }

    /// <summary>Thumbnail parameters, for a thumbnail block; null for every other type.</summary>
    public BgcodeThumbnailParameters? Thumbnail { get; }

    /// <summary>Where the block header starts in the stream. This is where a checksum starts covering.</summary>
    public long HeaderPosition { get; }

    /// <summary>Where the payload starts in the stream, past the header and parameters.</summary>
    public long DataPosition { get; }

    /// <summary>The payload encoding, for the four key-value metadata block types; null otherwise.</summary>
    public BgcodeMetadataEncoding? MetadataEncoding =>
        Type is BgcodeBlockType.FileMetadata
             or BgcodeBlockType.PrinterMetadata
             or BgcodeBlockType.PrintMetadata
             or BgcodeBlockType.SlicerMetadata
            ? (BgcodeMetadataEncoding)RawEncoding
            : null;

    /// <summary>The payload encoding, for a G-code block; null for every other type.</summary>
    public BgcodeGCodeEncoding? GCodeEncoding =>
        Type == BgcodeBlockType.GCode ? (BgcodeGCodeEncoding)RawEncoding : null;
}
