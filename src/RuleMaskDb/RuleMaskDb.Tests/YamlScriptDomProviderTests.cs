using RuleMaskDb.Yaml;

namespace RuleMaskDb.Tests;

public class YamlScriptDomProviderTests
{
    [Test]
    public async Task LoadScriptAsync_parses_yaml_correctly()
    {
        // Arrange: locate the sample YAML in the test output directory
        var assetPath = Path.Combine(TestContext.CurrentContext.TestDirectory, "Assets", "SampleRules1.yaml");
        Assert.That(File.Exists(assetPath), Is.True, $"Asset not found: {assetPath}");

        var provider = new YamlScriptDomProvider(assetPath);

        // Act
        var spec = await provider.LoadScriptAsync();

        // Assert database specification
        Assert.That(spec.Database.DatabaseType, Is.EqualTo(DatabaseType.SqlServer));
        Assert.That(spec.Database.ConnectionString, Does.Contain("Server=localhost").And.Contain("Database=ContosoRetailDW"));

        // Assert rules
        Assert.That(spec.Rules.Length, Is.EqualTo(2));

        var rule1 = spec.Rules[0];
        Assert.Multiple(() =>
        {
            Assert.That(rule1.Table, Is.EqualTo("DimCustomer"));
            Assert.That(rule1.Column, Is.EqualTo("FirstName"));
            Assert.That(rule1.Mask, Is.EqualTo("**"));
            Assert.That(rule1.Generator, Is.Null);
        });

        var rule2 = spec.Rules[1];
        Assert.Multiple(() =>
        {
            Assert.That(rule2.Table, Is.EqualTo("DimCustomer"));
            Assert.That(rule2.Column, Is.EqualTo("MiddleName"));
            Assert.That(rule2.Mask, Is.Null);
            Assert.That(rule2.Generator, Is.EqualTo(GeneratorType.Name));
        });
    }
}