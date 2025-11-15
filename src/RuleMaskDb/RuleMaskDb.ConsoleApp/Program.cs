using RuleMaskDb.SqlServerDriver;
using Spectre.Console;
using System.Linq;

namespace RuleMaskDb.ConsoleApp;

class Program
{
    public static async Task Main(string[] args)
    {
        AnsiConsole.Write(new FigletText("RuleMask DB")
        {
            Color = Color.Chartreuse2,
            Justification = Justify.Center
        });

        var databaseAnalyzer = new SqlServerDatabaseAnalyzer();

        var databaseDescription = await databaseAnalyzer.DescribeDatabaseAsync(new DatabaseSpecification(DatabaseType.SqlServer, 
            "Server=localhost\\SQLEXPRESS;Database=ContosoRetailDW;Integrated Security=True;TrustServerCertificate=True;"));

        
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
            var fieldsText = t.Fields.Length == 0
                ? "-"
                : string.Join('\n', t.Fields.Select(f => $"{ExtractColumnName(f.Path)} [grey]({f.DataType})[/]"));

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
    }
}