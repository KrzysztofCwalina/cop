using Cop.Lang;
using Cop.Providers.SourceParsers;
using NUnit.Framework;

namespace Cop.Tests.Lang;

[TestFixture]
public class CheckInterpreterTests
{
    private static string SamplePath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "Samples", fileName);

    [Test]
    public void Run_ClientChecks_GoodClient_NoDiagnostics()
    {
        var copSource = File.ReadAllText(SamplePath("client-checks.cop"));
        var ScriptFile = ScriptParser.Parse(copSource, "client-checks.cop");

        var interpreter = TestInterpreter.Create();

        var outputs = interpreter.Run([TestInterpreter.CodePackage, ScriptFile], TestInterpreter.ParseSourceFiles(SamplePath("GoodClient.cs"))).Outputs;

        var clientOutputs = outputs.Where(d =>
            d.Message.Contains("GoodClient") && !d.Message.Contains("ClientOptions")).ToList();
        Assert.That(clientOutputs, Is.Empty,
            $"Expected no outputs for GoodClient but got:\n{string.Join("\n", clientOutputs)}");
    }

    [Test]
    public void Run_ClientChecks_BadClient_ProducesOutputs()
    {
        var copSource = File.ReadAllText(SamplePath("client-checks.cop"));
        var ScriptFile = ScriptParser.Parse(copSource, "client-checks.cop");

        var interpreter = TestInterpreter.Create();

        var outputs = interpreter.Run([TestInterpreter.CodePackage, ScriptFile], TestInterpreter.ParseSourceFiles(SamplePath("BadClient.cs"))).Outputs;

        Assert.That(outputs.Any(d => d.Message.Contains("sealed") && d.Message.Contains("BadClient")),
            Is.True, "Should flag BadClient not sealed");
        Assert.That(outputs.Any(d => d.Message.Contains("options") || d.Message.Contains("Options")),
            Is.True, "Should flag BadClient missing options constructor");
        Assert.That(outputs.Any(d => d.Message.Contains("CancellationToken")),
            Is.True, "Should flag async without CancellationToken");
    }

    [Test]
    public void Run_MessageTemplate_ResolvesTypeName()
    {
        var copSource = File.ReadAllText(SamplePath("client-checks.cop"));
        var ScriptFile = ScriptParser.Parse(copSource, "client-checks.cop");

        var interpreter = TestInterpreter.Create();

        var outputs = interpreter.Run([TestInterpreter.CodePackage, ScriptFile], TestInterpreter.ParseSourceFiles(SamplePath("BadClient.cs"))).Outputs;

        var sealedOutput = outputs.FirstOrDefault(d => d.Message.Contains("sealed") && d.Message.Contains("BadClient"));
        Assert.That(sealedOutput, Is.Not.Null);
        Assert.That(sealedOutput!.Message, Does.Contain("BadClient"));
    }

    [Test]
    public void Run_CSharpScope_SkipsPythonFiles()
    {
        var copSource = File.ReadAllText(SamplePath("client-checks.cop"));
        var ScriptFile = ScriptParser.Parse(copSource, "client-checks.cop");

        var interpreter = TestInterpreter.Create();

        var outputs = interpreter.Run([TestInterpreter.CodePackage, ScriptFile], TestInterpreter.ParseSourceFiles(SamplePath("good_client.py"))).Outputs;

        Assert.That(outputs, Is.Empty);
    }

    [Test]
    public void Run_CodeChecks_DetectsVarAndThreadSleep()
    {
        var copSource = File.ReadAllText(SamplePath("code-checks.cop"));
        var ScriptFile = ScriptParser.Parse(copSource, "code-checks.cop");

        var interpreter = TestInterpreter.Create();

        var outputs = interpreter.Run([TestInterpreter.CodePackage, ScriptFile], TestInterpreter.ParseSourceFiles(SamplePath("BadClient.cs"))).Outputs;

        Assert.That(outputs.Any(d => d.Message.Contains("var")), Is.True, "Should detect var usage");
        Assert.That(outputs.Any(d => d.Message.Contains("Thread.Sleep")), Is.True, "Should detect Thread.Sleep");
    }

    [Test]
    public void Run_CodeChecks_DetectsSwallowedExceptionOnly()
    {
        var copSource = File.ReadAllText(SamplePath("code-checks.cop"));
        var ScriptFile = ScriptParser.Parse(copSource, "code-checks.cop");

        var interpreter = TestInterpreter.Create();

        var outputs = interpreter.Run([TestInterpreter.CodePackage, ScriptFile], TestInterpreter.ParseSourceFiles(SamplePath("BadClient.cs"))).Outputs;

        // BadClient has two catch(Exception) blocks: one with throw; (rethrow) and one without
        var swallowOutputs = outputs.Where(d => d.Message.Contains("Do not swallow")).ToList();
        Assert.That(swallowOutputs, Has.Count.EqualTo(1), "Should detect only the swallowed exception (not the rethrown one)");
    }

    [Test]
    public void Run_PlainMessage_NoWrapping()
    {
        var copSource = File.ReadAllText(SamplePath("client-checks.cop"));
        var ScriptFile = ScriptParser.Parse(copSource, "client-checks.cop");

        var interpreter = TestInterpreter.Create();

        var outputs = interpreter.Run([TestInterpreter.CodePackage, ScriptFile], TestInterpreter.ParseSourceFiles(SamplePath("BadClient.cs"))).Outputs;

        Assert.That(outputs, Is.Not.Empty);
        var msg = outputs[0].Message;
        // Message should NOT have the old diagnostic format prefix
        Assert.That(msg, Does.Not.Match(@"^\w+\(\d+,\d+\): \w+ [\w.]+:"));
    }

    [Test]
    public void Run_DerivedCollection_FiltersCorrectly()
    {
        var copSource = File.ReadAllText(SamplePath("client-checks.cop"));
        var ScriptFile = ScriptParser.Parse(copSource, "client-checks.cop");

        var interpreter = TestInterpreter.Create();

        var outputs = interpreter.Run([TestInterpreter.CodePackage, ScriptFile], TestInterpreter.ParseSourceFiles(SamplePath("BadClient.cs"))).Outputs;

        Assert.That(outputs.Any(d => d.Message.Contains("BadClient")), Is.True);
        Assert.That(outputs.Any(d => d.Message.Contains("BadClientOptions")), Is.False);
    }

    [Test]
    public void Run_InlineFilter_FiltersSameAsTarget()
    {
        var copSource = File.ReadAllText(SamplePath("client-checks.cop"));
        var ScriptFile = ScriptParser.Parse(copSource, "client-checks.cop");

        var interpreter = TestInterpreter.Create();

        var outputs = interpreter.Run([TestInterpreter.CodePackage, ScriptFile], TestInterpreter.ParseSourceFiles(SamplePath("BadClient.cs"))).Outputs;

        var sealedOutputs = outputs.Where(d => d.Message.Contains("sealed")).ToList();
        Assert.That(sealedOutputs, Is.Not.Empty);
        Assert.That(sealedOutputs[0].Message, Does.Contain("BadClient"));
    }

    [Test]
    public void Run_AzureChecks_DerivedCollections()
    {
        var copSource = File.ReadAllText(SamplePath("azure-checks.cop"));
        var ScriptFile = ScriptParser.Parse(copSource, "azure-checks.cop");

        var interpreter = TestInterpreter.Create();

        var outputs = interpreter.Run([TestInterpreter.CodePackage, ScriptFile], TestInterpreter.ParseSourceFiles(SamplePath("BadClient.cs"))).Outputs;

        Assert.That(outputs.Any(d => d.Message.Contains("BadClient")), Is.True);
    }

    [Test]
    public void Run_CommandName_RunsOnlyMatchingCommand()
    {
        var source = """
            let list-types = foreach Types => PRINT('{item.Name}')
            let check-sealed = foreach Types:csharp => PRINT('{warning:@yellow} {item.Name} not sealed')
            foreach Types => PRINT('unnamed check')
            """;
        var ScriptFile = ScriptParser.Parse(source, "test.cop");

        var interpreter = TestInterpreter.Create();

        var outputs = interpreter.Run([ScriptFile], TestInterpreter.ParseSourceFiles(SamplePath("BadClient.cs")), commandName: "list-types").Outputs;

        Assert.That(outputs, Is.Not.Empty);
        Assert.That(outputs.All(d => !d.Message.Contains("warning:")), Is.True,
            "Should only run list-types command, not check-sealed");
    }

    [Test]
    public void Run_NoCommandName_RunsAll()
    {
        var source = """
            let list-types = foreach Types => PRINT('{item.Name}')
            foreach Types => PRINT('unnamed')
            """;
        var ScriptFile = ScriptParser.Parse(source, "test.cop");

        var interpreter = TestInterpreter.Create();

        var outputs = interpreter.Run([ScriptFile], TestInterpreter.ParseSourceFiles(SamplePath("BadClient.cs"))).Outputs;

        Assert.That(outputs.Any(d => d.Message == "unnamed"), Is.True, "Unnamed command SHOULD run");
        Assert.That(outputs.Any(d => d.Message != "unnamed"), Is.True, "Named command SHOULD also run");
    }

    [Test]
    public void Run_UnknownCommandName_ReturnsEmpty()
    {
        var source = """
            let list-types = foreach Types => PRINT('{item.Name}')
            """;
        var ScriptFile = ScriptParser.Parse(source, "test.cop");

        var interpreter = TestInterpreter.Create();

        var outputs = interpreter.Run([ScriptFile], TestInterpreter.ParseSourceFiles(SamplePath("BadClient.cs")), commandName: "nonexistent").Outputs;

        Assert.That(outputs, Is.Empty);
    }

    [Test]
    public void Run_FunctionInLetChain_ProducesTransformedOutput()
    {
        // End-to-end: define a function, use it in a let declaration chain,
        // and verify PRINT resolves fields from the function-produced ScriptObject.
        var source = """
            predicate isVar(Statement) => Statement.Kind == 'declaration' && Statement.Keywords:contains('var')
            function error(Statement, message: string) => Violation {
                Severity = 'error',
                Message = message,
                Line = Statement.Line
            }
            let VarErrors = Statements:csharp:isVar:error('Do not use var')
            foreach VarErrors => PRINT('{item.Severity}:{item.Line} {item.Message}')
            """;
        var ScriptFile = ScriptParser.Parse(source, "test.cop");

        var interpreter = TestInterpreter.Create();

        var outputs = interpreter.Run([ScriptFile], TestInterpreter.ParseSourceFiles(SamplePath("BadClient.cs"))).Outputs;

        // BadClient.cs should have at least one var usage
        Assert.That(outputs, Is.Not.Empty, "Should produce outputs for var usages");
        // Each output should have the function-produced Violation fields resolved
        foreach (var output in outputs)
        {
            Assert.That(output.Message, Does.StartWith("error:"),
                $"Expected 'error:' prefix but got: {output.Message}");
            Assert.That(output.Message, Does.Contain("Do not use var"),
                $"Expected message text but got: {output.Message}");
        }
    }

    [Test]
    public void Run_FunctionInCheckInlineFilter_ProducesTransformedOutput()
    {
        // Function used directly in a command's inline filters (not via let)
        var source = """
            predicate isVar(Statement) => Statement.Kind == 'declaration' && Statement.Keywords:contains('var')
            function warning(Statement, message: string) => Violation {
                Severity = 'warning',
                Message = message
            }
            foreach Statements:csharp:isVar:warning('Avoid var usage') => PRINT('{item.Severity}: {item.Message}')
            """;
        var ScriptFile = ScriptParser.Parse(source, "test.cop");

        var interpreter = TestInterpreter.Create();

        var outputs = interpreter.Run([ScriptFile], TestInterpreter.ParseSourceFiles(SamplePath("BadClient.cs"))).Outputs;

        Assert.That(outputs, Is.Not.Empty, "Should produce outputs for var usages");
        foreach (var output in outputs)
        {
            Assert.That(output.Message, Does.StartWith("warning:"),
                $"Expected 'warning:' prefix but got: {output.Message}");
            Assert.That(output.Message, Does.Contain("Avoid var usage"));
        }
    }

    [Test]
    public void Run_FunctionWithStringTemplate_ResolvesPerItem()
    {
        // Function string arguments with {item.Prop} should resolve per-item
        var source = """
            predicate isVar(Statement) => Statement.Kind == 'declaration' && Statement.Keywords:contains('var')
            function error(Statement, message: string) => Violation {
                Severity = 'error',
                Message = message
            }
            foreach Statements:csharp:isVar:error('Var used for {item.MemberName}') => PRINT('{item.Message}')
            """;
        var ScriptFile = ScriptParser.Parse(source, "test.cop");

        var interpreter = TestInterpreter.Create();

        var outputs = interpreter.Run([ScriptFile], TestInterpreter.ParseSourceFiles(SamplePath("BadClient.cs"))).Outputs;

        Assert.That(outputs, Is.Not.Empty);
        // The template {item.MemberName} should NOT remain unresolved
        foreach (var output in outputs)
        {
            Assert.That(output.Message, Does.Not.Contain("{item.MemberName}"),
                $"Template should be resolved but got: {output.Message}");
        }
    }

    // ── Set Subtraction Tests ──

    [Test]
    public void SetSubtraction_RemovesMatchingItems()
    {
        // BadClient.cs has two var usages: 'var x' and 'var result'
        // Statement.Source = "{File.Path}:{MemberName}", so Source = "BadClient.cs:x" and "BadClient.cs:result"
        var source = @"
import code-analysis

let Accepted = ['BadClient.cs:x']

command NO-VAR = foreach Statements:csharp:varDeclaration:toError('Do not use \'var\' for {item.MemberName}') - Accepted => PRINT(
    '{item.Message}'
)";
        var allFiles = ParseWithImports(source);

        var interpreter = TestInterpreter.Create();
        var outputs = interpreter.Run(allFiles, TestInterpreter.ParseSourceFiles(SamplePath("BadClient.cs"))).Outputs;

        // Should NOT contain the excluded item 'x'
        Assert.That(outputs.Any(d => d.Message.Contains("'var' for x")), Is.False,
            "The accepted 'x' item should be excluded");
        // Should still contain 'result'
        Assert.That(outputs.Any(d => d.Message.Contains("'var' for result")), Is.True,
            "Non-accepted 'result' should still be present");
    }

    [Test]
    public void SetSubtraction_KeepsNonMatchingItems()
    {
        var source = @"
import code-analysis

let Accepted = ['BadClient.cs:nonExistentMember']

command NO-VAR = foreach Statements:csharp:varDeclaration:toError('Do not use \'var\' for {item.MemberName}') - Accepted => PRINT(
    '{item.Message}'
)";
        var allFiles = ParseWithImports(source);

        var interpreter = TestInterpreter.Create();
        var outputs = interpreter.Run(allFiles, TestInterpreter.ParseSourceFiles(SamplePath("BadClient.cs"))).Outputs;

        // Both var violations should remain (none were accepted)
        Assert.That(outputs.Count(d => d.Message.Contains("'var' for")), Is.EqualTo(2),
            "Non-matching accepted list should keep all violations");
    }

    [Test]
    public void SetSubtraction_EmptyAcceptedListKeepsAll()
    {
        var source = @"
import code-analysis

let Accepted = []

command NO-VAR = foreach Statements:csharp:varDeclaration:toError('Do not use \'var\' for {item.MemberName}') - Accepted => PRINT(
    '{item.Message}'
)";
        var allFiles = ParseWithImports(source);

        var interpreter = TestInterpreter.Create();
        var outputs = interpreter.Run(allFiles, TestInterpreter.ParseSourceFiles(SamplePath("BadClient.cs"))).Outputs;

        // Empty accepted list should not filter anything
        Assert.That(outputs.Count(d => d.Message.Contains("'var' for")), Is.EqualTo(2),
            "Empty accepted list should keep all violations");
    }

    [Test]
    public void SetSubtraction_InlineListExclusion()
    {
        // Test inline list literal (not a let-bound variable)
        var source = @"
import code-analysis

command NO-VAR = foreach Statements:csharp:varDeclaration:toError('Do not use \'var\' for {item.MemberName}') - ['BadClient.cs:x', 'BadClient.cs:result'] => PRINT(
    '{item.Message}'
)";
        var allFiles = ParseWithImports(source);

        var interpreter = TestInterpreter.Create();
        var outputs = interpreter.Run(allFiles, TestInterpreter.ParseSourceFiles(SamplePath("BadClient.cs"))).Outputs;

        // Both items excluded — no var violations should remain
        Assert.That(outputs.Where(d => d.Message.Contains("'var' for")).ToList(), Is.Empty,
            "All var violations should be excluded when both are in the inline list");
    }

    [Test]
    public void Run_ParameterizedCommand_BasicExecution()
    {
        var source = @"
import code-analysis

export command CHECK(violations) = PRINT(
    '{item.Message}',
    violations
)

export let var-usage = Statements:csharp:varDeclaration:toError('Do not use var for {item.MemberName}')

RUN CHECK(var-usage)
";
        var allFiles = ParseWithImports(source);
        var interpreter = TestInterpreter.Create();
        var outputs = interpreter.Run(allFiles, TestInterpreter.ParseSourceFiles(SamplePath("BadClient.cs"))).Outputs;

        Assert.That(outputs.Any(d => d.Message.Contains("Do not use var")), Is.True,
            "RUN CHECK(var-usage) should produce output for var violations");
    }

    [Test]
    public void Run_ParameterizedCommand_WithExclusions()
    {
        var source = @"
import code-analysis

export command CHECK(violations) = PRINT(
    '{item.Message}',
    violations
)

export let var-usage = Statements:csharp:varDeclaration:toError('Do not use var for {item.MemberName}')

let Accepted = ['BadClient.cs:x', 'BadClient.cs:result']
RUN CHECK(var-usage - Accepted)
";
        var allFiles = ParseWithImports(source);
        var interpreter = TestInterpreter.Create();
        var outputs = interpreter.Run(allFiles, TestInterpreter.ParseSourceFiles(SamplePath("BadClient.cs"))).Outputs;

        Assert.That(outputs.Where(d => d.Message.Contains("Do not use var")).ToList(), Is.Empty,
            "RUN CHECK(var-usage - Accepted) should exclude all var violations");
    }

    [Test]
    public void Run_NonParameterizedCommand_Executes()
    {
        var source = @"
import code-analysis

command GREET = PRINT('hello world')

RUN GREET()
";
        var allFiles = ParseWithImports(source);
        var interpreter = TestInterpreter.Create();
        var outputs = interpreter.Run(allFiles, []).Outputs;

        Assert.That(outputs.Any(d => d.Message.Contains("hello world")), Is.True,
            "RUN GREET() should output hello world");
    }

    [Test]
    public void ParameterizedCommand_AutoExecutes_WithoutRun()
    {
        var source = @"
import code-analysis

export command CHECK(violations) = PRINT(
    '{item.Message}',
    violations
)

export let var-usage = Statements:csharp:varDeclaration:toError('Do not use var for {item.MemberName}')

CHECK(var-usage)
";
        var allFiles = ParseWithImports(source);
        var interpreter = TestInterpreter.Create();
        var outputs = interpreter.Run(allFiles, TestInterpreter.ParseSourceFiles(SamplePath("BadClient.cs"))).Outputs;

        Assert.That(outputs.Any(d => d.Message.Contains("Do not use var")), Is.True,
            "CHECK(var-usage) should auto-execute without RUN keyword");
    }

    [Test]
    public void ParameterizedCommand_AutoExecutes_WithFilters()
    {
        var source = @"
import code-analysis

export command CHECK(violations) = PRINT(
    '{item.Message}',
    violations
)

export let var-usage = Statements:csharp:varDeclaration:toError('Do not use var for {item.MemberName}')

let Accepted = ['BadClient.cs:x', 'BadClient.cs:result']
CHECK(var-usage - Accepted)
";
        var allFiles = ParseWithImports(source);
        var interpreter = TestInterpreter.Create();
        var outputs = interpreter.Run(allFiles, TestInterpreter.ParseSourceFiles(SamplePath("BadClient.cs"))).Outputs;

        Assert.That(outputs.Where(d => d.Message.Contains("Do not use var")).ToList(), Is.Empty,
            "CHECK(var-usage - Accepted) should auto-execute and exclude accepted violations");
    }

    [Test]
    public void CommandFilter_RunsOnlyMatchingChecks()
    {
        var source = @"
import code-analysis

export command CHECK(violations) = PRINT(
    '{item.Message}',
    violations
)

export let var-usage = Statements:csharp:varDeclaration:toError('Do not use var')
export let dynamic-usage = Statements:csharp:dynamicDeclaration:toError('Do not use dynamic')

CHECK(var-usage)
CHECK(dynamic-usage)
";
        var allFiles = ParseWithImports(source);
        var interpreter = TestInterpreter.Create();

        // Run with filter — only var-usage
        var filter = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "var-usage" };
        var outputs = interpreter.Run(allFiles, TestInterpreter.ParseSourceFiles(SamplePath("BadClient.cs")),
            commandFilter: filter).Outputs;

        Assert.That(outputs.Any(d => d.Message.Contains("Do not use var")), Is.True,
            "var-usage should be in output when included in filter");
        Assert.That(outputs.Any(d => d.Message.Contains("Do not use dynamic")), Is.False,
            "dynamic-usage should NOT be in output when excluded from filter");
    }

    [Test]
    public void DottedCollection_CodeStatements_Works()
    {
        var source = @"
import code

predicate usesVar([Statement]) => Statement.Keywords:contains('var')
let StatementsUsingVar = Code.Statements:usesVar
foreach StatementsUsingVar => PRINT('uses var at {item.Line}')
";
        var allFiles = ParseWithImports(source);
        var interpreter = TestInterpreter.Create();
        var outputs = interpreter.Run(allFiles, TestInterpreter.ParseSourceFiles(SamplePath("BadClient.cs"))).Outputs;

        Assert.That(outputs.Any(d => d.Message.Contains("uses var")), Is.True,
            "Code.Statements:usesVar should resolve through the Code runtime binding");
    }

    [Test]
    public void DottedCollection_InLetDeclaration_Works()
    {
        var source = @"
import code-analysis

export let var-usage = Code.Statements:csharp:varDeclaration:toError('Do not use var')
CHECK(var-usage)
";
        var allFiles = ParseWithImports(source);
        var interpreter = TestInterpreter.Create();
        var outputs = interpreter.Run(allFiles, TestInterpreter.ParseSourceFiles(SamplePath("BadClient.cs"))).Outputs;

        Assert.That(outputs.Any(d => d.Message.Contains("Do not use var")), Is.True,
            "Code.Statements in let declaration should work with full filter chain");
    }

    [Test]
    public void CHECK_Output_HasAutoAnnotation_ForSeverity()
    {
        var source = @"
import code-analysis

export let var-usage = Code.Statements:csharp:varDeclaration:toError('Do not use var')
CHECK(var-usage)
";
        var allFiles = ParseWithImports(source);
        var interpreter = TestInterpreter.Create();
        var outputs = interpreter.Run(allFiles, TestInterpreter.ParseSourceFiles(SamplePath("BadClient.cs"))).Outputs;

        Assert.That(outputs.Count, Is.GreaterThan(0), "Should produce at least one violation");

        var first = outputs[0];
        // Verify the RichString has an "auto"-annotated span for severity
        var severitySpan = first.Content.Spans.FirstOrDefault(s =>
            s.HasAnnotations && s.Annotations!.ContainsKey("color")
            && s.Annotations["color"] == "auto");
        Assert.That(severitySpan, Is.Not.Null, "Severity span should have color=auto annotation");
        Assert.That(severitySpan!.Text, Is.EqualTo("error"), "Severity text should be 'error'");

        // Verify AnsiRenderer produces red for the severity
        var rendered = AnsiRenderer.Render(first.Content);
        Assert.That(rendered, Does.Contain("\x1b[31m"), "Rendered output should contain red ANSI code for 'error'");

        // Verify dim annotation on file path
        var dimSpan = first.Content.Spans.FirstOrDefault(s =>
            s.HasAnnotations && s.Annotations!.ContainsKey("weight")
            && s.Annotations["weight"] == "dim");
        Assert.That(dimSpan, Is.Not.Null, "File path span should have dim annotation");
    }

    /// <summary>
    /// Helper: parse cop source, resolve imports from packages/, and also load
    /// csharp package files directly (isVar etc. are not exported, so ImportResolver won't include them).
    /// </summary>
    private static List<ScriptFile> ParseWithImports(string copSource)
    {
        var scriptFile = ScriptParser.Parse(copSource, "test.cop");
        var apmRoot = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "packages");

        // Load only csharp definitions (not checks — they have their own commands)
        var csharpDir = ImportResolver.FindPackageDir(apmRoot, "csharp")
            ?? Path.Combine(apmRoot, "csharp");
        var csharpDefs = Path.Combine(csharpDir, "src", "definitions.cop");
        var allFiles = new List<ScriptFile> { TestInterpreter.CodePackage, scriptFile };
        allFiles.Add(ScriptParser.Parse(File.ReadAllText(csharpDefs), csharpDefs));

        // Resolve all imports from all files
        var importResolver = new ImportResolver(apmRoot);
        var resolved = new HashSet<string>();
        var imported = new List<ScriptFile>();
        foreach (var sf in allFiles)
            foreach (var imp in sf.Imports)
            {
                if (!resolved.Add(imp)) continue;
                var errors = new List<string>();
                var pkg = importResolver.Resolve(imp, errors);
                if (pkg != null) imported.Add(pkg);
            }
        allFiles.AddRange(imported);
        return allFiles;
    }

    // ── Collection Union Tests ──

    [Test]
    public void Run_CollectionUnion_CombinesMultipleLets()
    {
        // Two individual let collections, then a union that merges them
        var source = """
            predicate isVar(Statement) => Statement.Kind == 'declaration' && Statement.Keywords:contains('var')
            predicate threadSleep(Statement) => Statement.Kind == 'call'
                && Statement.TypeName == 'Thread' && Statement.MemberName == 'Sleep'

            function toWarning(Statement, message: string) => Violation {
                Severity = 'warning',
                Message = message
            }

            export let var-decls = Statements:csharp:isVar:toWarning('no var')
            export let sleep-calls = Statements:csharp:threadSleep:toWarning('no sleep')
            export let all-checks = [var-decls, sleep-calls]

            command CHECK(violations) = PRINT('{item.Message}', violations)
            RUN CHECK(all-checks)
            """;
        var scriptFile = ScriptParser.Parse(source, "test.cop");
        var interpreter = TestInterpreter.Create();
        var outputs = interpreter.Run([scriptFile], TestInterpreter.ParseSourceFiles(SamplePath("BadClient.cs"))).Outputs;

        var varOutputs = outputs.Where(o => o.Message.Contains("no var")).ToList();
        var sleepOutputs = outputs.Where(o => o.Message.Contains("no sleep")).ToList();

        Assert.That(varOutputs, Is.Not.Empty, "Union should include var-decls violations");
        Assert.That(sleepOutputs, Is.Not.Empty, "Union should include sleep-calls violations");
    }

    // ── Select Operation Tests ──

    [Test]
    public void Run_Select_ProjectsFieldToStringList()
    {
        var source = """
            let typeNames = Code.Types.Select(item.Name)
            predicate isInList(Type) => Type.Name:in(typeNames)
            let listed = Code.Types:isInList
            command CHECK = PRINT('{item.Name} is in the list', listed)
            """;
        var scriptFile = ScriptParser.Parse(source, "test.cop");
        var interpreter = TestInterpreter.Create();
        var outputs = interpreter.Run([scriptFile],
            TestInterpreter.ParseSourceFiles(SamplePath("GoodClient.cs"))).Outputs;

        Assert.That(outputs, Is.Not.Empty, "Should produce outputs for types in list");
        Assert.That(outputs.Any(o => o.Message.Contains("GoodClient")),
            Is.True, "Should find GoodClient in projected list");
    }
}
