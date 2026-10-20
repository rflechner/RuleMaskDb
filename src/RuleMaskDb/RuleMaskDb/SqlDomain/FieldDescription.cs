namespace RuleMaskDb.SqlDomain;

public record FieldDescription(string Path, DateType DataType, bool IsPrimaryKey)
{
    // Native metadata is optional for descriptions supplied by existing callers.
    public string? NativeTypeName { get; init; }
    public int? MaxLength { get; init; }
    public string? Collation { get; init; }

    /// <summary>
    /// One-based position in the primary key, used to page in index column order when available.
    /// </summary>
    public int? PrimaryKeyOrdinal { get; init; }
}
