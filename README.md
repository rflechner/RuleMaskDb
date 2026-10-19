# RuleMaskDb

RuleMaskDb replaces configured sensitive fields directly in a target database using YAML rules and Bogus generators. SQL Server and PostgreSQL are supported; RavenDB is reserved but not implemented. Requires .NET 10.

![RuleMaskDb](docs/images/logo-complex-1.png)

## In-place anonymization

Use a non-production copy. RuleMaskDb does not clone the database. Only configured columns are covered; review the resulting data for your privacy requirements.

![Anonymization pipeline](docs/images/anonymization-pipeline.png)

## Recommended workflow

1. Create a copy of your production database (backup, dump or snapshot).
2. Restore it in a non-production environment (development, test or QA).
3. Point RuleMaskDb to this copy using the target connection in your YAML configuration.
4. Run anonymization to replace the configured sensitive fields.
5. Verify the resulting data, then use it for development, testing or sharing.

![Recommended workflow: copy production data, restore outside production, point RuleMaskDb to the copy, run anonymization, then verify and use the result.](docs/images/recommended-workflow.png)

## PostgreSQL example

See [the runnable YAML example](docs/examples/postgresql.yaml). Its demo credentials refer to the disposable test container below.

```yaml
database:
  type: PostgreSQL
  connectionString: 'Host=127.0.0.1;Port=55433;Database=rulemask_test;Username=postgres;Password=rulemask_test_only'
rules:
  - table: public.people
    column: first_name
    generator: FirstName
  - table: public.people
    column: last_name
    generator: LastName
```

From the repository root (PowerShell):

```powershell
$script = (Resolve-Path docs/examples/postgresql.yaml).Path
dotnet run --project src/RuleMaskDb/RuleMaskDb.ConsoleApp -- describe --script $script
dotnet run --project src/RuleMaskDb/RuleMaskDb.ConsoleApp -- run --script $script
```

For SQL Server, use `type: SqlServer`, a SqlClient connection string, and table names such as `dbo.People`. An omitted type defaults to SQL Server; unknown types are rejected.

Use the exact `schema.table` and column names reported by `describe`, including case. These are literal metadata names, not SQL fragments: do not add SQL brackets or double-quote delimiters. Quote YAML strings when necessary. Schema and table identifiers are escaped separately for SQL execution; embedded spaces, dots, brackets and quotes are supported. Ambiguous concatenated table names are rejected.

![Targeted changes before and after](docs/images/anonymization-before-after.png)

## Engine behavior

The CLI dispatches schema discovery to the selected provider. A shared ADO.NET runner reads original row keys and targeted values, generates replacement values and issues parameterized updates using every component of the primary key, or a configured single-column key when no primary key exists. Only targeted columns appear in `SET`; row keys and untargeted columns are not assigned by the engine.

For generator rules, targeted fields containing SQL `NULL` or an empty string (`""`) are preserved without calling their generator, including plugin generators. Whitespace-only strings, zero and `false` are still anonymized. Rows with no populated targeted fields are not updated, but still count toward progress. This behavior applies to both SQL Server and PostgreSQL.

All rule targets are validated before writes. Missing tables/columns, duplicate column rules, row-key rules and tables without a usable key fail explicitly. PostgreSQL discovers ordinary and partitioned tables (partition children are not processed twice), columns and primary keys; row counts are exact and may be expensive on large databases.

PostgreSQL commits each table's anonymization independently and rolls back that anonymization on failure or cancellation. Previously committed tables and successful `beforeTableSql` scripts remain changed. SQL Server uses autocommit per update statement, avoiding a long updater transaction that could block its separate streaming reader. Run against an otherwise idle database copy. Database triggers, constraints and generated columns still apply: incompatible values can fail; triggers can have additional effects.

Generators must fit the target column type, length and constraints. Values are normally random; uniqueness and referential consistency for non-PK columns are not guaranteed. `Name` produces a full name. If neither `generator` nor `value` is supplied, `Name` is used. The legacy `mask` property is parsed but is not applied by the engine.

## Batched updates

