using Moq;
using RuleMaskDb.Generators;
using RuleMaskDb.ScriptDom;

namespace RuleMaskDb.Tests;

public sealed class ScriptExecutionReportTests
{
    [Test]
    public async Task Total_duration_includes_discovery_and_restarts_for_each_run()
    {
        var clock = new ManualClock();
        var specification = new DatabaseSpecification(DatabaseType.PostgreSQL, "unused");
        var analyzer = new Mock<IDatabaseAnalyzer>();
        analyzer.Setup(instance => instance.DescribeDatabaseAsync(specification)).Returns(() =>
        {
            clock.Advance(TimeSpan.FromSeconds(7.5));
            return Task.FromResult(new DatabaseDescription("test", []));
        });
        var runner = new ScriptRunner(analyzer.Object, Mock.Of<IDataGeneratorFactory>(), clock);

        var first = await runner.RunAsync(new ScriptSpecification(specification, []), Mock.Of<IProgressReporter>());
        var second = await runner.RunAsync(new ScriptSpecification(specification, []), Mock.Of<IProgressReporter>());

        Assert.That(first.TotalDuration, Is.EqualTo(TimeSpan.FromSeconds(7.5)));
        Assert.That(second.TotalDuration, Is.EqualTo(first.TotalDuration));
        Assert.That(first.Tables, Is.Empty);
        Assert.That(first.Truncation, Is.Null);
    }

    [Test]
    public void Cancellation_during_discovery_does_not_return_a_success_report()
    {
        using var cancellation = new CancellationTokenSource();
        var specification = new DatabaseSpecification(DatabaseType.PostgreSQL, "unused");
        var analyzer = new Mock<IDatabaseAnalyzer>();
        analyzer.Setup(instance => instance.DescribeDatabaseAsync(specification)).Returns(() =>
        {
            cancellation.Cancel();
            return Task.FromResult(new DatabaseDescription("test", []));
        });
        var runner = new ScriptRunner(analyzer.Object, Mock.Of<IDataGeneratorFactory>());

        Assert.CatchAsync<OperationCanceledException>(() => runner.RunAsync(
            new ScriptSpecification(specification, []), Mock.Of<IProgressReporter>(), cancellation.Token));
    }

    private sealed class ManualClock : TimeProvider
    {
        private long timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => timestamp;

        public void Advance(TimeSpan duration) => timestamp += duration.Ticks;
    }
}
