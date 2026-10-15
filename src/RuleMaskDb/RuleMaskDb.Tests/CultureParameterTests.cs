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

[NonParallelizable]
public sealed class CultureParameterTests
{
    private static Rule WithParameter(GeneratorParameter parameter, string name = "culture", string generator = "PhoneNumber")
        => new("dbo.Customers", "PhoneNumber", null, generator)
        {
            Parameters = ImmutableDictionary<string, GeneratorParameter>.Empty.Add(name, parameter)
        };

    [Test]
    public void Fixed_parameters_do_not_require_a_source_column()
    {
        var rule = WithParameter(new GeneratorParameter(Value: "Female"), "gender", "FirstName");
        var context = GeneratorParameterResolver.Resolve(rule, new Dictionary<string, object?>());
        Assert.That(context.GetEnum("gender", Gender.Unknown), Is.EqualTo(Gender.Female));
    }

    [TestCase("fr-FR", "fr-FR")]
    [TestCase("en-GB", "en-GB")]
    [TestCase(null, "fr-FR")]
    [TestCase("", "fr-FR")]
    public void Direct_column_uses_original_value_or_fallback(string? value, string expected)
    {
        var rule = WithParameter(new GeneratorParameter(Column: "CultureCode", Fallback: "fr-FR"));
        var context = GeneratorParameterResolver.Resolve(rule,
            new Dictionary<string, object?> { ["CultureCode"] = value });
        Assert.That(context.GetCulture()!.Name, Is.EqualTo(expected));
    }

    [Test]
    public void Direct_dbnull_without_fallback_omits_parameter()
    {
        var context = GeneratorParameterResolver.Resolve(WithParameter(new GeneratorParameter(Column: "CultureCode")),
            new Dictionary<string, object?> { ["CultureCode"] = DBNull.Value });
        Assert.That(context.GetCulture(), Is.Null);
    }

    [Test]
    public void Empty_mapping_is_not_direct_passthrough()
    {
        var rule = WithParameter(new GeneratorParameter("CultureCode", ImmutableDictionary<string, string>.Empty, "en-GB"));
        var context = GeneratorParameterResolver.Resolve(rule,
            new Dictionary<string, object?> { ["CultureCode"] = "fr-FR" });
        Assert.That(context.GetCulture()!.Name, Is.EqualTo("en-GB"));
    }

    [TestCase("FR", "fr-FR")]
    [TestCase("GB", "en-GB")]
    [TestCase("unknown", "de-DE")]
    [TestCase(null, "de-DE")]
    public void Culture_mapping_uses_configured_codes(string? value, string expected)
    {
        var rule = WithParameter(new GeneratorParameter("CountryCode",
            new Dictionary<string, string> { ["FR"] = "fr-FR", ["GB"] = "en-GB" }.ToImmutableDictionary(), "de-DE"));
        var context = GeneratorParameterResolver.Resolve(rule,
            new Dictionary<string, object?> { ["CountryCode"] = value });
        Assert.That(context.GetCulture()!.Name, Is.EqualTo(expected));
    }

    [TestCase("fr-FR", "fr")]
    [TestCase("fr", "fr")]
    [TestCase("fr-CA", "fr_CA")]
    [TestCase("en-GB", "en_GB")]
    [TestCase("de-DE", "de")]
    [TestCase("de-CH", "de_CH")]
    [TestCase("cs-CZ", "cz")]
    public void Resolves_verified_Bogus_locales(string culture, string expected)
        => Assert.That(BogusCultureResolver.Resolve(CultureInfo.GetCultureInfo(culture)), Is.EqualTo(expected));

    [TestCase("xx-XX")]
    [TestCase("fr-BE")]
    public void Unsupported_regions_do_not_silently_fall_back(string culture)
        => Assert.Throws<InvalidOperationException>(() => BogusCultureResolver.Resolve(CultureInfo.GetCultureInfo(culture)));

    [TestCase("")]
    [TestCase("fr_FR")]
    [TestCase(" fr-FR")]
    [TestCase("not a culture!")]
    public void Invalid_culture_parameter_is_rejected(string culture)
        => Assert.Throws<InvalidOperationException>(() => Context(culture).GetCulture());

    [TestCase("value: fr-FR")]
    [TestCase("column: CultureCode")]
    [TestCase("column: CountryCode\nmapping:\n  FR: fr-FR\nfallback: en-GB")]
    public async Task Yaml_accepts_all_three_binding_modes(string binding)
    {
        var script = await LoadYaml(binding);
        var parameter = script.Rules.Single().Parameters["culture"];
        Assert.That(parameter.Value is not null || parameter.Column is not null, Is.True);
        if (binding == "column: CultureCode") Assert.That(parameter.Mapping, Is.Null);
    }

