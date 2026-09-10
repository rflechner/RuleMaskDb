using System.Data.Common;
using System.Diagnostics;
using System.Net;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Npgsql;

var reportDir = Environment.GetEnvironmentVariable("REPORT_DIR") ?? "/reports";
Directory.CreateDirectory(reportDir);
var results = new List<Check>();
var started = DateTimeOffset.UtcNow;
var engines = new[] { "postgresql", "sqlserver" };
foreach (var engine in engines)
{
    try { await RunEngine(engine); }
    catch (Exception ex) { results.Add(new(engine, "Execution", false, ex.ToString())); }
    finally { WriteReport(); }
}
return results.All(r => r.Passed) ? 0 : 1;

void Assert(string engine, string name, bool condition, string detail = "")
{
    results.Add(new(engine, name, condition, detail));
    Console.WriteLine($"[{engine}] {(condition ? "PASS" : "FAIL")} {name} {detail}");
}

async Task RunEngine(string engine)
{
    bool pg = engine == "postgresql";
    string schema = pg ? "public" : "dbo";
    string connectionString = pg
        ? "Host=postgres;Database=rulemask_e2e;Username=postgres;Password=RuleMask_E2e_Only!42;Timeout=15"
        : "Server=sqlserver;Database=rulemask_e2e;User Id=sa;Password=RuleMask_E2e_Only!42;TrustServerCertificate=True;Connect Timeout=15";
    if (!pg)
    {
        await using var admin = new SqlConnection(connectionString.Replace("Database=rulemask_e2e", "Database=master"));
        await admin.OpenAsync();
        await Execute(admin, "IF DB_ID('rulemask_e2e') IS NULL CREATE DATABASE rulemask_e2e");
    }
    await using DbConnection db = pg ? new NpgsqlConnection(connectionString) : new SqlConnection(connectionString);
    await db.OpenAsync();
    // Reset only the dedicated synthetic fixture, preserving reproducibility between runs.
    await Execute(db, $"""
        DROP TABLE IF EXISTS {schema}.orders;
        DROP TABLE IF EXISTS {schema}.people;
        CREATE TABLE {schema}.people (
            id int NOT NULL PRIMARY KEY, first_name varchar(200) NOT NULL,
            email varchar(300) NOT NULL, age int NOT NULL,
            reference varchar(100) NOT NULL UNIQUE, note varchar(100) NULL);
        CREATE TABLE {schema}.orders (
            id int NOT NULL PRIMARY KEY, person_id int NOT NULL REFERENCES {schema}.people(id),
            amount decimal(12,2) NOT NULL);
        """);
    for (int i = 1; i <= 40; i++)
        await Execute(db, $"""
            INSERT INTO {schema}.people VALUES ({i}, 'SOURCE_NAME_{i}', 'source_{i}@fixture.invalid', {1000+i}, 'REF-{i:D4}', {(i % 2 == 0 ? "NULL" : "'preserve me'")});
            INSERT INTO {schema}.orders VALUES ({i}, {i}, {i * 7}.25);
            """);
    var before = await Read(db, $"SELECT id, first_name, email, age, reference, note FROM {schema}.people ORDER BY id");
    var orders = await Read(db, $"SELECT id, person_id, amount FROM {schema}.orders ORDER BY id");
    Assert(engine, "Before: all 40 deterministic source rows", before.Count == 40 && before.Select((r, i) =>
        r[0] == (i+1).ToString() && r[1] == $"SOURCE_NAME_{i+1}" && r[2] == $"source_{i+1}@fixture.invalid" &&
        r[3] == (1001+i).ToString() && r[4] == $"REF-{i+1:D4}" && r[5] == ((i+1)%2 == 0 ? null : "preserve me")).All(x => x));
    Assert(engine, "Before: all 40 linked orders", orders.Count == 40 && orders.Select((r,i) =>
        r[0] == (i+1).ToString() && r[1] == (i+1).ToString() && decimal.Parse(r[2]!, System.Globalization.CultureInfo.InvariantCulture) == (i+1)*7+0.25m).All(x => x));
    if (results.Any(r => r.Engine == engine && !r.Passed)) throw new Exception("Fixture preconditions failed");
    await File.WriteAllTextAsync(Path.Combine(reportDir, $"{engine}-before.json"), JsonSerializer.Serialize(before));

    // Exercise the actual published console in a separate process, with a bounded timeout.
    var info = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
    info.ArgumentList.Add(Environment.GetEnvironmentVariable("CLI_PATH") ?? "/app/cli/RuleMaskDb.ConsoleApp.dll");
    string script = $"/app/examples/{engine}.yaml";
    if (Environment.GetEnvironmentVariable("NO_OP_RULES") == "1")
    {
        // Negative control: a successful console run with no transformations must fail assertions.
        string yaml = await File.ReadAllTextAsync(script);
        script = Path.Combine(Path.GetTempPath(), $"{engine}-noop.yaml");
        await File.WriteAllTextAsync(script, yaml.Split("rules:")[0] + "rules: []\n");
    }
    info.ArgumentList.Add("run"); info.ArgumentList.Add("--script"); info.ArgumentList.Add(script);
    using var process = Process.Start(info) ?? throw new Exception("Console process did not start");
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEndAsync();
    using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
    try { await process.WaitForExitAsync(deadline.Token); }
    catch (OperationCanceledException)
    {
        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync();
        throw new TimeoutException("Console exceeded 3 minutes");
    }
    finally { await File.WriteAllTextAsync(Path.Combine(reportDir, $"{engine}-console.log"), await stdout + "\n" + await stderr); }
    Assert(engine, "Console exit code", process.ExitCode == 0, $"exit={process.ExitCode}");

    var after = await Read(db, $"SELECT id, first_name, email, age, reference, note FROM {schema}.people ORDER BY id");
    await File.WriteAllTextAsync(Path.Combine(reportDir, $"{engine}-after.json"), JsonSerializer.Serialize(after));
    Assert(engine, "After: exact row count and primary keys", after.Count == before.Count && after.Select(r => r[0]).SequenceEqual(before.Select(r => r[0])));
    foreach (var row in after)
    {
        var original = before.SingleOrDefault(r => r[0] == row[0]);
        Assert(engine, $"Row {row[0]}: FirstName replaced", !string.IsNullOrWhiteSpace(row[1]) && !row[1]!.Contains("SOURCE_NAME_") && row[1] != original?[1]);
        Assert(engine, $"Row {row[0]}: Email replaced and valid", MailAddress.TryCreate(row[2], out var mail) && mail.Address == row[2] && !mail.Host.EndsWith(".invalid") && row[2] != original?[2]);
        Assert(engine, $"Row {row[0]}: Age replaced and in 0..100", int.TryParse(row[3], out int age) && age is >= 0 and <= 100 && row[3] != original?[3]);
        Assert(engine, $"Row {row[0]}: untargeted values and NULL preserved", original != null && row.Skip(4).SequenceEqual(original.Skip(4)));
    }
    var afterOrders = await Read(db, $"SELECT id, person_id, amount FROM {schema}.orders ORDER BY id");
    Assert(engine, "After: untargeted table exactly preserved", JsonSerializer.Serialize(orders) == JsonSerializer.Serialize(afterOrders));
    var orphans = await Read(db, $"SELECT COUNT(*) FROM {schema}.orders o LEFT JOIN {schema}.people p ON o.person_id=p.id WHERE p.id IS NULL");
    Assert(engine, "After: foreign key relationships intact", orphans[0][0] == "0");
}

