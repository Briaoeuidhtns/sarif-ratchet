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

[MessagePackObject]
public record ResultKey(
    [property: Key(0)] string RuleId,
    [property: Key(1)] string FilePath,
    [property: Key(2)] string? Fingerprint = null,
    [property: Key(3)] string? LogicalLocation = null,
    [property: Key(4)] int? Line = null,
    [property: Key(5)] int? Column = null
);

[MessagePackObject]
public record RatchetReport(
    [property: Key(0)] List<ResultKey> NewErrors,
    [property: Key(1)] List<ResultKey> FixedErrors,
    [property: Key(2)] List<ResultKey> RemainingErrors
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
        var keys = new HashSet<ResultKey>();
        foreach (var run in log.Runs)
        {
            if (run.Results == null) continue;
            foreach (var result in run.Results)
            {
                keys.Add(CreateKey(result));
            }
        }
        return keys;
    }

    public ResultKey CreateKey(Result result)
    {
        string ruleId = result.RuleId;
        string filePath = GetRelativePath(result);

        if (_strictness == Strictness.Exact)
        {
            return new ResultKey(ruleId, filePath, Line: GetLine(result), Column: GetColumn(result));
        }

        if (_strictness == Strictness.Fingerprint || _strictness == Strictness.Logical)
        {
            string? fingerprint = GetFingerprint(result);
            if (fingerprint != null && _strictness == Strictness.Fingerprint)
            {
                return new ResultKey(ruleId, filePath, Fingerprint: fingerprint);
            }

            string? logical = GetLogicalLocation(result);
            if (logical != null)
            {
                return new ResultKey(ruleId, filePath, LogicalLocation: logical);
            }
        }

        return new ResultKey(ruleId, filePath);
    }

    private string GetRelativePath(Result result)
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
