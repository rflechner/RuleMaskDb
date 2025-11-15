using Spectre.Console;
using Spectre.Console.Rendering;

namespace RuleMaskDb.ConsoleApp;

internal class TotalStepsProgressColumn(int minWidth = 6) : ProgressColumn
{
    public override IRenderable Render(RenderOptions options, ProgressTask task, TimeSpan deltaTime)
    {
        var max = (long)Math.Round(task.MaxValue);
        var current = (long)Math.Round(task.Value);

        var maxDigits = Math.Max(1, max.ToString(System.Globalization.CultureInfo.InvariantCulture).Length);
        var width = Math.Max(minWidth, maxDigits);

        var valueStr = current.ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(width, ' ');
        var maxStr = max.ToString(System.Globalization.CultureInfo.InvariantCulture);

        return new Text($"[{valueStr} / {maxStr}]");
    }
}