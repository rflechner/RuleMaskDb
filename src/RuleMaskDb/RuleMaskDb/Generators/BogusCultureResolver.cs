using System.Globalization;

namespace RuleMaskDb.Generators;

/// <summary>Translate .NET culture names to verified Bogus locales without regional fallback.</summary>
public static class BogusCultureResolver
{
    private static readonly IReadOnlyDictionary<string, string> Locales =
        Bogus.Database.GetAllLocales().ToDictionary(locale => locale, StringComparer.OrdinalIgnoreCase);

    // Bogus uses a language-only identifier for these specific regional datasets.
    private static readonly IReadOnlyDictionary<string, string> Aliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["af-ZA"] = "af_ZA", ["ar-SA"] = "ar", ["az-AZ"] = "az",
            ["cs-CZ"] = "cz", ["cs"] = "cz", ["da-DK"] = "da",
            ["de-DE"] = "de", ["el-GR"] = "el", ["es-ES"] = "es",
            ["fa-IR"] = "fa", ["fi-FI"] = "fi", ["fr-FR"] = "fr",
            ["hr-HR"] = "hr", ["hy-AM"] = "hy", ["id-ID"] = "id_ID",
            ["it-IT"] = "it", ["ja-JP"] = "ja", ["ko-KR"] = "ko",
            ["lv-LV"] = "lv", ["ne-NP"] = "ne", ["nl-NL"] = "nl",
            ["pl-PL"] = "pl", ["ro-RO"] = "ro", ["ru-RU"] = "ru",
            ["sk-SK"] = "sk", ["sv-SE"] = "sv", ["tr-TR"] = "tr",
            ["uk-UA"] = "uk", ["vi-VN"] = "vi"
        };

    public static string Resolve(CultureInfo culture)
    {
        if (Locales.TryGetValue(culture.Name.Replace('-', '_'), out var exact)) return exact;
        if (Aliases.TryGetValue(culture.Name, out var alias) && Locales.TryGetValue(alias, out var mapped))
            return mapped;
        throw new InvalidOperationException($"Culture '{culture.Name}' is not supported by the installed Bogus locales.");
    }
}
