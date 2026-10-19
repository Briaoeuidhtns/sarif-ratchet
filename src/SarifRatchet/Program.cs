using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis.Sarif;
using SarifRatchet.Core;
using Spectre.Console;
using Spectre.Console.Cli;
using Newtonsoft.Json;

namespace SarifRatchet.Cli;

public class Program
{
    public static int Main(string[] args)
    {
        var app = new CommandApp();
        app.Configure(config =>
        {
            config.AddCommand<CompareCommand>("compare")
                .WithDescription("Compare a new SARIF file against a baseline");
            config.AddCommand<UpdateCommand>("update")
                .WithDescription("Update a baseline SARIF file from a new SARIF file");
            config.AddCommand<SanitizeCommand>("sanitize")
                .WithDescription("Sanitize paths in a SARIF file to remove local user info");
        });
        return app.Run(args);
    }

    public static void SaveSarif(SarifLog log, string path)
    {
        var settings = new JsonSerializerSettings
        {
            Formatting = Newtonsoft.Json.Formatting.Indented,
            NullValueHandling = NullValueHandling.Ignore
        };
        string json = JsonConvert.SerializeObject(log, settings);
        System.IO.File.WriteAllText(path, json);
    }
}

public class CommonSettings : CommandSettings
{
    [CommandArgument(0, "<BASELINE>")]
    public string BaselinePath { get; set; } = string.Empty;

    [CommandArgument(1, "<NEW>")]
    public string NewPath { get; set; } = string.Empty;

    [CommandOption("-s|--strictness")]
    [DefaultValue(Strictness.Fingerprint)]
    public Strictness Strictness { get; set; }

    [CommandOption("-r|--root")]
    public string? RootPath { get; set; }

    [CommandOption("-f|--format")]
    [DefaultValue(OutputFormat.Spectre)]
    public OutputFormat Format { get; set; }
}

public enum OutputFormat
{
    Spectre,
    Json,
    MessagePack,
    GitHub
}

public class CompareCommand : Command<CompareCommand.Settings>
{
    public class Settings : CommonSettings
    {
        [CommandOption("--warn-removed")]
        public bool WarnRemoved { get; set; }

        [CommandOption("--check")]
        public bool CheckMode { get; set; }
    }

    public override int Execute([NotNull] CommandContext context, [NotNull] Settings settings, CancellationToken cancellationToken)
    {
        var baseline = LoadSarif(settings.BaselinePath);
        var current = LoadSarif(settings.NewPath);
        
        var engine = new RatchetEngine(settings.Strictness, settings.RootPath);
        
        var report = engine.Compare(baseline, current);
        
        IOutputFormatter formatter = settings.Format switch
        {
            OutputFormat.Json => new JsonOutputFormatter(),
            OutputFormat.Spectre => new SpectreOutputFormatter(),
            OutputFormat.MessagePack => new MessagePackOutputFormatter(),
            OutputFormat.GitHub => new GitHubOutputFormatter(),
            _ => throw new ArgumentOutOfRangeException()
        };

        formatter.Format(report, settings.WarnRemoved);

        if (report.NewErrors.Count > 0) return 1;
        if (settings.CheckMode && report.FixedErrors.Count > 0) return 1;

        return 0;
    }

    private static SarifLog LoadSarif(string path)
    {
        return SarifLog.Load(path);
    }
}

public class UpdateCommand : Command<UpdateCommand.Settings>
{
    public class Settings : CommonSettings
    {
        [CommandOption("--strip")]
        [Description("Strip non-essential metadata (snippets, tool logs) from the baseline")]
        public bool Strip { get; set; }
    }

    public override int Execute([NotNull] CommandContext context, [NotNull] Settings settings, CancellationToken cancellationToken)
    {
        var baseline = SarifLog.Load(settings.BaselinePath);
        var current = SarifLog.Load(settings.NewPath);
        
        var engine = new RatchetEngine(settings.Strictness, settings.RootPath);
        engine.RetainBaselineResults(baseline, current);

        if (settings.Strip)
        {
            foreach (var run in baseline.Runs)
            {
                foreach (var result in run.Results ?? [])
                {
                    StripResult(result);
                }

                // Keep reference tables: retained findings can address them by index.
                if (run.Artifacts != null)
                {
                    foreach (var artifact in run.Artifacts)
                    {
                        artifact.Contents = null;
                    }
                }
                if (run.Invocations != null)
                {
                    foreach (var invocation in run.Invocations)
                    {
                        invocation.ToolExecutionNotifications = null;
                        invocation.ToolConfigurationNotifications = null;
                    }
                }
            }
        }

        Program.SaveSarif(baseline, settings.BaselinePath);
        
        AnsiConsole.MarkupLine("[green]Baseline updated successfully.[/]");
        return 0;
    }

    private static void StripResult(Result result)
    {
        if (result.Locations != null)
        {
            foreach (var loc in result.Locations)
            {
                if (loc.PhysicalLocation != null)
                {
                    if (loc.PhysicalLocation.Region != null)
                    {
                        loc.PhysicalLocation.Region.Snippet = null;
                    }
                    loc.PhysicalLocation.ContextRegion = null;
                }
            }
        }
        
        result.AnalysisTarget = null;
        result.CodeFlows = null;
        result.Graphs = null;
        result.RelatedLocations = null;
        result.Stacks = null;
        result.Taxa = null;
        result.WebRequest = null;
        result.WebResponse = null;
    }
}

public class SanitizeCommand : Command<SanitizeCommand.Settings>
{
    public class Settings : CommandSettings
    {
        [CommandArgument(0, "<PATH>")]
        public string Path { get; set; } = string.Empty;

        [CommandOption("-r|--root")]
        [Description("The root directory to trim paths relative to")]
        public string? RootPath { get; set; }

        [CommandOption("-o|--output")]
        [Description("The output path (defaults to overwriting input)")]
        public string? OutputPath { get; set; }
    }

    public override int Execute([NotNull] CommandContext context, [NotNull] Settings settings, CancellationToken cancellationToken)
    {
        var log = SarifLog.Load(settings.Path);
        SarifSanitizer.Sanitize(log, settings.RootPath);

        Program.SaveSarif(log, settings.OutputPath ?? settings.Path);
        AnsiConsole.MarkupLine("[green]Sanitization complete.[/]");
        return 0;
    }

}
