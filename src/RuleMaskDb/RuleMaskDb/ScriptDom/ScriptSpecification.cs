using System.Collections.Immutable;

namespace RuleMaskDb.ScriptDom;

public record ScriptSpecification(DatabaseSpecification Database, ImmutableArray<Rule> Rules);