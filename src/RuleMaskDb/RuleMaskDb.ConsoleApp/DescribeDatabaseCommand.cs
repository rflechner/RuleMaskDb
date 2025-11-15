using Spectre.Console;
using Spectre.Console.Cli;

namespace RuleMaskDb.ConsoleApp;

internal class DescribeDatabaseCommand(IDatabaseAnalyzer databaseAnalyzer, IScriptDomProviderFactory scriptDomProviderFactory, IScriptRunner scriptRunner) : AsyncCommand<DescribeDatabaseCommand.Args>
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
        
        var databaseDescription = await databaseAnalyzer.DescribeDatabaseAsync(new DatabaseSpecification(script.Database.DatabaseType, script.Database.ConnectionString));

        var table = new Table()
            .Title($"Database [bold yellow]{databaseDescription.Name}[/]")
            .Centered()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey54);

        table.AddColumn(new TableColumn("Table").LeftAligned());
        table.AddColumn(new TableColumn("Items Count").RightAligned());
        table.AddColumn(new TableColumn("Fields").LeftAligned());

        foreach (var t in databaseDescription.Tables.OrderBy(t => t.Name))
        {
            var fieldsTexts = new List<string>();

            foreach (var field in t.Fields)
            {
                var impacted = await scriptRunner.IsImpactedAsync(script, t, field, cancellationToken);
                
                if (impacted)
                    fieldsTexts.Add($"[{Color.Chartreuse1}]{ExtractColumnName(field.Path)}[/] [{Color.Grey54}]({field.DataType})[/]");
                else
                    fieldsTexts.Add($"{ExtractColumnName(field.Path)} [{Color.Grey54}]({field.DataType})[/]");
                    
            }

            var fieldsText = fieldsTexts.Count == 0
                ? "-"
                : string.Join('\n', fieldsTexts);
            table.AddRow(
                new Markup($"[cyan]{t.Name}[/]"),
                new Markup(t.RowCount.ToString("N0")),
                new Markup(fieldsText));
        }

        AnsiConsole.Write(table);

        static string ExtractColumnName(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            var idx = path.LastIndexOf('.')
                ;
            return idx >= 0 && idx < path.Length - 1 ? path[(idx + 1)..] : path;
        }

        return 0;
    }
}