using System.Collections.Immutable;

namespace RuleMaskDb.ScriptDom;

public record TableDescription(string Name, int RowCount, ImmutableArray<FieldDescription> Fields);