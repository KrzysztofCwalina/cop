using Cop.Lang;
using Cop.Providers;
using NUnit.Framework;

namespace Cop.Tests.Lang;

[TestFixture]
public class AzureSdkRuleTests
{
    private static string SamplePath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "Samples", fileName);

    private static readonly string PackagesRoot =
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "packages");

    private static string ApmSrcDir(string package)
    {
        var packageDir = ImportResolver.FindPackageDir(PackagesRoot, package)
            ?? Path.Combine(PackagesRoot, package);
        return Path.Combine(packageDir, "src");
    }

    private static List<PrintOutput> RunPackageChecks(string package, string sourceFile) =>
        RunPackageChecks(package, [sourceFile]);

    private static List<PrintOutput> RunPackageChecks(string package, string[] sourceFiles)
    {
        var srcDir = ApmSrcDir(package);
        var apmRoot = PackagesRoot;
        var copFiles = Directory.GetFiles(srcDir, "*.cop").OrderBy(f => f).ToList();
        var packageFiles = new List<ScriptFile>();
        foreach (var file in copFiles)
        {
            var source = File.ReadAllText(file);
            packageFiles.Add(ScriptParser.Parse(source, file));
        }

        // Collect exported let collections from this package's own files only
        var ownExportedLets = packageFiles
            .SelectMany(sf => sf.LetDeclarations)
            .Where(l => l.IsExported && !l.IsValueBinding && !l.IsRuntime)
            .Select(l => l.Name)
            .ToList();

        // Resolve imports (e.g., csharp-library-client-azure imports csharp-library-client)
        var importResolver = new ImportResolver(apmRoot);
        var resolved = new HashSet<string>();
        var imported = new List<ScriptFile>();
        foreach (var cf in packageFiles)
            foreach (var imp in cf.Imports)
            {
                if (!resolved.Add(imp)) continue;
                var errors = new List<string>();
                var pkg = importResolver.Resolve(imp, errors);
                if (pkg != null) imported.Add(pkg);
            }

        // Order: imports first, then package files (local overrides imported)
        var scriptFiles = new List<ScriptFile>();
        scriptFiles.AddRange(imported);
        scriptFiles.AddRange(packageFiles);

        // Generate RUN CHECK(name) for each of this package's own exported let collections
        if (ownExportedLets.Count > 0)
        {
            var runStatements = string.Join("\n", ownExportedLets.Select(name => $"RUN CHECK({name})"));
            var wrapperSource = $"import code-analysis\n{runStatements}";
            scriptFiles.Add(ScriptParser.Parse(wrapperSource, "test-runner.cop"));
        }

        var interpreter = TestInterpreter.Create();
        var documents = TestInterpreter.ParseSourceFiles(sourceFiles);
        return interpreter.Run(scriptFiles, documents).Outputs;
    }

    // ── Azure checks: Good client should produce no diagnostics for service types ──

    [Test]
    public void AzureChecks_GoodClient_NoClientDiagnostics()
    {
        var diags = RunPackageChecks("csharp-library-client-azure",
            SamplePath("AzureGoodClient.cs"));

        var clientDiags = diags.Where(d =>
            d.Message.Contains("GoodServiceClient") ||
            d.Message.Contains("GoodServiceClientOptions")).ToList();

        Assert.That(clientDiags, Is.Empty,
            $"Expected no outputs for GoodService* but got:\n{string.Join("\n", clientDiags)}");
    }

    // ── Azure checks: Bad client should trigger expected rules ──

    [Test]
    public void AzureChecks_BadClient_MissingTokenCredential()
    {
        var diags = RunPackageChecks("csharp-library-client-azure",
            SamplePath("AzureBadClient.cs"));

        Assert.That(diags.Any(d => d.Message.Contains("must accept TokenCredential")
            && d.Message.Contains("BadServiceClient")),
            Is.True, "Should flag BadServiceClient missing TokenCredential");
    }

    [Test]
    public void AzureChecks_BadClient_MissingMockingCtor()
    {
        var diags = RunPackageChecks("csharp-library-client-azure",
            SamplePath("AzureBadClient.cs"));

        Assert.That(diags.Any(d => d.Message.Contains("protected parameterless constructor")
            && d.Message.Contains("BadServiceClient")),
            Is.True, "Should flag BadServiceClient missing protected ctor");
    }

    [Test]
    public void AzureChecks_BadClient_MissingCtorWithOptions()
    {
        var diags = RunPackageChecks("csharp-library-client-azure",
            SamplePath("AzureBadClient.cs"));

        Assert.That(diags.Any(d => d.Message.Contains("should have a constructor that accepts options")
            && d.Message.Contains("BadServiceClient")),
            Is.True, "Should flag BadServiceClient missing options ctor");
    }

    [Test]
    public void AzureChecks_BadClient_ServiceVersionNaming()
    {
        var diags = RunPackageChecks("csharp-library-client-azure",
            SamplePath("AzureBadClient.cs"));

        Assert.That(diags.Any(d => d.Message.Contains("ServiceVersion")),
            Is.True, "Should flag bad ServiceVersion member names");
    }

    [Test]
    public void AzureChecks_BadClient_ServiceVersionFirstParam()
    {
        var diags = RunPackageChecks("csharp-library-client-azure",
            SamplePath("AzureBadClient.cs"));

        Assert.That(diags.Any(d => d.Message.Contains("BadServiceClientOptions")
            && d.Message.Contains("ServiceVersion")),
            Is.True, "Should flag BadServiceClientOptions ctor first param not ServiceVersion");
    }

    [Test]
    public void AzureChecks_BadClient_ConvenienceWithRequestContent()
    {
        var diags = RunPackageChecks("csharp-library-client-azure",
            SamplePath("AzureBadClient.cs"));

        Assert.That(diags.Any(d => d.Message.Contains("RequestContent")
            && d.Message.Contains("BadServiceClient")),
            Is.True, "Should flag convenience method with RequestContent");
    }

    [Test]
    public void AzureChecks_BadClient_BannedReturnType()
    {
        var diags = RunPackageChecks("csharp-library-client-azure",
            SamplePath("AzureBadClient.cs"));

        Assert.That(diags.Any(d => d.Message.Contains("BadServiceClient")
            && d.Message.Contains("raw HTTP")),
            Is.True, "Should flag HttpResponseMessage return type");
    }

    [Test]
    public void AzureChecks_BadClient_ModelNameSuffixes()
    {
        var diags = RunPackageChecks("csharp-library-client-azure",
            SamplePath("AzureBadClient.cs"));

        // Check for various suffix violations in messages
        var suffixRules = diags.Where(d =>
            d.Message.Contains("Collection") ||
            d.Message.Contains("Request") ||
            d.Message.Contains("Parameter") ||
            d.Message.Contains("Resource")).ToList();

        Assert.That(suffixRules.Count, Is.GreaterThanOrEqualTo(4),
            $"Expected at least 4 suffix violations but got {suffixRules.Count}:\n{string.Join("\n", suffixRules)}");
    }

    [Test]
    public void AzureChecks_BadClient_ProtocolMethodReturnType()
    {
        var diags = RunPackageChecks("csharp-library-client-azure",
            SamplePath("AzureBadClient.cs"));

        Assert.That(diags.Any(d => d.Message.Contains("BadServiceClient")
            && d.Message.Contains("protocol method")),
            Is.True, "Should flag protocol method returning Task<MyModel>");
    }

    // ── AZC0012: Single-word type names ──

    [Test]
    public void AzureChecks_BadClient_SingleWordTypeName()
    {
        var diags = RunPackageChecks("csharp-library-client-azure",
            SamplePath("AzureBadClient.cs"));

        Assert.That(diags.Any(d => d.Message.Contains("Processor")
            && d.Message.Contains("single-word")),
            Is.True, "Should flag Processor as single-word type name");
    }

    // ── AZC0020: CancellationToken propagation ──

    [Test]
    public void AzureChecks_BadClient_MissingTokenPropagation()
    {
        var diags = RunPackageChecks("csharp-library-client-azure",
            SamplePath("AzureBadClient.cs"));

        Assert.That(diags.Any(d => d.Message.Contains("propagate")
            && d.Message.Contains("CancellationToken")),
            Is.True, "Should flag async call not propagating CancellationToken");
    }

    // ── Client checks: Async/sync pairing and virtual methods ──

    [Test]
    public void ClientChecks_BadClient_NonVirtualMethods()
    {
        var diags = RunPackageChecks("csharp-library-client",
            SamplePath("AzureBadClient.cs"));

        Assert.That(diags.Any(d => d.Message.Contains("virtual")
            && d.Message.Contains("BadServiceClient")),
            Is.True, "Should flag non-virtual methods on BadServiceClient");
    }

    [Test]
    public void ClientChecks_BadClient_AsyncWithoutSync()
    {
        var diags = RunPackageChecks("csharp-library-client",
            SamplePath("AzureBadClient.cs"));

        Assert.That(diags.Any(d => d.Message.Contains("sync")),
            Is.True, "Should flag async methods without sync counterpart");
    }

    [Test]
    public void ClientChecks_GoodClient_NoDiagnostics()
    {
        var diags = RunPackageChecks("csharp-library-client",
            SamplePath("AzureGoodClient.cs"));

        var clientDiags = diags.Where(d =>
            d.Message.Contains("GoodServiceClient")).ToList();

        Assert.That(clientDiags, Is.Empty,
            $"Expected no outputs for GoodServiceClient but got:\n{string.Join("\n", clientDiags)}");
    }

    // ── Library checks: async bool parameter ──

    [Test]
    public void LibraryChecks_BadCode_AsyncBoolParam()
    {
        var diags = RunPackageChecks("csharp-library",
            SamplePath("LibraryBad.cs"));

        Assert.That(diags.Any(d => d.Message.Contains("BadLibraryService")
            && d.Message.Contains("bool") && d.Message.Contains("async")),
            Is.True, "Should flag public method with bool 'async' parameter");
    }

    [Test]
    public void LibraryChecks_BadCode_NonPublicAsyncMissingParam()
    {
        var diags = RunPackageChecks("csharp-library",
            SamplePath("LibraryBad.cs"));

        Assert.That(diags.Any(d => d.Message.Contains("BadLibraryService")
            && d.Message.Contains("async")),
            Is.True, "Should flag non-public async method without 'async' param");
    }

    [Test]
    public void LibraryChecks_BadCode_AwaitWithoutConfigureAwaitFalse()
    {
        var diags = RunPackageChecks("csharp-library",
            SamplePath("LibraryBad.cs"));

        Assert.That(diags.Any(d => d.Message.Contains("ConfigureAwait(false)")),
            Is.True, "Should flag await without ConfigureAwait(false)");
    }

    [Test]
    public void LibraryChecks_GoodCode_ConfigureAwaitFalse_NoDiagnostic()
    {
        var diags = RunPackageChecks("csharp-library",
            SamplePath("LibraryBad.cs"));

        // GoodLibraryService uses ConfigureAwait(false) — should not be flagged
        var goodDiags = diags.Where(d =>
            d.Message.Contains("ConfigureAwait") &&
            d.Message.Contains("GoodLibraryService")).ToList();

        Assert.That(goodDiags, Is.Empty,
            $"GoodLibraryService should not trigger ConfigureAwait rule but got:\n{string.Join("\n", goodDiags)}");
    }

    // ── Code checks: ConfigureAwait, GetAwaiter, TaskCompletionSource ──

    [Test]
    public void CodeChecks_BadCode_ConfigureAwaitTrue()
    {
        var diags = RunPackageChecks("csharp",
            SamplePath("LibraryBad.cs"));

        Assert.That(diags.Any(d => d.Message.Contains("ConfigureAwait")),
            Is.True, "Should flag ConfigureAwait(true)");
    }

    [Test]
    public void CodeChecks_BadCode_GetAwaiterGetResult()
    {
        var diags = RunPackageChecks("csharp",
            SamplePath("LibraryBad.cs"));

        Assert.That(diags.Any(d => d.Message.Contains("GetAwaiter")),
            Is.True, "Should flag GetAwaiter().GetResult()");
    }

    [Test]
    public void CodeChecks_BadCode_TaskCompletionSource()
    {
        var diags = RunPackageChecks("csharp",
            SamplePath("LibraryBad.cs"));

        Assert.That(diags.Any(d => d.Message.Contains("TaskCompletionSource")),
            Is.True, "Should flag TaskCompletionSource without RunContinuationsAsynchronously");
    }

    // ── AZC0011: InternalsVisibleTo ──

    [Test]
    public void AzureChecks_AssemblyAttributes_FlagsAllInternalsVisibleTo()
    {
        var diags = RunPackageChecks("csharp-library-client-azure",
            SamplePath("AssemblyAttributes.cs"));

        var ivtDiags = diags.Where(d => d.Message.Contains("InternalsVisibleTo")).ToList();
        // Two checks fire: internals-visible-to flags all 4 attributes;
        // internals-visible-to-non-test flags the 3 non-test targets
        Assert.That(ivtDiags.Count, Is.EqualTo(7),
            $"Expected 7 IVT diagnostics (4 all + 3 non-test) but got {ivtDiags.Count}:\n{string.Join("\n", ivtDiags)}");
    }

    // ── AZC0103: Sync-blocking in async methods ──

    [Test]
    public void CSharpChecks_AsyncSyncBad_SyncWaitInAsync()
    {
        var diags = RunPackageChecks("csharp",
            SamplePath("AsyncSyncBad.cs"));

        var syncInAsync = diags.Where(d =>
            d.Message.Contains("async method") &&
            (d.Message.Contains("Wait") || d.Message.Contains("GetResult"))).ToList();

        Assert.That(syncInAsync.Count, Is.EqualTo(2),
            $"Expected 2 sync-in-async violations but got {syncInAsync.Count}:\n{string.Join("\n", syncInAsync)}");
    }

    // ── AZC0110: Unconditional await in dual-mode method ──

    [Test]
    public void LibraryChecks_AsyncSyncBad_UnconditionalAwait()
    {
        var diags = RunPackageChecks("csharp-library",
            SamplePath("AsyncSyncBad.cs"));

        var unconditional = diags.Where(d =>
            d.Message.Contains("guarded") && d.Message.Contains("Await")).ToList();

        Assert.That(unconditional.Count, Is.EqualTo(1),
            $"Expected 1 unconditional await violation but got {unconditional.Count}:\n{string.Join("\n", unconditional)}");
    }

    // ── AZC0111: Unconditional EnsureCompleted in dual-mode method ──

    [Test]
    public void LibraryChecks_AsyncSyncBad_UnconditionalEnsureCompleted()
    {
        var diags = RunPackageChecks("csharp-library",
            SamplePath("AsyncSyncBad.cs"));

        var unconditional = diags.Where(d =>
            d.Message.Contains("EnsureCompleted") && d.Message.Contains("guarded")).ToList();

        Assert.That(unconditional.Count, Is.EqualTo(1),
            $"Expected 1 unconditional EnsureCompleted violation but got {unconditional.Count}:\n{string.Join("\n", unconditional)}");
    }

    // ── AZC0110/0111: Properly guarded dual-mode should NOT be flagged ──

    [Test]
    public void LibraryChecks_AsyncSyncBad_GuardedDualModeNoDiagnostic()
    {
        var diags = RunPackageChecks("csharp-library",
            SamplePath("AsyncSyncBad.cs"));

        // DualModeGood has guards — should not trigger unconditional-await or unconditional-sync
        var guardedDiags = diags.Where(d =>
            d.Message.Contains("guarded") && d.Message.Contains("DualModeGood")).ToList();

        Assert.That(guardedDiags, Is.Empty,
            $"DualModeGood should not trigger guard violations but got:\n{string.Join("\n", guardedDiags)}");
    }

    // ── AZC0104: Use EnsureCompleted instead of GetResult in sync methods ──

    [Test]
    public void AzureChecks_AsyncSyncBad_UseEnsureCompleted()
    {
        var diags = RunPackageChecks("csharp-library-client-azure",
            SamplePath("AsyncSyncBad.cs"));

        Assert.That(diags.Any(d => d.Message.Contains("EnsureCompleted")
            && d.Message.Contains("instead")),
            Is.True, "Should flag GetResult() in sync method");
    }

    // ── AZC0107: Don't call *Async from sync context ──

    [Test]
    public void AzureChecks_AsyncSyncBad_NoAsyncInSync()
    {
        var diags = RunPackageChecks("csharp-library-client-azure",
            SamplePath("AsyncSyncBad.cs"));

        Assert.That(diags.Any(d => d.Message.Contains("DoWorkAsync")
            && d.Message.Contains("sync")),
            Is.True, "Should flag DoWorkAsync() called from sync method");
    }

    // ── AZC0108: Wrong async argument value in guarded branches ──

    [Test]
    public void LibraryChecks_AsyncSyncBad_WrongAsyncArgValue()
    {
        var diags = RunPackageChecks("csharp-library",
            SamplePath("AsyncSyncBad.cs"));

        var wrongArgs = diags.Where(d =>
            d.Message.Contains("async:") && d.Message.Contains("guard")).ToList();

        Assert.That(wrongArgs.Count, Is.EqualTo(2),
            $"Expected 2 wrong async arg violations (one false, one true) but got {wrongArgs.Count}:\n{string.Join("\n", wrongArgs)}");
    }

    [Test]
    public void LibraryChecks_AsyncSyncBad_CorrectAsyncArgNoDiagnostic()
    {
        var diags = RunPackageChecks("csharp-library",
            SamplePath("AsyncSyncBad.cs"));

        // DualModeGoodAsyncArg passes correct values — should not trigger
        var goodDiags = diags.Where(d =>
            d.Message.Contains("async:") && d.Message.Contains("guard")
            && d.Message.Contains("DualModeGoodAsyncArg")).ToList();

        Assert.That(goodDiags, Is.Empty,
            $"DualModeGoodAsyncArg should not trigger but got:\n{string.Join("\n", goodDiags)}");
    }

    // ── AZC0109: Misuse of async parameter ──

    [Test]
    public void LibraryChecks_AsyncSyncBad_AsyncParamMisuse()
    {
        var diags = RunPackageChecks("csharp-library",
            SamplePath("AsyncSyncBad.cs"));

        Assert.That(diags.Any(d => d.Message.Contains("async parameter")
            && d.Message.Contains("argument")),
            Is.True, "Should flag async parameter passed as argument");
    }

    // ── Phase 1 & 2: Banned API + CA Rules ──

    [Test]
    public void CodeChecks_BadCode_UriToString()
    {
        var diags = RunPackageChecks("csharp",
            SamplePath("LibraryBad.cs"));

        Assert.That(diags.Any(d => d.Message.Contains("Uri.AbsoluteUri")
            && d.Message.Contains("ToString")),
            Is.True, "Should flag Uri.ToString() and suggest AbsoluteUri");
    }

    // ── Python package checks ──

    [Test]
    public void PythonChecks_BadClient_PrintCalls()
    {
        var diags = RunPackageChecks("python",
            SamplePath("bad_client.py"));

        Assert.That(diags.Any(d => d.Message.Contains("print()")),
            Is.True, "Should flag print() calls");
    }

    [Test]
    public void PythonChecks_BadClient_BareExcept()
    {
        var diags = RunPackageChecks("python",
            SamplePath("bad_client.py"));

        Assert.That(diags.Any(d => d.Message.Contains("bare except")),
            Is.True, "Should flag bare except clause");
    }

    [Test]
    public void PythonChecks_BadClient_SwallowedException()
    {
        var diags = RunPackageChecks("python",
            SamplePath("bad_client.py"));

        Assert.That(diags.Any(d => d.Message.Contains("silence")),
            Is.True, "Should flag silenced Exception");
    }

    [Test]
    public void PythonChecks_GoodClient_NoDiagnostics()
    {
        var diags = RunPackageChecks("python",
            SamplePath("good_client.py"));

        Assert.That(diags, Is.Empty,
            $"Expected no diagnostics for good_client.py but got:\n{string.Join("\n", diags)}");
    }

    // ── Python library package checks ──

    [Test]
    public void PythonLibraryChecks_BadClient_UntypedParams()
    {
        var diags = RunPackageChecks("python-library",
            SamplePath("bad_client.py"));

        Assert.That(diags.Any(d => d.Message.Contains("BadClient")
            && d.Message.Contains("parameter type hints")),
            Is.True, "Should flag public methods with missing type hints");
    }

    [Test]
    public void PythonLibraryChecks_BadClient_MissingReturnType()
    {
        var diags = RunPackageChecks("python-library",
            SamplePath("bad_client.py"));

        Assert.That(diags.Any(d => d.Message.Contains("BadClient")
            && d.Message.Contains("return type")),
            Is.True, "Should flag public methods without return type");
    }

    [Test]
    public void PythonLibraryChecks_GoodClient_NoDiagnostics()
    {
        var diags = RunPackageChecks("python-library",
            SamplePath("good_client.py"));

        var clientDiags = diags.Where(d => d.Message.Contains("GoodClient")).ToList();

        Assert.That(clientDiags, Is.Empty,
            $"Expected no outputs for GoodClient but got:\n{string.Join("\n", clientDiags)}");
    }

    // ── Inline cop helper: simulates per-repo .cop files that import packages ──

    private static InterpreterResult RunInlineCop(string inlineCop, string[] sourceFiles, string? commandName = null)
    {
        var apmRoot = PackagesRoot;
        var inlineScript = ScriptParser.Parse(inlineCop, "repo-policy.cop");

        // Resolve imports transitively
        var importResolver = new ImportResolver(apmRoot);
        var resolved = new HashSet<string>();
        var scriptFiles = new List<ScriptFile>();
        var queue = new Queue<string>(inlineScript.Imports);
        while (queue.Count > 0)
        {
            var imp = queue.Dequeue();
            if (!resolved.Add(imp)) continue;
            var errors = new List<string>();
            var pkg = importResolver.Resolve(imp, errors);
            if (pkg != null)
            {
                scriptFiles.Add(pkg);
                foreach (var transitive in pkg.Imports)
                    queue.Enqueue(transitive);
            }
        }
        scriptFiles.Add(inlineScript);

        var interpreter = TestInterpreter.Create();
        var documents = TestInterpreter.ParseSourceFiles(sourceFiles);
        return interpreter.Run(scriptFiles, documents, commandName: commandName);
    }

    private static List<PrintOutput> RunInlineCopChecks(string inlineCop, string[] sourceFiles)
    {
        // Collect exported let names for RUN CHECK(name) wrapper
        var inlineScript = ScriptParser.Parse(inlineCop, "temp.cop");
        var exportedLets = inlineScript.LetDeclarations
            .Where(l => l.IsExported && !l.IsValueBinding && !l.IsRuntime)
            .Select(l => l.Name)
            .ToList();

        if (exportedLets.Count > 0)
        {
            var runStatements = string.Join("\n", exportedLets.Select(name => $"RUN CHECK({name})"));
            inlineCop += $"\n{runStatements}";
        }

        return RunInlineCop(inlineCop, sourceFiles).Outputs;
    }

    // ── API Compat: Export generates canonical entries ──

    // Api-to-Api comparison: baseline is a C# stub file (like Azure SDK's api/*.cs)
    // Both sides parsed through the same C# parser, so signatures match naturally.
    private const string ApiCompatPolicy = @"
