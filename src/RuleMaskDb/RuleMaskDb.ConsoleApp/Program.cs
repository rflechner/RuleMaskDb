using RuleMaskDb.SqlServerDriver;
using Spectre.Console;
using Microsoft.Extensions.DependencyInjection;
using RuleMaskDb.ConsoleApp.DependencyInjection;
using RuleMaskDb.Yaml;
using Spectre.Console.Cli;

namespace RuleMaskDb.ConsoleApp;

class Program
{
    public static async Task<int> Main(string[] args)
    {
        AnsiConsole.Write(new FigletText("RuleMask DB")
        {
            Color = Color.Chartreuse2,
            Justification = Justify.Center
        });
        
        var services = new ServiceCollection();
        services.AddSingleton<IDatabaseAnalyzer, SqlServerDatabaseAnalyzer>();
        services.AddSingleton<IScriptDomProviderFactory, ScriptDomProviderFactory>();
        services.AddSingleton<DescribeDatabaseCommand>();
        
        var app = new CommandApp(new ServiceCollectionRegistar(services));
        app.Configure(config =>
        {
            config.AddCommand<DescribeDatabaseCommand>("describe");
        });

        return await app.RunAsync(args);
    }
}