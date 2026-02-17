using Microsoft.CodeAnalysis.Sarif;
using SarifRatchet.Core;

namespace SarifRatchet.Tests.Fixtures;

public static class SarifFixture
{
    public static SarifLog CreateSampleLogWithMetadata(params (string ruleId, string filePath, int line, int col, string? logicalLocation, string? fingerprint)[] issues)
    {
        var run = new Run
        {
            Tool = new Tool { Driver = new ToolComponent { Name = "TestTool" } },
            Results = issues.Select(i => new Result
            {
                RuleId = i.ruleId,
                Message = new Message { Text = "Test issue" },
                Locations = new List<Location>
                {
                    new Location
                    {
                        PhysicalLocation = new PhysicalLocation
                        {
                            ArtifactLocation = new ArtifactLocation { Uri = new Uri(i.filePath, UriKind.RelativeOrAbsolute) },
                            Region = new Region { StartLine = i.line, StartColumn = i.col }
                        },
                        LogicalLocation = i.logicalLocation != null ? new LogicalLocation { FullyQualifiedName = i.logicalLocation } : null
                    }
                },
                PartialFingerprints = i.fingerprint != null ? new Dictionary<string, string> { ["id"] = i.fingerprint } : null
            }).ToList()
        };

        return new SarifLog { Runs = new List<Run> { run } };
    }

    public static SarifLog CreateSampleLog(params (string ruleId, string filePath, int line, int col)[] issues)
    {
        return CreateSampleLogWithMetadata(issues.Select(i => (i.ruleId, i.filePath, i.line, i.col, (string?)null, (string?)null)).ToArray());
    }
}