Set the root-level `batchSize` to choose how many rows are read and prepared at a time.
The default is **200**; for example, use **100** for smaller batches:

```yaml
batchSize: 100

database:
  type: SqlServer
  connectionString: 'Server=localhost;Database=Demo;Integrated Security=true;TrustServerCertificate=true;'

rules:
  - table: dbo.Users
    column: FirstName
    generator: FirstName
  - table: dbo.Users
    column: IsActive
    value: false
```

See the [complete batch example](docs/examples/batched-updates.yaml).

Each batch is sent using a parameterized `UPDATE ... FROM (VALUES ...)` join on the
primary key (including composite keys) or configured row key. Rows sharing the same
columns to update use one statement. A uniform batch of 200 rows therefore needs one
update round trip instead of 200. Mixed NULL/empty generator fields can split a batch
into multiple statements so preserved columns never appear in `SET` for those rows.
Rows with nothing to update generate no write statement.

- `batchSize` must be positive. `1` limits processing to one row at a time.
- The effective size is capped by the number of keys and target columns: at most
  2,000 parameters per SQL Server command (leaving headroom below its 2,100 limit),
  or 65,535 for PostgreSQL. Large/wide configurations are automatically split.
- Memory holds at most one effective batch of generated values; the database reader
  remains streamed. Generation remains sequential, using each row's original values.
- Progress is reported after each completed chunk, including skipped rows, and the
  final partial chunk is flushed. Each submitted row must match exactly one updated row.
- PostgreSQL retains its transaction per table, including rollback on failure or
  cancellation. SQL Server retains autocommit per statement to avoid holding updater
  locks across a streamed table read; earlier successful batches remain committed if
  a later batch fails. Truncation behavior is unchanged.
- Statement-level triggers now receive multiple rows and must handle them correctly.
  Use `batchSize: 1` if a trigger requires one-row statements.

The reduction is in SQL commands and network round trips; actual elapsed-time gains
depend on the database, indexes, triggers, generator cost, and network latency.

## Static column values

Use `value` instead of `generator` to assign the same literal value to every row:

```yaml
rules:
  - table: dbo.Users
    column: Email
    value: 'redacted@example.invalid'
  - table: dbo.Users
    column: RetryCount
    value: 0
  - table: dbo.Users
    column: IsActive
    value: false
  - table: dbo.Users
    column: Notes
    value: ''
  - table: dbo.Users
    column: Phone
    value: null
```

See the [complete YAML example](docs/examples/static-values.yaml), which mixes static values and a generator.

- Strings, numbers, booleans, and YAML null are supported. Lists and objects are rejected.
- Quote values that must remain text, such as `'00123'`, `'true'`, or `'null'`.
- `value: null` (also `value: ~` or an empty `value:`) writes SQL `NULL`; `value: ''` writes an empty string. Omitting `value` retains generator behavior.
- Static assignments replace **every targeted cell**, including existing NULL and empty strings. Generator rules continue to preserve those cells.
- A static value cannot be combined with `generator`, `mask`, or `parameters` on the same rule.
- Values are sent as SQL parameters, never interpolated into SQL. They must fit the target column's type, length, nullability, and other constraints. Setting the same value on a unique column may violate its constraint.
- Primary keys and configured row keys remain protected. The existing row-key requirements apply to static assignments too.

## SQL before anonymizing each table

Use the optional root-level `beforeTableSql` mapping to prepare a table's data before
its anonymization. Each value is a SQL script; YAML's `|` syntax preserves multiple lines:

```yaml
beforeTableSql:
  dbo.Customers: |
    DELETE FROM [dbo].[Customers] WHERE [IsTestAccount] = 1;
    UPDATE [dbo].[Customers] SET [FirstName] = 'Unknown' WHERE [FirstName] IS NULL;

rules:
  - table: dbo.Customers
    column: FirstName
    generator: FirstName
  - table: dbo.Customers
    column: LastName
    generator: LastName
```

See the [complete SQL Server example](docs/examples/before-table-sql.yaml).
For PostgreSQL, use the same mapping with provider-native SQL, for example:

