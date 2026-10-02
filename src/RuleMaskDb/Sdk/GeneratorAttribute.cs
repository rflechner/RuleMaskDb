namespace RuleMaskDb.Sdk;

/// <summary>Names a public generator class with a public parameterless constructor.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class GeneratorAttribute(string name) : Attribute
{
    public string Name { get; } = !string.IsNullOrWhiteSpace(name)
        ? name.Trim() : throw new ArgumentException("A generator name is required.", nameof(name));
}
