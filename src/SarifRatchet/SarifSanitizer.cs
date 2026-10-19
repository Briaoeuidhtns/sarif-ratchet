using Microsoft.CodeAnalysis.Sarif;

namespace SarifRatchet.Core;

public static class SarifSanitizer
{
    public static void Sanitize(SarifLog log, string? rootPath = null)
    {
        var visitor = new SanitizingVisitor(rootPath ?? Directory.GetCurrentDirectory());
        visitor.Visit(log);
    }

    private sealed class SanitizingVisitor(string rootPath) : SarifRewritingVisitor
    {
        private Run? _run;
        private readonly Dictionary<ArtifactLocation, string> _paths = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<ArtifactLocation> _baseLocations = new(ReferenceEqualityComparer.Instance);

        public override Run VisitRun(Run node)
        {
            _run = node;
            _paths.Clear();
            _baseLocations.Clear();
            if (node.OriginalUriBaseIds != null)
            {
                _baseLocations.UnionWith(node.OriginalUriBaseIds.Values);
            }
            node.VersionControlProvenance = null;
            ZeroTimingProperties(node);
            SanitizeRules(node.Tool?.Driver);
            if (node.Tool?.Extensions != null)
            {
                foreach (var extension in node.Tool.Extensions)
                {
                    SanitizeRules(extension);
                }
            }

            // Resolve every reference against the original tables before rewriting any URI.
            base.VisitRun(node);
            foreach (var (location, path) in _paths)
            {
                location.Uri = SarifPaths.ToUri(path);
                location.UriBaseId = null;
                location.Index = -1;
            }
            node.OriginalUriBaseIds = null;
            _run = null;
            return node;
        }

        public override ArtifactLocation VisitArtifactLocation(ArtifactLocation node)
        {
            // Base declarations are removed after all their consumers have been resolved.
            if (_baseLocations.Contains(node))
            {
                return node;
            }

            if (!_paths.ContainsKey(node))
            {
                var path = SarifPaths.Resolve(node, _run, rootPath);
                if (path != null)
                {
                    if (SarifPaths.IsAbsolutePath(path) || path == ".." || path.StartsWith("../", StringComparison.Ordinal))
                    {
                        throw new InvalidDataException($"Cannot sanitize a file outside root '{rootPath}': '{path}'. Specify a common ancestor with --root.");
                    }
                    _paths.Add(node, path);
                }
            }
            return base.VisitArtifactLocation(node);
        }

        public override Invocation VisitInvocation(Invocation node)
        {
            // Keep the invocation entry so provenance invocationIndex references remain valid.
            node.CommandLine = null;
            node.Arguments = null;
            node.ResponseFiles = null;
            node.Machine = null;
            node.Account = null;
            node.EnvironmentVariables = null;
            node.ExecutableLocation = null;
            node.WorkingDirectory = null;
            node.Stdin = null;
            node.Stdout = null;
            node.Stderr = null;
            node.StdoutStderr = null;
            node.ToolExecutionNotifications = null;
            node.ToolConfigurationNotifications = null;
            node.ProcessStartFailureMessage = null;
            return base.VisitInvocation(node);
        }

        private static void SanitizeRules(ToolComponent? component)
        {
            if (component?.Rules == null) return;
            foreach (var rule in component.Rules)
            {
                ZeroTimingProperties(rule);
            }
        }

        private static void ZeroTimingProperties(PropertyBagHolder holder)
        {
            foreach (var key in TimingPropertyKeys)
            {
                if (holder.PropertyNames.Contains(key))
                {
                    holder.SetProperty(key, "0");
                }
            }
        }

        private static readonly string[] TimingPropertyKeys =
        [
            "analyzerExecutionTime",
            "executionTimeInSeconds",
            "executionTimeInPercentage",
        ];
    }
}
