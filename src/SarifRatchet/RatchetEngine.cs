using System.Text.Json;
using Microsoft.CodeAnalysis.Sarif;
using MessagePack;

namespace SarifRatchet.Core;

public enum Strictness { Exact, Fingerprint, Logical, Loose }

[MessagePackObject(keyAsPropertyName: true)]
public record ResultKey(
    string RuleId,
    string FilePath,
    string? Message = null,
    string? Fingerprint = null,
    string? LogicalLocation = null,
    int? Line = null,
    int? Column = null,
    string? ToolName = null
);

[MessagePackObject(keyAsPropertyName: true)]
public record RatchetReport(List<ResultKey> NewErrors, List<ResultKey> FixedErrors, List<ResultKey> RemainingErrors);

public class RatchetEngine
{
    private readonly Strictness _strictness;
    private readonly string? _rootPath;

    public RatchetEngine(Strictness strictness = Strictness.Fingerprint, string? rootPath = null)
    {
        _strictness = strictness;
        _rootPath = rootPath;
    }

    public RatchetReport Compare(SarifLog baseline, SarifLog current)
    {
        var matches = Match(baseline, current);
        return new RatchetReport(
            matches.Current.Where(e => !e.Matched).Select(e => e.Key).ToList(),
            matches.Baseline.Where(e => !e.Matched).Select(e => e.Key).ToList(),
            matches.Current.Where(e => e.Matched).Select(e => e.Key).ToList());
    }

    public void RetainBaselineResults(SarifLog baseline, SarifLog current)
    {
        var matches = Match(baseline, current);
        var retained = new Dictionary<Run, List<Result>>(ReferenceEqualityComparer.Instance);
        foreach (var entry in matches.Baseline)
        {
            if (!entry.Matched) continue;
            if (!retained.TryGetValue(entry.Run, out var results)) retained.Add(entry.Run, results = []);
            results.Add(entry.Result);
        }
        foreach (var run in baseline.Runs ?? [])
        {
            if (run.Results != null) run.Results = retained.GetValueOrDefault(run) ?? [];
        }
    }

    private readonly record struct RegionIdentity(int? Line, int? Column, int? EndLine, int? EndColumn,
        int? CharOffset, int? CharLength, int? ByteOffset, int? ByteLength);
    private readonly record struct MatchIdentity(string? Metadata, RegionIdentity Region = default);

    private sealed class Entry(Result result, Run run, string ruleId, string file, string? message,
        string? logical, int? line, int? column, string tool, RegionIdentity exact)
    {
        public Result Result { get; } = result;
        public Run Run { get; } = run;
        public Group Group { get; } = new(tool, ruleId, file);
        public string? Logical { get; } = logical;
        public string? Fingerprint { get; set; }
        public ResultKey Key => new(ruleId, file, message, Fingerprint, Logical, line, column, tool);
        public RegionIdentity Exact { get; } = exact;
        public bool Matched { get; set; }
    }

    private readonly record struct Group(string? Tool, string Rule, string File);

    private (List<Entry> Baseline, List<Entry> Current) Match(SarifLog baseline, SarifLog current)
    {
        var oldEntries = Read(baseline);
        var newEntries = Read(current);
        var groups = new Dictionary<Group, (List<Entry> Old, List<Entry> New)>();
        void Add(Entry entry, bool old)
        {
            var key = entry.Group;
            if (!groups.TryGetValue(key, out var group)) groups.Add(key, group = ([], []));
            (old ? group.Old : group.New).Add(entry);
        }
        foreach (var entry in oldEntries) Add(entry, true);
        foreach (var entry in newEntries) Add(entry, false);
        foreach (var group in groups.Values)
        {
            var tier = _strictness;
            bool AllHave(Func<Entry, bool> predicate) => group.Old.All(predicate) && group.New.All(predicate);
            var complete = tier == Strictness.Fingerprint && AllHave(e => Supported(e.Result.Fingerprints));
            var partial = tier == Strictness.Fingerprint && !complete && AllHave(e => Supported(e.Result.PartialFingerprints));
            if (tier == Strictness.Fingerprint && !complete && !partial) tier = Strictness.Logical;
            if (tier == Strictness.Logical && !AllHave(e => e.Logical != null)) tier = Strictness.Exact;
            MatchIdentity Identity(Entry e)
            {
                if (tier == Strictness.Fingerprint)
                {
                    e.Fingerprint = CanonicalFingerprint(complete ? e.Result.Fingerprints : e.Result.PartialFingerprints, complete);
                    return new MatchIdentity(e.Fingerprint);
                }
                return tier switch
                {
                    Strictness.Logical => new MatchIdentity(e.Logical),
                    Strictness.Loose => default,
                    _ => new MatchIdentity(null, e.Exact)
                };
            }
            var available = new Dictionary<MatchIdentity, Queue<Entry>>();
            foreach (var entry in group.Old)
            {
                var identity = Identity(entry);
                if (!available.TryGetValue(identity, out var queue)) available.Add(identity, queue = new Queue<Entry>());
                queue.Enqueue(entry);
            }
            foreach (var entry in group.New)
            {
                if (available.TryGetValue(Identity(entry), out var queue) && queue.TryDequeue(out var previous))
                {
                    previous.Matched = true;
                    entry.Matched = true;
                }
            }
        }
        return (oldEntries, newEntries);
    }

