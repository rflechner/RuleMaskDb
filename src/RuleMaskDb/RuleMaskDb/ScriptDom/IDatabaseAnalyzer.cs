namespace RuleMaskDb.ScriptDom;

public interface IDatabaseAnalyzer
{
    Task<DatabaseDescription> DescribeDatabaseAsync(DatabaseSpecification databaseSpecification);
}