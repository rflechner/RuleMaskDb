# Bogus generators from YAML

This reference describes the **Bogus 35.6.5** dependency installed by RuleMaskDb:
**247 qualified generator names, 254 signatures, 18 categories**. It includes the shipped
dataset APIs, random-value APIs, Person fields and data-generating extension methods.
The catalog is discovered from the pinned Bogus assembly, not from arbitrary assemblies or type names.
The [upstream project](https://github.com/bchavez/Bogus) documents Bogus's native behavior.

## Choosing a generator

Set `generator: Bogus.<Category>.<Method>`. Names are case-insensitive; parameter names are
case-sensitive and match Bogus exactly. Person fields also support paths such as
`Bogus.Person.Address.City`. Run the following from the repository root to inspect the
catalog installed in your build, without a database connection:

```powershell
dotnet run --project src/RuleMaskDb/RuleMaskDb.ConsoleApp -- generators
```

The legacy names (`FirstName`, `Name`, `LastName`, etc.) and plugin discovery/precedence remain
unchanged. `Name` still means a full name. The fully qualified names call the native Bogus API.
Country-specific generators are included under their receiver, for example
`Bogus.Person.Ssn`, `Bogus.Person.Cpf`, `Bogus.Finance.Nino`, `Bogus.Company.Ein` and
`Bogus.Vehicle.GbRegistrationPlate`. They retain their country-specific meaning regardless of culture.

## Arguments

Use the existing `parameters` bindings. Every argument supports a fixed `value`, an original-row
`column`, or a column with `mapping` and `fallback`. `culture` is reserved by RuleMaskDb and
selects the Bogus locale using the existing .NET culture mapping.

```yaml
rules:
  - table: dbo.Customers
    column: PhoneNumber
    generator: Bogus.Phone.PhoneNumber
    parameters:
      culture:
        column: CountryCode
        mapping: { FR: fr-FR, GB: en-GB }
        fallback: en-US
  - table: dbo.Customers
    column: FirstName
    generator: Bogus.Name.FirstName
    parameters:
      gender:
        column: GenderCode
        mapping: { F: Female, M: Male }
        fallback: Unknown
      culture: { value: fr-FR }
  - table: dbo.Customers
    column: CreditLimit
    generator: Bogus.Finance.Amount
    parameters:
      min: { value: '100.00' }
      max: { value: '5000.00' }
      decimals: { value: '2' }
```

See [the complete example](examples/bogus-generators.yaml) for dates, IBANs, choices and text.

- Required arguments must be configured. Omitted optional arguments use the defaults from the installed
  Bogus version. To use a native null default, omit the argument.
- Overloads are selected by supplied parameter names and compatible value types. For example,
  `Bogus.Random.String2` accepts either `length` or `minLength` and `maxLength`.
  Equivalent overloads are resolved deterministically.
- Numeric values use invariant culture: `'1.25'`, not `'1,25'`. Booleans use `'true'`/`'false'`;
  characters contain exactly one character. Non-finite floats are rejected.
- Enum values use names, case-insensitively, never numeric enum ordinals. The tables list allowed names.
  Nullable `Name.Gender` also accepts `Unknown`, meaning no gender restriction.
- Dates/times should use ISO forms: `'2020-01-01T00:00:00Z'`, `'2020-01-01'`, `'12:30:00'`.
  TimeSpan arguments use invariant .NET duration syntax, such as `'1.00:00:00'` (one day).
- `Finance.CreditCardNumber.provider` accepts the predefined card-type names:
  `Visa`, `Mastercard`, `Discover`, `AmericanExpress`, `DinersClub`, `Jcb`, `Switch`,
  `Solo`, `Maestro`, `Laser`, `Instapayment`. Omit it to let Bogus choose.
- Arrays/collections are JSON encoded inside a YAML string. For example,
  `array: { value: '["Retail", "Business", "Partner"]' }`.
  Generic collection APIs are specialized to strings; `WeightedRandom.weights` takes a JSON numeric array.
- Unknown arguments, missing required bindings and invalid fixed/mapped/fallback conversions fail during
  preflight. Direct-column values are checked when the row is read. If a required value is absent in a row,
  generation fails; configure a fallback where necessary.
- Bogus's semantic constraints still apply at generation time (ranges, collection lengths, country codes,
  locale coverage, format indexes, etc.). Fixed values are type-checked without generating preview values.
  Qualified methods preserve native locale fallback and gender support. The legacy `FirstName` alias
  retains its explicit check for locales without gender-specific first names.

