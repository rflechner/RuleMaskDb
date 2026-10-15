using System.Collections.ObjectModel;
using System.Globalization;

namespace RuleMaskDb.Sdk;

/// <summary>Resolved parameters from the original database row, before any anonymization.</summary>
public sealed class GenerationContext
{
    public static GenerationContext Empty { get; } = new(new Dictionary<string, string>());
    public IReadOnlyDictionary<string, string> Parameters { get; }

    public GenerationContext(IReadOnlyDictionary<string, string> parameters)
        => Parameters = new ReadOnlyDictionary<string, string>(
            parameters.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal));

    public TEnum GetEnum<TEnum>(string name, TEnum defaultValue) where TEnum : struct, Enum
    {
        if (!Parameters.TryGetValue(name, out var value)) return defaultValue;
        var match = Enum.GetNames<TEnum>().FirstOrDefault(n => n.Equals(value, StringComparison.OrdinalIgnoreCase));
        if (match is null) throw new InvalidOperationException($"Invalid {typeof(TEnum).Name} value for parameter '{name}': {value}");
        return Enum.Parse<TEnum>(match);
    }

    /// <summary>Read a .NET culture without changing the process or thread culture.</summary>
    public CultureInfo? GetCulture(string name = "culture")
    {
        if (!Parameters.TryGetValue(name, out var value)) return null;
        if (string.IsNullOrWhiteSpace(value) || value.Contains('_') || value != value.Trim())
            throw new InvalidOperationException($"Invalid culture for parameter '{name}': {value}");
        try
        {
            return CultureInfo.GetCultureInfo(value);
        }
        catch (CultureNotFoundException exception)
        {
            throw new InvalidOperationException($"Invalid culture for parameter '{name}': {value}", exception);
        }
    }
}
