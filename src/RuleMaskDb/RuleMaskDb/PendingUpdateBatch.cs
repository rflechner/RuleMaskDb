namespace RuleMaskDb;

/// <summary>
/// Buffers one chunk of prepared updates. Rows without changes still count toward its capacity
/// and progress, but never produce a SQL assignment.
/// </summary>
internal sealed class PendingUpdateBatch
{
    private readonly int capacity;
    private readonly List<PreparedRowUpdate> pendingUpdates = [];

    public PendingUpdateBatch(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        this.capacity = capacity;
    }

    public int ReadRowCount { get; private set; }

    public bool IsEmpty => ReadRowCount == 0;

    public bool IsFull => ReadRowCount == capacity;

    public void RecordRow(PreparedRowUpdate row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (IsFull) throw new InvalidOperationException("A full update batch cannot accept another row.");

        if (row.HasChanges) pendingUpdates.Add(row);
        ReadRowCount++;
    }

    public IEnumerable<ColumnUpdateGroup> GroupByChangedColumns()
    {
        // Preserved columns must be absent from SET, even with their original value:
        // otherwise column-specific triggers would fire for skipped NULL/empty cells.
        return pendingUpdates
            .GroupBy(row => string.Join(",", row.ChangedRuleIndices))
            .Select(group => new ColumnUpdateGroup(group.First().ChangedRuleIndices,
                group.Select(row => row.Values.ToArray()).ToArray()));
    }
}