```yaml
beforeTableSql:
  public.people: |
    UPDATE "public"."people" SET "first_name" = 'Unknown' WHERE "first_name" IS NULL;
```

- Keys are exact table names from `describe` and must have at least one matching
  anonymization rule. Empty names/scripts, duplicate YAML keys, unmatched targets,
  and hooks for truncate-only tables are rejected. Omit the mapping or use `{}`
  to preserve existing behavior. `describe` never executes these scripts.
- Tables run in order of their first appearance in `rules`. A script runs **once per
  table per run**, immediately before reading its rows, even when it is initially empty.
  Multiple rules or batches do not repeat it. Global `truncateTables` operations run first.
- Scripts are sent unchanged as a single ADO.NET command, with cancellation support.
  Use SQL for the selected database; multiple SQL statements are allowed, but client
  commands such as SQL Server `GO`, `sqlcmd` directives, and psql commands are not.
  There is no templating or automatic table-name substitution.
- All rule validation and initial configured-key checks happen before any writes.
  After a script, its configured row key is checked again and its row count is refreshed
  for progress. Inserted/changed rows are read with their new values; deleted rows are
  not anonymized. Filling NULL/empty fields makes them eligible for generators.
- Each script runs in its **own transaction**, committed before the separate reader
  starts. An SQL error or cancellation before commit rolls back that script and stops
  the run. A subsequent anonymization failure does **not** undo a successful script,
  on either provider. Earlier committed tables and truncations also remain changed.
- The SQL runs with the configured database account's permissions. Use trusted scripts
  that modify data in existing tables: schema changes are unsupported because metadata
  is discovered before execution. Do not include transaction-control commands. Session
  settings and temporary objects are not shared with the separate reader connection.

## Emptying entire tables

Use `truncateTables` to remove **all rows** from selected tables while keeping their schema.
This works with SQL Server and PostgreSQL and does not require a primary key or a `tableKeys` entry.
See [the complete YAML example](docs/examples/truncate-tables.yaml).

```yaml
database:
  type: SqlServer
  connectionString: 'Server=localhost;Database=MaskedCopy;Integrated Security=true'

truncateTables:
  - dbo.AuditEvents
  - dbo.UserSessions

rules:
  - table: dbo.Customers
    column: FirstName
    generator: FirstName
```

For PostgreSQL, use `type: PostgreSQL`, a PostgreSQL connection string and names such as
`public.audit_events`. Omit `rules` (or set `rules: []`) for a truncate-only script.
An omitted or empty `truncateTables` list preserves existing behavior.

- Use the exact table names reported by `describe`. That command marks selected tables **TRUNCATE**.
  Blank, duplicate, missing or ambiguous targets are rejected. A table cannot appear in both
  `truncateTables` and `rules`.
- All target/rule validation and configured row-key data checks run before any truncation.
  The engine then truncates all selected tables in one transaction, before anonymizing other tables.
  A failure or cancellation before commit rolls back the truncation transaction and stops execution.
  Once committed, truncations remain effective even if subsequent anonymization fails.
- Foreign-key constraints are never disabled and `CASCADE` is never requested. PostgreSQL uses one
  `TRUNCATE TABLE … CONTINUE IDENTITY RESTRICT` statement: include every referencing table explicitly
  or the database rejects the operation. SQL Server rejects truncation of a table referenced by a
  foreign key (except a self-reference), even if the referencing table is also listed or empty.
  There is no automatic fallback to `DELETE`.
- Native provider semantics apply: SQL Server resets identity counters; PostgreSQL preserves sequences.
  PostgreSQL also truncates descendants/partitions of selected parent tables.
  `TRUNCATE` does not fire `DELETE` triggers; PostgreSQL `TRUNCATE` triggers can run.
  Database permissions and other native truncation restrictions still apply.

Use an otherwise idle, non-production database copy: truncation takes strong table locks and permanently
removes the selected data once committed.

## Tables without a primary key

Use the optional top-level `tableKeys` mapping to designate an existing column as the row identifier for a table without a declared primary key:

