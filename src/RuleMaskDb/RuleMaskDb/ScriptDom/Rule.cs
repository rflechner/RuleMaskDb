namespace RuleMaskDb;

public record Rule(string Table, string Column, string? Mask, GeneratorType? Generator);