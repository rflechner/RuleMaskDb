namespace RuleMaskDb.ScriptDom;

public record Rule(string Table, string Column, string? Mask, string? Generator)
{
    public System.Collections.Immutable.ImmutableDictionary<string, GeneratorParameter> Parameters { get; init; }
        = System.Collections.Immutable.ImmutableDictionary<string, GeneratorParameter>.Empty;

    public Rule(string Table, string Column, string? Mask, GeneratorType Generator)
        : this(Table, Column, Mask, Generator.ToString()) { }
}
