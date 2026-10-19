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
public record TablePlan(TableDescription Table, Rule[] Rules, string[] Keys, IDataGenerator?[] Generators);