    private List<Entry> Read(SarifLog log)
    {
        var entries = new List<Entry>();
        foreach (var run in log.Runs ?? [])
        {
            var namespaces = new Dictionary<ToolComponent, string>(ReferenceEqualityComparer.Instance);
            foreach (var result in run.Results ?? [])
            {
                var component = ResolveComponent(result.Rule?.ToolComponent, run);
                if (!namespaces.TryGetValue(component, out var toolNamespace))
                {
                    toolNamespace = JsonSerializer.Serialize(new[] { run.Tool?.Driver?.Guid?.ToString(), run.Tool?.Driver?.Name,
                        component.Guid?.ToString(), component.Name });
                    namespaces.Add(component, toolNamespace);
                }
                var ruleIndex = result.Rule?.Index >= 0 ? result.Rule.Index : result.RuleIndex;
                ReportingDescriptor? rule = null;
                if (ruleIndex >= 0)
                {
                    if (component.Rules == null || ruleIndex >= component.Rules.Count)
                        throw new InvalidDataException($"Unresolved SARIF rule index {ruleIndex}.");
                    rule = component.Rules[ruleIndex];
                }
                var ruleId = result.RuleId ?? result.Rule?.Id ?? rule?.Id ?? result.Rule?.Guid?.ToString() ?? rule?.Guid?.ToString()
                    ?? throw new InvalidDataException("SARIF result has no resolvable rule identity.");
                rule ??= component.Rules?.FirstOrDefault(r => r.Id == ruleId);
                var location = result.Locations?.FirstOrDefault();
                var region = location?.PhysicalLocation?.Region;
                var exact = new RegionIdentity(region?.StartLine, region?.StartColumn, region?.EndLine,
                    region?.EndColumn, region?.CharOffset, region?.CharLength, region?.ByteOffset, region?.ByteLength);
                entries.Add(new Entry(result, run, ruleId,
                    SarifPaths.Resolve(location?.PhysicalLocation?.ArtifactLocation, run, _rootPath) ?? "unknown",
                    result.GetMessageText(rule),
                    _strictness is Strictness.Fingerprint or Strictness.Logical ? Logical(location, run) : null,
                    region?.StartLine, region?.StartColumn, toolNamespace, exact));
            }
        }
        return entries;
    }

    private static ToolComponent ResolveComponent(ToolComponentReference? reference, Run run)
    {
        var driver = run.Tool?.Driver ?? throw new InvalidDataException("SARIF run has no tool driver.");
        if (reference == null) return driver;
        if (reference.Index >= 0)
        {
            if (run.Tool!.Extensions == null || reference.Index >= run.Tool.Extensions.Count)
                throw new InvalidDataException($"Unresolved SARIF tool component index {reference.Index}.");
            return run.Tool.Extensions[reference.Index];
        }
        bool Matches(ToolComponent component) =>
            (reference.Guid != null || reference.Name != null) &&
            (reference.Guid == null || reference.Guid == component.Guid) &&
            (reference.Name == null || reference.Name == component.Name);
        if (Matches(driver)) return driver;
        return run.Tool!.Extensions?.FirstOrDefault(Matches)
            ?? throw new InvalidDataException("Unresolved SARIF tool component reference.");
    }

    private static bool Supported(IDictionary<string, string>? map) =>
        map is { Count: > 0 } && map.All(p => !string.IsNullOrEmpty(p.Key) && !string.IsNullOrEmpty(p.Value));

    private static string CanonicalFingerprint(IDictionary<string, string> values, bool complete) =>
        (complete ? "complete:" : "partial:") +
        JsonSerializer.Serialize(values.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new[] { p.Key, p.Value }));

    private static string? Logical(Location? location, Run run)
    {
        var logical = location?.LogicalLocations?.FirstOrDefault();
        if (logical == null) return null;
        HashSet<int>? visited = null;
        while (logical.Index >= 0)
        {
            if (!(visited ??= []).Add(logical.Index)) throw new InvalidDataException("Cyclic SARIF logical location reference.");
            if (run.LogicalLocations == null || logical.Index >= run.LogicalLocations.Count)
                throw new InvalidDataException($"Unresolved SARIF logical location index {logical.Index}.");
            var resolved = run.LogicalLocations[logical.Index];
            if (!string.IsNullOrEmpty(logical.FullyQualifiedName) || !string.IsNullOrEmpty(logical.DecoratedName)) break;
            logical = resolved;
        }
        return !string.IsNullOrEmpty(logical.FullyQualifiedName) ? logical.FullyQualifiedName :
            !string.IsNullOrEmpty(logical.DecoratedName) ? logical.DecoratedName : null;
    }
}
