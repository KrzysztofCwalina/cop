using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Cop.Driver.Pages;

/// <summary>
/// Renders a detail page for a single package with tabbed navigation
/// for overview, instructions, skills, checks, and tests.
/// Markdown files are rendered as formatted prose; other files as code blocks.
/// </summary>
public static class PackageDetail
{
    private static readonly (string Id, string Label, string Icon)[] TabDefinitions =
    [
        ("overview", "Overview", "📋"),
        ("instructions", "Instructions", "📖"),
        ("skills", "Skills", "⚡"),
        ("Rules", "Rules", "✅"),
        ("tests", "Tests", "🧪"),
    ];

    public static string Render(string feed, string packageName, Cop.Core.PackageMetadata metadata, List<PackageFile> files, bool isLocal = false)
    {
        var feedLabel = Cop.Core.FeedManager.IsLocalFeed(feed)
            ? $"📁 {Path.GetFileName(feed.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))} (local)"
            : feed;

        var categories = CategorizeFiles(files);

        // Build tab bar and panes
        var tabBar = new StringBuilder();
        var tabPanes = new StringBuilder();
        bool isFirst = true;

        foreach (var (id, label, icon) in TabDefinitions)
        {
            var tabFiles = categories[id];
            var displayable = GetDisplayableFiles(tabFiles);
            if (id != "overview" && displayable.Count == 0) continue;

            var activeClass = isFirst ? " active" : "";
            var hiddenStyle = isFirst ? "" : " style=\"display:none\"";
            tabBar.AppendLine($"            <button class=\"tab-btn{activeClass}\" data-tab=\"{id}\">{icon} {label}</button>");

            tabPanes.AppendLine($"        <div class=\"tab-pane\" id=\"tab-{id}\"{hiddenStyle}>");

            if (id == "overview")
            {
                // Compact metadata bar
                tabPanes.Append(BuildMetaBar(metadata, feedLabel, feed));

                // Main package .md rendered as overview prose (no file header)
                var mainMd = displayable.FirstOrDefault(f =>
                    f.RelativePath.Replace('\\', '/').EndsWith(".md", StringComparison.OrdinalIgnoreCase));
                if (mainMd != null)
                {
                    var content = StripFrontmatter(mainMd.Content ?? "");
                    content = StripLeadingHeading(content);
                    var mdId = "md-overview-main";
                    tabPanes.AppendLine($"            <div class=\"prose\" id=\"{mdId}\"></div>");
                    tabPanes.AppendLine($"            <textarea class=\"md-source\" data-target=\"{mdId}\" style=\"display:none\">{HtmlEncode(content)}</textarea>");
                }

                // Other root-level files (non-md)
                foreach (var f in displayable.Where(f => f != mainMd).OrderBy(f => f.RelativePath))
                    tabPanes.Append(RenderFileBlock(f, f.RelativePath.Replace('\\', '/'), "overview"));
            }
            else
            {
                var prefix = id + "/";
                foreach (var f in displayable.OrderBy(f => f.RelativePath))
                {
                    var normalized = f.RelativePath.Replace('\\', '/');
                    var displayPath = normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                        ? normalized[prefix.Length..] : normalized;
                    tabPanes.Append(RenderFileBlock(f, displayPath, id));
                }
            }

            tabPanes.AppendLine("        </div>");
            isFirst = false;
        }

        return $@"<!DOCTYPE html>
<html lang=""en"">
<head>
    <meta charset=""UTF-8"">
    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
    <title>copweb — {HtmlEncode(packageName)}</title>
    <script src=""https://cdn.jsdelivr.net/npm/marked/marked.min.js""></script>
    <link rel=""stylesheet"" href=""https://cdn.jsdelivr.net/gh/highlightjs/cdn-release@11/build/styles/github-dark.min.css"">
    <script src=""https://cdn.jsdelivr.net/gh/highlightjs/cdn-release@11/build/highlight.min.js""></script>
    <style>
        {Dashboard.SharedStyles()}

        .breadcrumb {{ color: #8b949e; font-size: 14px; margin-bottom: 12px; }}
        .breadcrumb a {{ color: #58a6ff; text-decoration: none; }}
        .breadcrumb a:hover {{ text-decoration: underline; }}

        .meta-bar {{
            display: flex; align-items: center; flex-wrap: wrap; gap: 8px;
            margin-bottom: 20px; padding: 10px 16px;
            background: #161b22; border: 1px solid #30363d; border-radius: 6px;
        }}
        .meta-badge {{
            display: inline-block; padding: 2px 10px; border-radius: 12px;
            font-size: 12px; background: #30363d; color: #c9d1d9;
        }}
        .meta-badge.version {{ background: #1f6feb; color: #fff; }}
        .meta-sep {{ color: #30363d; margin: 0 2px; }}
        .tag {{ background: #1f3a5f; color: #79c0ff; padding: 2px 10px; border-radius: 12px; font-size: 12px; }}
        .dep-link {{ display: inline-block; background: #1a2332; color: #79c0ff; padding: 2px 10px; border-radius: 4px; font-size: 12px; font-family: monospace; text-decoration: none; border: 1px solid #30363d; }}
        .dep-link:hover {{ border-color: #58a6ff; background: #1f3a5f; }}
        .muted {{ color: #8b949e; }}

        .tab-bar {{
            display: flex; gap: 0;
            border-bottom: 1px solid #30363d; margin-bottom: 24px;
        }}
        .tab-btn {{
            background: none; border: none;
            padding: 10px 20px; color: #8b949e; font-size: 14px; font-weight: 500;
            cursor: pointer; border-bottom: 2px solid transparent;
            transition: all 0.15s; font-family: inherit;
        }}
        .tab-btn:hover {{ color: #c9d1d9; background: #161b22; }}
        .tab-btn.active {{ color: #f0f6fc; border-bottom-color: #58a6ff; }}

        /* Rendered markdown prose */
        .prose {{
            font-size: 15px; line-height: 1.7; color: #c9d1d9;
        }}
        .prose h1 {{ font-size: 22px; color: #f0f6fc; border-bottom: 1px solid #30363d; padding-bottom: 6px; margin: 28px 0 12px; }}
        .prose h2 {{ font-size: 18px; color: #f0f6fc; border-bottom: 1px solid #21262d; padding-bottom: 4px; margin: 24px 0 10px; }}
        .prose h3 {{ font-size: 15px; color: #f0f6fc; margin: 20px 0 8px; }}
        .prose p {{ margin: 0 0 14px; }}
        .prose ul, .prose ol {{ padding-left: 24px; margin: 0 0 14px; }}
        .prose li {{ margin: 3px 0; }}
        .prose strong {{ color: #f0f6fc; }}
        .prose a {{ color: #58a6ff; text-decoration: none; }}
        .prose a:hover {{ text-decoration: underline; }}
        .prose blockquote {{ border-left: 3px solid #3b82f6; padding-left: 16px; color: #8b949e; margin: 0 0 14px; }}
        .prose table {{ border-collapse: collapse; width: 100%; margin: 0 0 14px; }}
        .prose th, .prose td {{ border: 1px solid #30363d; padding: 6px 12px; text-align: left; }}
        .prose th {{ background: #161b22; color: #f0f6fc; }}
        .prose code {{
            background: #161b22; padding: 2px 6px; border-radius: 4px;
            font-size: 13px; font-family: 'Monaco','Menlo','Ubuntu Mono',monospace;
        }}
        .prose pre {{
            background: #161b22; border: 1px solid #30363d; border-radius: 6px;
            padding: 14px; overflow-x: auto; margin: 0 0 14px;
        }}
        .prose pre code {{
            background: none; padding: 0; font-size: 13px; line-height: 1.5;
        }}

        /* File sections for non-markdown files */
        .file-section {{ margin-bottom: 20px; }}
        .file-name {{
            font-family: monospace; font-size: 12px; color: #8b949e;
            padding: 6px 14px; background: #161b22;
            border: 1px solid #30363d; border-bottom: none;
            border-radius: 6px 6px 0 0;
        }}
        .code-block {{
            background: #0d1117; border: 1px solid #30363d;
            border-radius: 0 0 6px 6px; padding: 14px; overflow-x: auto;
            font-family: 'Monaco','Menlo','Ubuntu Mono',monospace;
            font-size: 13px; line-height: 1.5; color: #c9d1d9; margin: 0;
        }}
        .code-block code {{ background: none; padding: 0; font-size: inherit; font-family: inherit; }}
        .hljs {{ background: transparent; }}
        .hljs-comment, .hljs-doctag {{ color: #6a9955 !important; }}

        /* Markdown file sections get a subtle top label, then prose below */
        .file-section .prose {{ padding: 16px; background: #0d1117; border: 1px solid #30363d; border-top: none; border-radius: 0 0 6px 6px; }}

        /* Command summary cards rendered from ## doc comments */
        .check-summaries {{
            padding: 10px 14px; background: #0d1117;
            border: 1px solid #30363d; border-bottom: none;
        }}
        .check-summary {{
            display: flex; align-items: baseline; gap: 10px;
            padding: 6px 0; border-bottom: 1px solid #21262d;
        }}
        .check-summary:last-child {{ border-bottom: none; }}
        .check-name {{
            font-family: 'Monaco','Menlo','Ubuntu Mono',monospace;
            font-size: 13px; font-weight: 600; color: #79c0ff;
            white-space: nowrap;
        }}
        .check-doc {{ font-size: 13px; color: #8b949e; }}

        .analyzer-list {{ margin: 8px 0 16px; }}
        .analyzer-item {{
            display: flex; align-items: center; gap: 10px;
            padding: 8px 14px;
            background: #161b22; border: 1px solid #21262d; border-radius: 6px;
            margin-bottom: 6px;
        }}
        .analyzer-name {{
            font-size: 14px; color: #58a6ff; text-decoration: none;
            font-family: 'Monaco','Menlo','Ubuntu Mono',monospace;
        }}
        .analyzer-name:hover {{ text-decoration: underline; }}
        .analyzer-ver {{ font-size: 12px; color: #8b949e; }}
    </style>
</head>
<body>
    <div class=""container"">
        {Dashboard.NavBar("packages", isLocal)}
        <div class=""breadcrumb"">
            <a href=""/"">Packages</a> / <a href=""/"">{HtmlEncode(feedLabel)}</a> / {HtmlEncode(packageName)}
        </div>
        <h1>{HtmlEncode(metadata.Title ?? packageName)}</h1>

        <div class=""tab-bar"">
{tabBar}
        </div>

{tabPanes}
    </div>
    <script>
        // Register custom cop check language grammar for highlight.js
        if (typeof hljs !== 'undefined') {{
            var copGrammar = function(hljs) {{
                return {{
                    name: 'cop',
                    keywords: {{
                        keyword: 'check',
                        built_in: 'Target Check Message error warning info CSharp Python Path Matches',
                        type: 'Type Method Constructor Parameter Statement Line Types Statements Lines',
                        literal: 'true false'
                    }},
                    contains: [
                        {{
                            className: 'doctag',
                            begin: /^##\s/,
                            end: /$/,
                            relevance: 10
                        }},
                        {{
                            className: 'comment',
                            begin: /^#\s*$/,
                            end: /^#\s*$/,
                            relevance: 0
                        }},
                        {{
                            className: 'comment',
                            begin: /#(?!#)/,
                            end: /$/,
                            relevance: 0
                        }},
                        hljs.C_LINE_COMMENT_MODE,
                        {{
                            className: 'string',
                            begin: '@""', end: '""',
                            contains: [{{ className: 'subst', begin: /\{{/, end: /\}}/ }}]
                        }},
                        {{
                            className: 'string',
                            begin: '""', end: '""', illegal: /\n/,
                            contains: [{{ className: 'subst', begin: /\{{/, end: /\}}/ }}]
                        }},
                        {{ className: 'operator', begin: /=>|&&|\|\||==|!=/ }},
                        {{ className: 'title.function', begin: /\b[A-Z]\w*(?=\s*[\(\{{])/ }},
                        {{ className: 'title.function', begin: /\.\w+(?=\s*\()/ }}
                    ]
                }};
            }};
            hljs.registerLanguage('cop', copGrammar);
        }}

        // Render markdown content via marked.js
        (function() {{
            var sources = document.querySelectorAll('.md-source');
            if (typeof marked !== 'undefined') {{
                sources.forEach(function(ta) {{
                    var target = document.getElementById(ta.dataset.target);
                    if (target) {{ target.innerHTML = marked.parse(ta.value); }}
                    ta.remove();
                }});
            }} else {{
                // Fallback: show as plain text if CDN unreachable
                sources.forEach(function(ta) {{
                    var target = document.getElementById(ta.dataset.target);
                    if (target) {{ target.textContent = ta.value; target.style.whiteSpace = 'pre-wrap'; }}
                    ta.remove();
                }});
            }}

            // Syntax-highlight all code blocks (both in rendered markdown and standalone)
            if (typeof hljs !== 'undefined') {{
                document.querySelectorAll('pre code').forEach(function(block) {{
                    hljs.highlightElement(block);
                }});
            }}

            // Tab switching
            document.querySelectorAll('.tab-btn').forEach(function(btn) {{
                btn.addEventListener('click', function() {{
                    document.querySelectorAll('.tab-btn').forEach(function(b) {{ b.classList.remove('active'); }});
                    document.querySelectorAll('.tab-pane').forEach(function(p) {{ p.style.display = 'none'; }});
                    btn.classList.add('active');
                    document.getElementById('tab-' + btn.dataset.tab).style.display = 'block';
                }});
            }});
        }})();
    </script>
</body>
</html>";
    }

    private static string RenderFileBlock(PackageFile f, string displayPath, string tabId)
    {
        var sb = new StringBuilder();
        var anchor = f.RelativePath.Replace('\\', '/').Replace("/", "-").Replace(".", "-");
        bool isMarkdown = displayPath.EndsWith(".md", StringComparison.OrdinalIgnoreCase);
        bool isCopScript = tabId == "Rules" && displayPath.EndsWith(".cop", StringComparison.OrdinalIgnoreCase);
        bool isAnalyzersYaml = displayPath.Equals("nuget-analyzers.yaml", StringComparison.OrdinalIgnoreCase);

        sb.AppendLine($"            <div class=\"file-section\" id=\"{anchor}\">");

        if (isAnalyzersYaml && f.Content != null)
        {
            // Render nuget-analyzers.yaml as a clean analyzer list
            sb.AppendLine("                <div class=\"file-name\">Roslyn Analyzers</div>");
            var analyzers = ParseAnalyzersYaml(f.Content);
            if (analyzers.Count > 0)
            {
                sb.AppendLine("                <div class=\"analyzer-list\">");
                foreach (var (pkg, ver) in analyzers)
                {
                    var nugetUrl = $"https://www.nuget.org/packages/{Uri.EscapeDataString(pkg)}";
                    sb.AppendLine($"                    <div class=\"analyzer-item\">");
                    sb.AppendLine($"                        <a class=\"analyzer-name\" href=\"{nugetUrl}\" target=\"_blank\" rel=\"noopener\">{HtmlEncode(pkg)}</a>");
                    if (!string.IsNullOrWhiteSpace(ver))
                        sb.AppendLine($"                        <span class=\"analyzer-ver\">{HtmlEncode(ver)}</span>");
                    sb.AppendLine($"                    </div>");
                }
                sb.AppendLine("                </div>");
            }
        }
        else
        {
            sb.AppendLine($"                <div class=\"file-name\">{HtmlEncode(displayPath)}</div>");

            if (isCopScript && f.Content != null)
            {
                var commandSummaries = ExtractCommandSummaries(f.Content);
                if (commandSummaries.Count > 0)
                {
                    sb.AppendLine("                <div class=\"check-summaries\">");
                    foreach (var (name, doc) in commandSummaries)
                    {
                        sb.AppendLine($"                    <div class=\"check-summary\">");
                        sb.AppendLine($"                        <span class=\"check-name\">{HtmlEncode(name)}</span>");
                        sb.AppendLine($"                        <span class=\"check-doc\">{HtmlEncode(doc)}</span>");
                        sb.AppendLine($"                    </div>");
                    }
                    sb.AppendLine("                </div>");
                }
            }

            if (isMarkdown)
            {
                var content = StripFrontmatter(f.Content ?? "");
                var mdId = $"md-{anchor}";
                sb.AppendLine($"                <div class=\"prose\" id=\"{mdId}\"></div>");
                sb.AppendLine($"                <textarea class=\"md-source\" data-target=\"{mdId}\" style=\"display:none\">{HtmlEncode(content)}</textarea>");
            }
            else
            {
                var lang = GetLanguageClass(displayPath);
                sb.AppendLine($"                <pre class=\"code-block\"><code class=\"{lang}\">{HtmlEncode(f.Content ?? "(empty)")}</code></pre>");
            }
        }

        sb.AppendLine("            </div>");
        return sb.ToString();
    }

    /// <summary>
    /// Extracts (checkName, docComment) pairs from .cop check file content.
    /// Doc comments are ## lines immediately preceding a check block.
    /// </summary>
    private static List<(string Name, string Doc)> ExtractCommandSummaries(string content)
    {
        var results = new List<(string Name, string Doc)>();
        var lines = content.Split('\n');
        var docLines = new List<string>();

        for (int i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith("##"))
            {
                var text = trimmed.Length > 2 ? trimmed[2..].TrimStart() : "";
                docLines.Add(text);
            }
            else if (trimmed.StartsWith("check ") && docLines.Count > 0)
            {
                // Extract check name: "check Name(Collection) {"
                var afterCmd = trimmed[6..].TrimStart();
                var parenIdx = afterCmd.IndexOf('(');
                var name = parenIdx > 0 ? afterCmd[..parenIdx] : afterCmd.Split(' ')[0];
                results.Add((name, string.Join(" ", docLines)));
                docLines.Clear();
            }
            else if (!string.IsNullOrWhiteSpace(trimmed))
            {
                docLines.Clear();
            }
        }

        return results;
    }

    /// <summary>
    /// Parses nuget-analyzers.yaml content into (package, version) pairs.
    /// </summary>
    private static List<(string Package, string Version)> ParseAnalyzersYaml(string content)
    {
        var results = new List<(string, string)>();
        string? currentPackage = null;

        foreach (var line in content.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("- package:"))
            {
                currentPackage = trimmed["- package:".Length..].Trim();
            }
            else if (trimmed.StartsWith("package:"))
            {
                currentPackage = trimmed["package:".Length..].Trim();
            }
            else if (trimmed.StartsWith("version:") && currentPackage != null)
            {
                var ver = trimmed["version:".Length..].Trim();
                results.Add((currentPackage, ver));
                currentPackage = null;
            }
        }

        if (currentPackage != null)
            results.Add((currentPackage, ""));

        return results;
    }

    private static string BuildMetaBar(Cop.Core.PackageMetadata metadata, string feedLabel, string feed)
    {
        var sb = new StringBuilder();
        sb.AppendLine("            <div class=\"meta-bar\">");

        if (!string.IsNullOrWhiteSpace(metadata.Version))
            sb.AppendLine($"                <span class=\"meta-badge version\">v{HtmlEncode(metadata.Version)}</span>");

        if (!string.IsNullOrWhiteSpace(metadata.Authors))
            sb.AppendLine($"                <span class=\"meta-badge\">{HtmlEncode(metadata.Authors)}</span>");

        if (!string.IsNullOrWhiteSpace(metadata.Tags))
        {
            foreach (var tag in metadata.Tags.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                sb.AppendLine($"                <span class=\"tag\">{HtmlEncode(tag)}</span>");
        }

        if (metadata.Dependencies.Count > 0)
        {
            sb.AppendLine("                <span class=\"meta-sep\">│</span>");
            foreach (var dep in metadata.Dependencies)
                sb.AppendLine($"                <a class=\"dep-link\" href=\"/packages/{Uri.EscapeDataString(feed)}/{HtmlEncode(dep)}\">{HtmlEncode(dep)}</a>");
        }

        sb.AppendLine("            </div>");
        return sb.ToString();
    }

    private static Dictionary<string, List<PackageFile>> CategorizeFiles(List<PackageFile> files)
    {
        var knownDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "instructions", "skills", "Rules", "tests" };

        var categories = new Dictionary<string, List<PackageFile>>
        {
            ["overview"] = new(),
            ["instructions"] = new(),
            ["skills"] = new(),
            ["Rules"] = new(),
            ["tests"] = new(),
        };

        foreach (var f in files)
        {
            var normalized = f.RelativePath.Replace('\\', '/');
            var slashIndex = normalized.IndexOf('/');
            if (slashIndex > 0)
            {
                var topDir = normalized[..slashIndex];
                if (knownDirs.Contains(topDir))
                {
                    categories[topDir.ToLowerInvariant()].Add(f);
                    continue;
                }
            }
            categories["overview"].Add(f);
        }

        return categories;
    }

    private static List<PackageFile> GetDisplayableFiles(List<PackageFile> files)
        => files.Where(f => !f.IsBinary
            && !Path.GetFileName(f.RelativePath).Equals(".gitkeep", StringComparison.OrdinalIgnoreCase)
            && !Path.GetFileName(f.RelativePath).Equals("nuget-analyzers.yaml", StringComparison.OrdinalIgnoreCase)).ToList();

    private static string GetLanguageClass(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext switch
        {
            ".cs" => "language-csharp",
            ".cop" => "language-cop",
            ".yaml" or ".yml" => "language-yaml",
            ".json" => "language-json",
            ".xml" or ".csproj" or ".props" or ".targets" => "language-xml",
            ".js" => "language-javascript",
            ".ts" => "language-typescript",
            ".py" => "language-python",
            ".sh" or ".bash" => "language-bash",
            ".ps1" => "language-powershell",
            ".html" or ".htm" => "language-html",
            ".css" => "language-css",
            ".sql" => "language-sql",
            ".toml" => "language-toml",
            _ => "nohighlight",
        };
    }

    /// <summary>Strips YAML frontmatter (--- ... ---) from markdown content.</summary>
    private static string StripFrontmatter(string content)
    {
        if (!content.TrimStart().StartsWith("---")) return content;
        var trimmed = content.TrimStart();
        var endIndex = trimmed.IndexOf("---", 3);
        if (endIndex < 0) return content;
        return trimmed[(endIndex + 3)..].TrimStart('\r', '\n');
    }

    /// <summary>Strips the first # heading if present (already shown as page title).</summary>
    private static string StripLeadingHeading(string markdown)
    {
        using var reader = new StringReader(markdown);
        string? line;
        bool skippedHeading = false;
        var sb = new StringBuilder();
        while ((line = reader.ReadLine()) != null)
        {
            if (!skippedHeading)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (line.TrimStart().StartsWith("# ")) { skippedHeading = true; continue; }
                skippedHeading = true;
            }
            sb.AppendLine(line);
        }
        return sb.ToString().TrimStart('\r', '\n');
    }

    private static string HtmlEncode(string text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        return text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
                   .Replace("\"", "&quot;").Replace("'", "&#39;");
    }
}

/// <summary>
/// Represents a file within a package for rendering.
/// </summary>
public class PackageFile
{
    public string RelativePath { get; set; } = "";
    public string? Content { get; set; }
    public bool IsBinary { get; set; }
}
