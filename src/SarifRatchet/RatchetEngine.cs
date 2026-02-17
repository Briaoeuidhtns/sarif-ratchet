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
        var keys = new HashSet<ResultKey>();
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

        if (_strictness == Strictness.Exact)
        {
            return new ResultKey(ruleId, filePath, message, Line: GetLine(result), Column: GetColumn(result));
        }

        if (_strictness == Strictness.Fingerprint || _strictness == Strictness.Logical)
        {
            string? fingerprint = GetFingerprint(result);
            if (fingerprint != null && _strictness == Strictness.Fingerprint)
            {
                return new ResultKey(ruleId, filePath, message, Fingerprint: fingerprint);
            }

            string? logical = GetLogicalLocation(result);
            if (logical != null)
            {
                return new ResultKey(ruleId, filePath, message, LogicalLocation: logical);
            }
        }

        return new ResultKey(ruleId, filePath, message);
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
