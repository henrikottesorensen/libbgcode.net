namespace libbgcode.NET;

/// <summary>
/// How a metadata block's key-value payload is encoded, with the wire values of the reference
/// implementation.
/// </summary>
/// <remarks>
/// The published specification stops at INI; JSON was added by the reference implementation for
/// PrusaSlicer 3, which writes its slicer metadata twice - a legacy INI block and a JSON block,
/// back to back and told apart by this parameter.
/// </remarks>
public enum BgcodeMetadataEncoding
{
    /// <summary>INI: one <c>key = value</c> pair per line, UTF-8.</summary>
    Ini = 0,

    /// <summary>One JSON document, UTF-8.</summary>
    Json = 1,
}
