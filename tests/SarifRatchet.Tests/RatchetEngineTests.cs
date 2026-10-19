using Microsoft.CodeAnalysis.Sarif;
using SarifRatchet.Core;
using SarifRatchet.Tests.Fixtures;
using Artifact = Microsoft.CodeAnalysis.Sarif.Artifact;

namespace SarifRatchet.Tests;

public class RatchetEngineTests
{
    private static SarifLog Log(int line = 1, string? logical = null, string? fingerprint = null) =>
        SarifFixture.CreateSampleLogWithMetadata(("R", "src/file.cs", line, 1, logical, fingerprint));
    private static Result First(SarifLog log) => log.Runs[0].Results[0];

    [Test]
    public async Task Compare_DefaultFallsBackToRegionAndDetectsNewSameFileOccurrence()
    {
        var baseline = Log();
        var current = SarifFixture.CreateSampleLog(("R", "src/file.cs", 1, 1), ("R", "src/file.cs", 2, 1));
        var report = new RatchetEngine().Compare(baseline, current);
        await Assert.That(report.NewErrors.Count).IsEqualTo(1);
        await Assert.That(report.NewErrors[0].Line).IsEqualTo(2);
        await Assert.That(report.RemainingErrors.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Compare_MultisetPreservesIdenticalOccurrencesInEveryMode()
    {
        foreach (var mode in Enum.GetValues<Strictness>())
        {
            var baseline = Log(1, "method", "fp");
            var current = Log(1, "method", "fp");
            current.Runs[0].Results.Add(First(Log(1, "method", "fp")));
            var engine = new RatchetEngine(mode);
            var report = engine.Compare(baseline, current);
            var reverse = engine.Compare(current, baseline);
            await Assert.That(report.NewErrors.Count).IsEqualTo(1);
            await Assert.That(report.RemainingErrors.Count).IsEqualTo(1);
            await Assert.That(reverse.FixedErrors.Count).IsEqualTo(1);
        }
    }

    [Test]
    public async Task Compare_MixedMetadataSelectsOneSymmetricTierPerGroup()
    {
        var baseline = Log(1, "method", "old");
        var current = Log(10, "method");
        var engine = new RatchetEngine();
        await Assert.That(engine.Compare(baseline, current).RemainingErrors.Count).IsEqualTo(1);
        await Assert.That(engine.Compare(current, baseline).RemainingErrors.Count).IsEqualTo(1);
        First(current).Locations[0].LogicalLocations = [];
        await Assert.That(engine.Compare(baseline, current).NewErrors.Count).IsEqualTo(1);
        await Assert.That(engine.Compare(current, baseline).NewErrors.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Compare_EmptyLogicalLocationsFallBackToExactUnlessExplicitlyLoose()
    {
        var baseline = Log();
        var current = Log(2);
        First(baseline).Locations[0].LogicalLocations = [new LogicalLocation { FullyQualifiedName = "" }];

        First(current).Locations[0].LogicalLocations = [];
        await Assert.That(new RatchetEngine(Strictness.Logical).Compare(baseline, current).NewErrors.Count).IsEqualTo(1);
        await Assert.That(new RatchetEngine(Strictness.Loose).Compare(baseline, current).NewErrors.Count).IsEqualTo(0);
        First(current).Locations = [];
        await Assert.That(new RatchetEngine().Compare(baseline, current).NewErrors.Count).IsEqualTo(1);
    }
    [Test]
    public async Task Compare_GroupFallbackIsIndependentOfResultOrder()
    {
        var baseline = Log(1, null, "fingerprint");
        baseline.Runs[0].Results.Add(First(Log(2)));
        var current = Log(1);
        current.Runs[0].Results.Add(First(Log(2, null, "different")));
        var engine = new RatchetEngine();
        await Assert.That(engine.Compare(baseline, current).RemainingErrors.Count).IsEqualTo(2);
        current.Runs[0].Results = current.Runs[0].Results.Reverse().ToList();
        await Assert.That(engine.Compare(baseline, current).RemainingErrors.Count).IsEqualTo(2);
        await Assert.That(engine.Compare(current, baseline).RemainingErrors.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Compare_FingerprintsUseCanonicalWholeMapsAndPreferComplete()
    {
        foreach (var complete in new[] { false, true })
        {
            var baseline = Log();
            var current = Log(20);
            var first = new Dictionary<string, string> { ["a"] = "one", ["b"] = "two" };
            var second = new Dictionary<string, string> { ["b"] = "two", ["a"] = "one" };
            First(baseline).PartialFingerprints = first;
            First(current).PartialFingerprints = second;
            if (complete)
            {
                First(baseline).Fingerprints = first;
                First(current).Fingerprints = second;
                First(current).PartialFingerprints = new Dictionary<string, string> { ["different"] = "ignored" };
            }
            var engine = new RatchetEngine();
            await Assert.That(engine.Compare(baseline, current).RemainingErrors.Count).IsEqualTo(1);
            second["a"] = "changed";
            await Assert.That(engine.Compare(baseline, current).NewErrors.Count).IsEqualTo(1);
        }
    }

    [Test]
    public async Task Compare_NegotiatesFingerprintKindAcrossBothLogs()
    {
        var baseline = Log(1, null, "partial");
        var current = Log(20, null, "partial");
        First(baseline).Fingerprints = new Dictionary<string, string> { ["full"] = "complete" };
        var engine = new RatchetEngine();
        await Assert.That(engine.Compare(baseline, current).RemainingErrors.Count).IsEqualTo(1);
        await Assert.That(engine.Compare(current, baseline).RemainingErrors.Count).IsEqualTo(1);
        First(baseline).PartialFingerprints = null;
        First(current).Locations[0].PhysicalLocation.Region.StartLine = 1;
        await Assert.That(engine.Compare(baseline, current).RemainingErrors.Count).IsEqualTo(1);
        First(current).Locations[0].PhysicalLocation.Region.StartLine = 2;
        await Assert.That(engine.Compare(baseline, current).NewErrors.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Compare_ToolNamespacesCannotCancelEachOther()
    {
        var baseline = Log();
        var current = Log();
        current.Runs[0].Tool.Driver.Name = "OtherTool";
        var report = new RatchetEngine(Strictness.Loose).Compare(baseline, current);
        await Assert.That(report.NewErrors.Count).IsEqualTo(1);
        await Assert.That(report.FixedErrors.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Compare_RuleAndExtensionIndexesResolveToStableIdentities()
    {
        var baseline = Log();
        var current = Log();
        current.Runs[0].Tool.Driver.Rules = [new ReportingDescriptor { Id = "R" }];
        First(current).RuleId = null;
        First(current).RuleIndex = 0;
        var engine = new RatchetEngine();
        await Assert.That(engine.Compare(baseline, current).RemainingErrors.Count).IsEqualTo(1);
        foreach (var log in new[] { baseline, current })
        {
            log.Runs[0].Tool.Extensions = [new ToolComponent { Name = "Extension", Rules = [new ReportingDescriptor { Id = "R" }] }];
            First(log).RuleId = null;
            First(log).RuleIndex = -1;
            First(log).Rule = new ReportingDescriptorReference { Index = 0, ToolComponent = new ToolComponentReference { Index = 0 } };
        }
        await Assert.That(engine.Compare(baseline, current).RemainingErrors.Count).IsEqualTo(1);
        current.Runs[0].Tool.Extensions[0].Name = "AnotherExtension";
        await Assert.That(engine.Compare(baseline, current).NewErrors.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Compare_IndexedArtifactsAndLogicalLocationsMatchInlineLocations()
    {
        var baseline = Log(1, "method");
        var current = Log(20);
        var run = current.Runs[0];
        run.Artifacts = [new Artifact { Location = new ArtifactLocation { Uri = new Uri("src/file.cs", UriKind.Relative) } }];
        run.LogicalLocations = [new LogicalLocation { FullyQualifiedName = "method" }];
        First(current).Locations[0].PhysicalLocation.ArtifactLocation = new ArtifactLocation { Index = 0 };
        First(current).Locations[0].LogicalLocations = [new LogicalLocation { Index = 0 }];
        await Assert.That(new RatchetEngine().Compare(baseline, current).RemainingErrors.Count).IsEqualTo(1);
        run.Artifacts[0].Location.Uri = new Uri("src/other.cs", UriKind.Relative);
        await Assert.That(new RatchetEngine().Compare(baseline, current).NewErrors.Count).IsEqualTo(1);
    }

    [Test]
    public async Task RetainBaselineResultsOnlyRetainsMatchedOriginalOccurrences()
    {
        var baseline = Log();
        var original = First(baseline);
        baseline.Runs[0].Results.Add(original);
        baseline.Runs[0].Results.Add(First(Log(3)));
        var current = Log();
        current.Runs[0].Results.Add(First(Log(9)));
        new RatchetEngine().RetainBaselineResults(baseline, current);
        await Assert.That(baseline.Runs[0].Results.Count).IsEqualTo(1);
        await Assert.That(ReferenceEquals(baseline.Runs[0].Results[0], original)).IsTrue();
        await Assert.That(current.Runs[0].Results.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Resolve_NormalizesEncodedPortablePathsAndRootBoundaries()
    {
        var cases = new[]
        {
            ("file:///home/user/project/src/a%20b.cs", "/home/user/project", "src/a b.cs"),
            ("/home/user/project2/file.cs", "/home/user/project", "/home/user/project2/file.cs"),
            ("C:\\project\\src\\..\\a%20b.cs", "C:/project", "a b.cs"),
            ("file:///C:/project/src/a.cs", "C:/project", "src/a.cs"),
            ("../outside.cs", "/project", "/outside.cs"),
            ("src/./a/../file.cs", "/project", "src/file.cs")
        };
        foreach (var (uri, root, expected) in cases)
            await Assert.That(SarifPaths.Resolve(new ArtifactLocation { Uri = new Uri(uri, UriKind.RelativeOrAbsolute) }, rootPath: root)).IsEqualTo(expected);
        var relative = new ArtifactLocation { Uri = new Uri("file.cs", UriKind.Relative) };
        await Assert.That(SarifPaths.Resolve(relative, rootPath: ".")).IsEqualTo("file.cs");
    }

    [Test]
    public async Task Resolve_ArtifactIndexAndBaseChainsDecodeOnce()
    {
        var run = new Run
        {
            OriginalUriBaseIds = new Dictionary<string, ArtifactLocation>
            {
                ["ROOT"] = new() { Uri = new Uri("file:///project/") },
                ["SRC"] = new() { Uri = new Uri("src/", UriKind.Relative), UriBaseId = "ROOT" }
            },
            Artifacts = [new Artifact { Location = new ArtifactLocation { Uri = new Uri("a%2520%20b.cs", UriKind.Relative), UriBaseId = "SRC" } }]
        };
        var path = SarifPaths.Resolve(new ArtifactLocation { Index = 0 }, run, "/project");
        await Assert.That(path).IsEqualTo("src/a%20 b.cs");
        await Assert.That(SarifPaths.Resolve(new ArtifactLocation { Uri = SarifPaths.ToUri(path!) })).IsEqualTo(path);
        await Assert.That(SarifPaths.Resolve(null)).IsNull();
        await Assert.That(SarifPaths.Resolve(new ArtifactLocation())).IsNull();
    }

    [Test]
    public async Task Resolve_ExternalUrisRetainSchemeHostAndEscapedIdentity()
    {
        const string external = "https://example.test/src/a%2Fb.cs?version=1";
        await Assert.That(SarifPaths.Resolve(new ArtifactLocation { Uri = new Uri(external) }, rootPath: "/project")).IsEqualTo(external);
        await Assert.That(SarifPaths.ToUri(external).AbsoluteUri).IsEqualTo(external);
        await Assert.That(SarifPaths.IsAbsolutePath("C:/project/file.cs")).IsTrue();
        await Assert.That(SarifPaths.IsAbsolutePath("/project/file.cs")).IsTrue();
    }

    [Test]
    public async Task Resolve_InvalidReferencesFailExplicitly()
    {
        var run = new Run { OriginalUriBaseIds = new Dictionary<string, ArtifactLocation>() };
        var cycle = new ArtifactLocation { Uri = new Uri("src/", UriKind.Relative), UriBaseId = "LOOP" };
        run.OriginalUriBaseIds["LOOP"] = cycle;
        var failures = 0;
        foreach (var location in new[] { cycle, new ArtifactLocation { Index = 1 }, new ArtifactLocation { Uri = new Uri("a.cs", UriKind.Relative), UriBaseId = "MISSING" } })
        {
            try { SarifPaths.Resolve(location, run); }
            catch (InvalidDataException) { failures++; }
        }
        await Assert.That(failures).IsEqualTo(3);
    }
}
