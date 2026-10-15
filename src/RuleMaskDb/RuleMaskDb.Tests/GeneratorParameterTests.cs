using System.Collections.Immutable;
using System.Globalization;
using Bogus;
using Moq;
using RuleMaskDb.Generators;
using RuleMaskDb.ScriptDom;
using RuleMaskDb.Sdk;
using RuleMaskDb.SqlDomain;
using RuleMaskDb.Yaml;

namespace RuleMaskDb.Tests;

public sealed class GeneratorParameterTests
{
    private static Rule RuleFor(string column = "Civilite", string? fallback = "Unknown")
        => new("dbo.Clients", "Prenom", null, "FirstName")
        {
            Parameters = ImmutableDictionary<string, GeneratorParameter>.Empty.Add("gender",
                new GeneratorParameter(column, new Dictionary<string, string>
                {
                    ["M."] = "Male", ["Mme"] = "Female", ["1"] = "Male",
                    ["2"] = "Female", ["True"] = "Male", ["1.5"] = "Female"
                }.ToImmutableDictionary(), fallback))
        };

    [TestCase("M.", Gender.Male)]
    [TestCase("Mme", Gender.Female)]
    [TestCase(1, Gender.Male)]
    [TestCase(2, Gender.Female)]
    [TestCase(true, Gender.Male)]
    [TestCase(null, Gender.Unknown)]
    [TestCase("", Gender.Unknown)]
    [TestCase("m.", Gender.Unknown)]
    [TestCase(" M.", Gender.Unknown)]
    [TestCase("other", Gender.Unknown)]
    public void Resolves_database_codes_with_exact_matching(object? original, Gender expected)
    {
        var context = GeneratorParameterResolver.Resolve(RuleFor(),
            new Dictionary<string, object?> { ["Civilite"] = original });
        Assert.That(context.GetEnum("gender", Gender.Unknown), Is.EqualTo(expected));
    }

    [Test]
    public void DbNull_uses_fallback_and_missing_fallback_omits_parameter()
    {
        var row = new Dictionary<string, object?> { ["Civilite"] = DBNull.Value };
        Assert.That(GeneratorParameterResolver.Resolve(RuleFor(fallback: "Female"), row)
            .GetEnum("gender", Gender.Unknown), Is.EqualTo(Gender.Female));
        Assert.That(GeneratorParameterResolver.Resolve(RuleFor(fallback: null), row).Parameters, Is.Empty);
    }

