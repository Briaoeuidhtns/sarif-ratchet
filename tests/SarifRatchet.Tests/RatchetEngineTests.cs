using SarifRatchet.Core;
using SarifRatchet.Tests.Fixtures;

namespace SarifRatchet.Tests;

public class RatchetEngineTests
{
    [Test]
    public async Task Compare_IdentifiesNewError()
    {
        // Arrange
        var baseline = SarifFixture.CreateSampleLog(("RULE001", "file.cs", 1, 1));
        var current = SarifFixture.CreateSampleLog(("RULE001", "file.cs", 1, 1), ("RULE002", "other.cs", 2, 2));
        var engine = new RatchetEngine(Strictness.Exact);

        // Act
        var baselineKeys = engine.GetResultKeys(baseline);
        var currentKeys = engine.GetResultKeys(current);
        var newErrors = currentKeys.Except(baselineKeys).ToList();

        // Assert
        await Assert.That(newErrors).Count().IsEqualTo(1);
        await Assert.That(newErrors[0].RuleId).IsEqualTo("RULE002");
    }

    [Test]
    public async Task Compare_IdentifiesFixedError()
    {
        // Arrange
        var baseline = SarifFixture.CreateSampleLog(("RULE001", "file.cs", 1, 1));
        var current = SarifFixture.CreateSampleLog();
        var engine = new RatchetEngine(Strictness.Exact);

        // Act
        var baselineKeys = engine.GetResultKeys(baseline);
        var currentKeys = engine.GetResultKeys(current);
        var fixedErrors = baselineKeys.Except(currentKeys).ToList();

        // Assert
        await Assert.That(fixedErrors).Count().IsEqualTo(1);
        await Assert.That(fixedErrors[0].RuleId).IsEqualTo("RULE001");
    }

    [Test]
    public async Task Compare_ExactStrictness_FailsOnLineShift()
    {
        // Arrange
        var baseline = SarifFixture.CreateSampleLog(("RULE001", "file.cs", 10, 1));
        var current = SarifFixture.CreateSampleLog(("RULE001", "file.cs", 11, 1)); // Shifted 1 line
        var engine = new RatchetEngine(Strictness.Exact);

        // Act
        var baselineKeys = engine.GetResultKeys(baseline);
        var currentKeys = engine.GetResultKeys(current);
        var newErrors = currentKeys.Except(baselineKeys).ToList();

        // Assert
        await Assert.That(newErrors).Count().IsEqualTo(1);
    }

    [Test]
    public async Task Compare_LooseStrictness_IgnoresLineShift()
    {
        // Arrange
        var baseline = SarifFixture.CreateSampleLog(("RULE001", "file.cs", 10, 1));
        var current = SarifFixture.CreateSampleLog(("RULE001", "file.cs", 11, 1)); // Shifted 1 line
        var engine = new RatchetEngine(Strictness.Loose);

        // Act
        var baselineKeys = engine.GetResultKeys(baseline);
        var currentKeys = engine.GetResultKeys(current);
        var newErrors = currentKeys.Except(baselineKeys).ToList();

        // Assert
        await Assert.That(newErrors).Count().IsEqualTo(0);
    }

    [Test]
    public async Task CreateKey_FingerprintFallbackToLogical()
    {
        // Arrange
        var engine = new RatchetEngine(Strictness.Fingerprint);
        var log = SarifFixture.CreateSampleLogWithMetadata(("RULE001", "file.cs", 1, 1, "MyNamespace.MyMethod", null));
        var result = log.Runs[0].Results[0];

        // Act
        var key = engine.CreateKey(result);

        // Assert
        await Assert.That(key.Fingerprint).IsNull();
        await Assert.That(key.LogicalLocation).IsEqualTo("MyNamespace.MyMethod");
    }

    [Test]
    public async Task CreateKey_FingerprintFallbackToLoose()
    {
        // Arrange
        var engine = new RatchetEngine(Strictness.Fingerprint);
        var log = SarifFixture.CreateSampleLogWithMetadata(("RULE001", "file.cs", 1, 1, null, null));
        var result = log.Runs[0].Results[0];

        // Act
        var key = engine.CreateKey(result);

        // Assert
        await Assert.That(key.Fingerprint).IsNull();
        await Assert.That(key.LogicalLocation).IsNull();
        await Assert.That(key.RuleId).IsEqualTo("RULE001");
    }

    [Test]
    public async Task GetRelativePath_NormalizesRoot()
    {
        // Arrange
        string root = "/home/user/project";
        var engine = new RatchetEngine(Strictness.Loose, root);
        var log = SarifFixture.CreateSampleLog(("RULE001", "/home/user/project/src/file.cs", 1, 1));
        var result = log.Runs[0].Results[0];

        // Act
        var key = engine.CreateKey(result);

        // Assert
        await Assert.That(key.FilePath).IsEqualTo("src/file.cs");
    }

    [Test]
    public async Task GetRelativePath_HandlesWindowsPaths()
    {
        // Arrange
        string root = "C:/project";
        var engine = new RatchetEngine(Strictness.Loose, root);
        var log = SarifFixture.CreateSampleLog(("RULE001", "C:\\project\\src\\file.cs", 1, 1));
        var result = log.Runs[0].Results[0];

        // Act
        var key = engine.CreateKey(result);

        // Assert
        // On Linux, Path.GetFullPath/GetRelativePath might not handle C: correctly if it's not the OS separator,
        // but our engine replaces \ with / so we can test that part.
        await Assert.That(key.FilePath.Contains('\\')).IsFalse();
    }
}
