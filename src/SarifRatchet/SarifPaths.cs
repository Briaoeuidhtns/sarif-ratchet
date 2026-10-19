using Microsoft.CodeAnalysis.Sarif;

namespace SarifRatchet.Core;

/// <summary>Resolves SARIF artifact references without depending on the host path syntax.</summary>
public static class SarifPaths
{
    public static string? Resolve(ArtifactLocation? location, Run? run = null, string? rootPath = null)
    {
        var path = ResolveReference(location, run, null);
        if (path == null || IsExternalUri(path)) return path;
        path = Normalize(path);
        if (rootPath == null) return path;
        var root = Normalize(rootPath);
        if (!IsAbsolutePath(root)) root = Normalize(Environment.CurrentDirectory.Replace('\\', '/') + "/" + root);
        var absolute = IsAbsolutePath(path) ? path : Normalize(root + "/" + path);
        var comparison = IsWindowsPath(root) ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var prefix = root.TrimEnd('/') + "/";
        if (absolute.Equals(root, comparison)) return ".";
        return absolute.StartsWith(prefix, comparison) ? absolute[prefix.Length..] : absolute;
    }

    public static bool IsAbsolutePath(string path)
    {
        path = path.Replace('\\', '/');
        return path.StartsWith('/') || IsWindowsPath(path);
    }

    public static Uri ToUri(string path)
    {
        if (IsExternalUri(path)) return new Uri(path, UriKind.Absolute);
        path = path.Replace('\\', '/');
        var encoded = string.Join("/", path.Split('/').Select(Uri.EscapeDataString));
        if (IsWindowsPath(path))
            return new Uri("file:///" + path[..2] + encoded[encoded.IndexOf('/')..], UriKind.Absolute);
        if (path.StartsWith("//")) return new Uri("file:" + encoded, UriKind.Absolute);
        if (path.StartsWith('/')) return new Uri("file://" + encoded, UriKind.Absolute);
        return new Uri(encoded, UriKind.Relative);
    }

    private static string? ResolveReference(ArtifactLocation? location, Run? run, HashSet<ArtifactLocation>? active)
    {
        if (location == null) return null;
        var tracksReference = (location.Uri == null && location.Index >= 0) || !string.IsNullOrEmpty(location.UriBaseId);
        if (tracksReference && !(active ??= new HashSet<ArtifactLocation>(ReferenceEqualityComparer.Instance)).Add(location))
            throw new InvalidDataException("Cyclic SARIF artifact or URI base reference.");
        try
        {
            var uri = location.Uri;
            var baseId = location.UriBaseId;
            if (uri == null && location.Index >= 0)
            {
                if (run?.Artifacts == null || location.Index >= run.Artifacts.Count)
                    throw new InvalidDataException($"Unresolved SARIF artifact index {location.Index}.");
                return ResolveReference(run.Artifacts[location.Index].Location, run, active)
                    ?? throw new InvalidDataException($"SARIF artifact index {location.Index} has no URI.");
            }
            if (uri == null) return null;
            var text = uri.OriginalString.Replace('\\', '/');
            if (uri.IsAbsoluteUri && uri.IsFile && text.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                text = Uri.UnescapeDataString(uri.AbsolutePath);
                if (!string.IsNullOrEmpty(uri.Host) && uri.Host != "localhost") text = "//" + uri.Host + text;
                if (text.Length >= 3 && text[0] == '/' && char.IsLetter(text[1]) && text[2] == ':') text = text[1..];
            }
            else if (IsExternalUri(text)) return uri.AbsoluteUri;
            else text = Uri.UnescapeDataString(text);
            if (!IsAbsolutePath(text) && !string.IsNullOrEmpty(baseId))
            {
                if (run?.OriginalUriBaseIds == null || !run.OriginalUriBaseIds.TryGetValue(baseId, out var baseLocation))
                    throw new InvalidDataException($"Unresolved SARIF URI base ID '{baseId}'.");
                var basePath = ResolveReference(baseLocation, run, active)
                    ?? throw new InvalidDataException($"SARIF URI base ID '{baseId}' has no URI.");
                if (IsExternalUri(basePath)) return new Uri(new Uri(basePath), ToUri(text)).AbsoluteUri;
                text = basePath[..(basePath.LastIndexOf('/') + 1)] + text;
            }
            var normalized = Normalize(text);
            if (normalized.Length == 0 && text.EndsWith('/')) return "./";
            return text.EndsWith('/') && !normalized.EndsWith('/') ? normalized + "/" : normalized;
        }
        finally { if (tracksReference) active!.Remove(location); }
    }

    private static bool IsWindowsPath(string path) => path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':' && path[2] == '/';
    private static bool IsExternalUri(string path) => !IsWindowsPath(path.Replace('\\', '/')) && Uri.TryCreate(path, UriKind.Absolute, out var uri) && !uri.IsFile;

    private static string Normalize(string path)
    {
        path = path.Replace('\\', '/');
        var prefix = path.StartsWith("//") ? "//" : path.StartsWith('/') ? "/" : IsWindowsPath(path) ? path[..3] : "";
        var parts = new List<string>();
        foreach (var part in path[prefix.Length..].Split('/'))
        {
            if (part.Length == 0 || part == ".") continue;
            if (part == ".." && parts.Count > 0 && parts[^1] != "..") parts.RemoveAt(parts.Count - 1);
            else if (part != ".." || prefix.Length == 0) parts.Add(part);
        }
        return prefix + string.Join("/", parts);
    }
}
