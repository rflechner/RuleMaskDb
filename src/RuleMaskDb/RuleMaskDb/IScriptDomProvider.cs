using System.Collections.Immutable;

namespace RuleMaskDb;

public interface IScriptDomProvider
{
    Task<ScriptSpecification> LoadScriptAsync();
}

public interface IScriptRunner
{
    Task RunAsync(ScriptSpecification scriptSpecification, CancellationToken cancellationToken = default);
}

public record DatabaseDescription(string Name, ImmutableArray<TableDescription> Tables);

public record TableDescription(string Name, int RowCount, ImmutableArray<FieldDescription> Fields);

public record FieldDescription(string Path, DateType DataType);

public enum DateType
{
    Text,
    IntegerNumber,
    DecimalNumber,
    Date,
    DateTime,
    Boolean
}

public interface IDatabaseAnalyzer
{
    Task<DatabaseDescription> DescribeDatabaseAsync(DatabaseSpecification databaseSpecification);
}
