namespace RuleMaskDb;

/// <summary>
/// A buffered page of row keys and the continuation information needed to process it.
/// </summary>
internal sealed class KeyPage
{
    private readonly int requestedSize;

    public KeyPage(IReadOnlyList<object?[]> rowKeys, int requestedSize)
    {
        ArgumentNullException.ThrowIfNull(rowKeys);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(requestedSize);
        if (rowKeys.Count > requestedSize)
            throw new ArgumentException("A key page cannot exceed its requested size.", nameof(rowKeys));

        // Keep page membership stable after the reader's buffer is handed over.
        RowKeys = Array.AsReadOnly(rowKeys.ToArray());
        this.requestedSize = requestedSize;
    }

    public IReadOnlyList<object?[]> RowKeys { get; }

    public int RowCount => RowKeys.Count;

    public bool IsEmpty => RowCount == 0;

    /// <summary>
    /// A full page requires another read to determine whether more rows remain.
    /// Empty and partial pages establish that the scan has reached its end.
    /// </summary>
    public bool RequiresNextPage => RowCount == requestedSize;

    /// <summary>
    /// The last row key in database order, or null when the page contains no rows.
    /// </summary>
    public IReadOnlyList<object?>? ContinuationKey => IsEmpty ? null : RowKeys[^1];
}