## Lorem ipsum

Use the [complete Lorem ipsum example](examples/lorem-ipsum.yaml) to replace descriptions,
summaries and notes with generated placeholder text.

```yaml
rules:
  - table: dbo.Products
    column: Description
    generator: Bogus.Lorem.Paragraph
    parameters:
      min: { value: '3' }

  - table: dbo.Products
    column: Summary
    generator: Bogus.Lorem.Sentence
    parameters:
      wordCount: { value: '12' }
      range: { value: '0' }

  - table: dbo.Products
    column: InternalNotes
    generator: Bogus.Lorem.Paragraphs
    parameters:
      count: { value: '2' }
      separator: { value: "\n\n" }
```

The first rule generates a paragraph with 3 to 6 sentences. The second generates one sentence with
12 words (`range: '0'` adds no extra words). The third generates two paragraphs separated by a blank
line; YAML double quotes turn `"\n\n"` into two newline characters.

These generators return text directly. `Bogus.Lorem.Words` returns a collection, which RuleMaskDb
stores as JSON text; use `Sentence` or `Paragraph` for prose.
The existing `culture` binding can also be supplied, subject to Bogus's locale data and fallback.
Output length varies and is not a maximum character count; use a sufficiently large text column.
SQL NULLs and empty strings remain unchanged.

## Values written to the database

| Bogus result | Database parameter value |
| --- | --- |
| String, Boolean, signed number, Decimal, DateTime, DateTimeOffset, TimeSpan, Guid | Native typed value |
| SByte / UInt16 / UInt32 / UInt64 | Int16 / Int32 / Int64 / Decimal |
| DateOnly / TimeOnly | DateTime at midnight / TimeSpan |
| Char / Char[] | Text |
| Byte[] | Binary |
| Enum, IP address, IP endpoint, Version, Exception | Text |
| Other collections and structured values (e.g. Finance.Currency, Person.Address) | JSON text |

Match the target column's SQL type, length and constraints. Collection output is not joined into a
human-readable phrase; choose `Lorem.Sentence` instead of `Lorem.Words` when you want prose.
Each Person call creates a fresh profile. Different columns use independent generators and do not
share one coherent Person. Existing null/empty-field preservation and transaction behavior are unchanged.
Image APIs produce URLs or data URI strings; RuleMaskDb does not download images or verify remote services.

## Scope

The catalog exposes data generation, not arbitrary C# execution. These APIs are deliberately excluded:

- `Random.Enum<T>` and `Random.EnumValues<T>` require an application-defined enum type.
  Use `Random.ArrayElement` or `Random.ArrayElements` with a JSON list of the desired names.
- `Random.ReplaceSymbols` requires a C# callback. Use `Random.Replace` or `Random.ReplaceNumbers`.
- Internal BSON/non-generic Array overloads are omitted; the corresponding string collection generators
  are available through the same names.
- Faker<T> object-building rules, configuration setters, general C# extension helpers and Premium
  datasets not shipped in this dependency are not generators in this catalog. Custom behavior can use a plugin.

## Complete catalog

The following signatures are generated from `BogusGeneratorCatalog.Generators`.
“Optional” means that the native Bogus default is used when omitted. Result types below are the
native API types; the conversion table above describes the actual database parameter.
All entries additionally accept `culture`.

### Address

| Generator | Parameters | Native result |
| --- | --- | --- |
| `Bogus.Address.BuildingNumber` | — | String |
| `Bogus.Address.CardinalDirection` | useAbbreviation: Boolean (optional) | String |
| `Bogus.Address.City` | — | String |
| `Bogus.Address.CityPrefix` | — | String |
| `Bogus.Address.CitySuffix` | — | String |
| `Bogus.Address.Country` | — | String |
| `Bogus.Address.CountryCode` | format: Alpha2\|Alpha3 (optional) | String |
| `Bogus.Address.CountryOfUnitedKingdom` | — | String |
| `Bogus.Address.County` | — | String |
| `Bogus.Address.Direction` | useAbbreviation: Boolean (optional) | String |
| `Bogus.Address.FullAddress` | — | String |
| `Bogus.Address.Latitude` | min: Double (optional), max: Double (optional) | Double |
| `Bogus.Address.Longitude` | min: Double (optional), max: Double (optional) | Double |
| `Bogus.Address.OrdinalDirection` | useAbbreviation: Boolean (optional) | String |
| `Bogus.Address.SecondaryAddress` | — | String |
| `Bogus.Address.State` | — | String |
| `Bogus.Address.StateAbbr` | — | String |
| `Bogus.Address.StreetAddress` | useFullAddress: Boolean (optional) | String |
| `Bogus.Address.StreetName` | — | String |
| `Bogus.Address.StreetSuffix` | — | String |
| `Bogus.Address.ZipCode` | format: String (optional) | String |

