using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Cop.Core
{
    /// <summary>
    /// Exception thrown when a circular dependency is detected during resolution.
    /// </summary>
    public class CircularDependencyException : Exception
    {
        public List<string> Cycle { get; }

        public CircularDependencyException(List<string> cycle)
            : base($"Circular dependency detected: {string.Join(" → ", cycle)}")
        {
            Cycle = cycle;
        }
    }

    /// <summary>
    /// Resolves transitive dependencies for cop packages.
    /// Handles version resolution, conflict resolution (highest version wins),
    /// and circular dependency detection.
    /// </summary>
    public class DependencyResolver
    {
        private readonly GitHubPackageSource _source;

        public DependencyResolver(GitHubPackageSource source)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
        }

        /// <summary>
        /// Resolves all direct and transitive dependencies.
        /// </summary>
        /// <param name="directDependencies">Direct dependencies from the project file</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>Flat list of all resolved packages (direct + transitive), deduplicated by highest version</returns>
        /// <exception cref="CircularDependencyException">Thrown if a circular dependency is detected</exception>
        public async Task<List<PackageReference>> ResolveAsync(
            List<PackageReference> directDependencies,
            CancellationToken ct = default)
        {
            // Dictionary to track the highest version for each package (by FullPath)
            var resolvedPackages = new Dictionary<string, PackageReference>();

            // Stack for DFS traversal
            var toProcess = new Stack<PackageReference>(directDependencies.AsEnumerable().Reverse());

            // Track the resolution path for cycle detection
            var resolutionPath = new Dictionary<string, List<string>>();

            while (toProcess.Count > 0)
            {
                var current = toProcess.Pop();

                // Resolve version if null
                var resolvedCurrent = await ResolveMissingVersionAsync(current, ct);

                // Check for circular dependency
                var packagePath = GetPackageIdentifier(resolvedCurrent);
                if (resolutionPath.ContainsKey(packagePath))
                {
                    var cycle = resolutionPath[packagePath];
                    cycle.Add(packagePath); // Complete the cycle
                    throw new CircularDependencyException(cycle);
                }

                // Update resolution path
                var currentPath = new List<string> { packagePath };
                resolutionPath[packagePath] = currentPath;

                // Check if this package version is already resolved
                if (resolvedPackages.TryGetValue(packagePath, out var existing))
                {
                    // Keep the highest version
                    if (CompareVersions(resolvedCurrent.Version ?? "0.0.0", existing.Version ?? "0.0.0") <= 0)
                    {
                        // Existing version is higher or equal, skip this one
                        resolutionPath.Remove(packagePath);
                        continue;
                    }
                    // New version is higher, replace it
                }

                // Add to resolved packages
                resolvedPackages[packagePath] = resolvedCurrent;

                // Fetch metadata to get transitive dependencies
                try
                {
                    var metadata = await _source.GetPackageMetadataAsync(resolvedCurrent, ct);

                    if (metadata?.Dependencies != null && metadata.Dependencies.Count > 0)
                    {
                        // Parse each dependency string
                        var transitiveDeps = new List<PackageReference>();
                        foreach (var depString in metadata.Dependencies)
                        {
                            var depRef = PackageReference.Parse(depString);
                            if (depRef != null)
                            {
                                transitiveDeps.Add(depRef);
                            }
                        }

                        // Add transitive dependencies to the processing queue
                        // Add to path tracking for cycle detection
                        foreach (var dep in transitiveDeps.AsEnumerable().Reverse())
                        {
                            var newPath = new List<string>(currentPath) { GetPackageIdentifier(dep) };
                            var depIdentifier = GetPackageIdentifier(dep);
                            resolutionPath[depIdentifier] = newPath;
                            toProcess.Push(dep);
                        }
                    }
                }
                finally
                {
                    // Remove from path when backtracking
                    resolutionPath.Remove(packagePath);
                }
            }

            // Return sorted list
            return resolvedPackages.Values.OrderBy(p => p.FullPath).ToList();
        }

        /// <summary>
        /// Resolves a package reference with a missing version by fetching the latest version.
        /// </summary>
        private async Task<PackageReference> ResolveMissingVersionAsync(
            PackageReference reference,
            CancellationToken ct)
        {
            if (reference.Version != null)
            {
                return reference;
            }

            var latestVersion = await _source.GetLatestVersionAsync(reference, ct);
            return new PackageReference
            {
                FullPath = reference.FullPath,
                Host = reference.Host,
                Owner = reference.Owner,
                Repo = reference.Repo,
                PackageName = reference.PackageName,
                Version = latestVersion
            };
        }

        /// <summary>
        /// Gets a unique identifier for a package (owner/repo/packageName).
        /// Version is not included so different versions of the same package have the same identifier.
        /// </summary>
        private static string GetPackageIdentifier(PackageReference package)
        {
            return $"{package.Owner}/{package.Repo}/{package.PackageName}";
        }

        /// <summary>
        /// Compares two semantic versions (Major.Minor.Patch format).
        /// </summary>
        /// <param name="v1">First version string</param>
        /// <param name="v2">Second version string</param>
        /// <returns>-1 if v1 &lt; v2, 0 if v1 == v2, 1 if v1 &gt; v2</returns>
        public static int CompareVersions(string v1, string v2)
        {
            if (v1 == v2)
                return 0;

            var parts1 = ParseVersion(v1);
            var parts2 = ParseVersion(v2);

            // Compare major
            if (parts1.Major != parts2.Major)
                return parts1.Major < parts2.Major ? -1 : 1;

            // Compare minor
            if (parts1.Minor != parts2.Minor)
                return parts1.Minor < parts2.Minor ? -1 : 1;

            // Compare patch
            if (parts1.Patch != parts2.Patch)
                return parts1.Patch < parts2.Patch ? -1 : 1;

            return 0;
        }

        private static (int Major, int Minor, int Patch) ParseVersion(string version)
        {
            var parts = version.Split('.');
            
            int major = 0, minor = 0, patch = 0;

            if (parts.Length > 0 && int.TryParse(parts[0], out var m))
                major = m;

            if (parts.Length > 1 && int.TryParse(parts[1], out var mi))
                minor = mi;

            if (parts.Length > 2 && int.TryParse(parts[2], out var p))
                patch = p;

            return (major, minor, patch);
        }
    }
}
