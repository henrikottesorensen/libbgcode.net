namespace libbgcode.NET;

/// <summary>
/// How a metadata block's key-value payload is encoded, with the specification's wire value.
/// </summary>
public enum BgcodeMetadataEncoding
{
    /// <summary>INI: one <c>key = value</c> pair per line, UTF-8.</summary>
    Ini = 0,
}
