namespace RuleMaskDb.ScriptDom;

public record Rule(string Table, string Column, string? Mask, GeneratorType? Generator);