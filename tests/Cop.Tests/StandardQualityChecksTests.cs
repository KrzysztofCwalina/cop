using Cop.Providers;
using NUnit.Framework;

namespace Cop.Tests;

[TestFixture]
public class StandardQualityChecksTests
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
        _root = Path.Combine(_repo, ".test-output", "quality-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "target"));
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_root, recursive: true);

    [TestCase("ai-slop-checks", "ai-slop-checks.aiSlopChecks", "ai-slop-checks.constantAssertions")]
    [TestCase("code-quality-checks", "code-quality-checks.codeQualityChecks", "code-quality-checks.constantAssertions")]
    [TestCase("ai-slop-checks\nimport code-quality-checks", "ai-slop-checks.aiSlopChecks", "code-quality-checks.constantAssertions")]
    public async Task CanonicalAndCompatibilityNamespacesPreserveFindings(
        string imports, string suite, string rule)
    {
        Write("Input.cs", "class Input { void Run(bool actual) { Assert.True(true); Assert.True(actual); } }");
        File.WriteAllText(Path.Combine(_root, "main.cop"), $$"""
            import code
            import csharp
            import {{imports}}
            let cb = csharp.parse()
            command MAIN = ({{suite}}(cb).Where(item.Check == 'constantAssertions') +
                {{rule}}(cb.Statements)).Select(item.Check)
            """);
        var result = await Engine.RunAsync(_root, Path.Combine(_root, "target"), new EngineRunOptions
        {
            AdditionalFeedPaths = [Path.Combine(_repo, "packages")]
        });
        Assert.That(result.HasParseErrors, Is.False, string.Join("\n", result.ParseErrors));
        Assert.That(result.HasFatalErrors, Is.False, string.Join("\n", result.Errors));
        Assert.That(result.Outputs.Select(output => output.Message),
            Is.EqualTo(new[] { "constantAssertions", "constantAssertions" }));
    }

    [TestCase("csharp", "Input.cs", "class Input { void Run(bool actual) { Assert.True(true); Assert.True(actual); Assert.False(true); } }")]
    [TestCase("python", "input.py", "def run(actual):\n    assert True\n    assert actual\n    assert False\n")]
    [TestCase("javascript", "input.js", "function run(actual) { assert.ok(true); assert.ok(actual); assert.ok(false); }")]
    [TestCase("java", "Input.java", "class Input { void run(boolean actual) { Assert.assertTrue(true); Assert.assertTrue(actual); Assert.assertFalse(true); } }")]
    [TestCase("rust", "input.rs", "fn run(actual: bool) { assert!(true); assert!(actual); assert!(false); }")]
    [TestCase("go", "input_test.go", "package input\nfunc TestRun(t *testing.T) { assert.True(t, true); assert.True(t, actual); assert.False(t, true) }\n")]
    public async Task ConstantAssertionsDetectOnlyUnconditionalSuccess(string language, string filename, string source)
    {
        Write(filename, source);
        await AssertRule(language, "constantAssertions(cb.Statements)", ["constantAssertions"]);
        for (int run = 0; run < 2; run++)
        {
            var result = await Engine.RunProjectAsync([Path.Combine(_repo, "packages")],
                [language + "-checks"], Path.Combine(_root, "target"), []);
            Assert.That(result.HasFatalErrors, Is.False, string.Join("\n", result.Errors));
            Assert.That(result.Outputs.Select(output => output.Message)
                .Where(message => message.EndsWith("[constantAssertions]", StringComparison.Ordinal)).Count(),
                Is.EqualTo(1), $"Standard suite run {run}");
        }
    }

    [TestCase("csharp", "Input.cs", "class Input { int Run() { try { Work(); } catch (IOException) { return 0; } } int Good() { try { Work(); } catch (IOException) { Log(); return 0; } } }")]
    [TestCase("python", "input.py", "def run():\n    try:\n        work()\n    except ValueError:\n        return 0\n\ndef good():\n    try:\n        work()\n    except ValueError:\n        log()\n        return 0\n")]
    [TestCase("java", "Input.java", "class Input { int run() { try { work(); } catch (RuntimeException error) { return 0; } } int good() { try { work(); } catch (RuntimeException error) { log(error); return 0; } } }")]
    public async Task SilentFallbackRequiresAnEntireLiteralOnlyHandler(string language, string filename, string source)
    {
        Write(filename, source);
        await AssertRule(language, "silentLiteralFallbacks(cb.Statements)", ["silentLiteralFallbacks"]);
    }

    // Try*/OrDefault methods advertise their fallback in the name.
    [TestCase("csharp", "Input.cs", "class Input { bool TryRun() { try { Work(); return true; } catch (IOException) { return false; } } int LoadOrDefault() { try { return Work(); } catch (IOException) { return 0; } } int Run() { try { return Work(); } catch (IOException) { return 0; } } }")]
    [TestCase("python", "input.py", "def try_run():\n    try:\n        work()\n    except ValueError:\n        return False\n\ndef load_or_none():\n    try:\n        work()\n    except ValueError:\n        return None\n\ndef run():\n    try:\n        work()\n    except ValueError:\n        return 0\n")]
    public async Task SilentFallbackExemptsMethodsThatDeclareTheirFallback(string language, string filename, string source)
    {
        Write(filename, source);
        await AssertRule(language, "silentLiteralFallbacks(cb.Statements)", ["silentLiteralFallbacks"]);
    }

    [Test]
    public async Task ClutterNamesIgnoreDescriptiveWordsAndPredicateAccessors()
    {
        Write("Input.cs", "class Input { void DeleteBackup() {} bool IsFixed { get; } void ParseFixed() {} }");
        await AssertRule("csharp", "clutterNames(cb.Types, cb.Methods)", ["clutterNames"]);
    }

    [TestCase("csharp", "Input.cs", "class Input { object Run(object input) { return (dynamic)input; } object Good(object input) { return (string)input; } }")]
    [TestCase("javascript", "input.ts", "function run(input: unknown) { return input as any; } function good(input: unknown) { return input as string; }")]
    public async Task TypeErasureDoesNotBanOrdinaryCasts(string language, string filename, string source)
    {
        Write(filename, source);
        await AssertRule(language, "typeErasingCasts(cb.Statements)", ["typeErasingCasts"]);
    }

    [Test]
    public async Task WarningPragmasAreActiveAndBlanketOnly()
    {
        Write("Input.cs", """
            #pragma warning disable
            #pragma warning restore
            #pragma warning disable CS0168
            #if false
            #pragma warning disable
            #endif
            class Input { }
            """);
        await AssertRule("csharp", "blanketWarningSuppressions(cb.Statements, cb.Methods, cb.Types)", ["blanketWarningSuppressions"]);
    }

    [Test]
    public async Task ProductionScopeExcludesTestAndExampleStubsAndLegitimateConstantFunctions()
    {
        Write("Input.cs", "class Input { void Run() { throw new NotImplementedException(); } int Default() => 0; }");
        Write("tests\\Fixture.cs", "class Fixture { void Run() { throw new NotImplementedException(); } }");
        Write("examples\\Example.cs", "class Example { void Run() { throw new NotImplementedException(); } }");
        await AssertRule("csharp", "codeQualityChecks(cb)", ["unfinishedImplementations"]);
    }

    [Test]
    public async Task PythonTestFileScopeExcludesProductionChecksButRetainsTestSleeps()
    {
        Write("test_settings.py", "import time\npassword = 'fixture'\n\ndef helper():\n    time.sleep(1)\n    raise NotImplementedError()\n");
        Write("settings.py", "def run():\n    raise NotImplementedError()\n");
        await AssertRule("python", "codeQualityChecks(cb)", ["unfinishedImplementations", "blockingTestSleeps"]);
    }

    [Test]
    public async Task CredentialsMustBeAssignmentsNotCommentsOrUnrelatedNames()
    {
        Write("Input.cs", """
            class Input {
                void Run() {
                    string apiKey = "literal";
                    string password = "";
                    string tokenCount = "not a credential";
                    // string clientSecret = "only a comment";
                }
            }
            """);
        await AssertRule("csharp", "credentialLiterals(cb.Statements)", ["credentialLiterals"]);
    }

    [Test]
    public async Task BackupFilesDoNotBanNormalNewOrOldNames()
    {
        Write("Input_copy.cs", "class Input { }");
        Write("NewCustomer.cs", "class NewCustomer { }");
        Write("OldCustomer.cs", "class OldCustomer { }");
        await AssertRule("csharp", "backupSourceFiles(cb.Files)", ["backupSourceFiles"]);
    }

    [Test]
    public async Task LoggingRulesDetectInterpolatedMessagesAndSensitiveArgumentsOnly()
    {
        Write("Input.cs", """
            class Input {
                ILogger logger;
                void Run(string accessToken, string status) {
                    logger.LogInformation($"Status {status}");
                    logger.LogInformation("Token {Token}", accessToken);
                    logger.LogInformation("Status {Status}", status);
                }
            }
            """);
        await AssertRule("csharp", "securityChecks(cb.Statements)",
            ["interpolatedLogMessages", "sensitiveDataInLogs"]);
    }

    [TestCase("python", "input.py", "import pickle\nimport yaml\ndef run(data, stream):\n    pickle.load(stream)\n    yaml.load(data)\n    yaml.load(data, Loader=yaml.SafeLoader)\n")]
    [TestCase("java", "Input.java", "class Input { Object run(ObjectInputStream input) { return input.readObject(); } }")]
    public async Task UnsafeDeserializationFlagsKnownUnsafeReaders(string language, string filename, string source)
    {
        Write(filename, source);
        await AssertRule(language, "unsafeDeserialization(cb.Statements)",
            language == "python" ? ["unsafeDeserialization", "unsafeDeserialization"] : ["unsafeDeserialization"]);
    }

    [Test]
    public async Task InsecureRandomRuleRequiresSecretLikeAssignment()
    {
        Write("Input.cs", """
            class Input {
                void Run() {
                    var token = new Random().Next();
                    var item = new Random().Next();
                    var secureToken = RandomNumberGenerator.GetInt32(100000);
                }
            }
            """);
        await AssertRule("csharp", "insecureRandomForSecrets(cb.Statements)", ["insecureRandomForSecrets"]);
    }

    [TestCase("javascript", "input.js", "function run() { const token = Math.random(); }")]
    [TestCase("rust", "input.rs", "fn run() { let token = rand::random::<u64>(); }\n")]
    public async Task InsecureRandomRuleSupportsParsedAssignmentsAcrossLanguages(string language, string filename, string source)
    {
        Write(filename, source);
        await AssertRule(language, "insecureRandomForSecrets(cb.Statements)", ["insecureRandomForSecrets"]);
    }

    [Test]
    public async Task LocalhostUrlsAreDetectedOnlyAsUrlLiterals()
    {
        Write("Input.cs", """
            class Input {
                void Run() {
                    Fetch("http://localhost:3000/api");
                    Fetch("https://127.0.0.1/health");
                    Fetch("https://service.example/api");
                    Fetch("localhost is a useful hostname");
                }
            }
            """);
        await AssertRule("csharp", "hardcodedLocalUrls(cb.Statements)",
            ["hardcodedLocalUrls", "hardcodedLocalUrls"]);
    }

    [Test]
    public async Task IoInLoopsFlagsDatabaseCallsButNotCallsOutsideLoops()
    {
        Write("Input.cs", """
            class Input {
                void Run(int[] ids, Database db) {
                    foreach (var id in ids) {
                        db.Query(id);
                    }
                    db.Query(0);
                }
            }
            """);
        await AssertRule("csharp", "ioInLoops(cb.Statements)", ["ioInLoops"]);
    }

    [Test]
    public async Task AsyncRulesFindMissingAwaitAndBlockingCalls()
    {
        Write("Input.cs", """
        using System.Threading.Tasks;
        class Input {
            async Task MissingAwait() { Work(); }
            async Task Good() { await Task.Delay(1); }
            void Work() { }
        }
        """);
        await AssertRule("csharp", "asyncWithoutAwait(cb.Methods)", ["asyncWithoutAwait"]);

        Write("input.py", """
            import time
            async def blocking():
                time.sleep(1)
                await other()
            """);
        await AssertRule("python", "blockingCallsInAsync(cb.Statements)", ["blockingCallsInAsync"]);
    }

    [Test]
    public async Task TestScaffoldingIsReportedOnlyInProductionScope()
    {
        Write("src\\service.py", "from unittest.mock import Mock\n");
        Write("tests\\test_service.py", "from unittest.mock import Mock\n");
        await AssertRule("python", "testScaffoldingInProduction(qualityProductionScope(cb).Files)",
            ["testScaffoldingInProduction"]);
    }

    [Test]
    public async Task SpeculativeAbstractionsFlagSingleImplementationInterfaces()
    {
        Write("Input.cs", """
            interface IRunner { void Run(); }
            class Runner : IRunner { public void Run() { } }
            class OtherRunner : IRunner { public void Run() { } }
            interface IStore { void Save(); }
            class Store : IStore { public void Save() { } }
            class Target { public void Run(int value) { } }
            class Facade {
                Target target;
                public void Forward(int value) { target.Run(value); }
            }
            """);
        await AssertRule("csharp", "speculativeAbstractions(cb.Methods, cb.Types)",
            ["speculativeAbstractions", "speculativeAbstractions"]);
    }

    [TestCase("csharp", "Input.cs", "class Input { void Run() { try { Work(); } catch (IOException error) { } } void Good() { try { Work(); } catch (IOException error) { throw; } } }")]
    [TestCase("java", "Input.java", "class Input { void run() { try { work(); } catch (RuntimeException error) {} } void good() { try { work(); } catch (RuntimeException error) { throw error; } } }")]
    public async Task EmptyHandlersDoNotCountCatchBindingsAsBodyStatements(string language, string filename, string source)
    {
        Write(filename, source);
        await AssertRule(language, "emptyErrorHandlers(cb.Statements)", ["emptyErrorHandlers"]);
    }

    // Catch-alls are reported by each suite's swallowed-exception rule and empty Python
    // handlers by python-checks' empty-except-body, so the shared rules must not repeat them.
    [TestCase("csharp", "Input.cs", "class Input { int Run() { try { Work(); } catch (Exception) { } try { Work(); } catch { return 0; } return 1; } }")]
    [TestCase("python", "input.py", "def run():\n    try:\n        work()\n    except ValueError:\n        pass\n")]
    [TestCase("javascript", "input.js", "function run() { try { work(); } catch (error) {} try { work(); } catch (error) { return null; } }")]
    [TestCase("java", "Input.java", "class Input { int run() { try { work(); } catch (Exception error) {} try { work(); } catch (Throwable error) { return 0; } return 1; } }")]
    public async Task HandlerRulesLeaveSuiteOwnedHandlersToLanguageSuites(string language, string filename, string source)
    {
        Write(filename, source);
        await AssertRule(language, "(emptyErrorHandlers(cb.Statements) + silentLiteralFallbacks(cb.Statements))", []);
    }

    [Test]
    public async Task DisabledTestsAreReviewSignalsNotABanOnExplicitTests()
    {
        Write("Input.cs", "class Input { [Test, Ignore] void Skipped() {} [Test, Explicit] void Manual() {} [Test] void Normal() {} }");
        await AssertRule("csharp", "disabledTests(cb.Methods, cb.Statements)", ["disabledTests"]);
    }

    [Test]
    public async Task WeakCryptoDoesNotMatchUnrelatedMemberNames()
    {
        Write("Input.cs", "class Input { void Run() { MD5.Create(); SHA256.Create(); Other.md5(); } }");
        await AssertRule("csharp", "weakCryptoCalls(cb.Statements)", ["weakCryptoCalls"]);
    }

    [Test]
    public async Task BlockingSleepIsScopedToTestsNotProductionTimers()
    {
        Write("tests\\Input.cs", "class Input { [Test] void Run() { Thread.Sleep(10); Task.Delay(10); } }");
        Write("Production.cs", "class Production { void Run() { Task.Delay(10); } }");
        await AssertRule("csharp",
            "(blockingTestSleeps(qualityTestScope(cb).Statements) + fixedDelays(qualityProductionScope(cb).Statements))",
            ["blockingTestSleeps", "blockingTestSleeps", "fixedDelays"]);
    }

    private void Write(string filename, string source)
    {
        string path = Path.Combine(_root, "target", filename);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, source);
    }

    private Task AssertRule(string language, string expression, string[] expected) =>
        AssertOutput(language, expression + ".Select(item.Check)", expected);

    private async Task AssertOutput(string language, string command, string[] expected)
    {
        File.WriteAllText(Path.Combine(_root, "main.cop"), $$"""
            import code
            import code-quality-checks
            import {{language}}
            let cb = {{language}}.parse()
            command MAIN = {{command}}
            """);
        for (int run = 0; run < 2; run++)
        {
            var result = await Engine.RunAsync(_root, Path.Combine(_root, "target"), new EngineRunOptions
            {
                AdditionalFeedPaths = [Path.Combine(_repo, "packages")]
            });
            Assert.That(result.HasParseErrors, Is.False, string.Join("\n", result.ParseErrors));
            Assert.That(result.HasFatalErrors, Is.False, string.Join("\n", result.Errors));
            Assert.That(result.Outputs.Select(output => output.Message).ToArray(), Is.EqualTo(expected), $"Run {run}");
        }
    }
}
