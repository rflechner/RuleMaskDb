using RuleMaskDb.Generators;
using RuleMaskDb.ScriptDom;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RuleMaskDb.ConsoleApp.AppCommands;

internal sealed class ListGeneratorsCommand : Command<ListGeneratorsCommand.Args>
{
    public sealed class Args : CommandSettings { }

    public override int Execute(CommandContext context, Args settings, CancellationToken cancellationToken)
    {
        AnsiConsole.WriteLine("Legacy names: " + string.Join(", ", Enum.GetNames<GeneratorType>()));
        AnsiConsole.WriteLine("Every Bogus generator also accepts the culture parameter. Run 'plugins' for installed plugins.");
        foreach (var generator in BogusGeneratorCatalog.Generators)
            AnsiConsole.WriteLine($"{generator.Name}({generator.Parameters}) -> {generator.ReturnType}");
        return 0;
    }
}
