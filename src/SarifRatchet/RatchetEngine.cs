using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis.Sarif;
using MessagePack;

namespace SarifRatchet.Core;

public enum Strictness
{
    Exact,
    Fingerprint,
    Logical,
    Loose
}

[MessagePackObject(keyAsPropertyName: true)]
public record ResultKey(
    string RuleId,
    string FilePath,
    string? Message = null,
    string? Fingerprint = null,
    string? LogicalLocation = null,
    int? Line = null,
    int? Column = null
);

[MessagePackObject(keyAsPropertyName: true)]
public record RatchetReport(
    List<ResultKey> NewErrors,
    List<ResultKey> FixedErrors,
    List<ResultKey> RemainingErrors
);

public class RatchetEngine
{
    private readonly Strictness _strictness;
    private readonly string? _rootPath;

    public RatchetEngine(Strictness strictness, string? rootPath = null)
    {
        _strictness = strictness;
        _rootPath = rootPath;
    }

    public HashSet<ResultKey> GetResultKeys(SarifLog log)
    {
        var keys = new HashSet<ResultKey>(GetComparer());
        foreach (var run in log.Runs)
        {
            if (run.Results == null) continue;
            foreach (var result in run.Results)
            {
                keys.Add(CreateKey(result, run));
            }
        }
        return keys;
    }

    public ResultKey CreateKey(Result result, Run? run = null)
    {
        string ruleId = result.RuleId;
        string filePath = GetRelativePath(result);
        string? message = null;

        if (run != null)
        {
            var rule = result.GetRule(run);
            message = result.GetMessageText(rule);
        }

        return new ResultKey(
            RuleId: ruleId,
            FilePath: filePath,
            Message: message,
            Fingerprint: GetFingerprint(result),
            LogicalLocation: GetLogicalLocation(result),
            Line: GetLine(result),
            Column: GetColumn(result)
        );
    }

    public IEqualityComparer<ResultKey> GetComparer() => new ResultKeyComparer(_strictness);

    private class ResultKeyComparer : IEqualityComparer<ResultKey>
    {
        private readonly Strictness _strictness;
        public ResultKeyComparer(Strictness strictness) => _strictness = strictness;

        public bool Equals(ResultKey? x, ResultKey? y)
        {
            if (ReferenceEquals(x, y)) return true;
            if (x is null || y is null) return false;

            if (x.RuleId != y.RuleId || x.FilePath != y.FilePath) return false;

            if (_strictness == Strictness.Exact)
            {
                return x.Line == y.Line && x.Column == y.Column;
            }

            if (_strictness == Strictness.Fingerprint || _strictness == Strictness.Logical)
            {
                if (_strictness == Strictness.Fingerprint && x.Fingerprint != null && y.Fingerprint != null)
                {
                    return x.Fingerprint == y.Fingerprint;
                }

                if (x.LogicalLocation != null && y.LogicalLocation != null)
                {
                    return x.LogicalLocation == y.LogicalLocation;
                }
            }

            return true; // Loose fallback
        }

        public int GetHashCode(ResultKey obj)
        {
            var hash = new HashCode();
            hash.Add(obj.RuleId);
            hash.Add(obj.FilePath);

            if (_strictness == Strictness.Exact)
            {
                hash.Add(obj.Line);
                hash.Add(obj.Column);
            }
            else if (_strictness == Strictness.Fingerprint && obj.Fingerprint != null)
            {
                hash.Add(obj.Fingerprint);
            }
            else if (obj.LogicalLocation != null)
            {
                hash.Add(obj.LogicalLocation);
            }
            return hash.ToHashCode();
        }
    }

    public string GetRelativePath(Result result)
    {
        var location = result.Locations?.FirstOrDefault();
        var uri = location?.PhysicalLocation?.ArtifactLocation?.Uri;
        
        if (uri == null) return "unknown";
        
        string path = uri.OriginalString;
        if (uri.IsAbsoluteUri)
        {
            path = uri.LocalPath;
        }

        if (_rootPath != null)
        {
            string absoluteRoot = Path.GetFullPath(_rootPath);
            string absolutePath = Path.IsPathRooted(path) ? path : Path.GetFullPath(path, absoluteRoot);
            
            if (absolutePath.StartsWith(absoluteRoot, StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetRelativePath(absoluteRoot, absolutePath).Replace('\\', '/');
            }
        }
        return path.Replace('\\', '/');
    }

    private int? GetLine(Result result) => 
        result.Locations?.FirstOrDefault()?.PhysicalLocation?.Region?.StartLine;

    private int? GetColumn(Result result) => 
        result.Locations?.FirstOrDefault()?.PhysicalLocation?.Region?.StartColumn;

    private string? GetFingerprint(Result result) => 
        result.PartialFingerprints?.FirstOrDefault().Value;

    private string? GetLogicalLocation(Result result) => 
        result.Locations?.FirstOrDefault()?.LogicalLocation?.FullyQualifiedName;
}
