using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Moq;
using RuleMaskDb.Generators;
using RuleMaskDb.ScriptDom;
using RuleMaskDb.Sdk;
using RuleMaskDb.SqlDomain;
using RuleMaskDb.Yaml;

namespace RuleMaskDb.Tests;

[NonParallelizable]
public sealed class BogusCatalogTests
{
    private static readonly BogusDataGeneratorFactory Factory = new();

    public static IEnumerable<TestCaseData> CatalogCases()
        => BogusGeneratorCatalog.Generators.Select(description => new TestCaseData(description)
            .SetName($"Catalog: {description.Name}({description.Parameters})"));

    [TestCaseSource(nameof(CatalogCases))]
    public async Task Every_catalog_signature_can_generate_a_database_value(BogusGeneratorDescription description)
    {
        var parameters = description.Parameters.Split(", ", StringSplitOptions.RemoveEmptyEntries)
            .Where(p => !p.EndsWith("(optional)", StringComparison.Ordinal))
            .Select(p => p.Split(": ", 2)).ToDictionary(p => p[0], p => Sample(p[0], p[1]));
        var generator = (IContextualDataGenerator)Factory.Create(description.Name);
        generator.ValidateParameters(parameters.ToDictionary(p => p.Key, p => (IReadOnlyCollection<string>)new[] { p.Value }));
        var value = await generator.GenerateValueAsync(new GenerationContext(parameters));
        Assert.That(value, Is.Not.Null.And.Not.EqualTo(DBNull.Value));
        Assert.That(value, Is.InstanceOf<string>().Or.InstanceOf<byte[]>().Or.InstanceOf<bool>()
            .Or.InstanceOf<short>().Or.InstanceOf<int>().Or.InstanceOf<long>().Or.InstanceOf<byte>()
            .Or.InstanceOf<float>().Or.InstanceOf<double>().Or.InstanceOf<decimal>()
            .Or.InstanceOf<DateTime>().Or.InstanceOf<DateTimeOffset>().Or.InstanceOf<TimeSpan>().Or.InstanceOf<Guid>());
    }

    private static string Sample(string name, string type) => type switch
    {
        "String[]" => """["Alpha", "Beta"]""",
        "Single[]" => "[0.5, 0.5]",
        "DateTime" or "DateTimeOffset" => name is "end" or "dateTo" ? "2012-01-01T00:00:00Z" : "2010-01-01T00:00:00Z",
        "DateOnly" => name == "end" ? "2012-01-01" : "2010-01-01",
        "TimeOnly" => name == "end" ? "11:00:00" : "10:00:00",
        "Boolean" => "true",
        "Double" => "1",
        "Single" => "0.5",
        "Standard|BranchTrader|GovernmentDepartment|HealthAuthority" => "Standard",
        "Int32" => name is "max" or "maxLength" or "width" or "height" ? "4" : "2",
        "String" => name == "format" ? "##??" : "Alex",
        _ => throw new InvalidOperationException($"Add an intentional test value for {name}: {type}.")
    };

    [TestCase("Bogus.System.GetType")]
    [TestCase("Bogus.Random.Seed")]
    [TestCase("System.IO.File.ReadAllText")]
    [TestCase("Bogus.Random.ReplaceSymbols")]
    [TestCase("Bogus.Random.Enum")]
    public void Unknown_and_non_data_APIs_are_rejected(string name)
        => Assert.Throws<InvalidOperationException>(() => Factory.Create(name));

    [TestCase("Bogus.Random.Int", "min", "oops")]
    [TestCase("Bogus.Random.Int", "min", "9999999999999")]
    [TestCase("Bogus.Random.Double", "min", "NaN")]
    [TestCase("Bogus.Name.FirstName", "gender", "1")]
    [TestCase("Bogus.Name.FirstName", "gender", "invalid")]
    [TestCase("Bogus.Finance.CreditCardNumber", "provider", "invalid")]
    [TestCase("Bogus.Random.Bool", "weight", "wrong")]
    [TestCase("Bogus.Internet.Email", "typo", "value")]
    [TestCase("Bogus.Random.ArrayElement", "array", "not-json")]
    public void Invalid_configured_values_fail_before_opening_update_connections(string name, string parameter, string value)
    {
        var database = new DatabaseSpecification(DatabaseType.SqlServer, "invalid connection string");
        var table = new TableDescription("dbo.Customers", 1,
            [new FieldDescription("Id", DateType.IntegerNumber, true), new FieldDescription("Value", DateType.Text, false)]);
        var analyzer = new Mock<IDatabaseAnalyzer>();
        analyzer.Setup(a => a.DescribeDatabaseAsync(database)).ReturnsAsync(new DatabaseDescription("test", [table]));
        var script = new ScriptSpecification(database, [new Rule(table.Name, "Value", null, name)
        {
            Parameters = ImmutableDictionary<string, GeneratorParameter>.Empty.Add(parameter, new(Value: value))
        }]);
        Assert.That(async () => await new ScriptRunner(analyzer.Object, Factory)
            .RunAsync(script, new Mock<IProgressReporter>().Object), Throws.InvalidOperationException);
    }

    [Test]
    public void Missing_required_arguments_are_rejected()
    {
        var generator = (IContextualDataGenerator)Factory.Create("Bogus.Date.Between");
        Assert.Throws<InvalidOperationException>(() => generator.ValidateParameters(new Dictionary<string, IReadOnlyCollection<string>>()));
    }