static async Task Execute(DbConnection db, string sql)
{
    await using var cmd = db.CreateCommand(); cmd.CommandText = sql; cmd.CommandTimeout = 30;
    await cmd.ExecuteNonQueryAsync();
}
static async Task<List<string?[]>> Read(DbConnection db, string sql)
{
    await using var cmd = db.CreateCommand(); cmd.CommandText = sql; cmd.CommandTimeout = 30;
    await using var reader = await cmd.ExecuteReaderAsync();
    var rows = new List<string?[]>();
    while (await reader.ReadAsync())
        rows.Add(Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? null : Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture)).ToArray());
    return rows;
}
void WriteReport()
{
    string Enc(string? value) => WebUtility.HtmlEncode(value) ?? string.Empty;
    // Keep textual labels alongside symbols so status never depends on color alone.
    string Badge(string state) => $"<span class='status {state.ToLowerInvariant()}'><span aria-hidden='true'>{(state == "PASS" ? "&#10003;" : state == "FAIL" ? "&#10007;" : "&#8230;")}</span> {state}</span>";
    bool passed = results.All(r => r.Passed);
    string status = results.Select(r => r.Engine).Distinct().Count() < engines.Length ? "RUNNING" : passed ? "PASS" : "FAIL";
    var html = new StringBuilder($"<!doctype html><html lang='en'><meta charset='utf-8'><title>RuleMaskDb {status}</title><style>body{{font:16px system-ui;max-width:1100px;margin:40px auto;padding:0 16px}}table{{border-collapse:collapse;width:100%}}td,th{{padding:8px;border:1px solid #ccc;text-align:left}}tr.fail{{background:#fff0f0}}.status{{display:inline-block;font-weight:700;white-space:nowrap;padding:2px 8px;border-radius:5px}}.status.pass{{color:#146c2e;background:#e8f5ec}}.status.fail{{color:#a11616;background:#ffe3e3}}.status.running{{color:#624900;background:#fff4cc}}pre{{white-space:pre-wrap}}</style><h1>RuleMaskDb integration: {Badge(status)}</h1><p>Started {started:O}; updated {DateTimeOffset.UtcNow:O}. <strong>{results.Count(r => r.Passed)}/{results.Count} checks passed; {results.Count(r => !r.Passed)} failed.</strong></p>");
    foreach (var engine in engines)
    {
        var checks = results.Where(r => r.Engine == engine).ToArray();
        string engineStatus = checks.Length == 0 ? "RUNNING" : checks.All(r => r.Passed) ? "PASS" : "FAIL";
        html.Append($"<p><strong>{engine}</strong> {Badge(engineStatus)} — {checks.Count(r => r.Passed)}/{checks.Length} passed; {checks.Count(r => !r.Passed)} failed. <a href='{engine}-console.log'>Console log</a> · <a href='{engine}-before.json'>Before</a> · <a href='{engine}-after.json'>After</a></p>");
    }
    html.Append("<table><tr><th>Engine</th><th>Assertion</th><th>Result</th><th>Details</th></tr>");
    foreach (var r in results) html.Append($"<tr class='{(r.Passed ? "pass" : "fail")}'><td>{Enc(r.Engine)}</td><td>{Enc(r.Name)}</td><td>{Badge(r.Passed ? "PASS" : "FAIL")}</td><td><pre>{Enc(r.Detail)}</pre></td></tr>");
    html.Append("</table></html>");
    File.WriteAllText(Path.Combine(reportDir, "index.html.tmp"), html.ToString());
    File.Move(Path.Combine(reportDir, "index.html.tmp"), Path.Combine(reportDir, "index.html"), true);
    File.WriteAllText(Path.Combine(reportDir, "results.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
}
record Check(string Engine, string Name, bool Passed, string Detail = "");
