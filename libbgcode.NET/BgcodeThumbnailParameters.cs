namespace libbgcode.NET;

/// <summary>
/// A thumbnail block's parameters: what image it holds and at which pixel size.
/// </summary>
/// <param name="Format">The image format, as declared. An unknown wire value survives the cast.</param>
/// <param name="Width">Image width in pixels.</param>
/// <param name="Height">Image height in pixels.</param>
public readonly record struct BgcodeThumbnailParameters(BgcodeThumbnailFormat Format, ushort Width, ushort Height);
