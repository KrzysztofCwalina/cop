using System.Net.Http;
using System.Text;
using System.Text.Json;
using Cop.Core;
using Cop.Lang;

namespace Cop.Providers.Ai;

/// <summary>
/// LLM-based code-review provider. Exposes ai.judge(prompt, code?) which sends the requirement
/// and code to an OpenAI-compatible chat-completions endpoint and returns a list of Violations.
/// Configuration (endpoint, model, apiKey) is read from ~/.cop/ai.json.
/// </summary>
public class AiProvider : DataProvider
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(120) };

    public override ReadOnlyMemory<byte> GetSchema()
        => new ProviderSchema { Types = [], Collections = [] }.ToJson();

    public override object? Query(ProviderQuery query) => null;

    public override Dictionary<string, Func<List<object?>, Task<object?>>>? GetFunctions()
        => new(StringComparer.OrdinalIgnoreCase)
        {
            ["judge"] = JudgeAsync,
        };

    private static async Task<object?> JudgeAsync(List<object?> args)
    {
        var prompt = args.Count > 0 ? args[0]?.ToString() ?? "" : "";
        var code = args.Count > 1 ? SerializeCode(args[1]) : "";
        if (string.IsNullOrWhiteSpace(prompt))
            return One("ai.judge requires a requirement/prompt argument");

        var config = LoadConfig(out var configError);
        if (config is null)
            return One($"ai.judge: {configError}");

        const string system =
            "You are a strict code reviewer. Evaluate the CODE against the REQUIREMENT. " +
            "Respond with ONLY a JSON array of violations; each element is " +
            "{\"file\":string,\"line\":number,\"message\":string}. " +
            "Return [] if the code fully satisfies the requirement. No prose, no markdown fences.";
        var user = $"REQUIREMENT:\n{prompt}\n\nCODE UNDER REVIEW:\n{code}";

        var requestBody = JsonSerializer.Serialize(new
        {
            model = config.Model,
            temperature = 0.0,
            messages = new[]
            {
                new { role = "system", content = system },
                new { role = "user", content = user },
            },
        });

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, config.Endpoint);
            if (!string.IsNullOrEmpty(config.ApiKey))
                req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {config.ApiKey}");
            req.Content = new StringContent(requestBody, Encoding.UTF8, "application/json");

            using var resp = await _http.SendAsync(req);
            var respText = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                LogInteraction(config, system, user, $"{(int)resp.StatusCode} FAILED", respText);
                return One($"ai.judge: LLM call failed ({(int)resp.StatusCode}): {Truncate(respText, 300)}");
            }

            using var doc = JsonDocument.Parse(respText);
            var content = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? "[]";

            LogInteraction(config, system, user, $"{(int)resp.StatusCode} OK", content);
            return ParseViolations(StripFences(content));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return One($"ai.judge error: {ex.Message}");
        }
    }

    // When --ai-log names a file path, append a transcript of the agent↔judge
    // interaction (the request the runtime sent and the raw response). Opt-in
    // transparency for LLM-based rules; no effect unless --ai-log is passed.
    private static void LogInteraction(AiConfig config, string system, string user, string status, string assistant)
    {
        var path = Cop.Core.CopDiagnostics.AiLogPath;
        if (string.IsNullOrEmpty(path)) return;
        var sb = new StringBuilder();
        sb.AppendLine("================== ai.judge interaction ==================");
        sb.AppendLine($"model:    {config.Model}");
        sb.AppendLine($"endpoint: {config.Endpoint}");
        sb.AppendLine($"status:   {status}");
        sb.AppendLine();
        sb.AppendLine("------------------ REQUEST  (agent → judge) ------------------");
        sb.AppendLine("[system]");
        sb.AppendLine(system);
        sb.AppendLine();
        sb.AppendLine("[user]");
        sb.AppendLine(user);
        sb.AppendLine();
        sb.AppendLine("------------------ RESPONSE (judge → agent) -----------------");
        sb.AppendLine(assistant);
        sb.AppendLine("=========================================================");
        sb.AppendLine();
        try { File.AppendAllText(path, sb.ToString()); } catch { /* logging is best-effort */ }
    }

    // Renders the second ai.judge argument into a readable code context for the LLM.
    // Accepts a literal string, or a collection (e.g. Types/Lines/Files) marshaled to a
    // list of DataObjects — each is summarized as one line (source text, or name + path).
    private static string SerializeCode(object? arg)
    {
        switch (arg)
        {
            case null:
                return "";
            case string s:
                return s;
            case System.Collections.IEnumerable seq:
                var sb = new StringBuilder();
                foreach (var el in seq)
                {
                    var line = SerializeElement(el);
                    if (!string.IsNullOrWhiteSpace(line)) sb.AppendLine(line);
                }
                return sb.ToString().TrimEnd();
            default:
                return arg.ToString() ?? "";
        }
    }

    // Summarizes one collection element as a single line. Elements may be DataObjects
    // (some providers) or arbitrary provider source-model objects such as csharp's
    // TypeDeclaration / LineInfo (read via reflection). Only Name/Text/Path/Line are
    // read — never a full-source field — to keep the LLM payload small.
    private static string SerializeElement(object? el)
    {
        if (el is null) return "";
        if (el is string s) return s;

        var text = Member(el, "Text") as string;
        var name = Member(el, "Name") as string;
        var path = PathOf(el);

        if (text is not null)
        {
            var num = Member(el, "Number") ?? Member(el, "Line");
            return path is not null ? $"{path}:{num}: {text}" : text;
        }
        if (name is not null)
            return path is not null ? $"- {name}   [{path}]" : $"- {name}";
        if (path is not null) return path;
        return el is DataObject ? "" : el.GetType().Name;
    }

    private static object? Member(object obj, string name)
    {
        try
        {
            if (obj is DataObject d) return d.GetField(name);
            return obj.GetType().GetProperty(name)?.GetValue(obj);
        }
        catch
        {
            return null;
        }
    }

    private static string? PathOf(object obj)
    {
        if (Member(obj, "Path") is string p) return p;
        if (Member(obj, "File") is object f && Member(f, "Path") is string fp) return fp;
        return null;
    }

    private static List<object?> ParseViolations(string content)
    {
        var result = new List<object?>();
        try
        {
            using var doc = JsonDocument.Parse(content);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return One($"ai.judge: model did not return a JSON array: {Truncate(content, 200)}");
            foreach (var v in doc.RootElement.EnumerateArray())
            {
                var file = v.TryGetProperty("file", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() ?? "" : "";
                var line = v.TryGetProperty("line", out var l) && l.ValueKind == JsonValueKind.Number ? l.GetInt32() : 0;
                var message = v.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() ?? "" : v.ToString();
                result.Add(MakeViolation(message, 0.8, file, line, 0.7));
            }
        }
        catch (JsonException)
        {
            return One($"ai.judge: could not parse model output as JSON violations: {Truncate(content, 200)}");
        }
        return result;
    }

    private static List<object?> One(string message) => new() { MakeViolation(message, 1.0, "", 0, 1.0) };

    private static DataObject MakeViolation(string message, double certainty, string file, int line, double severity)
    {
        var v = new DataObject("Violation");
        v.Set("Severity", severity);
        v.Set("Certainty", certainty);
        v.Set("Message", message);
        v.Set("File", file);
        v.Set("Line", line);
        v.Set("Source", "ai.judge");
        return v;
    }

    private static AiConfig? LoadConfig(out string error)
    {
        error = "";
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cop", "ai.json");
        if (!File.Exists(path))
        {
            error = $"config not found at {path} (needs endpoint, model, apiKey)";
            return null;
        }
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            return new AiConfig
            {
                Endpoint = GetStr(root, "endpoint"),
                Model = GetStr(root, "model"),
                ApiKey = GetStr(root, "apiKey"),
            };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            error = $"invalid {path}: {ex.Message}";
            return null;
        }
    }

    private static string GetStr(JsonElement root, string name)
        => root.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : "";

    private static string StripFences(string s)
    {
        s = s.Trim();
        if (s.StartsWith("```"))
        {
            int nl = s.IndexOf('\n');
            if (nl >= 0) s = s[(nl + 1)..];
            if (s.EndsWith("```")) s = s[..^3];
        }
        return s.Trim();
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private sealed class AiConfig
    {
        public string Endpoint { get; init; } = "";
        public string Model { get; init; } = "";
        public string ApiKey { get; init; } = "";
    }
}
