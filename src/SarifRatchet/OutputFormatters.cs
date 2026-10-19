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
        table.AddColumn("Message");
        table.AddColumn("File");
        table.AddColumn("Location");

        foreach (var error in report.NewErrors)
        {
            table.AddRow(
                "[red]New[/]", 
                Markup.Escape(error.RuleId),
                Markup.Escape(error.Message ?? "-"),
                Markup.Escape(error.FilePath),
                $"{error.Line}:{error.Column}"
            );
        }

        foreach (var error in report.FixedErrors)
        {
            var color = warnRemoved ? "yellow" : "green";
            table.AddRow(
                $"[{color}]Fixed[/]", 
                Markup.Escape(error.RuleId),
                Markup.Escape(error.Message ?? "-"),
                Markup.Escape(error.FilePath),
                $"{error.Line}:{error.Column}"
            );
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

public class GitHubOutputFormatter : IOutputFormatter
{
    public void Format(RatchetReport report, bool warnRemoved)
    {
        foreach (var error in report.NewErrors)
        {
            var message = error.Message ?? $"New static analysis error found: {error.RuleId}";
            WriteAnnotation("error", error, message);
        }

        if (warnRemoved)
        {
            foreach (var error in report.FixedErrors)
            {
                WriteAnnotation("warning", error, $"Fixed error was removed but --warn-removed is set: {error.RuleId}");
            }
        }
    }

    private static void WriteAnnotation(string level, ResultKey error, string message)
    {
        System.Console.WriteLine($"::{level} file={EscapeProperty(error.FilePath)},line={error.Line},col={error.Column},title={EscapeProperty(error.RuleId)}::{EscapeData(message)}");
    }

    private static string EscapeData(string value) =>
        value.Replace("%", "%25").Replace("\r", "%0D").Replace("\n", "%0A");

    private static string EscapeProperty(string value) =>
        EscapeData(value).Replace(":", "%3A").Replace(",", "%2C");
}
