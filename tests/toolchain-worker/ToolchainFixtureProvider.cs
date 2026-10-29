using System.Text.Json;
using Cop.Core;

namespace Cop.Tests;

public sealed class ToolchainFixtureProvider : DataProvider
{
    public override string QueryResultType => "FixtureData";

    public override ReadOnlyMemory<byte> GetSchema() => new ProviderSchema
    {
        Types = [new() { Name = "FixtureItem", Properties = [new() { Name = "Name" }] }],
        Collections = [new() { Name = "Items", ItemType = "FixtureItem" }]
    }.ToJson();

    public override RuntimeBindings GetRuntimeBindings() => new()
    {
        ClrTypeMappings = { [typeof(Item)] = "FixtureItem" },
        Accessors = { ["FixtureItem"] = new() { ["Name"] = value => ((Item)value).Name } }
    };

    public override object QueryDeferred(ProviderQuery query) =>
        new DeferredProviderResult(new Dictionary<string, IEnumerable<object>> { ["Items"] = Enumerate(query) });

    private static IEnumerable<object> Enumerate(ProviderQuery query)
    {
        var root = query.RootPath!;
        var mode = File.ReadAllText(Path.Combine(root, "mode.txt"));
        var dotnet = ToolchainProcess.FindExecutable("dotnet")!;
        var helper = File.ReadAllText(Path.Combine(root, "helper.txt"));
        for (int i = 0; i < 2; i++)
        {
            var result = ToolchainExecutionScope.RequestAsync(dotnet, [helper, "worker"], root, "{}").GetAwaiter().GetResult();
            if (result.Status != ToolchainStatus.Success) throw new InvalidOperationException(result.Diagnostic);
            using var response = JsonDocument.Parse(result.StandardOutput);
            File.AppendAllText(Path.Combine(root, "pids.txt"), response.RootElement.GetProperty("pid") + "\n");
            if (mode == "throw" && i == 1) throw new InvalidOperationException("Fixture pipeline failed after starting its worker.");
            ProviderWarnings.ReportOnce("fixture", root, "Optional tool unavailable; using simpler fixture analysis.");
            if (mode == "syntax")
                ProviderErrors.Report("sample.fixture(3): invalid syntax");
            yield return new Item(i == 0 ? "first" : "second");
        }
    }

    public sealed record Item(string Name);
}
