using System.Collections.Immutable;

namespace RuleMaskDb.ScriptDom;

public sealed record GeneratorParameter(
    string Column,
    ImmutableDictionary<string, string> Mapping,
    string? Fallback = null);
