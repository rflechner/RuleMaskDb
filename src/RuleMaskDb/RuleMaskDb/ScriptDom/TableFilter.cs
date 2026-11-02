using System.Collections.Immutable;

namespace RuleMaskDb.ScriptDom;

/// <summary>
/// Limits a table's anonymization to rows whose column value matches one of the
/// configured scalar values. SQL identifiers are quoted and values are parameterized.
/// </summary>
/// <param name="Column">Exact database column name, without SQL quoting.</param>
/// <param name="Values">Non-null scalar values accepted by an SQL IN predicate.</param>
public sealed record TableFilter(string Column, ImmutableArray<object?> Values)
{
    /// <summary>
    /// Validates the filter before any database operations are performed.
    /// </summary>
    public void Validate(string tableName)
    {
        if (string.IsNullOrWhiteSpace(Column))
            throw new InvalidOperationException($"Table filter column must not be empty: {tableName}");
        if (Values.IsDefaultOrEmpty)
            throw new InvalidOperationException($"Table filter values must not be empty: {tableName}.{Column}");
        if (Values.Any(value => value is not (string or bool or byte or sbyte or short or ushort or int
            or uint or long or ulong or float or double or decimal)))
            throw new InvalidOperationException($"Table filter values must be non-null scalars: {tableName}.{Column}");
        if (Values.Any(value => value is double d && !double.IsFinite(d) || value is float f && !float.IsFinite(f)))
            throw new InvalidOperationException($"Table filter values must be finite: {tableName}.{Column}");
    }
}
