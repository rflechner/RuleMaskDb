# Generator cultures

Culture is a per-rule generator parameter, not a database or process setting. Use .NET culture names with hyphens, such as `fr-FR`, `fr-CA`, `en-GB`, and `de-CH`. Do not put Bogus identifiers such as `fr_CA` in the YAML.

For addresses and phone numbers, prefer a regional culture or a country-code mapping: language alone does not identify regional formats. The example mapping of Canada to `fr-CA` is an explicit configuration choice, not an inference about the customer's language.

## Fixed culture

```yaml
rules:
  - table: dbo.Customers
    column: PhoneNumber
    generator: PhoneNumber
    parameters:
      culture:
        value: fr-FR
```

Every populated target uses the same culture. No source column is needed.

## Mapped culture

```yaml
rules:
  - table: dbo.Customers
    column: Address
    generator: AddressLine
    parameters:
      culture:
        column: CountryCode
        mapping:
          "FR": fr-FR
          "GB": en-GB
          "DE": de-DE
          "CA": fr-CA
        fallback: fr-FR
```

Values come from the original row, even if another rule anonymizes `CountryCode`. Matching is exact and case-sensitive; SQL NULL or an unmatched value uses `fallback`. Without a fallback the parameter is omitted, so built-in generators use their existing `en` default. An empty string can be explicitly mapped with `"": fr-FR`.

Numeric and boolean source values use invariant string conversion; quote keys such as `"1"` or `"True"`. The mapping mechanism is not specific to culture.

## Direct culture column

```yaml
rules:
  - table: dbo.Customers
    column: City
    generator: City
    parameters:
      culture:
        column: CultureCode
        fallback: fr-FR
```

With no `mapping`, values such as `fr-CA` and `en-GB` pass directly to the generator. SQL NULL and empty strings use the fallback (or omit the parameter if absent). Whitespace is not trimmed and is not treated as empty: it is an invalid culture. A nonempty invalid or unsupported culture fails; it does not use the fallback.

An explicit `mapping: {}` is an empty mapping, not direct passthrough. Every source value then uses the fallback.

## Combining culture and gender

```yaml
rules:
  - table: dbo.Customers
    column: FirstName
    generator: FirstName
    parameters:
      culture:
        value: fr-FR
      gender:
        column: Title
        mapping:
          "Mr": Male
          "Ms": Female
        fallback: Unknown
  - table: dbo.Customers
    column: LastName
    generator: LastName
    parameters:
      culture:
        value: fr-FR
```

Fixed values also work for non-culture parameters, for example `gender: { value: Female }`. A gender-aware first-name request is rejected if the selected Bogus locale lacks gender-specific name lists. Culture and gender selection do not control the number of components in a first name.

## Supported cultures and validation

The adapter first tries an exact supported Bogus locale after translating hyphens to underscores, then an explicit alias table for datasets stored under language-only names. Every result is checked against the locales shipped with the installed Bogus package. There is no automatic parent-culture/regional fallback.

Examples:

| .NET culture | Bogus locale |
| --- | --- |
| `fr-FR` | `fr` |
| `fr-CA` | `fr_CA` |
| `en-GB` | `en_GB` |
| `de-DE` | `de` |
| `de-CH` | `de_CH` |
| `cs-CZ` | `cz` |

The full alias table is in [BogusCultureResolver](../src/RuleMaskDb/RuleMaskDb/Generators/BogusCultureResolver.cs). A valid .NET culture is not necessarily supported by Bogus; for example, an unavailable regional dataset is not silently replaced with a different region.

- Exactly one of `value` or `column` is required.
- A fixed `value` cannot be combined with `mapping` or `fallback`.
- Missing source columns and invalid fixed values, mapping outputs or fallbacks fail before opening update connections.
- Direct-column values cannot all be checked during configuration validation. They are checked when each populated target is processed; errors follow the existing transaction behavior (PostgreSQL rolls back the current table; SQL Server keeps previous autocommitted rows).
- Null or empty target fields are still skipped without invoking the generator.
- Bogus generators are cached per rule and locale, not recreated for every row. Process/thread culture is unchanged.

Locales select Bogus datasets and format templates, not real-world validation. Some datasets are incomplete and Bogus may internally fall back to English for missing entries. Generated numbers are not guaranteed to be assigned or callable, and independently generated address components are not guaranteed to describe a consistent real address. Numeric and DateTime results remain typed values; selecting a culture does not turn them into localized strings.

## Plugins

Context-aware plugins receive the same resolved strings as built-in generators. `context.GetCulture()` returns a read-only .NET `CultureInfo`, or null when omitted. Plugins remain independent of Bogus and decide how to use the culture and which ones they support:

```csharp
var culture = context.GetCulture() ?? CultureInfo.GetCultureInfo("en");
```

Import `System.Globalization` for `CultureInfo`. Validate fixed/mapped/fallback values in `ValidateParameters`, and direct values in `GenerateValueAsync`. Do not change `CultureInfo.CurrentCulture` globally. Old plugins implementing only `IDataGenerator` continue to work without parameters and reject configured parameters explicitly.