    [Test]
    public async Task Named_arguments_select_overloads_and_preserve_typed_values()
    {
        Assert.That(await Generate("Bogus.Random.Int", ("min", "42"), ("max", "42")), Is.EqualTo(42));
        Assert.That(await Generate("Bogus.Random.Bool", ("weight", "1")), Is.EqualTo(true));
        Assert.That(await Generate("Bogus.Random.Bool", ("weight", "0")), Is.EqualTo(false));
        Assert.That(await Generate("Bogus.Company.CompanyName", ("format", "Company {{name.lastName}}")), Does.StartWith("Company "));
        Assert.That(await Generate("Bogus.Random.String2", ("minLength", "7"), ("maxLength", "7")), Has.Length.EqualTo(7));
        Assert.That(await Generate("Bogus.Random.String2", ("length", "4")), Has.Length.EqualTo(4));
        Assert.That(await Generate("Bogus.Finance.CreditCardNumber", ("provider", "Visa")), Is.TypeOf<string>());
    }

    [Test]
    public async Task Numbers_use_invariant_culture_and_unsigned_values_are_sql_compatible()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            Assert.That(await Generate("Bogus.Finance.Amount", ("min", "1.25"), ("max", "1.25")), Is.EqualTo(1.25m));
            Assert.That(await Generate("Bogus.Random.UInt", ("min", "4294967295"), ("max", "4294967295")), Is.EqualTo(4294967295L));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Test]
    public async Task Collections_and_structured_values_have_explicit_database_representations()
    {
        var words = (string)await Generate("Bogus.Lorem.Words", ("num", "3"));
        Assert.That(JsonSerializer.Deserialize<string[]>(words), Has.Length.EqualTo(3));
        Assert.That(await Generate("Bogus.Random.Bytes", ("count", "4")), Is.TypeOf<byte[]>().And.Length.EqualTo(4));
        Assert.That(await Generate("Bogus.Random.Chars", ("count", "4")), Is.TypeOf<string>().And.Length.EqualTo(4));
        using var currency = JsonDocument.Parse((string)await Generate("Bogus.Finance.Currency"));
        Assert.That(currency.RootElement.ValueKind, Is.EqualTo(JsonValueKind.Object));
        Assert.That(await Generate("Bogus.Internet.IpAddress"), Is.TypeOf<string>());
        Assert.That(await Generate("Bogus.Date.BetweenDateOnly", ("start", "2020-01-01"), ("end", "2020-01-01")), Is.EqualTo(new DateTime(2020, 1, 1)));
    }

    [Test]
    public async Task Person_fields_and_country_extensions_are_fresh_for_each_row()
    {
        var generator = Factory.Create("Bogus.Person.Email");
        var values = new HashSet<object>();
        for (var i = 0; i < 10; i++) values.Add(await generator.GenerateValueAsync());
        Assert.That(values.Count, Is.GreaterThan(1));
        Assert.That(await Generate("Bogus.Person.Ssn"), Does.Match(@"^\d{3}-\d{2}-\d{4}$"));
        Assert.That(await Generate("Bogus.Person.Address.City"), Is.TypeOf<string>());
    }

    [Test]
    public async Task Yaml_supports_fixed_values_dynamic_culture_and_enum_mapping()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, """
                rules:
                  - table: dbo.Customers
                    column: FirstName
                    generator: Bogus.Name.FirstName
                    parameters:
                      culture:
                        column: CountryCode
                        mapping: { FR: fr-FR, GB: en-GB }
                        fallback: en-US
                      gender:
                        column: GenderCode
                        mapping: { F: Female, M: Male }
                        fallback: Unknown
                  - table: dbo.Customers
                    column: CreditLimit
                    generator: Bogus.Finance.Amount
                    parameters:
                      min: { value: '10.50' }
                      max: { value: '10.50' }
                """);
            var script = await new YamlScriptDomProvider(path).LoadScriptAsync();
            var context = GeneratorParameterResolver.Resolve(script.Rules[0],
                new Dictionary<string, object?> { ["CountryCode"] = "FR", ["GenderCode"] = "F" });
            Assert.That(context.Parameters["culture"], Is.EqualTo("fr-FR"));
            Assert.That(context.Parameters["gender"], Is.EqualTo("Female"));
            Assert.That(await ((IContextualDataGenerator)Factory.Create(script.Rules[0].Generator!))
                .GenerateValueAsync(context), Is.TypeOf<string>());
            var amount = GeneratorParameterResolver.Resolve(script.Rules[1], new Dictionary<string, object?>());
            Assert.That(await ((IContextualDataGenerator)Factory.Create(script.Rules[1].Generator!))
                .GenerateValueAsync(amount), Is.EqualTo(10.50m));
        }
        finally { File.Delete(path); }
    }

    [Test]
    public void Direct_column_values_are_checked_at_generation_time()
        => Assert.That(async () => await Generate("Bogus.Random.Int", ("min", "not-a-number")), Throws.InvalidOperationException);

    [Test]
    public void Qualified_names_work_through_the_plugin_factory()
        => Assert.That(new PluginDataGeneratorFactory(PluginCatalog.Discover(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())))
            .Create("bogus.internet.username"), Is.InstanceOf<IContextualDataGenerator>());

    [Test]
    public void Cancellation_is_observed_before_generating()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAsync<OperationCanceledException>(() =>
            Factory.Create("Bogus.Internet.Email").GenerateValueAsync(cancellation.Token));
    }

    private static Task<object> Generate(string name, params (string Name, string Value)[] values)
        => ((IContextualDataGenerator)Factory.Create(name)).GenerateValueAsync(
            new GenerationContext(values.ToDictionary(p => p.Name, p => p.Value)));
}
