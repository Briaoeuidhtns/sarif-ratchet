using System.Diagnostics;
using System.Text.Json;
using SarifRatchet.Core;

namespace SarifRatchet.Tests;

public class IntegrationTests
{
    private static string GetProjectPath() => 
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "..", "src", "SarifRatchet", "SarifRatchet.csproj");

    private static string GetSamplePath(string version, string fileName) =>
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "..", "tests", "Samples", version, fileName);

    private async Task<(int ExitCode, string Output)> RunCli(params string[] args)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"run --project {GetProjectPath()} -- " + string.Join(" ", args),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();
        string output = await process.StandardOutput.ReadToEndAsync();
        string error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return (process.ExitCode, output + error);
    }

    [Test]
    public async Task Cli_Compare_V1_vs_V2_ReturnsExitCode1()
    {
        // Arrange
        var v1 = GetSamplePath("ProjectV1", "ProjectV1.sarif");
        var v2 = GetSamplePath("ProjectV2", "ProjectV2.sarif");

        // Act
        var (exitCode, output) = await RunCli("compare", v1, v2, "--strictness", "Loose");

        // Assert
        await Assert.That(exitCode).IsEqualTo(1);
        await Assert.That(output).Contains("New Errors: 1");
    }

    [Test]
    public async Task Cli_Compare_Json_Output_Is_Valid()
    {
        // Arrange
        var v1 = GetSamplePath("ProjectV1", "ProjectV1.sarif");
        var v2 = GetSamplePath("ProjectV2", "ProjectV2.sarif");

        // Act
        var (exitCode, output) = await RunCli("compare", v1, v2, "--strictness", "Loose", "--format", "Json");

        // Assert
        await Assert.That(exitCode).IsEqualTo(1);
        var report = JsonSerializer.Deserialize<RatchetReport>(output, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        await Assert.That(report).IsNotNull();
        await Assert.That(report!.NewErrors).Count().IsEqualTo(1);
        await Assert.That(report.NewErrors[0].RuleId).IsEqualTo("CS0168");
    }

    [Test]
    public async Task Cli_Update_Ratchets_Baseline()
    {
        // Arrange
        var v1Original = GetSamplePath("ProjectV1", "ProjectV1.sarif");
        var v1Temp = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".sarif");
        File.Copy(v1Original, v1Temp);
        
        var v2 = GetSamplePath("ProjectV2", "ProjectV2.sarif");

        try
        {
            // First compare shows 1 new error
            var (exitCode1, _) = await RunCli("compare", v1Temp, v2, "--strictness", "Loose");
            await Assert.That(exitCode1).IsEqualTo(1);

            // Act: Update baseline (though in our current logic update keeps ONLY what's in current, 
            // but the prompt said "not adding any new errors but removing errors that have been fixed")
            // Actually, my current update logic is: baseline = baseline intersect current.
            // If baseline is empty (V1), and current has 1 (V2), intersection is empty.
            // Wait, "not adding any new errors but removing errors that have been fixed" means:
            // NewBaseline = Baseline - (Baseline - Current) = Baseline intersect Current.
            // This is exactly what I implemented.
            
            await RunCli("update", v1Temp, v2, "--strictness", "Loose");

            // Assert: Compare again should still show 1 new error because we DIDN'T add it to baseline
            var (exitCode2, _) = await RunCli("compare", v1Temp, v2, "--strictness", "Loose");
            await Assert.That(exitCode2).IsEqualTo(1);
            
            // Now let's test "removing errors that have been fixed".
            // If we use V2 as baseline and V1 as current.
            var v2Temp = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".sarif");
            File.Copy(v2, v2Temp);
            
            // V2 (baseline) has 1 error. V1 (current) has 0 errors.
            // Fixed = 1.
            var (exitCode3, output3) = await RunCli("compare", v2Temp, v1Original, "--strictness", "Loose");
            await Assert.That(output3).Contains("Fixed Errors: 1");

            // Update V2 with V1
            await RunCli("update", v2Temp, v1Original, "--strictness", "Loose");
            
            // Now V2 should have 0 errors.
            var (exitCode4, output4) = await RunCli("compare", v2Temp, v1Original, "--strictness", "Loose");
            await Assert.That(output4).Contains("Fixed Errors: 0");
        }
        finally
        {
            if (File.Exists(v1Temp)) File.Delete(v1Temp);
        }
    }
}
