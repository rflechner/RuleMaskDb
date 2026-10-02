namespace RuleMaskDb.ScriptDom;

public record Rule(string Table, string Column, string? Mask, string? Generator)
{
    public Rule(string Table, string Column, string? Mask, GeneratorType Generator)
        : this(Table, Column, Mask, Generator.ToString()) { }
}
