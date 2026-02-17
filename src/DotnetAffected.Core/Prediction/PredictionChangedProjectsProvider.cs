using DotnetAffected.Abstractions;
using Microsoft.Build.Graph;
using Microsoft.Build.Prediction;
using Microsoft.Build.Prediction.Predictors;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace DotnetAffected.Core
{
    /// <summary>
    /// Determines which projects have changed based on the list of files that have changed.
    /// Uses MSBuild.Prediction to figure out which files are input of which projects.
    /// </summary>
    public class PredictionChangedProjectsProvider : IChangedProjectsProvider
    {
        private readonly ProjectGraph _graph;

        private static readonly ProjectFileAndImportsGraphPredictor[] GraphPredictors = new[]
        {
            new ProjectFileAndImportsGraphPredictor()
        };

        /// <summary>
        /// Keeps a list of all predictors that predict input files.
        /// When Microsoft.Build.Prediction is updated, this list needs to be reviewed.
        /// </summary>
        private static readonly IProjectPredictor[] ProjectPredictors = Microsoft.Build.Prediction.ProjectPredictors
            .AllProjectPredictors
            .Where(p => p.GetType() != typeof(OutDirOrOutputPathPredictor))
            .ToArray();

        private readonly ProjectGraphPredictionExecutor _executor = new ProjectGraphPredictionExecutor(
            GraphPredictors,
            ProjectPredictors);

        /// <summary>
        /// REMARKS: we have other means for detecting changes excluded files 
        /// </summary>
        private readonly string[] _fileExclusions = new[]
        {
            // Predictors won't take into account package references
            "Directory.Packages.props"
        };

        private readonly string _repositoryPath;

        /// <summary>
        /// Creates the <see cref="PredictionChangedProjectsProvider"/>.
        /// </summary>
        /// <param name="graph"></param>
        /// <param name="options"></param>
        public PredictionChangedProjectsProvider(
            ProjectGraph graph,
            IDiscoveryOptions options)
        {
            _graph = graph;
            _repositoryPath = options.RepositoryPath;
        }

        /// <inheritdoc />
        public IEnumerable<ProjectGraphNode> GetReferencingProjects(
            IEnumerable<string> files)
        {
            var hasReturned = new HashSet<string>(PathComparer.Instance);

            var collector = new FilesByProjectGraphCollector(this._graph, this._repositoryPath);
            _executor.PredictInputsAndOutputs(_graph, collector);

            var normalizedFiles = files
                .Where(f => !_fileExclusions.Any(f.EndsWith))
                .Select(Path.GetFullPath)
                .ToList();

            var projectPathToNode = _graph.ProjectNodes
                .ToDictionary(n => n.ProjectInstance.FullPath, n => n, PathComparer.Instance);

            foreach (var file in normalizedFiles)
            {
                if (file.EndsWith(".csproj", PathComparer.DefaultStringComparison)
                    && projectPathToNode.TryGetValue(file, out var node)
                    && hasReturned.Add(node.ProjectInstance.FullPath))
                {
                    yield return node;
                }

                var nodesWithFiles = collector.PredictionsPerNode
                    .Where(x => x.Value.Contains(file));

                foreach (var (key, _) in nodesWithFiles)
                {
                    if (hasReturned.Add(key.ProjectInstance.FullPath))
                    {
                        yield return key;
                    }
                }
            }
        }

        private sealed class PathComparer : IEqualityComparer<string>
        {
            internal static readonly PathComparer Instance = new();
            internal static readonly StringComparison DefaultStringComparison =
                RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

            public bool Equals(string? x, string? y)
            {
                if (x == null && y == null) return true;
                if (x == null || y == null) return false;
                return string.Equals(Path.GetFullPath(x), Path.GetFullPath(y), DefaultStringComparison);
            }

            public int GetHashCode(string obj)
            {
                return (Path.GetFullPath(obj) ?? "").GetHashCode(DefaultStringComparison);
            }
        }
    }
}
