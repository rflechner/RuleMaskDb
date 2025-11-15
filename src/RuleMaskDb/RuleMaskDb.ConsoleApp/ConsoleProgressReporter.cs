using Spectre.Console;

namespace RuleMaskDb.ConsoleApp;

public class ConsoleProgressReporter(ProgressContext context) : IProgressReporter
{
    private string _currentTable = string.Empty;
    private ProgressTask? _task;

    public Task ReportProgressAsync(string tableName, int step, int totalStepCount, CancellationToken cancellationToken = default)
    {
        var lastStep = totalStepCount-1;
        
        if (_currentTable != tableName)
        {
            if (_task is not null)
            {
                _task = _task?.Value(_task.MaxValue);
                context.Refresh();
            }
            
            _currentTable = tableName;
            _task = context.AddTask($"[{Color.Wheat1}]{tableName}[/]").MaxValue(lastStep);
        }
        
        _task = _task?.Value(step);
        
        if (step % 100 == 0 || step >= lastStep)
            context.Refresh();
        
        return Task.CompletedTask;
    }
}