    [TestCase("value: fr-FR\ncolumn: CultureCode")]
    [TestCase("value: fr-FR\nfallback: en-GB")]
    [TestCase("value: fr-FR\nmapping: {}")]
    [TestCase("column: ''")]
    [TestCase("fallback: fr-FR")]
    public void Yaml_rejects_ambiguous_or_incomplete_bindings(string binding)
        => Assert.ThrowsAsync<InvalidOperationException>(async () => await LoadYaml(binding));

    [TestCase("fixed")]
    [TestCase("mapping")]
    [TestCase("fallback")]
    public void Unsupported_configured_cultures_fail_before_update_connections(string mode)
    {
        var binding = mode switch
        {
            "fixed" => new GeneratorParameter(Value: "fr-BE"),
            "mapping" => new GeneratorParameter("CultureCode",
                new Dictionary<string, string> { ["unused"] = "fr-BE" }.ToImmutableDictionary()),
            _ => new GeneratorParameter(Column: "CultureCode", Fallback: "fr-BE")
        };
        var database = new DatabaseSpecification(DatabaseType.PostgreSQL, "invalid connection string");
        var analyzer = new Mock<IDatabaseAnalyzer>();
        var fields = ImmutableArray.Create(new FieldDescription("Id", DateType.Text, true),
            new FieldDescription("PhoneNumber", DateType.Text, false),
            new FieldDescription("CultureCode", DateType.Text, false));
        analyzer.Setup(a => a.DescribeDatabaseAsync(database)).ReturnsAsync(new DatabaseDescription("test",
            ImmutableArray.Create(new TableDescription("dbo.Customers", 1, fields))));
        var runner = new ScriptRunner(analyzer.Object, new BogusDataGeneratorFactory());
        Assert.That(async () => await runner.RunAsync(new ScriptSpecification(database,
                ImmutableArray.Create(WithParameter(binding))), new Mock<IProgressReporter>().Object),
            Throws.InvalidOperationException.With.Message.Contains("not supported"));
    }