### Commerce

| Generator | Parameters | Native result |
| --- | --- | --- |
| `Bogus.Commerce.Categories` | num: Int32 | String[] |
| `Bogus.Commerce.Color` | — | String |
| `Bogus.Commerce.Department` | max: Int32 (optional), returnMax: Boolean (optional) | String |
| `Bogus.Commerce.Ean13` | — | String |
| `Bogus.Commerce.Ean8` | — | String |
| `Bogus.Commerce.Price` | min: Decimal (optional), max: Decimal (optional), decimals: Int32 (optional), symbol: String (optional) | String |
| `Bogus.Commerce.Product` | — | String |
| `Bogus.Commerce.ProductAdjective` | — | String |
| `Bogus.Commerce.ProductDescription` | — | String |
| `Bogus.Commerce.ProductMaterial` | — | String |
| `Bogus.Commerce.ProductName` | — | String |

### Company

| Generator | Parameters | Native result |
| --- | --- | --- |
| `Bogus.Company.Bs` | — | String |
| `Bogus.Company.CatchPhrase` | — | String |
| `Bogus.Company.Cnpj` | includeFormatSymbols: Boolean (optional) | String |
| `Bogus.Company.CompanyName` | formatIndex: Int32? (optional) | String |
| `Bogus.Company.CompanyName` | format: String | String |
| `Bogus.Company.CompanySuffix` | — | String |
| `Bogus.Company.Ein` | — | String |
| `Bogus.Company.Nip` | — | String |
| `Bogus.Company.Nipc` | — | String |
| `Bogus.Company.Regon` | type: Regon9\|Regon14\|Random (optional) | String |

### Database

| Generator | Parameters | Native result |
| --- | --- | --- |
| `Bogus.Database.Collation` | — | String |
| `Bogus.Database.Column` | — | String |
| `Bogus.Database.Engine` | — | String |
| `Bogus.Database.Type` | — | String |

### Date

| Generator | Parameters | Native result |
| --- | --- | --- |
| `Bogus.Date.Between` | start: DateTime, end: DateTime | DateTime |
| `Bogus.Date.BetweenDateOnly` | start: DateOnly, end: DateOnly | DateOnly |
| `Bogus.Date.BetweenOffset` | start: DateTimeOffset, end: DateTimeOffset | DateTimeOffset |
| `Bogus.Date.BetweenTimeOnly` | start: TimeOnly, end: TimeOnly | TimeOnly |
| `Bogus.Date.Future` | yearsToGoForward: Int32 (optional), refDate: DateTime? (optional) | DateTime |
| `Bogus.Date.FutureDateOnly` | yearsToGoForward: Int32 (optional), refDate: DateOnly? (optional) | DateOnly |
| `Bogus.Date.FutureOffset` | yearsToGoForward: Int32 (optional), refDate: DateTimeOffset? (optional) | DateTimeOffset |
| `Bogus.Date.Month` | abbreviation: Boolean (optional), useContext: Boolean (optional) | String |
| `Bogus.Date.Past` | yearsToGoBack: Int32 (optional), refDate: DateTime? (optional) | DateTime |
| `Bogus.Date.PastDateOnly` | yearsToGoBack: Int32 (optional), refDate: DateOnly? (optional) | DateOnly |
| `Bogus.Date.PastOffset` | yearsToGoBack: Int32 (optional), refDate: DateTimeOffset? (optional) | DateTimeOffset |
| `Bogus.Date.Recent` | days: Int32 (optional), refDate: DateTime? (optional) | DateTime |
| `Bogus.Date.RecentDateOnly` | days: Int32 (optional), refDate: DateOnly? (optional) | DateOnly |
| `Bogus.Date.RecentOffset` | days: Int32 (optional), refDate: DateTimeOffset? (optional) | DateTimeOffset |
| `Bogus.Date.RecentTimeOnly` | mins: Int32 (optional), refTime: TimeOnly? (optional) | TimeOnly |
| `Bogus.Date.Soon` | days: Int32 (optional), refDate: DateTime? (optional) | DateTime |
| `Bogus.Date.SoonDateOnly` | days: Int32 (optional), refDate: DateOnly? (optional) | DateOnly |
| `Bogus.Date.SoonOffset` | days: Int32 (optional), refDate: DateTimeOffset? (optional) | DateTimeOffset |
| `Bogus.Date.SoonTimeOnly` | mins: Int32 (optional), refTime: TimeOnly? (optional) | TimeOnly |
| `Bogus.Date.TimeZoneString` | — | String |
| `Bogus.Date.Timespan` | maxSpan: TimeSpan? (optional) | TimeSpan |
| `Bogus.Date.Weekday` | abbreviation: Boolean (optional), useContext: Boolean (optional) | String |

