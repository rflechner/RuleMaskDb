using RuleMaskDb.ConsoleApp;
using Spectre.Console;

namespace RuleMaskDb.Tests;

public sealed class ConsoleExecutionReportTests
{
    [Test]
    public void Summary_preserves_table_order_and_renders_identifiers_as_plain_text()
    {
        var report = new ScriptExecutionReport(TimeSpan.FromSeconds(8),
            [
                new("dbo.[red]Customers[/]", TimeSpan.FromSeconds(3)),
                new("public.empty", TimeSpan.Zero)
            ],
            new TruncationExecutionTiming(["dbo.Audit", "dbo.Sessions"], TimeSpan.FromSeconds(2)));

        var output = Render(report);

        Assert.That(output, Does.Contain("Total duration: 00:00:08.000"));
        Assert.That(output, Does.Contain("dbo.[red]Customers[/]"));
        Assert.That(output, Does.Contain("public.empty"));
        Assert.That(output, Does.Contain("Truncate (shared)"));
        Assert.That(output, Does.Contain("00:00:00.000"));
        Assert.That(output.IndexOf("dbo.Audit", StringComparison.Ordinal),
            Is.LessThan(output.IndexOf("dbo.[red]Customers[/]", StringComparison.Ordinal)));
        Assert.That(output.IndexOf("dbo.[red]Customers[/]", StringComparison.Ordinal),
            Is.LessThan(output.IndexOf("public.empty", StringComparison.Ordinal)));
        Assert.That(output.Split("00:00:02.000").Length, Is.EqualTo(2),
            "The shared truncation duration should appear only once.");
    }

    [TestCase(0, "00:00:00.000")]
    [TestCase(1, "00:00:00.001")]
    [TestCase(90061234, "25:01:01.234")]
    public void Empty_runs_show_total_duration_without_wrapping_hours(long milliseconds, string expectedDuration)
    {
        var output = Render(new ScriptExecutionReport(TimeSpan.FromMilliseconds(milliseconds), [], null));

        Assert.That(output, Does.Contain($"Total duration: {expectedDuration}"));
        Assert.That(output, Does.Contain("No tables processed."));
        Assert.That(output, Does.Not.Contain("Anonymize"));
    }

    [Test]
    public void Truncate_only_runs_show_the_shared_operation()
    {
        var output = Render(new ScriptExecutionReport(TimeSpan.FromSeconds(5), [],
            new TruncationExecutionTiming(["dbo.Audit"], TimeSpan.FromSeconds(4))));

        Assert.That(output, Does.Contain("dbo.Audit"));
        Assert.That(output, Does.Contain("Truncate"));
        Assert.That(output, Does.Contain("00:00:04.000"));
        Assert.That(output, Does.Not.Contain("No tables processed."));
    }

    private static string Render(ScriptExecutionReport report)
    {
        using var output = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(output),
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.No
        });
        console.Profile.Width = 160;

        new ConsoleExecutionReportWriter(console).Write(report);
        return output.ToString();
    }
}
