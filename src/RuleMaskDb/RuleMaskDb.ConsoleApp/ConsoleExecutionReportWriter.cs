using System.Globalization;
using Spectre.Console;

namespace RuleMaskDb.ConsoleApp;

/// <summary>
/// Renders completed execution timings after the live progress display has stopped.
/// Table names are rendered as text so database identifiers cannot be interpreted as markup.
/// </summary>
public sealed class ConsoleExecutionReportWriter(IAnsiConsole console)
{
    /// <summary>
    /// Writes the total duration and the operations in execution order.
    /// Truncation is shown once for its entire transaction instead of duplicating a shared duration.
    /// </summary>
    public void Write(ScriptExecutionReport report)
    {
        console.WriteLine();
        console.MarkupLine("[bold]Execution summary[/]");
        console.WriteLine($"Total duration: {FormatDuration(report.TotalDuration)}");

        if (report.Tables.IsEmpty && report.Truncation is null)
        {
            console.WriteLine("No tables processed.");
            return;
        }

        var table = new Table()
            .AddColumn("Table")
            .AddColumn("Operation")
            .AddColumn(new TableColumn("Duration").RightAligned());

        if (report.Truncation is { } truncation)
            table.AddRow(new Text(string.Join(Environment.NewLine, truncation.TableNames)),
                new Text(truncation.TableNames.Length > 1 ? "Truncate (shared)" : "Truncate"),
                new Text(FormatDuration(truncation.Duration)));

        foreach (var timing in report.Tables)
            table.AddRow(new Text(timing.TableName), new Text("Anonymize"), new Text(FormatDuration(timing.Duration)));

        console.Write(table);
    }

    private static string FormatDuration(TimeSpan duration)
        => string.Create(CultureInfo.InvariantCulture,
            $"{(long)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}.{duration.Milliseconds:000}");
}
