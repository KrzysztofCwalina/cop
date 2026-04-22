using System;
using System.Collections.Generic;
using System.Linq;

namespace Cop.Driver.Pages
{
    public record PackageSummary(string Name, string Feed, string? Title, string? Description, string? Version, string? Authors, string? Tags, string? Language,
        bool HasInstructions = false, bool HasSkills = false, bool hasRules = false, bool HasTests = false);

    /// <summary>
    /// Renders the package listing page — search bar + package list.
    /// </summary>
    public static class PackageDirectory
    {
        public static string Render(List<PackageSummary> packages, bool isLocal = false)
        {
            // Collect distinct languages for filter pills
            var languages = packages
                .Select(p => p.Language?.Trim())
                .Where(l => !string.IsNullOrEmpty(l))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(l => l, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var filterPills = string.Empty;
            if (languages.Count > 0)
            {
                filterPills = "<button class=\"filter-pill active\" data-lang=\"\">All</button>";
                foreach (var lang in languages)
                    filterPills += $"<button class=\"filter-pill\" data-lang=\"{HtmlEncode(lang!.ToLowerInvariant())}\">{HtmlEncode(lang)}</button>";
            }

            var rows = string.Empty;

            foreach (var pkg in packages)
            {
                var detailUrl = $"/packages/{Uri.EscapeDataString(pkg.Feed)}/{Uri.EscapeDataString(pkg.Name)}";
                var title = HtmlEncode(pkg.Title ?? pkg.Name);
                var desc = string.IsNullOrWhiteSpace(pkg.Description) ? "" : $"<p class=\"pkg-desc\">{HtmlEncode(Truncate(pkg.Description, 180))}</p>";
                var version = string.IsNullOrWhiteSpace(pkg.Version) ? "" : $"<span class=\"pkg-version\">{HtmlEncode(pkg.Version)}</span>";
                var authors = string.IsNullOrWhiteSpace(pkg.Authors) ? "" : $"<span class=\"pkg-authors\">by {HtmlEncode(pkg.Authors)}</span>";
                var langLabel = string.IsNullOrWhiteSpace(pkg.Language) ? "" : $"<span class=\"pkg-lang\">{HtmlEncode(pkg.Language)}</span>";
                var langData = HtmlEncode((pkg.Language ?? "").Trim().ToLowerInvariant());
                var tags = string.Empty;
                if (!string.IsNullOrWhiteSpace(pkg.Tags))
                {
                    foreach (var tag in pkg.Tags.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Take(5))
                        tags += $"<span class=\"pkg-tag\">{HtmlEncode(tag)}</span>";
                }

                var contentGlyphs = string.Empty;
                if (pkg.HasInstructions) contentGlyphs += "<span class=\"pkg-glyph\" title=\"Instructions\">📖</span>";
                if (pkg.HasSkills) contentGlyphs += "<span class=\"pkg-glyph\" title=\"Skills\">⚡</span>";
                if (pkg.hasRules) contentGlyphs += "<span class=\"pkg-glyph\" title=\"Checks\">✅</span>";
                if (pkg.HasTests) contentGlyphs += "<span class=\"pkg-glyph\" title=\"Tests\">🧪</span>";

                rows += $@"
                <a class=""pkg-row"" href=""{detailUrl}"" data-lang=""{langData}"" data-search=""{HtmlEncode((pkg.Name + " " + (pkg.Title ?? "") + " " + (pkg.Tags ?? "")).ToLowerInvariant())}"">
                    <div class=""pkg-row-main"">
                        <span class=""pkg-name"">{title}</span>
                        {version}
                        {langLabel}
                        {(contentGlyphs.Length > 0 ? $"<span class=\"pkg-glyphs\">{contentGlyphs}</span>" : "")}
                    </div>
                    {desc}
                    <div class=""pkg-row-meta"">{authors}{tags}</div>
                </a>";
            }

            var html = $@"<!DOCTYPE html>
<html lang=""en"">
<head>
    <meta charset=""UTF-8"">
    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
    <title>copweb — Packages</title>
    <style>
        {Dashboard.SharedStyles()}

        .search-bar {{
            margin: 20px 0 12px;
        }}
        .search-bar input {{
            width: 100%; padding: 10px 14px;
            background: #161b22; border: 1px solid #30363d; border-radius: 6px;
            color: #f0f6fc; font-size: 14px; outline: none;
            transition: border-color 0.15s;
            box-sizing: border-box;
        }}
        .search-bar input:focus {{ border-color: #58a6ff; }}
        .search-bar input::placeholder {{ color: #484f58; }}

        .filter-bar {{
            display: flex; align-items: center; gap: 6px; flex-wrap: wrap;
            margin-bottom: 16px;
        }}
        .filter-pill {{
            padding: 4px 12px; font-size: 13px;
            background: transparent; border: 1px solid #30363d; border-radius: 16px;
            color: #8b949e; cursor: pointer; transition: all 0.15s;
            font-family: inherit;
        }}
        .filter-pill:hover {{ border-color: #58a6ff; color: #c9d1d9; }}
        .filter-pill.active {{ background: #58a6ff; border-color: #58a6ff; color: #0d1117; font-weight: 600; }}

        .result-count {{ color: #8b949e; font-size: 13px; margin-bottom: 16px; }}

        .pkg-list {{ display: flex; flex-direction: column; gap: 1px; }}
        .pkg-row {{
            display: block; padding: 16px 20px;
            background: #0d1117;
            border-bottom: 1px solid #21262d;
            text-decoration: none;
            transition: background 0.1s;
        }}
        .pkg-row:first-child {{ border-top: 1px solid #21262d; }}
        .pkg-row:hover {{ background: #161b22; }}
        .pkg-row.hidden {{ display: none; }}

        .pkg-row-main {{ display: flex; align-items: baseline; gap: 10px; margin-bottom: 4px; }}
        .pkg-name {{ font-size: 16px; color: #58a6ff; font-weight: 600; }}
        .pkg-version {{ font-size: 13px; color: #8b949e; }}
        .pkg-lang {{
            font-size: 11px; color: #c9d1d9; background: #21262d;
            padding: 2px 8px; border-radius: 4px; font-weight: 500;
        }}
        .pkg-glyphs {{ display: inline-flex; gap: 2px; margin-left: 4px; }}
        .pkg-glyph {{ font-size: 13px; opacity: 0.7; cursor: default; }}
        .pkg-desc {{ font-size: 14px; color: #c9d1d9; margin: 2px 0 6px; line-height: 1.4; }}
        .pkg-row-meta {{ display: flex; align-items: center; gap: 8px; flex-wrap: wrap; }}
        .pkg-authors {{ font-size: 12px; color: #8b949e; }}
        .pkg-tag {{
            font-size: 11px; color: #58a6ff; background: #161b22;
            padding: 1px 8px; border-radius: 10px; border: 1px solid #21262d;
        }}

        .muted {{ color: #8b949e; }}
        .no-results {{ display: none; text-align: center; color: #8b949e; padding: 2rem; font-size: 14px; }}
    </style>
</head>
<body>
    <div class=""container"">
        {Dashboard.NavBar("packages", isLocal)}

        <div class=""search-bar"">
            <input type=""text"" id=""pkg-search"" placeholder=""Search packages..."" autocomplete=""off"">
        </div>
        {(languages.Count > 0 ? $@"<div class=""filter-bar"">{filterPills}</div>" : "")}
        <p class=""result-count"" id=""result-count"">{packages.Count} {(packages.Count == 1 ? "package" : "packages")} available</p>

        {(packages.Count == 0 ? @"
        <p class=""muted"" style=""text-align:center"">No packages found. Use <code>cop feed add</code> to configure a feed.</p>"
        : $@"<div class=""pkg-list"">{rows}</div>")}

        <p class=""no-results"" id=""no-results"">No packages match your search.</p>
    </div>
    <script>
        var activeLang = '';
        function applyFilters() {{
            var q = document.getElementById('pkg-search').value.toLowerCase();
            var count = 0;
            document.querySelectorAll('.pkg-row').forEach(function(r) {{
                var matchSearch = !q || r.dataset.search.includes(q);
                var matchLang = !activeLang || r.dataset.lang === activeLang;
                var visible = matchSearch && matchLang;
                r.classList.toggle('hidden', !visible);
                if (visible) count++;
            }});
            document.getElementById('no-results').style.display = count ? 'none' : 'block';
            document.getElementById('result-count').textContent = count + (count === 1 ? ' package' : ' packages') + (q || activeLang ? ' found' : ' available');
        }}
        document.getElementById('pkg-search').addEventListener('input', applyFilters);
        document.querySelectorAll('.filter-pill').forEach(function(btn) {{
            btn.addEventListener('click', function() {{
                document.querySelectorAll('.filter-pill').forEach(function(b) {{ b.classList.remove('active'); }});
                btn.classList.add('active');
                activeLang = btn.dataset.lang;
                applyFilters();
            }});
        }});
    </script>
</body>
</html>";

            return html;
        }

        private static string Truncate(string text, int maxLength)
            => text.Length <= maxLength ? text : text[..maxLength] + "…";

        private static string HtmlEncode(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            return text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
                       .Replace("\"", "&quot;").Replace("'", "&#39;");
        }
    }
}
