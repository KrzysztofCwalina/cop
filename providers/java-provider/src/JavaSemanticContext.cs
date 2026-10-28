using System.Xml.Linq;
using Cop.Providers.SourceModel;

namespace Cop.Providers.SourceParsers;

internal sealed record JavaSemanticContext(string Path, List<string> Sources, List<string> Roots,
    List<string> Classpath, List<string> Errors)
{
    public static JavaSemanticContext Discover(SourceFile file)
    {
        string path = System.IO.Path.GetFullPath(string.IsNullOrEmpty(file.FullPath) ? file.Path : file.FullPath);
        string directory = System.IO.Path.GetDirectoryName(path)!;
        string packageRoot = directory;
        foreach (var _ in (file.Namespace ?? "").Split('.', StringSplitOptions.RemoveEmptyEntries))
            packageRoot = System.IO.Path.GetDirectoryName(packageRoot) ?? packageRoot;
        string root = packageRoot;
        for (string? ancestor = directory; ancestor is not null; ancestor = System.IO.Path.GetDirectoryName(ancestor))
        {
            if (File.Exists(System.IO.Path.Combine(ancestor, "pom.xml")) ||
                File.Exists(System.IO.Path.Combine(ancestor, "build.gradle")) ||
                File.Exists(System.IO.Path.Combine(ancestor, "build.gradle.kts")))
            { root = ancestor; break; }
            if (ancestor == file.SourceRoot) break;
        }
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string testRoot = System.IO.Path.Combine(root, "src", "test", "java");
        bool test = path.StartsWith(testRoot + System.IO.Path.DirectorySeparatorChar, comparison);
        var roots = new[] { packageRoot, System.IO.Path.Combine(root, "src", "main", "java") }
            .Where(Directory.Exists).Distinct(comparer).ToList();
        var sources = new HashSet<string>(comparer);
        var classpath = new List<string>();
        var errors = new List<string>();
        long bytes = 0;
        void Collect(string folder)
        {
            foreach (string source in Directory.EnumerateFiles(folder, "*.java"))
            {
                if (!sources.Add(source)) continue;
                bytes += new FileInfo(source).Length;
                if (sources.Count > 10_000 || bytes > 64 * 1024 * 1024)
                    throw new InvalidOperationException("Java semantic source context exceeds 10,000 files or 64 MiB.");
            }
            foreach (string child in Directory.EnumerateDirectories(folder))
                if (System.IO.Path.GetFileName(child) is not ("target" or "build" or ".git" or ".gradle")
                    && (File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0) Collect(child);
        }
        foreach (string sourceRoot in roots) Collect(sourceRoot);
        foreach (string output in new[] { "target\\classes", "target\\test-classes", "build\\classes\\java\\main",
                     "build\\classes\\java\\test" })
        {
            string candidate = System.IO.Path.Combine(root, output.Replace('\\', System.IO.Path.DirectorySeparatorChar));
            if (Directory.Exists(candidate)) classpath.Add(candidate);
        }
        foreach (string folder in new[] { "lib", "libs" })
        {
            string candidate = System.IO.Path.Combine(root, folder);
            if (Directory.Exists(candidate)) classpath.AddRange(Directory.EnumerateFiles(candidate, "*.jar"));
        }
        string pom = System.IO.Path.Combine(root, "pom.xml");
        if (File.Exists(pom))
        {
            var document = XDocument.Load(pom).Root ?? throw new InvalidOperationException($"{pom}: empty Maven project.");
            string? Child(XElement element, string name) => element.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim();
            var properties = document.Elements().FirstOrDefault(e => e.Name.LocalName == "properties")?.Elements()
                .ToDictionary(e => e.Name.LocalName, e => e.Value.Trim()) ?? [];
            foreach (var dependency in document.Elements().Where(e => e.Name.LocalName == "dependencies")
                         .SelectMany(e => e.Elements()).Where(e => e.Name.LocalName == "dependency"))
            {
                string? scope = Child(dependency, "scope");
                if (scope == "test" && !test || scope == "runtime" && !test) continue;
                string group = Child(dependency, "groupId") ?? "";
                string artifact = Child(dependency, "artifactId") ?? "";
                string version = Child(dependency, "version") ?? "";
                if (version.StartsWith("${") && version.EndsWith('}'))
                    version = properties.GetValueOrDefault(version[2..^1], version);
                string? systemPath = Child(dependency, "systemPath");
                string repository = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".m2", "repository");
                string jar = systemPath ?? System.IO.Path.Combine(repository, group.Replace('.', System.IO.Path.DirectorySeparatorChar),
                    artifact, version, $"{artifact}-{version}.jar");
                if (File.Exists(jar)) classpath.Add(jar);
                else errors.Add($"{pom}: Java semantic dependency unavailable locally: {group}:{artifact}:{version}. No dependency download or build was attempted.");
            }
        }
        foreach (string manifest in new[] { "build.gradle", "build.gradle.kts" })
        {
            string build = System.IO.Path.Combine(root, manifest);
            if (!File.Exists(build)) continue;
            HashSet<string> configurations = ["implementation", "api", "compile", "compileOnly"];
            if (test) configurations.UnionWith(["testImplementation", "testCompileOnly", "runtimeOnly", "testRuntimeOnly"]);
            foreach (string coordinate in JavaProjectDiscovery.ReadGradleDependencies(File.ReadAllText(build), configurations))
            {
                string[] parts = coordinate.Split(':');
                if (parts.Length != 3)
                {
                    errors.Add($"{build}: Java semantic dependency has no exact local version: {coordinate}.");
                    continue;
                }
                string cache = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".gradle", "caches", "modules-2", "files-2.1", parts[0], parts[1], parts[2]);
                var jars = Directory.Exists(cache) ? Directory.EnumerateDirectories(cache)
                    .Where(p => (File.GetAttributes(p) & FileAttributes.ReparsePoint) == 0)
                    .SelectMany(p => Directory.EnumerateFiles(p, $"{parts[1]}-{parts[2]}.jar")).ToList() : [];
                if (jars.Count == 0)
                    errors.Add($"{build}: Java semantic dependency unavailable locally: {coordinate}. No Gradle script was evaluated.");
                classpath.AddRange(jars);
            }
        }
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CLASSPATH")))
            errors.Add("Java semantic analysis ignores ambient CLASSPATH; use local lib/libs jars or declared Maven dependencies.");
        return new(path, sources.Where(p => !comparer.Equals(p, path)).ToList(),
            roots, classpath.Distinct(comparer).ToList(), errors);
    }
}
