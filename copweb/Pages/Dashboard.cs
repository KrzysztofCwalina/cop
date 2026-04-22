namespace Cop.Driver.Pages;

using Cop.Driver.Models;

public static class Dashboard
{
    public static string Render(List<DriverTask> tasks)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("<!DOCTYPE html><html><head>");
        sb.AppendLine("<meta charset=\"UTF-8\">");
        sb.AppendLine("<title>copweb — Agents</title>");
        sb.AppendLine("<style>");
        sb.AppendLine(SharedStyles());
        sb.AppendLine("table { border-collapse: collapse; width: 100%; }");
        sb.AppendLine("th, td { border: 1px solid #30363d; padding: 8px 12px; text-align: left; }");
        sb.AppendLine("th { background: #161b22; }");
        sb.AppendLine("tr:hover { background: #161b22; }");
        sb.AppendLine(".phase-completed { color: #3fb950; } .phase-failed { color: #f85149; }");
        sb.AppendLine(".phase-executing { color: #d29922; } .phase-pending { color: #8b949e; }");
        sb.AppendLine(".empty-state { text-align: center; color: #8b949e; padding: 3rem; font-size: 16px; }");
        sb.AppendLine("</style></head><body>");
        sb.AppendLine("<div class=\"container\">");
        sb.AppendLine(NavBar("agents", isLocal: true));
        sb.AppendLine("<h1>🤖 Agents</h1>");
        sb.AppendLine($"<p>{tasks.Count} total tasks, {tasks.Count(t => !t.IsTerminal)} active</p>");

        if (tasks.Count == 0)
        {
            sb.AppendLine("<div class=\"empty-state\">No agent tasks running. Use <code>cop run &lt;spec.md&gt;</code> to submit work.</div>");
        }
        else
        {
            sb.AppendLine("<table><tr><th>ID</th><th>Spec</th><th>Phase</th><th>Branch</th><th>Elapsed</th><th>Checks</th></tr>");
            foreach (var t in tasks)
            {
                var phaseClass = t.Phase switch {
                    TaskPhase.Completed => "phase-completed",
                    TaskPhase.Failed => "phase-failed",
                    TaskPhase.Executing or TaskPhase.Verifying => "phase-executing",
                    _ => "phase-pending"
                };
                sb.AppendLine($"<tr><td>{t.Id}</td><td>{HtmlEncode(t.SpecPath)}</td>");
                sb.AppendLine($"<td class=\"{phaseClass}\">{t.Phase}</td><td>{t.Branch ?? "-"}</td>");
                sb.AppendLine($"<td>{t.Elapsed:hh\\:mm\\:ss}</td><td>{t.VerifyAttempts}/{t.MaxVerifyAttempts}</td></tr>");
            }
            sb.AppendLine("</table>");
        }

        sb.AppendLine("</div></body></html>");
        return sb.ToString();
    }

    internal static string SharedStyles() => """
        body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; margin: 0; background: #0d1117; color: #c9d1d9; }
        .container { max-width: 1200px; margin: 0 auto; padding: 24px; }
        h1 { color: #f0f6fc; font-size: 28px; margin-bottom: 8px; }
        p { color: #8b949e; margin-bottom: 16px; }
        code { background: #161b22; padding: 2px 6px; border-radius: 4px; font-size: 13px; }
        nav { display: flex; align-items: center; margin-bottom: 24px; border-bottom: 1px solid #30363d; }
        nav a { padding: 12px 20px; color: #8b949e; text-decoration: none; font-weight: 500; font-size: 15px; border-bottom: 2px solid transparent; transition: all 0.15s; }
        nav a:hover { color: #c9d1d9; }
        nav .nav-brand { color: #f0f6fc; font-weight: 700; border-bottom: none; }
        nav .nav-brand:hover { color: #f0f6fc; background: transparent; }
        nav .spacer { flex: 1; }
        nav .nav-local { padding: 8px 16px; font-size: 18px; color: #484f58; border-bottom: none; }
        nav .nav-local:hover { color: #8b949e; background: #161b22; }
        nav .nav-local.active { color: #8b949e; border-bottom: none; }
        """;

    internal static string NavBar(string activePage, bool isLocal = false)
    {
        var localLink = isLocal
            ? $"<div class=\"spacer\"></div><a href=\"/agents\" class=\"nav-local{(activePage == "agents" ? " active" : "")}\" title=\"Agent workitems\">⚙️</a>"
            : "";
        return $"""
            <nav>
                <a href="/" class="nav-brand">📦 cop</a>
                {localLink}
            </nav>
            """;
    }

    private static string HtmlEncode(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;
        
        return text
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&#39;");
    }
}
