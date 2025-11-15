using RuleMaskDb.ScriptDom;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RuleMaskDb.ConsoleApp;

internal class RunScriptCommand(
    IScriptDomProviderFactory scriptDomProviderFactory,
    IScriptRunner scriptRunner) : AsyncCommand<RunScriptCommand.Args>
{
    internal class Args : CommandSettings
    {
        [CommandOption("-s|--script <SCRIPT_PATH>")]
        public string? ScriptPath { get; init; }
    }
    
    public override ValidationResult Validate(CommandContext context, Args settings)
    {
        if (string.IsNullOrEmpty(settings.ScriptPath))
            return ValidationResult.Error("Script path is required.");
        
        return base.Validate(context, settings);
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Args settings, CancellationToken cancellationToken)
    {
        var scriptDomProvider = await scriptDomProviderFactory.CreateAsync(new Uri(settings.ScriptPath!));
        var script = await scriptDomProvider.LoadScriptAsync();

        AnsiConsole.MarkupLine($"[bold yellow]Running script [bold green]{script.Database.DatabaseType}[/] ... [/]");
        
        await AnsiConsole.Progress()
            .AutoRefresh(false)
            .Columns(
                new TaskDescriptionColumn(), 
                new ProgressBarColumn(), 
                new PercentageColumn(), 
                new RemainingTimeColumn(), 
                new TotalStepsProgressColumn()
            )
            .StartAsync(async ctx => 
            {
                await scriptRunner.RunAsync(script, new ConsoleProgressReporter(ctx), cancellationToken);
            });
        
        return 0;
    }
}