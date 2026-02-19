using DotnetAffected.Abstractions;
using Microsoft.Build.Construction;
using Microsoft.Build.Evaluation;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace DotnetAffected.Core
{
    internal class MSBuildProjectDiscoverer : IProjectDiscoverer
    {
        private static readonly bool IsWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        public IEnumerable<string> DiscoverProjects(IDiscoveryOptions options)
        {
            var traversalProjectPath = options.FilterFilePath;
            if (string.IsNullOrEmpty(traversalProjectPath) || !traversalProjectPath.EndsWith(".proj"))
            {
                throw new InvalidOperationException($"{traversalProjectPath} should be a .proj file");
            }

            var fullProjPath = Path.GetFullPath(traversalProjectPath);
            var traversalProjectDirectory = Path.GetDirectoryName(fullProjPath)!;

            if (TraversalProjectUsesGlobIncludes(fullProjPath))
            {
                return ExpandGlobsFromTraversalProject(fullProjPath, traversalProjectDirectory);
            }

            var traversalProject = new Project(fullProjPath);
            return traversalProject
                .GetItems("ProjectReference")
                .Select(i => i.EvaluatedInclude)
                .Select(p => Path.IsPathRooted(p) ? p : Path.Join(traversalProjectDirectory, p))
                .Select(Path.GetFullPath)
                .Distinct()
                .ToArray();
        }

        private static bool TraversalProjectUsesGlobIncludes(string traversalProjectPath)
        {
            var fullPath = Path.GetFullPath(traversalProjectPath);
            if (!File.Exists(fullPath))
            {
                return false;
            }

            var root = ProjectRootElement.Open(fullPath);
            foreach (var itemGroup in root.ItemGroups)
            {
                foreach (var elem in itemGroup.Children.OfType<ProjectItemElement>())
                {
                    var name = elem.ElementName ?? elem.ItemType ?? string.Empty;
                    if (!string.Equals(name, "ProjectReference", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var include = elem.Include ?? string.Empty;
                    if (include.IndexOf('*') >= 0)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static IEnumerable<string> ExpandGlobsFromTraversalProject(string fullProjPath, string traversalProjectDirectory)
        {
            var excludePrefixes = GetProjectReferenceRemovePrefixes(fullProjPath);
            var includePatterns = GetProjectReferenceIncludePatterns(fullProjPath);
            var sep = Path.DirectorySeparatorChar;
            var all = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var (includeBase, searchPattern) in includePatterns)
            {
                var baseDir = string.IsNullOrEmpty(includeBase)
                    ? traversalProjectDirectory
                    : Path.GetFullPath(Path.Combine(traversalProjectDirectory, includeBase));
                if (!Directory.Exists(baseDir))
                    continue;
                foreach (var file in Directory.EnumerateFiles(baseDir, searchPattern, SearchOption.AllDirectories))
                {
                    var fullPath = Path.GetFullPath(file);
                    var relative = Path.GetRelativePath(baseDir, fullPath);
                    var relativeNormalized = relative.Replace('\\', sep).Replace('/', sep);
                    if (excludePrefixes.Any(prefix => relativeNormalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    all.Add(fullPath);
                }
            }

            return all.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        private static List<(string IncludeBase, string SearchPattern)> GetProjectReferenceIncludePatterns(string fullProjPath)
        {
            var result = new List<(string, string)>();
            if (!File.Exists(fullProjPath))
                return result;

            var root = ProjectRootElement.Open(fullProjPath);
            var sep = Path.DirectorySeparatorChar;

            foreach (var itemGroup in root.ItemGroups)
            {
                var condition = itemGroup.Condition ?? string.Empty;
                var onlyWhenNotWindows = condition.IndexOf("IsOsPlatform", StringComparison.OrdinalIgnoreCase) >= 0
                    && condition.IndexOf("false", StringComparison.OrdinalIgnoreCase) >= 0;
                if (onlyWhenNotWindows && IsWindows)
                    continue;

                foreach (var elem in itemGroup.Children.OfType<ProjectItemElement>())
                {
                    if (!string.Equals(elem.ItemType, "ProjectReference", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var include = (elem.Include ?? string.Empty).Replace('/', sep).Replace('\\', sep).Trim();
                    if (include.IndexOf('*') < 0)
                        continue;
                    var starStar = include.IndexOf("**", StringComparison.Ordinal);
                    if (starStar < 0)
                        continue;
                    var includeBase = include.Substring(0, starStar).TrimEnd(sep);
                    var afterStar = include.Substring(starStar + 2).TrimStart(sep);
                    var lastSep = afterStar.LastIndexOf(sep);
                    var searchPattern = lastSep >= 0 ? afterStar.Substring(lastSep + 1) : afterStar;
                    if (string.IsNullOrEmpty(searchPattern) || searchPattern.IndexOf('*') < 0)
                        searchPattern = "*.csproj";
                    if (includeBase.IndexOf('*') >= 0)
                        includeBase = string.Empty;
                    result.Add((includeBase, searchPattern));
                }
            }

            return result;
        }

        private static List<string> GetProjectReferenceRemovePrefixes(string fullProjPath)
        {
            if (!File.Exists(fullProjPath))
            {
                return new List<string>();
            }

            var root = ProjectRootElement.Open(fullProjPath);
            var excludePrefixes = new List<string>();

            foreach (var itemGroup in root.ItemGroups)
            {
                var condition = itemGroup.Condition ?? string.Empty;
                var onlyWhenNotWindows = condition.IndexOf("IsOsPlatform", StringComparison.OrdinalIgnoreCase) >= 0
                    && condition.IndexOf("false", StringComparison.OrdinalIgnoreCase) >= 0;
                if (onlyWhenNotWindows && IsWindows)
                {
                    continue;
                }

                foreach (var elem in itemGroup.Children.OfType<ProjectItemElement>())
                {
                    if (!string.Equals(elem.ItemType, "ProjectReference", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var remove = elem.Remove;
                    if (string.IsNullOrWhiteSpace(remove))
                    {
                        continue;
                    }

                    var sep = Path.DirectorySeparatorChar;
                    var normalized = remove.Replace('\\', sep).Replace('/', sep).Trim();
                    var starStar = normalized.IndexOf("**", StringComparison.Ordinal);
                    var prefix = starStar > 0
                        ? normalized.Substring(0, starStar).TrimEnd(sep) + sep
                        : normalized + sep;
                    if (prefix.Length > 0 && !excludePrefixes.Contains(prefix, StringComparer.OrdinalIgnoreCase))
                    {
                        excludePrefixes.Add(prefix);
                    }
                }
            }

            return excludePrefixes;
        }
    }
}
