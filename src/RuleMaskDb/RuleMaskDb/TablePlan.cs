using RuleMaskDb.ScriptDom;
using RuleMaskDb.Sdk;
using RuleMaskDb.SqlDomain;

namespace RuleMaskDb;

/// <summary>
/// Represents a table generation plan that includes details about the target table,
/// associated rules for masking or generating data, defined keys, and data generators
/// for the table columns.
/// </summary>
/// <param name="Table">
/// The description of the target table, including its name, schema, fields, and row count.
/// </param>
/// <param name="Rules">
/// The set of rules that define how data masking or generation should be applied for
/// specific columns in the table.
/// </param>
/// <param name="Keys">
/// The collection of key identifiers used for uniquely identifying or interacting with
/// data in the table.
/// </param>
/// <param name="Generators">
/// The array of data generators, each corresponding to a column in the table. These
/// generators may be used to create synthetic or masked data based on predefined rules.
/// Null entries indicate no specific generator is associated with the respective column.
/// </param>
public record TablePlan(TableDescription Table, Rule[] Rules, string[] Keys, IDataGenerator?[] Generators)
{
    /// <summary>
    /// Gets the configured key requiring data validation, or null when no key was configured.
    /// Database primary keys are enforced by the database and do not need this validation.
    /// </summary>
    public string? ConfiguredRowKey { get; init; }

    /// <summary>
    /// Gets the validated SQL to execute before anonymization, or null when none was supplied.
    /// </summary>
    public string? PreparationSql { get; init; }

    /// <summary>
    /// Returns a plan with the current row count after preparation, preserving its rules and metadata.
    /// </summary>
    public TablePlan WithRowCount(int rowCount) => this with { Table = Table with { RowCount = rowCount } };

    /// <summary>
    /// Indicates whether all rules defined in the table plan have static values.
    /// </summary>
    /// <remarks>
    /// This property evaluates to true if every rule in the <c>Rules</c> collection
    /// has its <c>HasValue</c> property set to true. It provides a way to determine
    /// if all rules are fully defined without requiring dynamic or computed values.
    /// </remarks>
    public bool HasOnlyStaticValues => Rules.All(r => r.HasValue);
}
