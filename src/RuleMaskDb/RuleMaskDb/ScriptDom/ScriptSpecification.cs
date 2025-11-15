using System.Collections.Immutable;

namespace RuleMaskDb;

public record ScriptSpecification(DatabaseSpecification Database, ImmutableArray<Rule> Rules);