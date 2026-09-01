namespace libbgcode.NET;

/// <summary>
/// The block types of the binary G-code container, with the specification's wire values.
/// </summary>
/// <remarks>
/// The specification also fixes their order in a file: file metadata (optional), printer metadata,
/// thumbnails (optional), print metadata, slicer metadata, then the G-code blocks. A reader looking
/// for one early block can therefore stop as soon as a later type appears. Slicer metadata may
/// appear twice: PrusaSlicer 3 writes a legacy INI block and a JSON block back to back,
/// distinguished by their encoding parameter.
/// </remarks>
public enum BgcodeBlockType
{
    /// <summary>Generic key-value metadata about the file itself, such as its producer.</summary>
    FileMetadata = 0,

    /// <summary>The G-code itself. A file may carry several of these, and they come last.</summary>
    GCode = 1,

    /// <summary>Key-value metadata produced and consumed by the slicer.</summary>
    SlicerMetadata = 2,

    /// <summary>Key-value metadata consumed by the printer: model, nozzle diameter and the like.</summary>
    PrinterMetadata = 3,

    /// <summary>Key-value metadata about the print: estimated time, filament consumed.</summary>
    PrintMetadata = 4,

    /// <summary>One embedded preview image; each thumbnail gets its own block.</summary>
    Thumbnail = 5,
}
