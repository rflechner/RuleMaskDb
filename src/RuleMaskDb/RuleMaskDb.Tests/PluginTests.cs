using Moq;
using RuleMaskDb.Generators;
using RuleMaskDb.LocalEmailPlugin;
using RuleMaskDb.Sdk;
using RuleMaskDb.Yaml;

namespace RuleMaskDb.Tests;

public sealed class PluginTests
{
    private string directory = null!;

    [SetUp]
    public void SetUp() => directory = Directory.CreateTempSubdirectory("rulemask-plugins-").FullName;

    [TearDown]
    public void TearDown() => Directory.Delete(directory, recursive: true);

    private void CopyPlugin() => File.Copy(typeof(LocalEmailGenerator).Assembly.Location,
        Path.Combine(directory, "RuleMaskDb.LocalEmailPlugin.dll"));

    [Test]
    public async Task Discovers_dll_and_resolves_custom_yaml_name_with_shared_sdk()
    {
        CopyPlugin();
        File.Copy(typeof(GeneratorAttribute).Assembly.Location, Path.Combine(directory, "RuleMaskDb.Sdk.dll"));
        var path = Path.Combine(directory, "rules.yaml");
        await File.WriteAllTextAsync(path, "rules:\n  - table: public.people\n    column: email\n    generator: localemail\n");
        var script = await new YamlScriptDomProvider(path).LoadScriptAsync();
        var catalog = PluginCatalog.Discover(directory);
        Assert.That(catalog.Plugins.Select(p => p.Name), Is.EqualTo(new[] { "LocalEmail" }));
        var generator = new PluginDataGeneratorFactory(catalog).Create(script.Rules.Single().Generator!);
        Assert.That(generator.GetType().Assembly, Is.Not.SameAs(typeof(LocalEmailGenerator).Assembly));
        for (var i = 0; i < 10; i++)
            Assert.That(await generator.GenerateValueAsync(), Does.EndWith("@local"));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.ThrowsAsync<OperationCanceledException>(async () => await generator.GenerateValueAsync(cancelled.Token));
    }

    [Test]
    public async Task Plugins_override_builtin_names_case_insensitively_including_enum_api()
    {
        File.Copy(typeof(EmailOverride).Assembly.Location, Path.Combine(directory, "TestPlugins.dll"));
        var factory = new PluginDataGeneratorFactory(PluginCatalog.Discover(directory));
        Assert.That(await factory.Create("eMaIl").GenerateValueAsync(), Is.EqualTo("override@local"));
        Assert.That(await factory.Create(ScriptDom.GeneratorType.Email).GenerateValueAsync(), Is.EqualTo("override@local"));
    }

    [Test]
    public async Task Missing_directory_uses_builtins_and_rejects_unknown_names()
    {
        var catalog = PluginCatalog.Discover(Path.Combine(directory, "missing"));
        Assert.That(catalog.Plugins, Is.Empty);
        var factory = new PluginDataGeneratorFactory(catalog);
        Assert.That(await factory.Create("firstNAME").GenerateValueAsync(), Is.TypeOf<string>());
        Assert.Throws<InvalidOperationException>(() => factory.Create("Typo"));
        Assert.Throws<InvalidOperationException>(() => factory.Create("0"));
    }

    [Test]
    public void Unknown_generator_in_later_table_is_rejected_before_opening_update_connections()
    {
        var analyzer = new Moq.Mock<ScriptDom.IDatabaseAnalyzer>();
        var database = new ScriptDom.DatabaseSpecification(ScriptDom.DatabaseType.PostgreSQL, "invalid connection string");
        var fields = System.Collections.Immutable.ImmutableArray.Create(
            new SqlDomain.FieldDescription("id", DateType.Text, true),
            new SqlDomain.FieldDescription("email", DateType.Text, false));
        analyzer.Setup(a => a.DescribeDatabaseAsync(database)).ReturnsAsync(new ScriptDom.DatabaseDescription("test",
            System.Collections.Immutable.ImmutableArray.Create(
                new SqlDomain.TableDescription("public.first", 1, fields),
                new SqlDomain.TableDescription("public.second", 1, fields))));
        var script = new ScriptDom.ScriptSpecification(database,
            System.Collections.Immutable.ImmutableArray.Create(
                new ScriptDom.Rule("public.first", "email", null, "Email"),
                new ScriptDom.Rule("public.second", "email", null, "Typo")));
        var runner = new ScriptRunner(analyzer.Object, new PluginDataGeneratorFactory(PluginCatalog.Discover(directory)));
        Assert.That(async () => await runner.RunAsync(script, new Moq.Mock<IProgressReporter>().Object),
            Throws.InvalidOperationException.With.Message.EqualTo("Unknown generator: Typo"));
    }

    [Test]
    public void Duplicate_plugin_names_are_rejected_instead_of_depending_on_file_order()
    {
        CopyPlugin();
        var nested = Directory.CreateDirectory(Path.Combine(directory, "second")).FullName;
        File.Copy(typeof(LocalEmailGenerator).Assembly.Location, Path.Combine(nested, "Duplicate.dll"));
        Assert.That(() => PluginCatalog.Discover(directory), Throws.InvalidOperationException.With.Message.Contains("Duplicate plugin generator 'LocalEmail'"));
    }
}

[Generator("Email")]
public sealed class EmailOverride : Sdk.IDataGenerator
{
    public Task<object> GenerateValueAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<object>("override@local");
}
