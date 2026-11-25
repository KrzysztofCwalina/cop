using Cop.Providers;
using NUnit.Framework;

namespace Cop.Tests;

[TestFixture]
public partial class OperationalSafetyChecksTests
{
    private string _root = "";
    private string _repo = "";

    [SetUp]
    public void SetUp()
    {
        DirectoryInfo? directory = new(TestContext.CurrentContext.TestDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "cop.sln")))
            directory = directory.Parent;
        _repo = directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
        _root = Path.Combine(_repo, ".test-output", "operational-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "target"));
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_root, recursive: true);

    private static IEnumerable<TestCaseData> RuleCases()
    {
        yield return Case("certificateValidationDisabled", "python",
            "import requests\nrequests.get('https://service.example', verify=False)",
            "import requests\nrequests.get('https://service.example', verify=True)");
        yield return Case("missingRequestTimeout", "python",
            "import requests\nrequests.get('https://service.example')",
            "import requests\nrequests.get('https://service.example', timeout=10)");
        yield return Case("unsafeArchiveExtraction", "python",
            "import tarfile\ndef run(tar):\n    tar.extractall(path='output')",
            "import tarfile\ndef run(tar):\n    tar.extractall(path='output', filter='data')");
        yield return Case("insecureTemporaryNames", "python",
            "import tempfile\ntempfile.mktemp()",
            "import tempfile\ntempfile.mkstemp()");
        yield return Case("wildcardCorsOrigins", "csharp",
            "class Bad { void Run(CorsOptions cors) { cors.AllowAnyOrigin(); } }",
            "class Good { void Run(CorsOptions cors) { cors.WithOrigins(\"https://service.example\"); } }");
        yield return Case("developmentServerDebug", "python",
            "def run(app):\n    app.run(debug=True)",
            "def run(app):\n    app.run(debug=False)");
        yield return Case("jwtSignatureBypass", "python",
            "import jwt\njwt.decode(token, verify=False)",
            "import jwt\njwt.decode(token, key, verify=True)");
        yield return Case("fastPasswordHashes", "python",
            "import hashlib\ndef run(password):\n    hashlib.sha256(password)",
            "import hashlib\ndef run(payload):\n    hashlib.sha256(payload)");
        yield return Case("insecureCookieFlags", "python",
            "def run(response):\n    response.set_cookie('session', token, secure=False)",
            "def run(response):\n    response.set_cookie('session', token, secure=True)");
        yield return Case("worldWritableModes", "python",
            "import os\nos.chmod('output', 0o777)",
            "import os\nos.chmod('output', 0o600)");
        yield return Case("xmlEntityExpansion", "python",
            "from lxml import etree\netree.XMLParser(resolve_entities=True)",
            "from lxml import etree\netree.XMLParser(resolve_entities=False)");
        yield return Case("dynamicCodeLoading", "python",
            "def run(source):\n    exec(source)",
            "def run(source):\n    parse(source)");
        yield return Case("literalShellCommands", "python",
            "import subprocess\nsubprocess.run('echo hello', shell=True)",
            "import subprocess\nsubprocess.run(['echo', 'hello'], shell=False)");
        yield return Case("unboundedRegex", "csharp",
            "using System.Text.RegularExpressions; class Bad { void Run(string input) { Regex.IsMatch(input, \"a+\"); } }",
            "using System.Text.RegularExpressions; using System; class Good { void Run(string input) { Regex.IsMatch(input, \"a+\", RegexOptions.None, TimeSpan.FromSeconds(1)); } }");
        yield return Case("abruptFinally", "javascript",
            "function run() { try { work(); } finally { return 1; } }",
            "function run() { try { work(); } finally { cleanup(); } }");
        yield return Case("redundantCatchRethrow", "csharp",
            "class Bad { void Run() { try { Work(); } catch (System.IO.IOException) { throw; } } }",
            "class Good { void Run() { try { Work(); } catch (System.IO.IOException error) { Log(error); throw; } } }");
        yield return Case("constantBranchConditions", "csharp",
            "class Bad { void Run(bool flag) { if (true) { Work(); } } }",
            "class Good { void Run(bool flag) { if (flag) { Work(); } } }");
        yield return Case("selfComparisonConditions", "csharp",
            "class Bad { void Run(int value, int other) { if (value == value) { Work(); } } }",
            "class Good { void Run(int value, int other) { if (value == other) { Work(); } } }");
        yield return Case("selfAssignments", "csharp",
            "class Bad { void Run(int value, int other) { value = value; } }",
            "class Good { void Run(int value, int other) { value = other; } }");
        yield return Case("duplicateSourceImports", "python",
            "import json\nimport json",
            "from json import dumps\nfrom json import loads");
        yield return Case("excessiveControlNesting", "csharp",
            "class Bad { void Run(bool flag) { if(flag) { if(flag) { if(flag) { if(flag) { if(flag) { Work(); } } } } } } }",
            "class Good { void Run(bool flag) { if(flag) { if(flag) { if(flag) { if(flag) { Work(); } } } } } }");
        yield return Case("sameValueAssertions", "csharp",
            "class Bad { void Run(int actual, int expected) { Assert.AreEqual(actual, actual); } }",
            "class Good { void Run(int actual, int expected) { Assert.AreEqual(expected, actual); } }");
        yield return Case("productionAssertValidation", "python",
            "def run(value):\n    assert value",
            "def run(value):\n    if not value:\n        raise ValueError('missing')");
        yield return Case("asyncVoidMethods", "csharp",
            "using System.Threading.Tasks; class Bad { async void Run() { await Task.Delay(1); } }",
            "using System; using System.Threading.Tasks; class Good { async void Run(object sender, EventArgs args) { await Task.Delay(1); } }");
        yield return Case("unsafeThreadStopping", "csharp",
            "using System.Threading; class Bad { void Run() { Thread.Abort(); } }",
            "using System.Threading; class Good { void Run(CancellationTokenSource source) { source.Cancel(); } }");
        yield return Case("forcedGarbageCollection", "python",
            "import gc\ngc.collect()",
            "import gc\ngc.isenabled()");
        yield return Case("unboundedProcessWait", "csharp",
            "class Bad { void Run(System.Diagnostics.Process process) { process.WaitForExit(); } }",
            "class Good { void Run(System.Diagnostics.Process process) { process.WaitForExit(1000); } }");
        yield return Case("publicListenerBinding", "python",
            "def run(app):\n    app.run(host='0.0.0.0')",
            "def run(app):\n    app.run(host='127.0.0.1')");
        yield return Case("errorMessageControlFlow", "csharp",
            "class Bad { void Run(System.Exception error) { if(error.Message == \"missing\") { Work(); } } }",
            "class Good { void Run(System.Exception error) { if(error is System.IO.FileNotFoundException) { Work(); } } }");
        yield return Case("legacyTlsProtocols", "python",
            "import ssl\nconfigure(ssl.PROTOCOL_TLSv1)",
            "import ssl\nconfigure(ssl.PROTOCOL_TLS_CLIENT)");
        yield return Case("forcedGarbageCollection", "csharp",
            "class Input { void Run() { System.GC.Collect(); } }",
            "class Input { void Run() { Work(); } }");
        yield return Case("forcedGarbageCollection", "java",
            "class Input { void run() { System.gc(); } }",
            "class Input { void run() { work(); } }");
        yield return Case("forcedGarbageCollection", "go",
            "package input\nimport \"runtime\"\nfunc run() { runtime.GC() }",
            "package input\nfunc run() { work() }");
        yield return Case("wildcardCorsOrigins", "javascript",
            "function run(response) { response.setHeader('Access-Control-Allow-Origin', '*'); }",
            "function run(response) { response.setHeader('Access-Control-Allow-Origin', 'https://service.example'); }");
        yield return Case("publicListenerBinding", "go",
            "package input\nimport \"net/http\"\nfunc run() { http.ListenAndServe(\":8080\", nil) }",
            "package input\nimport \"net/http\"\nfunc run() { http.ListenAndServe(\"127.0.0.1:8080\", nil) }");
        yield return Case("dynamicCodeLoading", "javascript",
            "import vm from 'node:vm'; vm.runInNewContext(source);",
            "import vm from 'node:vm'; parse(source);");
        yield return Case("legacyTlsProtocols", "csharp",
            "class Input { void Run() { Configure(System.Security.Authentication.SslProtocols.Tls); } }",
            "class Input { void Run() { Configure(System.Security.Authentication.SslProtocols.Tls12); } }");
        yield return Case("legacyTlsProtocols", "go",
            "package input\nimport \"crypto/tls\"\nfunc run() { configure(tls.VersionTLS10) }",
            "package input\nimport \"crypto/tls\"\nfunc run() { configure(tls.VersionTLS12) }");
    }

    private static TestCaseData Case(string rule, string language, string bad, string good) =>
        new TestCaseData(rule, language, bad, good).SetName(rule + "_" + language);

    [TestCaseSource(nameof(RuleCases))]
    public async Task RulesReportOnlyThePositiveFixture(string rule, string language, string bad, string good)
    {
        string extension = language switch
        {
            "csharp" => ".cs", "javascript" => ".js", "java" => ".java", "go" => ".go",
            "rust" => ".rs", _ => ".py"
        };
        Write("Bad" + extension, bad);
        Write("Good" + extension, good);
        string arguments = rule switch
        {
            "duplicateSourceImports" => "cb.Files",
            "asyncVoidMethods" => "cb.Methods",
            _ => "cb.Statements"
        };
        await AssertOutput(language,
            $"{rule}({arguments}).Select(item.Check + ' ' + item.File)",
            [rule + " Bad" + extension]);
    }

    [TestCase("csharp", ".cs", "class Input { void Run(bool flag) { if(true) { Work(); } if(flag) { Work(); } } }")]
    [TestCase("python", ".py", "def run(flag):\n    if True:\n        work()\n    if flag:\n        work()")]
    [TestCase("javascript", ".js", "function run(flag) { if(true) { work(); } if(flag) { work(); } }")]
    [TestCase("java", ".java", "class Input { void run(boolean flag) { if(true) { work(); } if(flag) { work(); } } }")]
    [TestCase("go", ".go", "package input\nfunc run(flag bool) { if true { work() }; if flag { work() } }")]
    [TestCase("rust", ".rs", "fn run(flag: bool) { if true { work(); } if flag { work(); } }")]
    public async Task ConstantBranchesRunAcrossAllSixParsers(string language, string extension, string source)
    {
        Write("Input" + extension, source);
        await AssertOutput(language, "constantBranchConditions(cb.Statements).Select(item.Check)",
            ["constantBranchConditions"]);
    }

    [TestCase("csharp", ".cs", "class Input { void Run(int a, int b) { if(a == a) { Work(); } if(a == b) { Work(); } } }")]
    [TestCase("python", ".py", "def run(a, b):\n    if a == a:\n        work()\n    if a == b:\n        work()")]
    [TestCase("javascript", ".js", "function run(a, b) { if(a === a) { work(); } if(a === b) { work(); } }")]
    [TestCase("java", ".java", "class Input { void run(int a, int b) { if(a == a) { work(); } if(a == b) { work(); } } }")]
    [TestCase("go", ".go", "package input\nfunc run(a, b int) { if a == a { work() }; if a == b { work() } }")]
    [TestCase("rust", ".rs", "fn run(a: i32, b: i32) { if a == a { work(); } if a == b { work(); } }")]
    public async Task SelfComparisonConditionsRunAcrossAllSixParsers(string language, string extension, string source)
    {
        Write("Input" + extension, source);
        await AssertOutput(language, "selfComparisonConditions(cb.Statements).Select(item.Check)",
            ["selfComparisonConditions"]);
    }

    [TestCase("csharp", ".cs", "class Input { void Run(int a, int b) { Assert.AreEqual(a, a); Assert.AreEqual(a, b); } }")]
    [TestCase("python", ".py", "def run(self, a, b):\n    self.assertEqual(a, a)\n    self.assertEqual(a, b)")]
    [TestCase("javascript", ".js", "import assert from 'node:assert'; function run(a, b) { assert.strictEqual(a, a); assert.strictEqual(a, b); }")]
    [TestCase("java", ".java", "class Input { void run(int a, int b) { Assertions.assertEquals(a, a); Assertions.assertEquals(a, b); } }")]
    [TestCase("go", ".go", "package input\nfunc run(t *testing.T, a, b int) { require.Equal(t, a, a); require.Equal(t, a, b) }")]
    [TestCase("rust", ".rs", "fn run(a: i32, b: i32) { assert_eq!(a, a); assert_eq!(a, b); }")]
    public async Task SameValueAssertionsRunAcrossAllSixParsers(string language, string extension, string source)
    {
        Write("Input" + extension, source);
        await AssertOutput(language, "sameValueAssertions(cb.Statements).Select(item.Check)", ["sameValueAssertions"]);
    }

    [TestCase("csharp", ".cs", "class Input { void Run(int a, int b) { a = a; a = b; } }")]
    [TestCase("python", ".py", "def run(a, b):\n    a = a\n    a = b")]
    [TestCase("javascript", ".js", "function run(a, b) { a = a; a = b; }")]
    [TestCase("java", ".java", "class Input { void run(int a, int b) { a = a; a = b; } }")]
    [TestCase("go", ".go", "package input\nfunc run(a, b int) { a = a; a = b }")]
    [TestCase("rust", ".rs", "fn run(mut a: i32, b: i32) { a = a; a = b; }")]
    public async Task SelfAssignmentsRunAcrossAllSixParsers(string language, string extension, string source)
    {
        Write("Input" + extension, source);
        await AssertOutput(language, "selfAssignments(cb.Statements).Select(item.Check)", ["selfAssignments"]);
    }

    [TestCase("csharp", ".cs", "class Input { void Run(bool enabled) { if(enabled) { if(enabled) { if(enabled) { if(enabled) { if(enabled) { if(enabled) { Work(); } } } } } } } }")]
    [TestCase("python", ".py", "def run(enabled):\n    if enabled:\n        if enabled:\n            if enabled:\n                if enabled:\n                    if enabled:\n                        if enabled:\n                            work()")]
    [TestCase("javascript", ".js", "function run(enabled) { if(enabled) { if(enabled) { if(enabled) { if(enabled) { if(enabled) { if(enabled) { work(); } } } } } } }")]
    [TestCase("java", ".java", "class Input { void run(boolean enabled) { if(enabled) { if(enabled) { if(enabled) { if(enabled) { if(enabled) { if(enabled) { work(); } } } } } } } }")]
    [TestCase("go", ".go", "package input\nfunc run(enabled bool) { if enabled { if enabled { if enabled { if enabled { if enabled { if enabled { work() } } } } } } }")]
    [TestCase("rust", ".rs", "fn run(enabled: bool) { if enabled { if enabled { if enabled { if enabled { if enabled { if enabled { work(); } } } } } } }")]
    public async Task DeepControlNestingReportsOnceAcrossAllSixParsers(string language, string extension, string source)
    {
        Write("Input" + extension, source);
        await AssertOutput(language, "excessiveControlNesting(cb.Statements).Select(item.Check)", ["excessiveControlNesting"]);
    }

    [TestCase("csharp", ".cs", "using System;\nusing System;\nusing System.IO;\nclass Input {}")]
    [TestCase("python", ".py", "import json\nimport json\nfrom json import dumps")]
    [TestCase("javascript", ".js", "import 'node:fs';\nimport 'node:fs';\nimport 'node:path';")]
    [TestCase("java", ".java", "import java.util.List;\nimport java.util.List;\nimport java.util.Map;\nclass Input {}")]
    [TestCase("go", ".go", "package input\nimport (\n\"fmt\"\n\"fmt\"\n\"os\"\n)")]
    [TestCase("rust", ".rs", "use std::fmt;\nuse std::fmt;\nuse std::io;")]
    public async Task DuplicateImportsRunAcrossAllSixParsers(string language, string extension, string source)
    {
        Write("Input" + extension, source);
        await AssertOutput(language, "duplicateSourceImports(cb.Files).Select(item.Check)", ["duplicateSourceImports"]);
    }

    [Test]
    public async Task HttpTimeoutRuleRespectsClientDefaultsAndDoesNotBorrowUnrelatedArguments()
    {
        Write("Input.py", """
            import requests
            import httpx
            requests.get('https://service.example', headers={'timeout': '10'})
            requests.get('https://service.example', timeout=10)
            httpx.get('https://service.example')
            httpx.get('https://service.example', timeout=None)
            """);
        await AssertOutput("python", "missingRequestTimeout(cb.Statements).Select(item.Line)", ["3", "6"]);
    }

    [Test]
    public async Task DynamicShellCommandsRemainOwnedByTheExistingInjectionRule()
    {
        Write("Input.py", """
            import subprocess
            subprocess.run('echo ' + user_input, shell=True)
            subprocess.run('echo hello', shell=True)
            """);
        await AssertOutput("python", "literalShellCommands(cb.Statements).Select(item.Line)", ["3"]);
    }

    [Test]
    public async Task StandardSuiteScopesProductionRulesButKeepsTestAssertionChecks()
    {
        Write("Input.cs", "class Input { void Run() { if(true) { Work(); } } }");
        Write("tests\\Fixture.cs", """
            class Fixture {
                void Run(int actual) {
                    if(true) { Work(); }
                    Assert.AreEqual(actual, actual);
                }
            }
            """);
        await AssertOutput("csharp",
            "codeQualityChecks(cb).Where(item.Check == 'constantBranchConditions' || item.Check == 'sameValueAssertions').Select(item.Check)",
            ["constantBranchConditions", "sameValueAssertions"]);
    }

    [Test]
    public async Task StatementSuppressionsPreventNewFindings()
    {
        Write("Input.cs", """
            class Input {
                void Run() {
                    // cop-ignore: constantBranchConditions
                    if(true) { Work(); }
                }
            }
            """);
        await AssertOutput("csharp", "constantBranchConditions(cb.Statements).Select(item.Check)", []);
    }

    private void Write(string filename, string source)
    {
        string path = Path.Combine(_root, "target", filename);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, source + "\n");
    }

    private async Task AssertOutput(string language, string expression, string[] expected)
    {
        File.WriteAllText(Path.Combine(_root, "main.cop"), $$"""
            import code
            import ai-slop-checks
            import {{language}}
            let cb = {{language}}.parse()
            command MAIN = {{expression}}
            """);
        var result = await Engine.RunAsync(_root, Path.Combine(_root, "target"), new EngineRunOptions
        {
            AdditionalFeedPaths = [Path.Combine(_repo, "packages")]
        });
        Assert.That(result.HasParseErrors, Is.False, string.Join("\n", result.ParseErrors));
        Assert.That(result.HasFatalErrors, Is.False, string.Join("\n", result.Errors));
        Assert.That(result.Outputs.Select(output => output.Message).ToArray(), Is.EqualTo(expected));
    }
}
