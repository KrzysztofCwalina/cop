using System.Text;
using System.Text.Json;
using Cop.Core;
using Cop.Providers.SourceModel;

namespace Cop.Providers.SourceParsers;

internal static class GoSemanticAnalysis
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private static readonly Dictionary<string, string?> EnvironmentOverrides = new()
    {
        ["GOTOOLCHAIN"] = "local",
        ["GOFLAGS"] = "",
        ["GOPROXY"] = "off",
        ["GOSUMDB"] = "off",
        ["GONOPROXY"] = "none",
        ["GONOSUMDB"] = "none",
        ["GOENV"] = "off",
        ["GOVCS"] = "*:off",
        ["GOTELEMETRY"] = "off",
        ["CGO_ENABLED"] = "0",
        ["GOOS"] = null,
        ["GOARCH"] = null,
        ["GOEXPERIMENT"] = null,
        ["GOROOT"] = null,
        ["GO111MODULE"] = null,
        ["GOWORK"] = null
    };

    public static void Attach(SourceFile file, string go, string helper)
    {
        var analysis = new Lazy<bool>(() =>
        {
            string path = Path.GetFullPath(string.IsNullOrEmpty(file.FullPath) ? file.Path : file.FullPath);
            string directory = Path.GetDirectoryName(path)!;
            var environment = new Dictionary<string, string?>(EnvironmentOverrides);
            bool module = false;
            bool vendor = false;
            for (string? ancestor = directory; ancestor is not null; ancestor = Path.GetDirectoryName(ancestor))
            {
                if (File.Exists(Path.Combine(ancestor, "go.work")))
                {
                    module = true;
                    vendor = File.Exists(Path.Combine(ancestor, "vendor", "modules.txt"));
                    break;
                }
                if (!module && File.Exists(Path.Combine(ancestor, "go.mod")))
                {
                    module = true;
                    vendor = File.Exists(Path.Combine(ancestor, "vendor", "modules.txt"));
                }
            }
            if (!module) environment["GO111MODULE"] = "off";
            string[] arguments = module ? ["list", "-e", "-json", "-deps", vendor ? "-mod=vendor" : "-mod=readonly", "."]
                : ["list", "-e", "-json", "-deps", "."];
            if (path.EndsWith("_test.go", StringComparison.Ordinal))
                arguments = arguments[..^1].Concat(new[] { "-test", "." }).ToArray();
            var listing = ToolchainProcess.RunAsync(go, arguments, directory,
                timeout: TimeSpan.FromMinutes(2), maxOutputCharacters: 16_777_216, environment: environment).GetAwaiter().GetResult();
            if (listing.Status != ToolchainStatus.Success)
                throw new InvalidOperationException($"Go semantic package discovery {listing.Status}: {listing.Diagnostic ?? listing.StandardError}");
            var packages = ReadPackages(listing.StandardOutput);
            if (packages.Count == 0) throw new InvalidOperationException("Go semantic package discovery returned no package context.");
            string request = JsonSerializer.Serialize(new
            {
                Path = Convert.ToBase64String(Encoding.UTF8.GetBytes(path)),
                Source = Convert.ToBase64String(Encoding.UTF8.GetBytes(file.RawText)),
                Semantic = true,
                Packages = packages
            });
            var result = ToolchainExecutionScope.RequestAsync(helper, [], Path.GetDirectoryName(helper)!, request,
                timeout: TimeSpan.FromMinutes(2), maxOutputCharacters: 16_777_216).GetAwaiter().GetResult();
            if (result.Status != ToolchainStatus.Success)
                throw new InvalidOperationException($"Go semantic analysis {result.Status}: {result.Diagnostic ?? result.StandardError}");
            var response = JsonSerializer.Deserialize<SemanticResponse>(result.StandardOutput, Json)
                ?? throw new InvalidOperationException("Go semantic analysis returned null.");
            if (response.Facts is null || response.Errors is null)
                throw new InvalidOperationException("Go semantic analysis omitted bindings or diagnostics.");
            foreach (string error in response.Errors) ProviderErrors.Report("Go semantic context: " + error);
            foreach (var fact in response.Facts)
            {
                var call = file.Statements.FirstOrDefault(s => s.Kind == "call" && s.Line == fact.Line && s.Column == fact.Column
                    && s.EndLine == fact.EndLine && s.EndColumn == fact.EndColumn);
                if (call is null) throw new InvalidOperationException("Go semantic binding has no matching source call.");
                call.ReceiverType = fact.ReceiverType;
                call.ValueType = fact.ValueType;
                call.ReferencedCallable = fact.Callable;
            }
            return true;
        });
        foreach (var statement in file.Statements) statement.DeferSemanticAnalysis(analysis);
    }

    private static List<JsonElement> ReadPackages(string json)
    {
        var result = new List<JsonElement>();
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(json), new JsonReaderOptions { AllowMultipleValues = true });
        while (reader.Read())
        {
            using var document = JsonDocument.ParseValue(ref reader);
            result.Add(document.RootElement.Clone());
        }
        return result;
    }

    private sealed record SemanticResponse(List<SemanticFact>? Facts, List<string>? Errors);
    private sealed record SemanticFact(int Line, int Column, int EndLine, int EndColumn, TypeReference? ReceiverType,
        TypeReference? ValueType, CallableReference? Callable);
}
