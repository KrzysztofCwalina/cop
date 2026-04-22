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

    private static List<PrintOutput> RunPackageChecks(string package, string sourceFile)
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
        var documents = TestInterpreter.ParseSourceFiles(sourceFile);
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
        // Policy-free: package flags all InternalsVisibleTo; project filters by test keywords
        Assert.That(ivtDiags.Count, Is.EqualTo(4),
            $"Expected 4 IVT diagnostics (all assemblies) but got {ivtDiags.Count}:\n{string.Join("\n", ivtDiags)}");
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
}
