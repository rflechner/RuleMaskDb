using System.Collections.Immutable;

namespace RuleMaskDb;

/// <summary>
/// Rows sharing the same SET columns and therefore eligible for a single update statement.
/// </summary>
internal sealed record ColumnUpdateGroup(ImmutableArray<int> ChangedRuleIndices, object?[][] Rows)
{
    public int RowCount => Rows.Length;
}
