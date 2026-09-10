using RuleMaskDb.PostgreSqlDriver;
using RuleMaskDb.SqlServerDriver;

namespace RuleMaskDb.ScriptDom;

public sealed class DatabaseAnalyzer : IDatabaseAnalyzer
{
    public Task<DatabaseDescription> DescribeDatabaseAsync(DatabaseSpecification specification) =>
        (specification.DatabaseType switch
        {
            DatabaseType.SqlServer => (IDatabaseAnalyzer)new SqlServerDatabaseAnalyzer(),
            DatabaseType.PostgreSQL => new PostgreSqlDatabaseAnalyzer(),
            _ => throw new NotSupportedException($"Unsupported database type: {specification.DatabaseType}")
        }).DescribeDatabaseAsync(specification);
}