import csharp-api
import code-analysis

# Baseline: C# stub files in api/ directory (Azure SDK convention)
predicate baselineApi(Api) => publicApi && Api.File.Path:rx('[/\\\\]api[/\\\\]')
# Source: everything NOT in api/
predicate sourceApi(Api) => publicApi && !Api.File.Path:rx('[/\\\\]api[/\\\\]')

let baselineSignatures = Code.Api:csharp:baselineApi.Select(item.Signature)
let currentSignatures = Code.Api:csharp:sourceApi.Select(item.Signature)

predicate removedApi(Api) => baselineApi && !Api.Signature:in(currentSignatures)
predicate addedApi(Api) => sourceApi && !Api.Signature:in(baselineSignatures)

export let api-removed = Code.Api:removedApi:toError('API REMOVED (breaking): {item.Signature}')
export let api-added = Code.Api:addedApi:toInfo('API ADDED: {item.Signature}')
export let api-compat = [api-removed, api-added]
";

    [Test]
    public void ApiCompat_Export_GeneratesCanonicalEntries()
    {
        // Use a simple export policy to verify Api collection works
        var exportPolicy = @"
import csharp-api
import code-analysis
export command api-export = SAVE('api-baseline.txt', '{item.Signature}', Code.Api:csharp:publicApi)
";
        var result = RunInlineCop(exportPolicy,
            [SamplePath("GoodClient.cs")], commandName: "api-export");

        var fileOutput = result.FileOutputs.FirstOrDefault();
        Assert.That(fileOutput, Is.Not.Null, "Should produce file output from api-export");

        var lines = fileOutput!.Content.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.That(lines.Any(l => l.StartsWith("class GoodClient")),
            Is.True, $"Should have GoodClient class entry. Lines:\n{string.Join("\n", lines)}");
        Assert.That(lines.Any(l => l.StartsWith("class GoodClientOptions")),
            Is.True, "Should have GoodClientOptions entry");
        Assert.That(lines.Any(l => l.StartsWith("class TokenCredential")),
            Is.True, "Should have TokenCredential entry");
        Assert.That(lines.Any(l => l.StartsWith("method GoodClient.GetItemAsync")),
            Is.True, "Should have GetItemAsync method entry");
        Assert.That(lines.Any(l => l.StartsWith("method GoodClient.DeleteItemAsync")),
            Is.True, "Should have DeleteItemAsync method entry");
        Assert.That(lines.Any(l => l.StartsWith("ctor GoodClient(")),
            Is.True, "Should have GoodClient constructor entry");
    }

    // ── API Compat: Detect removed API (C# stub baseline) ──

    [Test]
    public void ApiCompat_DetectsRemovedApi()
    {
        // Baseline stub has an extra type and method that don't exist in source
        var baselineDir = Path.Combine(Path.GetTempPath(), $"api-compat-{Guid.NewGuid()}");
        var apiDir = Path.Combine(baselineDir, "api");
        Directory.CreateDirectory(apiDir);
        try
        {
            var stubPath = Path.Combine(apiDir, "Baseline.cs");
            File.WriteAllText(stubPath, @"
public sealed class GoodClient
{
    public GoodClient(GoodClientOptions options, TokenCredential credential) { }
    protected GoodClient() { }
    public Task<string> GetItemAsync(string id, CancellationToken cancellationToken) { throw null; }
    public Task DeleteItemAsync(string id, CancellationToken cancellationToken) { throw null; }
    public void OldMethod(string id) { throw null; }
}
public class GoodClientOptions : ClientOptions { }
public abstract class ClientOptions { }
public class TokenCredential { }
public class OldService { }
");

            var diags = RunInlineCopChecks(ApiCompatPolicy,
                [SamplePath("GoodClient.cs"), stubPath]);

            var removed = diags.Where(d => d.Message.Contains("REMOVED")).ToList();
            Assert.That(removed.Count, Is.EqualTo(2),
                $"Expected 2 removed API entries but got {removed.Count}:\n{string.Join("\n", removed)}");
            Assert.That(removed.Any(d => d.Message.Contains("OldMethod")),
                Is.True, "Should detect removed OldMethod");
            Assert.That(removed.Any(d => d.Message.Contains("OldService")),
                Is.True, "Should detect removed OldService");
        }
        finally
        {
            Directory.Delete(baselineDir, true);
        }
    }

    // ── API Compat: Detect added API (C# stub baseline) ──

    [Test]
    public void ApiCompat_DetectsAddedApi()
    {
        // Baseline stub has only GoodClient class — everything else is "added"
        var baselineDir = Path.Combine(Path.GetTempPath(), $"api-compat-{Guid.NewGuid()}");
        var apiDir = Path.Combine(baselineDir, "api");
        Directory.CreateDirectory(apiDir);
        try
        {
            var stubPath = Path.Combine(apiDir, "Baseline.cs");
            File.WriteAllText(stubPath, @"
public sealed class GoodClient { }
");

            var diags = RunInlineCopChecks(ApiCompatPolicy,
                [SamplePath("GoodClient.cs"), stubPath]);

            var added = diags.Where(d => d.Message.Contains("ADDED")).ToList();
            Assert.That(added.Count, Is.GreaterThan(3),
                $"Expected multiple added API entries but got {added.Count}:\n{string.Join("\n", added)}");
            Assert.That(added.Any(d => d.Message.Contains("GoodClientOptions")),
                Is.True, "Should detect GoodClientOptions as added");
            Assert.That(added.Any(d => d.Message.Contains("GetItemAsync")),
                Is.True, "Should detect GetItemAsync as added");
        }
        finally
        {
            Directory.Delete(baselineDir, true);
        }
    }

    // ── API Compat: No baseline = no removed ──

    [Test]
    public void ApiCompat_NoBaseline_NoRemovedDiagnostics()
    {
        // No api/ directory files — no baseline entries, so nothing "removed"
        var diags = RunInlineCopChecks(ApiCompatPolicy,
            [SamplePath("GoodClient.cs")]);

        var removed = diags.Where(d => d.Message.Contains("REMOVED")).ToList();
        Assert.That(removed, Is.Empty,
            $"No baseline file means no removed APIs, but got:\n{string.Join("\n", removed)}");
    }

    // ── API Compat: Matching baseline = no diagnostics ──

    [Test]
    public void ApiCompat_MatchingBaseline_NoDiagnostics()
    {
        // Baseline stub matches the source exactly
        var baselineDir = Path.Combine(Path.GetTempPath(), $"api-compat-{Guid.NewGuid()}");
        var apiDir = Path.Combine(baselineDir, "api");
        Directory.CreateDirectory(apiDir);
        try
        {
            var stubPath = Path.Combine(apiDir, "Baseline.cs");
            File.WriteAllText(stubPath, @"
public abstract class ClientOptions { }
public class GoodClientOptions : ClientOptions { }
public sealed class GoodClient
{
    public GoodClient(GoodClientOptions options, TokenCredential credential) { }
    protected GoodClient() { }
    public Task<string> GetItemAsync(string id, CancellationToken cancellationToken) { throw null; }
    public Task DeleteItemAsync(string id, CancellationToken cancellationToken) { throw null; }
}
public class TokenCredential { }
");

            var diags = RunInlineCopChecks(ApiCompatPolicy,
                [SamplePath("GoodClient.cs"), stubPath]);

            var removed = diags.Where(d => d.Message.Contains("REMOVED")).ToList();
            var added = diags.Where(d => d.Message.Contains("ADDED")).ToList();

            Assert.That(removed, Is.Empty,
                $"Matching baseline should produce no REMOVED, but got:\n{string.Join("\n", removed)}");
            Assert.That(added, Is.Empty,
                $"Matching baseline should produce no ADDED, but got:\n{string.Join("\n", added)}");
        }
        finally
        {
            Directory.Delete(baselineDir, true);
        }
    }
}
