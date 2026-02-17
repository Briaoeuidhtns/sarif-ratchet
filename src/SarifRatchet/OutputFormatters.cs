using SarifRatchet.Core;
using Spectre.Console;
using MessagePack;

namespace SarifRatchet.Cli;

public interface IOutputFormatter
{
    void Format(RatchetReport report, bool warnRemoved);
}

public class SpectreOutputFormatter : IOutputFormatter
{
    public void Format(RatchetReport report, bool warnRemoved)
    {
        var table = new Table();
        table.AddColumn("Status");
        table.AddColumn("Rule ID");
        table.AddColumn("File");
        table.AddColumn("Location");

        foreach (var error in report.NewErrors)
        {
            table.AddRow("[red]New[/]", error.RuleId, error.FilePath, $"{error.Line}:{error.Column}");
        }

        foreach (var error in report.FixedErrors)
        {
            var color = warnRemoved ? "yellow" : "green";
            table.AddRow($"[{color}]Fixed[/]", error.RuleId, error.FilePath, $"{error.Line}:{error.Column}");
        }

        AnsiConsole.Write(table);

        var summary = new Panel(new Rows(
            new Markup($"[red]New Errors: {report.NewErrors.Count}[/]"),
            new Markup($"[green]Fixed Errors: {report.FixedErrors.Count}[/]"),
            new Markup($"[blue]Remaining Errors: {report.RemainingErrors.Count}[/]")
        ));
        summary.Header = new PanelHeader("Summary");
        AnsiConsole.Write(summary);
    }
}

public class JsonOutputFormatter : IOutputFormatter
{
    public void Format(RatchetReport report, bool warnRemoved)
    {
        var options = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
        System.Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(report, options));
    }
}

public class MessagePackOutputFormatter : IOutputFormatter
{
    public void Format(RatchetReport report, bool warnRemoved)
    {
        var bytes = MessagePackSerializer.Serialize(report);
        using var stdout = System.Console.OpenStandardOutput();
        stdout.Write(bytes, 0, bytes.Length);
    }
}
