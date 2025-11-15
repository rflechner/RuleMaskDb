using System.Collections.Immutable;

namespace RuleMaskDb.SqlDomain;

public record TableDescription(string Name, int RowCount, ImmutableArray<FieldDescription> Fields);