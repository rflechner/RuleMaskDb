using RuleMaskDb.ScriptDom;
using RuleMaskDb.SqlDomain;

namespace RuleMaskDb;

public interface IScriptRunner
{
    /// <summary>
    /// Executes the script and returns timing details after all operations complete successfully.
    /// Failures and cancellation propagate to the caller without returning a success report.
    /// </summary>
    Task<ScriptExecutionReport> RunAsync(ScriptSpecification script, IProgressReporter progressReporter,
        CancellationToken cancellationToken = default);
    
    Task<bool> IsImpactedAsync(ScriptSpecification script, TableDescription table, FieldDescription field, CancellationToken cancellationToken = default);
}

public record TableRecord(string DatabaseName, string TableName, TableRecordField?[] Fields);

public record TableRecordField(DateType DataType, string Name, object? Value);