    [Test]
    public void Numeric_keys_are_culture_independent()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var context = GeneratorParameterResolver.Resolve(RuleFor(),
                new Dictionary<string, object?> { ["Civilite"] = 1.5m });
            Assert.That(context.GetEnum("gender", Gender.Unknown), Is.EqualTo(Gender.Female));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Test]
    public void Empty_string_can_be_explicitly_mapped_and_context_is_a_snapshot()
    {
        var rule = RuleFor();
        var binding = rule.Parameters["gender"];
        rule = rule with { Parameters = rule.Parameters.SetItem("gender",
            binding with { Mapping = binding.Mapping!.Add("", "Female") }) };
        var row = new Dictionary<string, object?> { ["Civilite"] = "" };
        var context = GeneratorParameterResolver.Resolve(rule, row);
        row["Civilite"] = "M.";
        Assert.That(context.GetEnum("gender", Gender.Unknown), Is.EqualTo(Gender.Female));
    }

    [TestCase("Female", Gender.Female)]
    [TestCase("male", Gender.Male)]
    [TestCase("UNKNOWN", Gender.Unknown)]
    public void Enum_names_are_provider_independent_and_case_insensitive(string value, Gender expected)
        => Assert.That(new GenerationContext(new Dictionary<string, string> { ["gender"] = value })
            .GetEnum("gender", Gender.Unknown), Is.EqualTo(expected));

    [TestCase("0")]
    [TestCase("Typo")]
    [TestCase("")]
    public void Invalid_enum_outputs_are_rejected(string value)
        => Assert.Throws<InvalidOperationException>(() =>
            new GenerationContext(new Dictionary<string, string> { ["gender"] = value })
                .GetEnum("gender", Gender.Unknown));

    [TestCase(Gender.Male)]
    [TestCase(Gender.Female)]
    [TestCase(Gender.Unknown)]
    public async Task FirstName_forwards_gender_to_Bogus(Gender gender)
    {
        var expected = new Faker("en") { Random = new Randomizer(42) };
        var actual = new Faker("en") { Random = new Randomizer(42) };
        Bogus.DataSets.Name.Gender? bogusGender = gender switch
        {
            Gender.Male => Bogus.DataSets.Name.Gender.Male,
            Gender.Female => Bogus.DataSets.Name.Gender.Female,
            _ => null
        };
        var generator = new FirstNameGenerator(actual);
        var context = new GenerationContext(new Dictionary<string, string> { ["gender"] = gender.ToString() });
        for (var i = 0; i < 20; i++)
            Assert.That(await generator.GenerateValueAsync(context), Is.EqualTo(expected.Name.FirstName(bogusGender)));
    }

    [Test]
    public async Task LastName_generates_a_surname_instead_of_a_full_name()
    {
        var expected = new Faker("en") { Random = new Randomizer(42) };
        var actual = new Faker("en") { Random = new Randomizer(42) };
        var generator = new LastNameGenerator(actual);
        for (var i = 0; i < 20; i++)
            Assert.That(await generator.GenerateValueAsync(), Is.EqualTo(expected.Name.LastName()));
        Sdk.IDataGenerator resolved = ((IDataGeneratorFactory)new BogusDataGeneratorFactory()).Create("LastName");
        Assert.That(resolved, Is.InstanceOf<IContextualDataGenerator>());
        Assert.That(await resolved.GenerateValueAsync(), Is.TypeOf<string>());
    }

    [TestCase("column")]
    [TestCase("mapping")]
    [TestCase("fallback")]
    [TestCase("parameter")]
    [TestCase("legacy")]
    public void Invalid_configuration_in_later_table_fails_before_opening_connections(string error)
    {
        var database = new DatabaseSpecification(DatabaseType.PostgreSQL, "invalid connection string");
        var fields = ImmutableArray.Create(new FieldDescription("Id", DateType.Text, true),
            new FieldDescription("Prenom", DateType.Text, false),
            new FieldDescription("Civilite", DateType.Text, false));
        var rule = RuleFor() with { Table = "dbo.Second" };
        var parameter = rule.Parameters["gender"];
        rule = error switch
        {
            "column" => rule with { Parameters = rule.Parameters.SetItem("gender", parameter with { Column = "Missing" }) },
            "mapping" => rule with { Parameters = rule.Parameters.SetItem("gender", parameter with { Mapping = parameter.Mapping!.Add("unused", "Typo") }) },
            "fallback" => rule with { Parameters = rule.Parameters.SetItem("gender", parameter with { Fallback = "Typo" }) },
            "parameter" => rule with { Parameters = rule.Parameters.Remove("gender").Add("typo", parameter) },
            _ => rule with { Generator = "Name" }
        };
        var analyzer = new Mock<IDatabaseAnalyzer>();
        analyzer.Setup(a => a.DescribeDatabaseAsync(database)).ReturnsAsync(new DatabaseDescription("test",
            ImmutableArray.Create(new TableDescription("dbo.Clients", 1, fields),
                new TableDescription("dbo.Second", 1, fields))));
        var script = new ScriptSpecification(database, ImmutableArray.Create(RuleFor(), rule));
        var exception = Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ScriptRunner(analyzer.Object, new BogusDataGeneratorFactory())
                .RunAsync(script, new Mock<IProgressReporter>().Object));
        Assert.That(exception!.Message, Does.Contain(error switch
        {
            "column" => "Unknown parameter column",
            "mapping" or "fallback" => "Invalid Gender",
            "parameter" => "Unknown FirstName parameter",
            _ => "does not support parameters"
        }));
    }

    [Test]
    public async Task Yaml_parses_generic_parameter_bindings()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, """
                rules:
                  - table: dbo.Clients
                    column: Prenom
                    generator: FirstName
                    parameters:
                      gender:
                        column: Civilite
                        mapping:
                          "M.": Male
                          "Mme": Female
                          "1": Male
                        fallback: Unknown
                  - table: dbo.Clients
                    column: Nom
                    generator: LastName
                """);
            var script = await new YamlScriptDomProvider(path).LoadScriptAsync();
            var parameter = script.Rules[0].Parameters["gender"];
            Assert.That(parameter.Column, Is.EqualTo("Civilite"));
            Assert.That(parameter.Mapping!["1"], Is.EqualTo("Male"));
            Assert.That(parameter.Fallback, Is.EqualTo("Unknown"));
            Assert.That(script.Rules[1].Parameters, Is.Empty);
            Assert.That(script.Rules[1].Generator, Is.EqualTo("LastName"));
        }
        finally { File.Delete(path); }
    }

    [Test]
    public async Task Contextual_plugin_receives_parameters_through_string_and_enum_factory_paths()
    {
        var directory = Directory.CreateTempSubdirectory("rulemask-context-").FullName;
        try
        {
            File.Copy(typeof(ContextualNamePlugin).Assembly.Location, Path.Combine(directory, "TestPlugins.dll"));
            var factory = new PluginDataGeneratorFactory(PluginCatalog.Discover(directory));
            var context = new GenerationContext(new Dictionary<string, string> { ["kind"] = "Business" });
            foreach (Sdk.IDataGenerator generator in new[] { factory.Create("Name"), factory.Create(GeneratorType.Name) })
            {
                var contextual = (IContextualDataGenerator)generator;
                contextual.ValidateParameters(new Dictionary<string, IReadOnlyCollection<string>> { ["kind"] = new[] { "Business" } });
                Assert.That(await contextual.GenerateValueAsync(context), Is.EqualTo("Business"));
            }
        }
        finally { Directory.Delete(directory, true); }
    }
}

[Generator("Name")]
public sealed class ContextualNamePlugin : IContextualDataGenerator
{
    public enum Category { Unknown, Business }
    public void ValidateParameters(IReadOnlyDictionary<string, IReadOnlyCollection<string>> possibleValues)
    {
        foreach (var value in possibleValues.SelectMany(p => p.Value))
            _ = new GenerationContext(new Dictionary<string, string> { ["kind"] = value }).GetEnum("kind", Category.Unknown);
    }
    public Task<object> GenerateValueAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<object>("legacy");
    public Task<object> GenerateValueAsync(GenerationContext context, CancellationToken cancellationToken = default)
        => Task.FromResult<object>(context.GetEnum("kind", Category.Unknown).ToString());
}
