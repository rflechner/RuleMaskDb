using System.Collections.Immutable;

namespace RuleMaskDb;

/// <summary>
/// Describes the elapsed time of a successfully completed script.
/// </summary>
/// <param name="TotalDuration">
/// Total execution time, including schema discovery, validation, truncation, and anonymization.
/// Loading the YAML and rendering the final report are excluded.
/// </param>
/// <param name="Tables">Anonymized tables in execution order, including empty tables.</param>
/// <param name="Truncation">The shared truncation operation, or null when no tables were truncated.</param>
public sealed record ScriptExecutionReport(
    TimeSpan TotalDuration,
    ImmutableArray<TableExecutionTiming> Tables,
    TruncationExecutionTiming? Truncation);

/// <summary>
/// Records a table's full anonymization duration, including preparation SQL, key revalidation,
/// row-count refresh, connection setup, updates, and transaction completion.
/// </summary>
/// <param name="TableName">The exact table name from database metadata.</param>
/// <param name="Duration">Elapsed time measured with a monotonic clock.</param>
public sealed record TableExecutionTiming(string TableName, TimeSpan Duration);

/// <summary>
/// Records the duration of the shared truncation transaction.
/// PostgreSQL truncates these tables in one statement, so individual durations cannot be measured.
/// </summary>
/// <param name="TableNames">All tables participating in the truncation transaction.</param>
/// <param name="Duration">Elapsed time including connection setup and transaction completion.</param>
public sealed record TruncationExecutionTiming(ImmutableArray<string> TableNames, TimeSpan Duration);
