using System.Collections.Immutable;
using RuleMaskDb.Generators;
using RuleMaskDb.ScriptDom;
using RuleMaskDb.SqlDomain;

namespace RuleMaskDb.Tests;

public class ScriptRunnerTests
{
    private sealed class DummyDatabaseAnalyzer : IDatabaseAnalyzer
    {
        public Task<DatabaseDescription> DescribeDatabaseAsync(DatabaseSpecification specification)
            => throw new NotImplementedException();
    }

    private sealed class DummyDataGeneratorFactory : IDataGeneratorFactory
    {
        public IDataGenerator Create(GeneratorType generatorType) => throw new NotImplementedException();
    }

    private static ScriptRunner CreateSut()
        => new(new DummyDatabaseAnalyzer(), new DummyDataGeneratorFactory());

    [Test]
    public async Task IsImpactedAsync_returns_true_when_table_and_column_match()
    {
        // Arrange
        var script = new ScriptSpecification(
            new DatabaseSpecification(DatabaseType.SqlServer, "Server=(local);Database=Dummy;"),
            ImmutableArray.Create(new Rule("dbo.People", "FirstName", null, null)));

        var table = new TableDescription("dbo.People", 0, ImmutableArray<FieldDescription>.Empty);
        var field = new FieldDescription("FirstName", DateType.Text, false);

        var sut = CreateSut();

        // Act
        var impacted = await sut.IsImpactedAsync(script, table, field);

        // Assert
        Assert.That(impacted, Is.True);
    }

    [Test]
    public async Task IsImpactedAsync_returns_false_when_table_does_not_match()
    {
        // Arrange
        var script = new ScriptSpecification(
            new DatabaseSpecification(DatabaseType.SqlServer, "Server=(local);Database=Dummy;"),
            ImmutableArray.Create(new Rule("dbo.Customers", "FirstName", null, null)));

        var table = new TableDescription("dbo.People", 0, ImmutableArray<FieldDescription>.Empty);
        var field = new FieldDescription("FirstName", DateType.Text, false);

        var sut = CreateSut();

        // Act
        var impacted = await sut.IsImpactedAsync(script, table, field);

        // Assert
        Assert.That(impacted, Is.False);
    }

    [Test]
    public async Task IsImpactedAsync_returns_false_when_column_does_not_match()
    {
        // Arrange
        var script = new ScriptSpecification(
            new DatabaseSpecification(DatabaseType.SqlServer, "Server=(local);Database=Dummy;"),
            ImmutableArray.Create(new Rule("dbo.People", "LastName", null, null)));

        var table = new TableDescription("dbo.People", 0, ImmutableArray<FieldDescription>.Empty);
        var field = new FieldDescription("FirstName", DateType.Text, false);

        var sut = CreateSut();

        // Act
        var impacted = await sut.IsImpactedAsync(script, table, field);

        // Assert
        Assert.That(impacted, Is.False);
    }

    [Test]
    public async Task IsImpactedAsync_returns_true_when_one_of_multiple_rules_matches()
    {
        // Arrange
        var rules = ImmutableArray.Create(
            new Rule("dbo.Customers", "Email", null, null),
            new Rule("dbo.People", "FirstName", null, null),
            new Rule("dbo.Orders", "OrderNumber", null, null)
        );

        var script = new ScriptSpecification(
            new DatabaseSpecification(DatabaseType.SqlServer, "Server=(local);Database=Dummy;"),
            rules);

        var table = new TableDescription("dbo.People", 0, ImmutableArray<FieldDescription>.Empty);
        var field = new FieldDescription("FirstName", DateType.Text, false);

        var sut = CreateSut();

        // Act
        var impacted = await sut.IsImpactedAsync(script, table, field);

        // Assert
        Assert.That(impacted, Is.True);
    }
}
