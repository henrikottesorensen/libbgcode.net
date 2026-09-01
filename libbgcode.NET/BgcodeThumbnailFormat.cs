namespace libbgcode.NET;

/// <summary>
/// The image format of a thumbnail block, with the specification's wire values.
/// </summary>
public enum BgcodeThumbnailFormat
{
    /// <summary>PNG.</summary>
    Png = 0,

    /// <summary>JPEG.</summary>
    Jpg = 1,

    /// <summary>QOI, the "Quite OK Image" format.</summary>
    Qoi = 2,
}
