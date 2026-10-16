namespace RuleMaskDb.ScriptDom;

public record Rule(string Table, string Column, string? Mask, string? Generator)
{
    private object? value;

    // Presence is separate from the value so an explicit null means SQL NULL.
    public object? Value
    {
        get => value;
        init { this.value = value; HasValue = true; }
    }

    public bool HasValue { get; private init; }

    public void ValidateValue()
    {
        if (!HasValue) return;
        if (Generator is not null || Mask is not null || Parameters.Count > 0)
            throw new InvalidOperationException($"Static value for {Table}.{Column} cannot be combined with generator, mask, or parameters.");
        if (Value is not (null or string or bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal))
            throw new InvalidOperationException($"Static value for {Table}.{Column} must be a scalar or null.");
        if (Value is double d && !double.IsFinite(d) || Value is float f && !float.IsFinite(f))
            throw new InvalidOperationException($"Static value for {Table}.{Column} must be finite.");
    }

    public System.Collections.Immutable.ImmutableDictionary<string, GeneratorParameter> Parameters { get; init; }
        = System.Collections.Immutable.ImmutableDictionary<string, GeneratorParameter>.Empty;

    public Rule(string Table, string Column, string? Mask, GeneratorType Generator)
        : this(Table, Column, Mask, Generator.ToString()) { }
}
