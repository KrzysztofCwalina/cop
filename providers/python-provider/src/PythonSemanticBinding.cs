using System.Text;
using System.Text.Json;
using Cop.Core;
using Cop.Providers.SourceModel;

namespace Cop.Providers.SourceParsers;

internal static class PythonSemanticBinding
{
    public static void Attach(SourceFile file, PythonToolchain toolchain)
    {
        var path = Path.GetFullPath(file.Path);
        var lines = file.RawText.Split('\n');
        foreach (var statement in file.Statements.Where(s => s.Kind == "call"))
        {
            statement.DeferSemanticAnalysis(new(() =>
            {
                var line = lines[statement.Line - 1];
                var byteColumn = Encoding.UTF8.GetByteCount(line.AsSpan(0, statement.Column!.Value - 1));
                var endByteColumn = Encoding.UTF8.GetByteCount(lines[statement.EndLine!.Value - 1].AsSpan(0, statement.EndColumn!.Value - 1));
                using var result = toolchain.Analyze(path, file.RawText, "binding", statement.Line,
                    byteColumn, statement.EndLine, endByteColumn)
                    ?? throw new InvalidOperationException("The Python interpreter became unavailable during semantic analysis.");
                var root = result.RootElement;
                statement.ReceiverType = Type(Get(root, "receiver"));
                statement.ValueType = Type(Get(root, "value"));
                var selected = Get(root, "callable");
                if (selected.ValueKind == JsonValueKind.Object)
                    statement.ReferencedCallable = new(String(selected, "name")!, String(selected, "signature")!)
                    {
                        DeclaringType = String(selected, "declaringType"), Namespace = String(selected, "namespace"),
                        ReturnType = Type(Get(selected, "returnType")),
                        Parameters = Get(selected, "parameters").EnumerateArray().Select(p => Type(p)!).ToList(),
                        SourceFile = String(selected, "sourceFile"),
                        Line = Integer(selected, "line"), Column = Integer(selected, "column"),
                        EndLine = Integer(selected, "endLine"), EndColumn = Integer(selected, "endColumn"),
                    };
                var diagnostics = Get(root, "diagnostics").EnumerateArray().Select(d => d.GetString()).ToList();
                if (diagnostics.Count > 0)
                    ProviderWarnings.ReportOnce("python", $"binding:{file.Path}:{statement.Line}:{statement.Column}",
                        $"Partial source binding at {file.Path}({statement.Line},{statement.Column}): {string.Join("; ", diagnostics)}. Unknown semantic facts remain unavailable.");
                return true;
            }));
        }
    }

    private static JsonElement Get(JsonElement node, string key) =>
        node.TryGetProperty(key, out var value) ? value : default;
    private static string? String(JsonElement node, string key) =>
        Get(node, key).ValueKind == JsonValueKind.String ? Get(node, key).GetString() : null;
    private static int? Integer(JsonElement node, string key) =>
        Get(node, key).ValueKind == JsonValueKind.Number ? Get(node, key).GetInt32() : null;
    private static TypeReference? Type(JsonElement node) => node.ValueKind != JsonValueKind.Object ? null :
        new(String(node, "name")!, String(node, "namespace"),
            Get(node, "args").EnumerateArray().Select(p => Type(p)!).ToList(), String(node, "text")!);
}
