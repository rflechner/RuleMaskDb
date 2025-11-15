using System.Collections.Immutable;
using RuleMaskDb.SqlDomain;

namespace RuleMaskDb.ScriptDom;

public record DatabaseDescription(string Name, ImmutableArray<TableDescription> Tables);