```yaml
database:
  type: SqlServer
  connectionString: 'Server=localhost;Database=MaskedCopy;Integrated Security=true'

tableKeys:
  dbo.LegacyCustomers: CustomerCode

rules:
  - table: dbo.LegacyCustomers
    column: FirstName
    generator: FirstName
  - table: dbo.LegacyCustomers
    column: LastName
    generator: LastName
```

Both SQL Server and PostgreSQL use the configured column in the parameterized `WHERE` clause, just as they use a primary key. This does not add a column, index, or constraint, and it does not change the database schema.

Safety and scope:

- Configure one existing column per table, using the exact table/column names reported by `describe`. It need not have a UNIQUE constraint or an index.
- The column must contain no SQL NULLs and no duplicate values. The engine checks **every configured key before updating any table**, using the database's own grouping/equality/collation semantics. Empty tables pass the data check.
- Key columns cannot themselves be anonymized. A rule targeting the configured key is rejected, and `describe` does not mark it as impacted.
- A configured key is only for tables without a primary key. If the table already has one (including a composite primary key), remove its `tableKeys` entry; normal primary-key handling remains unchanged.
- Missing columns, blank names and entries with no matching rule are errors. Tables without a primary key still fail if no `tableKeys` entry is supplied.
- Configured composite keys and automatic UNIQUE-index discovery are not implemented.
- The key's SQL type must support equality and `GROUP BY`. Checking large tables may require a full scan; an index on the key can improve validation and per-row update performance.

Run against an otherwise idle, non-production copy. The data check is not a new uniqueness constraint and does not prevent concurrent inserts or key changes. Triggers that change key values are also outside this guarantee. The existing one-row affected check and provider transaction behavior remain in effect.

## Bogus generator catalog

Use `Bogus.<Category>.<Method>` to access the installed Bogus data APIs, for example
`Bogus.Internet.UserName`, `Bogus.Finance.Iban`, `Bogus.Commerce.ProductName`,
`Bogus.Vehicle.Vin` or `Bogus.Random.Int`. Existing names such as `FirstName`, `Name`,
`PhoneNumber` and plugin names keep their behavior.

See the [complete catalog and parameter reference](docs/bogus-generators.md) and the
[complete YAML example](docs/examples/bogus-generators.yaml). The catalog covers all shipped dataset
families, random values, Person fields and data-generating country extensions in **Bogus 35.6.5**.

```yaml
rules:
  - table: dbo.Customers
    column: Iban
    generator: Bogus.Finance.Iban
    parameters:
      countryCode: { value: FR }
      formatted: { value: 'true' }
  - table: dbo.Customers
    column: CreditLimit
    generator: Bogus.Finance.Amount
    parameters:
      min: { value: '100.00' }
      max: { value: '5000.00' }
      decimals: { value: '2' }
  - table: dbo.Customers
    column: Segment
    generator: Bogus.Random.ArrayElement
    parameters:
      array: { value: '["Retail", "Business", "Partner"]' }
```

Arguments use Bogus's parameter names. Each accepts the existing `value` or `column`/`mapping`/`fallback`
binding. The `culture` parameter remains available for every generator. Missing optional arguments use
Bogus defaults; missing required arguments and unknown parameter names are rejected. Numbers are parsed
using invariant culture, enums use names, and collection arguments use JSON encoded as a YAML string.

List available signatures without connecting to a database:

```powershell
dotnet run --project src/RuleMaskDb/RuleMaskDb.ConsoleApp -- generators
```

Scalar results keep database-compatible types. Collections and structured objects become JSON text
(except `byte[]`, which stays binary, and `char[]`, which becomes text). Select an appropriate target
column. APIs requiring C# delegates or application-defined enum types, and Bogus Premium packages,
are outside this YAML catalog; details and alternatives are in the reference.

### Lorem ipsum example

Replace a free-text column with a randomly generated paragraph:

```yaml
rules:
  - table: dbo.Products
    column: Description
    generator: Bogus.Lorem.Paragraph
    parameters:
      min: { value: '3' }
```

