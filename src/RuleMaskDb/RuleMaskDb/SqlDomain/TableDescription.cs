using System.Collections.Immutable;

namespace RuleMaskDb.SqlDomain;

public record TableDescription(string Name, int RowCount, ImmutableArray<FieldDescription> Fields)
{
    public string? Schema { get; init; }
    public string? LocalName { get; init; }
}
