using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis.Sarif;
using Microsoft.CodeAnalysis.Sarif.Converters;
using SarifRatchet.Core;
using Spectre.Console;
using Spectre.Console.Cli;
using System.Text.Json;

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
        });
        return app.Run(args);
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
    MessagePack
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
        
        // Multi-run logic: We flatten all results from all runs for comparison.
        // In the future, we could offer a flag to compare runs individually (e.g. by tool name).
        var baselineKeys = engine.GetResultKeys(baseline);
        var currentKeys = engine.GetResultKeys(current);

        var newErrors = currentKeys.Except(baselineKeys).ToList();
        var fixedErrors = baselineKeys.Except(currentKeys).ToList();
        var remainingErrors = currentKeys.Intersect(baselineKeys).ToList();
        
        // If the current file has more runs than baseline, we might want to alert the user
        // but for now, we treat the entire log as a single unit of analysis.

        var report = new RatchetReport(newErrors, fixedErrors, remainingErrors);
        
        IOutputFormatter formatter = settings.Format switch
        {
            OutputFormat.Json => new JsonOutputFormatter(),
            OutputFormat.Spectre => new SpectreOutputFormatter(),
            OutputFormat.MessagePack => new MessagePackOutputFormatter(),
            _ => throw new ArgumentOutOfRangeException()
        };

        formatter.Format(report, settings.WarnRemoved);

        if (newErrors.Count > 0) return 1;
        if (settings.CheckMode && fixedErrors.Count > 0) return 1;

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
        var currentKeys = engine.GetResultKeys(current);

        foreach (var run in baseline.Runs)
        {
            var resultsToKeep = new List<Result>();
            if (run.Results != null)
            {
                foreach (var result in run.Results)
                {
                    if (currentKeys.Contains(engine.CreateKey(result)))
                    {
                        if (settings.Strip)
                        {
                            StripResult(result);
                        }
                        resultsToKeep.Add(result);
                    }
                }
            }
            run.Results = resultsToKeep;

            if (settings.Strip)
            {
                run.Artifacts = null;
                run.Graphs = null;
                run.LogicalLocations = null;
                run.ThreadFlowLocations = null;
                run.Taxonomies = null;
                run.Invocations = null;
            }
        }

        baseline.Save(settings.BaselinePath);
        
        AnsiConsole.MarkupLine("[green]Baseline updated successfully.[/]");
        return 0;
    }

    private void StripResult(Result result)
    {
        // Keep only essential info for identification and a basic message
        // result.Message = result.Message; // Keep message
        
        if (result.Locations != null)
        {
            foreach (var loc in result.Locations)
            {
                if (loc.PhysicalLocation != null)
                {
                    loc.PhysicalLocation.Region.Snippet = null;
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
