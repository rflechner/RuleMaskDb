using RuleMaskDb.ScriptDom;
using RuleMaskDb.SqlDomain;

namespace RuleMaskDb;

public interface IScriptRunner
{
    Task RunAsync(ScriptSpecification script, IProgressReporter progressReporter, CancellationToken cancellationToken = default);
    
    Task<bool> IsImpactedAsync(ScriptSpecification script, TableDescription table, FieldDescription field, CancellationToken cancellationToken = default);
}

public record TableRecord(string DatabaseName, string TableName, TableRecordField?[] Fields);

public record TableRecordField(DateType DataType, string Name, object Value);