### Finance

| Generator | Parameters | Native result |
| --- | --- | --- |
| `Bogus.Finance.Account` | length: Int32 (optional) | String |
| `Bogus.Finance.AccountName` | — | String |
| `Bogus.Finance.Amount` | min: Decimal (optional), max: Decimal (optional), decimals: Int32 (optional) | Decimal |
| `Bogus.Finance.Bic` | — | String |
| `Bogus.Finance.BitcoinAddress` | — | String |
| `Bogus.Finance.CodiceFiscale` | lastName: String, firstName: String, birthday: DateTime, isMale: Boolean, validChecksum: Boolean (optional) | String |
| `Bogus.Finance.CreditCardCvv` | — | String |
| `Bogus.Finance.CreditCardNumber` | provider: CardType (optional) | String |
| `Bogus.Finance.CreditCardNumberLastFourDigits` | — | String |
| `Bogus.Finance.CreditCardNumberObfuscated` | separator: String (optional) | String |
| `Bogus.Finance.Currency` | includeFundCodes: Boolean (optional) | Currency |
| `Bogus.Finance.EthereumAddress` | — | String |
| `Bogus.Finance.Iban` | formatted: Boolean (optional), countryCode: String (optional) | String |
| `Bogus.Finance.LitecoinAddress` | — | String |
| `Bogus.Finance.Nino` | includeSeparator: Boolean (optional) | String |
| `Bogus.Finance.RoutingNumber` | — | String |
| `Bogus.Finance.SortCode` | includeSeparator: Boolean (optional) | String |
| `Bogus.Finance.TransactionType` | — | String |
| `Bogus.Finance.VatNumber` | registrationNumberType: Standard\|BranchTrader\|GovernmentDepartment\|HealthAuthority, includeSeparator: Boolean (optional) | String |

### Hacker

| Generator | Parameters | Native result |
| --- | --- | --- |
| `Bogus.Hacker.Abbreviation` | — | String |
| `Bogus.Hacker.Adjective` | — | String |
| `Bogus.Hacker.IngVerb` | — | String |
| `Bogus.Hacker.Noun` | — | String |
| `Bogus.Hacker.Phrase` | — | String |
| `Bogus.Hacker.Verb` | — | String |

### Image

| Generator | Parameters | Native result |
| --- | --- | --- |
| `Bogus.Image.DataUri` | width: Int32, height: Int32, htmlColor: String (optional) | String |
| `Bogus.Image.LoremFlickrUrl` | width: Int32 (optional), height: Int32 (optional), keywords: String (optional), grascale: Boolean (optional), matchAllKeywords: Boolean (optional), lockId: Int32? (optional) | String |
| `Bogus.Image.PicsumUrl` | width: Int32 (optional), height: Int32 (optional), grayscale: Boolean (optional), blur: Boolean (optional), imageId: Int32? (optional) | String |
| `Bogus.Image.PlaceImgUrl` | width: Int32 (optional), height: Int32 (optional), category: String (optional), filter: Grayscale\|Sepia? (optional) | String |
| `Bogus.Image.PlaceholderUrl` | width: Int32, height: Int32, text: String (optional), backColor: String (optional), textColor: String (optional), format: String (optional) | String |

### Internet

