using System.Collections.Immutable;

namespace RuleMaskDb.ScriptDom;

public sealed record GeneratorParameter(
    string? Column = null,
    ImmutableDictionary<string, string>? Mapping = null,
    string? Fallback = null,
    string? Value = null)
{
    public void Validate(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("Generator parameter names must not be empty.");
        if ((Value is not null) == (Column is not null))
            throw new InvalidOperationException($"Parameter '{name}' requires exactly one of value or column.");
        if (Value is not null && (Mapping is not null || Fallback is not null))
            throw new InvalidOperationException($"Fixed parameter '{name}' cannot specify mapping or fallback.");
        if (Column is not null && string.IsNullOrWhiteSpace(Column))
            throw new InvalidOperationException($"Parameter '{name}' requires a non-empty source column.");
        if (Mapping?.Values.Any(v => v is null) == true)
            throw new InvalidOperationException($"Null mapping output for parameter '{name}'.");
    }
}
