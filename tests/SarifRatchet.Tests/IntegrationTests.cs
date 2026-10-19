using System.Diagnostics;
using System.Text.Json.Nodes;
using SarifRatchet.Cli;

namespace SarifRatchet.Tests;

public class IntegrationTests
{
    private static JsonObject Result(string path = "src/a.cs", int line = 1) => new()
    {
        ["ruleId"] = "R",
        ["message"] = new JsonObject { ["text"] = "Diagnostic" },
        ["locations"] = new JsonArray(new JsonObject
        {
            ["physicalLocation"] = new JsonObject
            {
                ["artifactLocation"] = new JsonObject { ["uri"] = path },
                ["region"] = new JsonObject { ["startLine"] = line, ["startColumn"] = 1 }
            }
        })
    };

    private static JsonObject Log(params JsonObject[] results) => new()
    {
        ["version"] = "2.1.0",
        ["runs"] = new JsonArray(new JsonObject
        {
            ["tool"] = new JsonObject
            {
                ["driver"] = new JsonObject { ["name"] = "IntegrationTool" }
            },
            ["results"] = new JsonArray(results.Cast<JsonNode>().ToArray())
        })
    };

    private static async Task<(int ExitCode, string Output, string Error)> RunCli(params string[] args)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add(typeof(Program).Assembly.Location);
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("CLI did not exit within 30 seconds.");
        }
        return (process.ExitCode, await output, await error);
    }

    [Test]
    public async Task Compare_DefaultDetectsAdditionalOccurrence_AndEmitsOnlyJson()
    {
        using var files = new Workspace();
        var baseline = files.Save("baseline.sarif", Log(Result()));
        var current = files.Save("current.sarif", Log(Result(), Result(line: 2)));
        var (code, output, _) = await RunCli("compare", baseline, current, "--format", "Json");
        var report = JsonNode.Parse(output)!;
        await Assert.That(code).IsEqualTo(1);
        await Assert.That(report["NewErrors"]!.AsArray().Count).IsEqualTo(1);
        await Assert.That(report["NewErrors"]![0]!["Line"]!.GetValue<int>()).IsEqualTo(2);
        await Assert.That(report["RemainingErrors"]!.AsArray().Count).IsEqualTo(1);
    }

    [Test]
    public async Task Compare_CheckFailsWhenBaselineContainsFixedFindings()
    {
        using var files = new Workspace();
        var baseline = files.Save("baseline.sarif", Log(Result()));
        var current = files.Save("current.sarif", Log());
        var (normalCode, _, _) = await RunCli("compare", baseline, current, "--format", "Json");
        var (checkCode, output, _) = await RunCli("compare", baseline, current, "--format", "Json", "--check");
        await Assert.That(normalCode).IsEqualTo(0);
        await Assert.That(checkCode).IsEqualTo(1);
        await Assert.That(JsonNode.Parse(output)!["FixedErrors"]!.AsArray().Count).IsEqualTo(1);
    }

    [Test]
    public async Task Update_RemovesFixedAndExcessDuplicates_WithoutAddingNewFindings()
    {
        using var files = new Workspace();
        var baseline = files.Save("baseline.sarif", Log(Result(), Result(), Result("fixed.cs")));
        var current = files.Save("current.sarif", Log(Result(), Result("new.cs")));
        var (code, _, _) = await RunCli("update", baseline, current);
        var kept = JsonNode.Parse(File.ReadAllText(baseline))!["runs"]![0]!["results"]!.AsArray();
        await Assert.That(code).IsEqualTo(0);
        await Assert.That(kept.Count).IsEqualTo(1);
        await Assert.That(kept[0]!["locations"]![0]!["physicalLocation"]!["artifactLocation"]!["uri"]!.GetValue<string>()).IsEqualTo("src/a.cs");
        var (compareCode, output, _) = await RunCli("compare", baseline, current, "--format", "Json");
        await Assert.That(compareCode).IsEqualTo(1);
        await Assert.That(JsonNode.Parse(output)!["NewErrors"]![0]!["FilePath"]!.GetValue<string>()).IsEqualTo("new.cs");
    }

    [Test]
    public async Task Strip_PreservesIndexedTargets_AndSupportsLocationsWithoutRegions()
    {
        using var files = new Workspace();
        var result = Result();
        var physical = result["locations"]![0]!["physicalLocation"]!.AsObject();
        physical.Remove("region");
        physical["artifactLocation"] = new JsonObject { ["index"] = 0 };
        result["locations"]![0]!["logicalLocations"] = new JsonArray(new JsonObject { ["index"] = 0 });
        var log = Log(result);
        var run = log["runs"]![0]!;
        run["artifacts"] = new JsonArray(new JsonObject
        {
            ["location"] = new JsonObject { ["uri"] = "src/a.cs" },
            ["contents"] = new JsonObject { ["text"] = "source text" }
        });
        run["logicalLocations"] = new JsonArray(new JsonObject { ["fullyQualifiedName"] = "Example.Method" });
        var baseline = files.Save("baseline.sarif", log);
        var current = files.Save("current.sarif", log);
        var (code, _, _) = await RunCli("update", baseline, current, "--strip");
        var stripped = JsonNode.Parse(File.ReadAllText(baseline))!["runs"]![0]!;
        await Assert.That(code).IsEqualTo(0);
        await Assert.That(stripped["artifacts"]![0]!["location"]!["uri"]!.GetValue<string>()).IsEqualTo("src/a.cs");
        await Assert.That(stripped["artifacts"]![0]!["contents"] == null).IsTrue();
        await Assert.That(stripped["logicalLocations"]![0]!["fullyQualifiedName"]!.GetValue<string>()).IsEqualTo("Example.Method");
        var (compareCode, output, _) = await RunCli("compare", baseline, current, "--format", "Json");
        await Assert.That(compareCode).IsEqualTo(0);
        await Assert.That(JsonNode.Parse(output)!["RemainingErrors"]!.AsArray().Count).IsEqualTo(1);
    }

    [Test]
    public async Task Sanitize_ResolvesEachLocation_AndRemovesPrivateInvocationAndBaseMetadata()
    {
        using var files = new Workspace();
        var sourceRoot = Path.Combine(files.DirectoryPath, "Alice project");
        var rootUri = new Uri(sourceRoot + Path.DirectorySeparatorChar).AbsoluteUri;
        JsonObject Location(string file) => new()
        {
            ["physicalLocation"] = new JsonObject
            {
                ["artifactLocation"] = new JsonObject { ["uri"] = file, ["uriBaseId"] = "SRC" }
            }
        };
        var result = Result();
        result["locations"] = new JsonArray(Location("a.cs"), Location("b.cs"));
        result["relatedLocations"] = new JsonArray(Location("c.cs"));
        result["provenance"] = new JsonObject { ["invocationIndex"] = 0 };
        var log = Log(result);
        var run = log["runs"]![0]!;
        run["originalUriBaseIds"] = new JsonObject
        {
            ["ROOT"] = new JsonObject { ["uri"] = rootUri },
            ["SRC"] = new JsonObject { ["uri"] = "src/", ["uriBaseId"] = "ROOT" }
        };
        run["artifacts"] = new JsonArray(new JsonObject { ["location"] = new JsonObject { ["uri"] = rootUri + "src/a.cs" } });
        run["invocations"] = new JsonArray(new JsonObject
        {
            ["executionSuccessful"] = true,
            ["account"] = "Alice",
            ["commandLine"] = "tool " + sourceRoot,
            ["workingDirectory"] = new JsonObject { ["uri"] = rootUri },
            ["environmentVariables"] = new JsonObject { ["HOME"] = sourceRoot }
        });
        run["versionControlProvenance"] = new JsonArray(new JsonObject { ["repositoryUri"] = "https://Alice@example.invalid/repository" });
        var input = files.Save("input.sarif", log);
        var output = Path.Combine(files.DirectoryPath, "output.sarif");
        var (code, _, _) = await RunCli("sanitize", input, "--root", sourceRoot, "--output", output);
        await Assert.That(code).IsEqualTo(0);
        var text = File.ReadAllText(output);
        var sanitized = JsonNode.Parse(text)!["runs"]![0]!;
        var locations = sanitized["results"]![0]!["locations"]!;
        await Assert.That(locations[0]!["physicalLocation"]!["artifactLocation"]!["uri"]!.GetValue<string>()).IsEqualTo("src/a.cs");
        await Assert.That(locations[1]!["physicalLocation"]!["artifactLocation"]!["uri"]!.GetValue<string>()).IsEqualTo("src/b.cs");
        await Assert.That(sanitized["results"]![0]!["relatedLocations"]![0]!["physicalLocation"]!["artifactLocation"]!["uri"]!.GetValue<string>()).IsEqualTo("src/c.cs");
        await Assert.That(sanitized["artifacts"]![0]!["location"]!["uri"]!.GetValue<string>()).IsEqualTo("src/a.cs");
        await Assert.That(sanitized["results"]![0]!["provenance"]!["invocationIndex"]!.GetValue<int>()).IsEqualTo(0);
        await Assert.That(sanitized["invocations"]!.AsArray().Count).IsEqualTo(1);
        await Assert.That(text.Contains("Alice", StringComparison.Ordinal)).IsFalse();
        await Assert.That(sanitized["originalUriBaseIds"] == null).IsTrue();
        await Assert.That(File.ReadAllText(input)).IsEqualTo(log.ToJsonString());
    }

    [Test]
    public async Task Sanitize_RefusesOutOfRootFiles_WithoutOverwritingInput()
    {
        using var files = new Workspace();
        var input = files.Save("input.sarif", Log(Result(new Uri(Path.Combine(files.DirectoryPath, "outside.cs")).AbsoluteUri)));
        var original = File.ReadAllText(input);
        var (code, _, _) = await RunCli("sanitize", input, "--root", Path.Combine(files.DirectoryPath, "project"));
        await Assert.That(code == 0).IsFalse();
        await Assert.That(File.ReadAllText(input)).IsEqualTo(original);
    }

    [Test]
    public async Task Sanitize_ZerosExistingTimingValuesAtEverySupportedScope()
    {
        using var files = new Workspace();
        var log = Log();
        var run = log["runs"]![0]!;
        run["properties"] = new JsonObject { ["analyzerExecutionTime"] = "12.34" };
        JsonObject Rule() => new()
        {
            ["id"] = "R",
            ["properties"] = new JsonObject { ["executionTimeInSeconds"] = "4.2", ["executionTimeInPercentage"] = "25" }
        };
        run["tool"]!["driver"]!["rules"] = new JsonArray(Rule());
        run["tool"]!["extensions"] = new JsonArray(new JsonObject { ["name"] = "Extension", ["rules"] = new JsonArray(Rule()) });
        var input = files.Save("input.sarif", log);
        var (code, _, _) = await RunCli("sanitize", input);
        var sanitized = JsonNode.Parse(File.ReadAllText(input))!["runs"]![0]!;
        await Assert.That(code).IsEqualTo(0);
        await Assert.That(sanitized["properties"]!["analyzerExecutionTime"]!.GetValue<string>()).IsEqualTo("0");
        foreach (var component in new[] { sanitized["tool"]!["driver"]!, sanitized["tool"]!["extensions"]![0]! })
        {
            var properties = component["rules"]![0]!["properties"]!;
            await Assert.That(properties["executionTimeInSeconds"]!.GetValue<string>()).IsEqualTo("0");
            await Assert.That(properties["executionTimeInPercentage"]!.GetValue<string>()).IsEqualTo("0");
        }
    }

    [Test]
    public async Task Sanitize_MaterializesIndexedUris_AndPreservesExternalUris()
    {
        using var files = new Workspace();
        var result = Result();
        result["locations"]![0]!["physicalLocation"]!["artifactLocation"] = new JsonObject { ["index"] = 0 };
        result["relatedLocations"] = new JsonArray(new JsonObject
        {
            ["physicalLocation"] = new JsonObject
            {
                ["artifactLocation"] = new JsonObject { ["uri"] = "https://example.invalid/a%20b.cs" }
            }
        });
        var log = Log(result);
        log["runs"]![0]!["artifacts"] = new JsonArray(new JsonObject
        {
            ["location"] = new JsonObject
            {
                ["uri"] = new Uri(Path.Combine(files.DirectoryPath, "a b.cs")).AbsoluteUri
            }
        });
        var input = files.Save("input.sarif", log);
        var (code, _, _) = await RunCli("sanitize", input, "--root", files.DirectoryPath);
        var sanitized = JsonNode.Parse(File.ReadAllText(input))!["runs"]![0]!["results"]![0]!;
        var location = sanitized["locations"]![0]!["physicalLocation"]!["artifactLocation"]!;
        await Assert.That(code).IsEqualTo(0);
        await Assert.That(location["uri"]!.GetValue<string>()).IsEqualTo("a%20b.cs");
        await Assert.That(location["index"] == null).IsTrue();
        await Assert.That(sanitized["relatedLocations"]![0]!["physicalLocation"]!["artifactLocation"]!["uri"]!.GetValue<string>()).IsEqualTo("https://example.invalid/a%20b.cs");
    }

    [Test]
    public async Task Spectre_RendersLiteralDiagnosticBrackets()
    {
        using var files = new Workspace();
        var result = Result("a.cs");
        result["message"]!["text"] = "array[int]";
        var (code, output, _) = await RunCli("compare", files.Save("baseline.sarif", Log()), files.Save("current.sarif", Log(result)));
        await Assert.That(code).IsEqualTo(1);
        await Assert.That(output).Contains("array[int]");
    }

    [Test]
    public async Task GitHub_EscapesAnnotationPropertiesAndMultilineMessages()
    {
        using var files = new Workspace();
        var result = Result("dir,a.cs");
        result["ruleId"] = "R,detail:%";
        result["message"]!["text"] = "first%\r\n::error file=injected.cs::Injected";
        var (code, output, _) = await RunCli("compare", files.Save("baseline.sarif", Log()), files.Save("current.sarif", Log(result)), "--format", "GitHub");
        await Assert.That(code).IsEqualTo(1);
        await Assert.That(output.TrimEnd('\r', '\n')).IsEqualTo("::error file=dir%2Ca.cs,line=1,col=1,title=R%2Cdetail%3A%25::first%25%0D%0A::error file=injected.cs::Injected");
    }

    [Test]
    public async Task Compare_AcceptsEmptyLogicalLocationArray()
    {
        using var files = new Workspace();
        var result = Result();
        result["locations"]![0]!["logicalLocations"] = new JsonArray();
        var (code, output, _) = await RunCli("compare", files.Save("baseline.sarif", Log()), files.Save("current.sarif", Log(result)), "--strictness", "Exact", "--format", "Json");
        await Assert.That(code).IsEqualTo(1);
        await Assert.That(JsonNode.Parse(output)!["NewErrors"]![0]!["FilePath"]!.GetValue<string>()).IsEqualTo("src/a.cs");
    }

    private sealed class Workspace : IDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "sarif regression " + Guid.NewGuid());

        public Workspace() => Directory.CreateDirectory(DirectoryPath);

        public string Save(string name, JsonObject log)
        {
            var path = Path.Combine(DirectoryPath, name);
            File.WriteAllText(path, log.ToJsonString());
            return path;
        }

        public void Dispose() => Directory.Delete(DirectoryPath, recursive: true);
    }
}