| Generator | Parameters | Native result |
| --- | --- | --- |
| `Bogus.Internet.Avatar` | ipfsGatewayRootUrl: String (optional) | String |
| `Bogus.Internet.Color` | baseRed: Byte (optional), baseGreen: Byte (optional), baseBlue: Byte (optional), grayscale: Boolean (optional), format: Hex\|Rgb\|Delimited (optional) | String |
| `Bogus.Internet.DomainName` | — | String |
| `Bogus.Internet.DomainSuffix` | — | String |
| `Bogus.Internet.DomainWord` | — | String |
| `Bogus.Internet.Email` | firstName: String (optional), lastName: String (optional), provider: String (optional), uniqueSuffix: String (optional) | String |
| `Bogus.Internet.ExampleEmail` | firstName: String (optional), lastName: String (optional) | String |
| `Bogus.Internet.Ip` | — | String |
| `Bogus.Internet.IpAddress` | — | IPAddress |
| `Bogus.Internet.IpEndPoint` | — | IPEndPoint |
| `Bogus.Internet.Ipv6` | — | String |
| `Bogus.Internet.Ipv6Address` | — | IPAddress |
| `Bogus.Internet.Ipv6EndPoint` | — | IPEndPoint |
| `Bogus.Internet.Mac` | separator: String (optional) | String |
| `Bogus.Internet.Password` | length: Int32 (optional), memorable: Boolean (optional), regexPattern: String (optional), prefix: String (optional) | String |
| `Bogus.Internet.Port` | — | Int32 |
| `Bogus.Internet.Protocol` | — | String |
| `Bogus.Internet.Url` | — | String |
| `Bogus.Internet.UrlRootedPath` | fileExt: String (optional) | String |
| `Bogus.Internet.UrlWithPath` | protocol: String (optional), domain: String (optional), fileExt: String (optional) | String |
| `Bogus.Internet.UserAgent` | — | String |
| `Bogus.Internet.UserName` | firstName: String (optional), lastName: String (optional) | String |
| `Bogus.Internet.UserNameUnicode` | firstName: String (optional), lastName: String (optional) | String |

### Lorem

| Generator | Parameters | Native result |
| --- | --- | --- |
| `Bogus.Lorem.Letter` | num: Int32 (optional) | String |
| `Bogus.Lorem.Lines` | lineCount: Int32? (optional), separator: String (optional) | String |
| `Bogus.Lorem.Paragraph` | min: Int32 (optional) | String |
| `Bogus.Lorem.Paragraphs` | count: Int32 (optional), separator: String (optional) | String |
| `Bogus.Lorem.Paragraphs` | min: Int32, max: Int32, separator: String (optional) | String |
| `Bogus.Lorem.Sentence` | wordCount: Int32? (optional), range: Int32? (optional) | String |
| `Bogus.Lorem.Sentences` | sentenceCount: Int32? (optional), separator: String (optional) | String |
| `Bogus.Lorem.Slug` | wordcount: Int32 (optional) | String |
| `Bogus.Lorem.Text` | — | String |
| `Bogus.Lorem.Word` | — | String |
| `Bogus.Lorem.Words` | num: Int32 (optional) | String[] |

### Music

| Generator | Parameters | Native result |
| --- | --- | --- |
| `Bogus.Music.Genre` | — | String |

### Name

| Generator | Parameters | Native result |
| --- | --- | --- |
| `Bogus.Name.FindName` | firstName: String (optional), lastName: String (optional), withPrefix: Boolean? (optional), withSuffix: Boolean? (optional), gender: Male\|Female? (optional) | String |
| `Bogus.Name.FirstName` | gender: Male\|Female? (optional) | String |
| `Bogus.Name.FullName` | gender: Male\|Female? (optional) | String |
| `Bogus.Name.JobArea` | — | String |
| `Bogus.Name.JobDescriptor` | — | String |
| `Bogus.Name.JobTitle` | — | String |
| `Bogus.Name.JobType` | — | String |
| `Bogus.Name.LastName` | gender: Male\|Female? (optional) | String |
| `Bogus.Name.Prefix` | gender: Male\|Female? (optional) | String |
| `Bogus.Name.Suffix` | — | String |

### Person

