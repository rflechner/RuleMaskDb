namespace RuleMaskDb;

public interface IProgressReporter
{
    Task ReportProgressAsync(string tableName, int step, int totalStepCount, CancellationToken cancellationToken = default);
}
