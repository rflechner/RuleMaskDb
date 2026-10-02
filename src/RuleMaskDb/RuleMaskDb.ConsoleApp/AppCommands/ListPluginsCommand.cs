using RuleMaskDb.Generators;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RuleMaskDb.ConsoleApp.AppCommands;

internal sealed class ListPluginsCommand(PluginCatalog catalog) : Command<ListPluginsCommand.Args>
{
    public sealed class Args : CommandSettings { }

    public override int Execute(CommandContext context, Args settings, CancellationToken cancellationToken)
    {
        if (catalog.Plugins.Count == 0)
        {
            AnsiConsole.WriteLine("No generator plugins found.");
            return 0;
        }
        var table = new Table().AddColumn("Generator").AddColumn("Type").AddColumn("DLL");
        foreach (var plugin in catalog.Plugins)
            table.AddRow(Markup.Escape(plugin.Name), Markup.Escape(plugin.GeneratorType.FullName!), Markup.Escape(plugin.AssemblyPath));
        AnsiConsole.Write(table);
        return 0;
    }
}