Here, `min: '3'` produces a paragraph containing **3 to 6 sentences**, not three words.
For a single sentence, use `Bogus.Lorem.Sentence` with `wordCount`; for multiple paragraphs,
use `Bogus.Lorem.Paragraphs` with `count`.
See the [complete Lorem ipsum YAML example](docs/examples/lorem-ipsum.yaml) and
[argument details](docs/bogus-generators.md#lorem-ipsum).
Generated text is automatically truncated to the target column's declared length when written to SQL Server or PostgreSQL. See [automatic text truncation](#automatic-text-truncation).

## First names, surnames and mapped parameters

Use `FirstName` for a given name, `LastName` for a surname, and `Name` for a full name. Existing `Name` rules keep their meaning; change surname rules explicitly to `LastName`. A surname can naturally contain spaces: `LastName` does not guarantee a single word.

Generator parameters can read another column in the **same original row** and map database-specific codes to provider-independent values:

```yaml
rules:
  - table: dbo.Customers
    column: FirstName
    generator: FirstName
    parameters:
      gender:
        column: Title
        mapping:
          "M.": Male
          "Mme": Female
        fallback: Unknown
  - table: dbo.Customers
    column: LastName
    generator: LastName
```

Here `gender` resolves to the SDK enum `RuleMaskDb.Sdk.Gender`: `Male`, `Female`, or `Unknown`. The Bogus adapter converts those values to Bogus's enum. `Unknown` (or no resolved parameter) calls Bogus without a gender constraint; it does not select a special neutral-name list. No gender is inferred from an existing name.

Mapping rules:

- Each parameter accepts either a fixed `value` or a source `column`, never both. Fixed values cannot have a mapping or fallback.
- Column names and mapping keys are exact and case-sensitive; no trimming is performed.
- Database values are converted to strings using invariant culture. For numeric codes use quoted YAML keys, such as `"1": Male` and `"2": Female`. Boolean keys are `"True"` and `"False"`.
- SQL NULL and unmatched values use `fallback`. An empty string is matched by `"": ...` if configured, otherwise it also uses the fallback.
- Without a fallback, an unmatched parameter is omitted. For `FirstName.gender`, omission means `Unknown`.
- With a column but no `mapping`, the original value is passed directly; SQL NULL and empty strings use the fallback (or omit the parameter). An explicit empty mapping (`mapping: {}`) still uses mapping semantics.
- Enum output names are case-insensitive. Numeric enum outputs and invalid names are rejected.
- Missing source columns, invalid gender mappings/fallbacks, unknown FirstName parameters and parameters on generators that do not support them fail before any update connections are opened.
- All source values are captured before generators run. A source column can also be anonymized by another rule: mapping still uses its original value, independent of rule order.
- Source columns are only read unless explicitly targeted by their own rule. Null/empty target values remain skipped as described above.

Bogus supports gender-specific first names only for locales containing the corresponding lists. The default locale remains `en`; use the `culture` parameter below to select another locale. The adapter rejects explicit Male/Female values if the selected locale lacks that capability.

This feature does not preserve or force compound first names. It uses the selected Bogus name list as supplied; controlling the number of name components is a separate feature.

### Generator cultures

All built-in generators accept a `culture` parameter using .NET culture names. Fix a culture, map database-specific codes, or read a culture directly from a column:

```yaml
rules:
  - table: dbo.Customers
    column: PhoneNumber
    generator: PhoneNumber
    parameters:
      culture:
        value: fr-FR
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
  - table: dbo.Customers
    column: City
    generator: City
    parameters:
      culture:
        column: CultureCode
        fallback: fr-FR
```

The adapter translates supported .NET cultures to verified Bogus locales (for example, `fr-FR → fr`, `en-GB → en_GB`). It does not modify the process/thread culture. Omitting the parameter retains the current English default. Generators whose data is culture-independent can produce the same formats regardless of culture.

Fixed values, mapping outputs and fallbacks are checked before updates. Unmapped direct-column values are checked as each populated target row is processed; invalid values fail rather than silently choosing another locale. Existing transaction/partial-update behavior still applies. Bogus can internally fall back to English for missing parts of a supported locale's dataset.

See [generator culture configuration](docs/generator-cultures.md) for all three modes, compatibility rules, combined gender/culture generation, and plugin guidance.

### Context-aware plugins

Existing plugins implementing only `IDataGenerator` work unchanged for rules without parameters. To consume parameters, additionally implement `IContextualDataGenerator`:

```csharp
using RuleMaskDb.Sdk;

[Generator("CustomerSegment")]
public sealed class CustomerSegmentGenerator : IContextualDataGenerator
{
    public enum Segment { Unknown, Retail, Business }

    public void ValidateParameters(
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> possibleValues)
    {
        foreach (var (name, values) in possibleValues)
        {
            if (name != "segment")
                throw new InvalidOperationException($"Unknown parameter: {name}");
            foreach (var value in values)
                _ = new GenerationContext(
                    new Dictionary<string, string> { [name] = value })
                    .GetEnum("segment", Segment.Unknown);
        }
    }

    public Task<object> GenerateValueAsync(CancellationToken cancellationToken = default)
        => GenerateValueAsync(GenerationContext.Empty, cancellationToken);

    public Task<object> GenerateValueAsync(
        GenerationContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var segment = context.GetEnum("segment", Segment.Unknown);
        return Task.FromResult<object>($"anonymous-{segment}");
    }
}
```

The same mapping mechanism can supply that plugin's own enum:

```yaml
rules:
  - table: dbo.Customers
    column: DisplayName
    generator: CustomerSegment
    parameters:
      segment:
        column: CustomerType
        mapping:
          "P": Retail
          "C": Business
        fallback: Unknown
```

`ValidateParameters` receives every fixed value, configured mapping output and fallback during preflight, including values not currently present in the database. Direct-column values are only known while processing rows; plugins must validate them during generation as well. `GenerateValueAsync` receives a read-only snapshot of resolved parameters for one row, not the entire row. Plugins can use `context.GetCulture()` to read a .NET `CultureInfo` (or null if omitted). The SDK has no dependency on Bogus. Old plugins reject configured parameters explicitly rather than silently ignoring them.

## Generator plugins

The `Sdk` project builds `RuleMaskDb.Sdk.dll` and has no dependency on the engine or Bogus. Reference it from a .NET 10 class library, implement `RuleMaskDb.Sdk.IDataGenerator`, and annotate a public class with `[Generator("YourName")]`. The class must have a public parameterless constructor. One instance is created per configured column; `GenerateValueAsync` receives the run's cancellation token.

The included `RuleMaskDb.LocalEmailPlugin` is a complete example:

```csharp
using RuleMaskDb.Sdk;

[Generator("LocalEmail")]
public sealed class LocalEmailGenerator : IDataGenerator
{
    public Task<object> GenerateValueAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<object>($"user-{Guid.NewGuid():N}@local");
    }
}
```

By default the CLI discovers DLLs in `plugins` beside its executable, including subdirectories. Alternatively set `RULEMASK_PLUGINS_DIRECTORY` to a plugin directory (relative paths are relative to the working directory). Keep each plugin and its dependencies together, preferably in its own subdirectory; publishing the class library supplies its dependencies and `.deps.json`. The host shares its SDK assembly with every plugin. Install only trusted DLLs: plugins execute inside the application process. Restart the application after changing plugins.

Build and discover the example from the repository root in PowerShell:

```powershell
dotnet build src/RuleMaskDb/RuleMaskDb.slnx
$env:RULEMASK_PLUGINS_DIRECTORY = (Resolve-Path src/RuleMaskDb/RuleMaskDb.LocalEmailPlugin/bin/Debug/net10.0).Path
dotnet run --project src/RuleMaskDb/RuleMaskDb.ConsoleApp -- plugins
# Run your YAML using this same environment:
# dotnet run --project src/RuleMaskDb/RuleMaskDb.ConsoleApp -- run --script $script
```

Select the plugin by name for a column:

```yaml
rules:
  - table: public.people
    column: email
    generator: LocalEmail
```

`LocalEmail` always produces an address ending in `@local`. Names are matched without regard to case. Plugins take priority over built-in generators: naming a plugin `Email` overrides the built-in `Email`. Duplicate names between plugins and invalid generator declarations are errors. Missing or blank names still default to `Name`; unknown names fail before any database updates. DLL loading failures include the file path in the error. A missing plugin directory simply leaves the built-in generators available. The example plugin is installed explicitly and is not bundled into the CLI automatically.

## Reproducible PostgreSQL tests

Create a dedicated container; ports 5432 and 5433 and existing Pagila instances are not used:

```powershell
docker run --detach --name rulemask-engine-test --publish 127.0.0.1:55433:5432 --env POSTGRES_PASSWORD=rulemask_test_only --env POSTGRES_DB=rulemask_test postgres:17.11
docker exec rulemask-engine-test pg_isready -U postgres -d rulemask_test
$env:RULEMASK_TEST_POSTGRES = 'Host=127.0.0.1;Port=55433;Database=rulemask_test;Username=postgres;Password=rulemask_test_only'
dotnet test src/RuleMaskDb/RuleMaskDb.slnx
```

Wait for `pg_isready` to report accepting connections. Tests create uniquely named schemas and remove only those schemas in teardown. Deterministic test generators verify YAML parsing through real database updates and re-reading by composite ID, including row counts, unchanged fields, case-sensitive columns, escaped identifiers, rejected rules and rollback. Without the environment variable, database tests are skipped. SQL Server escaping and existing unit tests run without a server; live SQL Server is not covered by this PostgreSQL suite.

To try the example CLI in this dedicated container:

```powershell
docker exec rulemask-engine-test psql -U postgres -d rulemask_test -c "CREATE TABLE public.people (id integer PRIMARY KEY, first_name text, last_name text, note text); INSERT INTO public.people VALUES (1, 'Alice', 'Smith', 'keep'), (2, 'Bob', 'Jones', 'keep too');"
# Run describe/run above, then inspect:
docker exec rulemask-engine-test psql -U postgres -d rulemask_test -c 'SELECT * FROM public.people ORDER BY id;'
# Remove only the dedicated test container when finished:
docker rm --force --volumes rulemask-engine-test
```

## Sample datasets

- SQL Server: [Contoso](https://www.microsoft.com/en-us/download/details.aspx?id=54427)
- PostgreSQL: [Pagila](https://github.com/devrimgunduz/pagila)

Restore a separate copy before applying any anonymization rules.

### SQL Server batch integration tests

Set `RULEMASK_TEST_SQLSERVER` to a connection string for an explicitly disposable
SQL Server database and run `dotnet test src/RuleMaskDb/RuleMaskDb.Tests`. The optional
batch tests create and drop uniquely named tables and verify composite keys, triggers,
NULL preservation, static values, and 100/200-row chunks. PostgreSQL batch tests use
the existing `RULEMASK_TEST_POSTGRES` variable.

## Automatic text truncation

Text values longer than the destination column are automatically truncated on the right
during updates, without additional YAML configuration. This applies to generated values
(including plugins), static YAML values, and every batch size.

For example, writing `Alexander` to a `varchar(5)` column stores `Alexa`.
SQL Server `char(n)`, `varchar(n)`, `nchar(n)`, `nvarchar(n)` and PostgreSQL
`char(n)` / `varchar(n)` use explicit database casts to their declared size.
SQL Server uses the destination collation/code page, so byte limits and Unicode
conversion follow the database's semantics. PostgreSQL lengths count characters.
Unbounded text and `(max)` columns are unchanged. Custom analyzers must supply
`FieldDescription.NativeTypeName`, `MaxLength`, and SQL Server `Collation`
to enable this behavior; custom PostgreSQL domains are not handled automatically.

Existing null/empty-value skipping remains unchanged. Row keys are never truncated.
This does not suppress other database errors or relax constraints: truncation can
produce duplicate values, and UNIQUE, CHECK and foreign-key violations still fail.
Numeric overflow and binary values are outside this text-truncation behavior.