    [TestCase(GeneratorType.PhoneNumber, "fr-FR", "fr")]
    [TestCase(GeneratorType.AddressLine, "en-GB", "en_GB")]
    [TestCase(GeneratorType.City, "de-DE", "de")]
    [TestCase(GeneratorType.ZipCode, "de-CH", "de_CH")]
    [TestCase(GeneratorType.State, "en-GB", "en_GB")]
    [TestCase(GeneratorType.Email, "fr-FR", "fr")]
    [TestCase(GeneratorType.Name, "fr-FR", "fr")]
    [TestCase(GeneratorType.LastName, "fr-CA", "fr_CA")]
    [TestCase(GeneratorType.FirstName, "fr-FR", "fr")]
    public async Task Builtins_use_the_requested_locale(GeneratorType type, string culture, string locale)
    {
        var seed = Randomizer.Seed;
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            Randomizer.Seed = new Random(123);
            var faker = new Faker(locale);
            var expected = type switch
            {
                GeneratorType.PhoneNumber => faker.Phone.PhoneNumber(),
                GeneratorType.AddressLine => faker.Address.StreetAddress(),
                GeneratorType.City => faker.Address.City(),
                GeneratorType.ZipCode => faker.Address.ZipCode(),
                GeneratorType.State => faker.Address.StateAbbr(),
                GeneratorType.Email => faker.Internet.Email(),
                GeneratorType.Name => faker.Name.FullName(),
                GeneratorType.LastName => faker.Name.LastName(),
                _ => faker.Name.FirstName()
            };
            Randomizer.Seed = new Random(123);
            var generator = (IContextualDataGenerator)new BogusDataGeneratorFactory().Create(type);
            generator.ValidateParameters(new Dictionary<string, IReadOnlyCollection<string>>
                { ["culture"] = new[] { culture } });
            Assert.That(await generator.GenerateValueAsync(Context(culture)), Is.EqualTo(expected));
            Assert.That(CultureInfo.CurrentCulture, Is.SameAs(originalCulture));
            Assert.That(CultureInfo.CurrentUICulture, Is.SameAs(originalUiCulture));
        }
        finally { Randomizer.Seed = seed; }
    }

    [Test]
    public async Task Same_generator_switches_culture_per_row_and_retains_default()
    {
        var seed = Randomizer.Seed;
        try
        {
            var cases = new[] { ("fr-FR", "fr"), ("en-GB", "en_GB"), ("fr-FR", "fr"), (null, "en") };
            Randomizer.Seed = new Random(234);
            var fakers = new Dictionary<string, Faker>();
            var expected = cases.Select(pair =>
            {
                if (!fakers.TryGetValue(pair.Item2, out var faker))
                    fakers.Add(pair.Item2, faker = new Faker(pair.Item2));
                return faker.Phone.PhoneNumber();
            }).ToArray();
            Randomizer.Seed = new Random(234);
            var generator = (IContextualDataGenerator)new BogusDataGeneratorFactory().Create(GeneratorType.PhoneNumber);
            for (var i = 0; i < cases.Length; i++)
            {
                var culture = cases[i].Item1;
                var context = culture is null ? GenerationContext.Empty : Context(culture);
                Assert.That(await generator.GenerateValueAsync(context), Is.EqualTo(expected[i]));
            }
            Assert.ThrowsAsync<InvalidOperationException>(() => generator.GenerateValueAsync(Context("fr-BE")));
        }
        finally { Randomizer.Seed = seed; }
    }

    [Test]
    public async Task FirstName_combines_culture_and_gender()
    {
        var seed = Randomizer.Seed;
        try
        {
            Randomizer.Seed = new Random(345);
            var expected = new Faker("fr").Name.FirstName(Bogus.DataSets.Name.Gender.Female);
            Randomizer.Seed = new Random(345);
            var generator = (IContextualDataGenerator)new BogusDataGeneratorFactory().Create(GeneratorType.FirstName);
            var context = new GenerationContext(new Dictionary<string, string> { ["culture"] = "fr-FR", ["gender"] = "Female" });
            generator.ValidateParameters(context.Parameters.ToDictionary(p => p.Key,
                p => (IReadOnlyCollection<string>)new[] { p.Value }));
            Assert.That(await generator.GenerateValueAsync(context), Is.EqualTo(expected));
        }
        finally { Randomizer.Seed = seed; }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Plugins_receive_fixed_and_direct_cultures_without_Bogus_dependency(bool direct)
    {
        var directory = Directory.CreateTempSubdirectory("rulemask-culture-plugin-").FullName;
        try
        {
            File.Copy(typeof(CultureEchoPlugin).Assembly.Location, Path.Combine(directory, "TestPlugins.dll"));
            var factory = new PluginDataGeneratorFactory(PluginCatalog.Discover(directory));
            var generator = (IContextualDataGenerator)factory.Create("CultureEcho");
            var binding = direct ? new GeneratorParameter(Column: "CultureCode") : new GeneratorParameter(Value: "fr-FR");
            var rule = WithParameter(binding, generator: "CultureEcho");
            var context = GeneratorParameterResolver.Resolve(rule,
                new Dictionary<string, object?> { ["CultureCode"] = "en-GB" });
            Assert.That(await generator.GenerateValueAsync(context), Is.EqualTo(direct ? "en-GB" : "fr-FR"));
        }
        finally { Directory.Delete(directory, true); }
    }

    private static GenerationContext Context(string culture)
        => new(new Dictionary<string, string> { ["culture"] = culture });

    private static async Task<ScriptSpecification> LoadYaml(string binding)
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, $"""
                rules:
                  - table: dbo.Customers
                    column: PhoneNumber
                    generator: PhoneNumber
                    parameters:
                      culture:
                        {binding.Replace("\n", "\n        ")}
                """);
            return await new YamlScriptDomProvider(path).LoadScriptAsync();
        }
        finally { File.Delete(path); }
    }
}

[Generator("CultureEcho")]
public sealed class CultureEchoPlugin : IContextualDataGenerator
{
    public void ValidateParameters(IReadOnlyDictionary<string, IReadOnlyCollection<string>> possibleValues)
    {
        foreach (var (name, values) in possibleValues)
        {
            if (name != "culture") throw new InvalidOperationException($"Unknown parameter: {name}");
            foreach (var value in values)
                _ = new GenerationContext(new Dictionary<string, string> { ["culture"] = value }).GetCulture();
        }
    }
    public Task<object> GenerateValueAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<object>("en");
    public Task<object> GenerateValueAsync(GenerationContext context, CancellationToken cancellationToken = default)
        => Task.FromResult<object>(context.GetCulture()?.Name ?? "en");
}
