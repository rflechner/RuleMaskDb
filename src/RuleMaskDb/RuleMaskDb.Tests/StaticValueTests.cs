using RuleMaskDb.ScriptDom;
using RuleMaskDb.Yaml;

namespace RuleMaskDb.Tests;

public class StaticValueTests
{
    [TestCase("'redacted'", "redacted")]
    [TestCase("\"\"", "")]
    [TestCase("'00123'", "00123")]
    [TestCase("'null'", "null")]
    [TestCase("'true'", "true")]
    [TestCase("null", null)]
    [TestCase("~", null)]
    [TestCase("", null)]
    public async Task Explicit_value_preserves_strings_and_null(string yaml, string? expected)
    {
        var rule = await LoadRule("value: " + yaml);
        Assert.That(rule.HasValue, Is.True);
        Assert.That(rule.Value, Is.EqualTo(expected));
        Assert.That(rule.Generator, Is.Null);
    }

    [TestCase("42", 42)]
    [TestCase("-3", -3)]
    [TestCase("12.5", 12.5)]
    [TestCase("true", true)]
    [TestCase("false", false)]
    public async Task Unquoted_numbers_and_booleans_are_typed(string yaml, object expected)
    {
        var rule = await LoadRule("value: " + yaml);
        Assert.That(rule.Value, Is.Not.InstanceOf<string>());
        Assert.That(rule.Value, Is.EqualTo(expected));
    }

    [Test]
    public async Task Missing_value_keeps_legacy_generator_behavior()
    {
        var rule = await LoadRule("generator: Name");
        Assert.That(rule.HasValue, Is.False);
        Assert.That(rule.Generator, Is.EqualTo("Name"));
        Assert.That((await LoadRule("")).HasValue, Is.False);
    }

    [TestCase("value: x\n    generator: Name")]
    [TestCase("value: null\n    generator: Name")]
    [TestCase("value: x\n    generator: ''")]
    [TestCase("value: x\n    mask: '***'")]
    [TestCase("value: x\n    parameters: {}")]
    [TestCase("value: [one, two]")]
    [TestCase("value: {nested: value}")]
    public void Invalid_static_rules_are_rejected(string properties)
        => Assert.That(async () => await LoadRule(properties), Throws.InvalidOperationException);

    [Test]
    public void Programmatic_rules_validate_conflicts_and_explicit_null()
    {
        var rule = new Rule("People", "Name", null, (string?)null) { Value = null };
        Assert.That(rule.HasValue, Is.True);
        Assert.DoesNotThrow(rule.ValidateValue);
        Assert.Throws<InvalidOperationException>(() => (rule with { Generator = "Name" }).ValidateValue());
        Assert.Throws<InvalidOperationException>(() => (rule with { Value = new[] { "x" } }).ValidateValue());
    }

    private static async Task<Rule> LoadRule(string properties)
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "rules:\n  - table: People\n    column: Name\n    " + properties + "\n");
            return (await new YamlScriptDomProvider(path).LoadScriptAsync()).Rules.Single();
        }
        finally { File.Delete(path); }
    }
}