| Generator | Parameters | Native result |
| --- | --- | --- |
| `Bogus.Person.Address` | — | CardAddress |
| `Bogus.Person.Address.City` | — | String |
| `Bogus.Person.Address.Geo` | — | CardGeo |
| `Bogus.Person.Address.State` | — | String |
| `Bogus.Person.Address.Street` | — | String |
| `Bogus.Person.Address.Suite` | — | String |
| `Bogus.Person.Address.ZipCode` | — | String |
| `Bogus.Person.Avatar` | — | String |
| `Bogus.Person.Cnp` | — | String |
| `Bogus.Person.CodiceFiscale` | validChecksum: Boolean (optional) | String |
| `Bogus.Person.Company` | — | CardCompany |
| `Bogus.Person.Company.Bs` | — | String |
| `Bogus.Person.Company.CatchPhrase` | — | String |
| `Bogus.Person.Company.Name` | — | String |
| `Bogus.Person.Cpf` | includeFormatSymbols: Boolean (optional) | String |
| `Bogus.Person.Cpr` | validChecksum: Boolean (optional), includeDash: Boolean (optional) | String |
| `Bogus.Person.DateOfBirth` | — | DateTime |
| `Bogus.Person.Email` | — | String |
| `Bogus.Person.FirstName` | — | String |
| `Bogus.Person.Fodselsnummer` | — | String |
| `Bogus.Person.FullName` | — | String |
| `Bogus.Person.Gender` | — | Male\|Female |
| `Bogus.Person.Henkilotunnus` | — | String |
| `Bogus.Person.IranianNationalNumber` | — | String |
| `Bogus.Person.LastName` | — | String |
| `Bogus.Person.NationalNumber` | includeFormatSymbols: Boolean (optional) | String |
| `Bogus.Person.Nif` | — | String |
| `Bogus.Person.Personnummer` | — | String |
| `Bogus.Person.Pesel` | — | String |
| `Bogus.Person.Phone` | — | String |
| `Bogus.Person.Samordningsnummer` | — | String |
| `Bogus.Person.Sin` | — | String |
| `Bogus.Person.Ssn` | — | String |
| `Bogus.Person.UserName` | — | String |
| `Bogus.Person.Website` | — | String |

### Phone

| Generator | Parameters | Native result |
| --- | --- | --- |
| `Bogus.Phone.PhoneNumber` | format: String (optional) | String |
| `Bogus.Phone.PhoneNumberFormat` | phoneFormatsArrayIndex: Int32 (optional) | String |

### Random

