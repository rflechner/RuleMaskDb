using System.Collections.Immutable;

namespace RuleMaskDb.ScriptDom;

public record DatabaseDescription(string Name, ImmutableArray<TableDescription> Tables);