| Generator | Parameters | Native result |
| --- | --- | --- |
| `Bogus.Random.AlphaNumeric` | length: Int32 | String |
| `Bogus.Random.ArrayElement` | array: String[] | String |
| `Bogus.Random.ArrayElements` | array: String[], count: Int32? (optional) | String[] |
| `Bogus.Random.Bool` | — | Boolean |
| `Bogus.Random.Bool` | weight: Single | Boolean |
| `Bogus.Random.Byte` | min: Byte (optional), max: Byte (optional) | Byte |
| `Bogus.Random.Bytes` | count: Int32 | Byte[] |
| `Bogus.Random.Char` | min: Char (optional), max: Char (optional) | Char |
| `Bogus.Random.Chars` | min: Char (optional), max: Char (optional), count: Int32 (optional) | Char[] |
| `Bogus.Random.ClampString` | str: String, min: Int32? (optional), max: Int32? (optional) | String |
| `Bogus.Random.CollectionItem` | collection: String[] | String |
| `Bogus.Random.Decimal` | min: Decimal (optional), max: Decimal (optional) | Decimal |
| `Bogus.Random.Decimal2` | min: Decimal (optional), max: Decimal (optional) | Decimal |
| `Bogus.Random.Digits` | count: Int32, minDigit: Int32 (optional), maxDigit: Int32 (optional) | Int32[] |
| `Bogus.Random.Double` | min: Double (optional), max: Double (optional) | Double |
| `Bogus.Random.Even` | min: Int32 (optional), max: Int32 (optional) | Int32 |
| `Bogus.Random.Float` | min: Single (optional), max: Single (optional) | Single |
| `Bogus.Random.GaussianDecimal` | mean: Double, standardDeviation: Double | Decimal |
| `Bogus.Random.GaussianDouble` | mean: Double, standardDeviation: Double | Double |
| `Bogus.Random.GaussianFloat` | mean: Double, standardDeviation: Double | Single |
| `Bogus.Random.GaussianInt` | mean: Double, standardDeviation: Double | Int32 |
| `Bogus.Random.Guid` | — | Guid |
| `Bogus.Random.Hash` | length: Int32 (optional), upperCase: Boolean (optional) | String |
| `Bogus.Random.Hexadecimal` | length: Int32 (optional), prefix: String (optional) | String |
| `Bogus.Random.Int` | min: Int32 (optional), max: Int32 (optional) | Int32 |
| `Bogus.Random.ListItem` | list: String[] | String |
| `Bogus.Random.ListItems` | items: String[], count: Int32? (optional) | String[] |
| `Bogus.Random.Long` | min: Int64 (optional), max: Int64 (optional) | Int64 |
| `Bogus.Random.Number` | max: Int32 | Int32 |
| `Bogus.Random.Number` | min: Int32 (optional), max: Int32 (optional) | Int32 |
| `Bogus.Random.Odd` | min: Int32 (optional), max: Int32 (optional) | Int32 |
| `Bogus.Random.RandomLocale` | — | String |
| `Bogus.Random.Replace` | format: String | String |
| `Bogus.Random.ReplaceNumbers` | format: String, symbol: Char (optional) | String |
| `Bogus.Random.SByte` | min: SByte (optional), max: SByte (optional) | SByte |
| `Bogus.Random.Short` | min: Int16 (optional), max: Int16 (optional) | Int16 |
| `Bogus.Random.Shuffle` | source: String[] | String[] |
| `Bogus.Random.String` | length: Int32? (optional), minChar: Char (optional), maxChar: Char (optional) | String |
| `Bogus.Random.String` | minLength: Int32, maxLength: Int32, minChar: Char (optional), maxChar: Char (optional) | String |
| `Bogus.Random.String2` | length: Int32, chars: String (optional) | String |
| `Bogus.Random.String2` | minLength: Int32, maxLength: Int32, chars: String (optional) | String |
| `Bogus.Random.UInt` | min: UInt32 (optional), max: UInt32 (optional) | UInt32 |
| `Bogus.Random.ULong` | min: UInt64 (optional), max: UInt64 (optional) | UInt64 |
| `Bogus.Random.UShort` | min: UInt16 (optional), max: UInt16 (optional) | UInt16 |
| `Bogus.Random.Utf16String` | minLength: Int32 (optional), maxLength: Int32 (optional), excludeSurrogates: Boolean (optional) | String |
| `Bogus.Random.Uuid` | — | Guid |
| `Bogus.Random.WeightedRandom` | items: String[], weights: Single[] | String |
| `Bogus.Random.Word` | — | String |
| `Bogus.Random.Words` | count: Int32? (optional) | String |
| `Bogus.Random.WordsArray` | count: Int32 | String[] |
| `Bogus.Random.WordsArray` | min: Int32, max: Int32 | String[] |

### Rant

| Generator | Parameters | Native result |
| --- | --- | --- |
| `Bogus.Rant.Review` | product: String (optional) | String |
| `Bogus.Rant.Reviews` | product: String (optional), lines: Int32 (optional) | String[] |

### System

| Generator | Parameters | Native result |
| --- | --- | --- |
| `Bogus.System.AndroidId` | — | String |
| `Bogus.System.ApplePushToken` | — | String |
| `Bogus.System.BlackBerryPin` | — | String |
| `Bogus.System.CommonFileExt` | — | String |
| `Bogus.System.CommonFileName` | ext: String (optional) | String |
| `Bogus.System.CommonFileType` | — | String |
| `Bogus.System.DirectoryPath` | — | String |
| `Bogus.System.Exception` | — | Exception |
| `Bogus.System.FileExt` | mimeType: String (optional) | String |
| `Bogus.System.FileName` | ext: String (optional) | String |
| `Bogus.System.FilePath` | — | String |
| `Bogus.System.FileType` | — | String |
| `Bogus.System.MimeType` | — | String |
| `Bogus.System.Semver` | — | String |
| `Bogus.System.Version` | — | Version |

### Vehicle

| Generator | Parameters | Native result |
| --- | --- | --- |
| `Bogus.Vehicle.Fuel` | — | String |
| `Bogus.Vehicle.GbRegistrationPlate` | dateFrom: DateTime, dateTo: DateTime | String |
| `Bogus.Vehicle.Manufacturer` | — | String |
| `Bogus.Vehicle.Model` | — | String |
| `Bogus.Vehicle.Type` | — | String |
| `Bogus.Vehicle.Vin` | strict: Boolean (optional) | String |

