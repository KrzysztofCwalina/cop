using Cop.Providers;
using NUnit.Framework;

namespace Cop.Tests;

[TestFixture]
public class CodeQueryTests
{
    private string _root = null!;
    private string _repo = null!;

    [SetUp]
    public void SetUp()
    {
        var repo = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "cop.sln")))
            repo = repo.Parent;
        _repo = repo?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
        _root = Path.Combine(TestContext.CurrentContext.WorkDirectory, "code-queries",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_root, recursive: true);

    [TestCase("hasStringArgument(item, 0, 'print')", new[] { 4, 7 })]
    [TestCase("hasStringArgument(item, 0, 'PRINT')", new int[] { })]
    [TestCase("hasStringArgumentIn(item, 0, ['PRINT'])", new[] { 4, 7 })]
    [TestCase("hasStringArgumentIn(item, 0, [])", new int[] { })]
    [TestCase("hasStringArgument(item, 1, 'print')", new[] { 5 })]
    [TestCase("hasStringArgument(item, -1, 'print')", new int[] { })]
    [TestCase("hasStringArgument(item, 20, 'print')", new int[] { })]
    [TestCase("hasIdentifierArgument(item, 0, 'name')", new[] { 5 })]
    [TestCase("hasIdentifierArgument(item, -1, 'name')", new int[] { })]
    [TestCase("hasIdentifierReceiver(item, 'ffi')", new[] { 4, 5, 6, 8, 9, 10 })]
    [TestCase("hasIdentifierReceiver(item, 'FFI')", new int[] { })]
    [TestCase("hasIdentifierReceiverIgnoringCase(item, 'FFI')", new[] { 4, 5, 6, 8, 9, 10 })]
    [TestCase("hasIdentifierReceiver(item, 'other')", new int[] { })]
    [TestCase("stringArgumentContainsAny(item, 0, ['TOKEN'])", new[] { 10 })]
    [TestCase("stringArgumentContainsAny(item, 1, ['TOKEN', 'SECRET'])", new[] { 4, 9 })]
    [TestCase("stringArgumentContainsAny(item, -1, ['TOKEN'])", new int[] { })]
    [TestCase("stringArgumentContainsAny(item, 0, [])", new int[] { })]
    [TestCase("hasMemberName(item, ['Register'])", new[] { 4, 5, 6, 7, 8, 9, 10 })]
    [TestCase("hasMemberName(item, ['register'])", new int[] { })]
    [TestCase("hasMemberName(item, [])", new int[] { })]
    public async Task CallQueriesUseParsedReceiversAndExactArgumentPositions(string query, int[] lines)
    {
        Write("target/Input.cs", """
            namespace Fixture;
            class Example {
                void Run(string name) {
                    ffi.Register("print", "TOKEN");
                    ffi.Register(name, "print");
                    ffi.Register();
                    other.ffi.Register("print");
                    ffi.Register(name + "print");
                    ffi.Register("unapproved", "SECRET");
                    ffi.Register("escaped\u005fTOKEN");
                }
            }
            """);
        await AssertProgram($$"""
            import code
            import csharp
            let cb = csharp.parse()
            command MAIN = {
                foreach cb.Calls.Where({{query}}) => '{item.Line}'
                0
            }
            """, lines.Select(line => line.ToString()).ToArray());
    }

    [TestCase("hasMemberReceiver(item, 'System', ['out', 'err'])", new[] { 4, 5 })]
    [TestCase("hasMemberReceiver(item, 'System', ['out'])", new[] { 4 })]
    [TestCase("hasMemberReceiver(item, 'system', ['out'])", new int[] { })]
    [TestCase("hasMemberReceiver(item, 'System', ['OUT'])", new int[] { })]
    [TestCase("hasMemberReceiver(item, 'System', [])", new int[] { })]
    [TestCase("hasIdentifierReceiver(item, 'System')", new[] { 6 })]
    public async Task ReceiverQueriesPreserveSyntacticDepth(string query, int[] lines)
    {
        Write("target/Input.cs", """
            namespace Fixture;
            class Example {
                void Run() {
                    System.@out.println("one");
                    System.err.println("two");
                    System.println("three");
                    Other.System.@out.println("four");
                    System.factory().println("five");
                }
            }
            """);
        await AssertProgram($$"""
            import code
            import csharp
            let cb = csharp.parse()
            command MAIN = {
                foreach cb.Calls.Where(item.MemberName == 'println')
                    .Where({{query}}) => '{item.Line}'
                0
            }
            """, lines.Select(line => line.ToString()).ToArray());
    }

    [TestCase("referencesAny(item, ['Beta'])", new[] { "Alpha" })]
    [TestCase("referencesAny(item, [])", new string[] { })]
    [TestCase("hasPackage(item, 'Fixture.Package')", new[] { "Alpha" })]
    [TestCase("hasPackage(item, 'Fixture')", new string[] { })]
    [TestCase("hasFramework(item, 'Fixture.Framework')", new[] { "Alpha" })]
    [TestCase("hasFramework(item, 'Fixture')", new string[] { })]
    [TestCase("hasProjectProperty(item, 'RootNamespace')", new[] { "Alpha", "Gamma" })]
    [TestCase("hasProjectProperty(item, 'Missing')", new string[] { })]
    [TestCase("hasProjectPropertyValue(item, 'RootNamespace', 'fast')", new[] { "Alpha" })]
    [TestCase("hasProjectPropertyValue(item, 'ROOTNAMESPACE', 'FAST')", new[] { "Alpha" })]
    [TestCase("hasOtherProjectPropertyValue(item, 'RootNamespace', 'fast')", new[] { "Gamma" })]
    [TestCase("hasOtherProjectPropertyValue(item, 'Missing', 'fast')", new string[] { })]
    public async Task ProjectQueriesDoNotInventMissingPropertiesOrDependencies(
        string query, string[] names)
    {
        Write("target/Alpha/Alpha.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <RootNamespace>fast</RootNamespace>
              </PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="..\Beta\Beta.csproj" />
                <PackageReference Include="Fixture.Package" Version="1.0.0" />
                <FrameworkReference Include="Fixture.Framework" />
              </ItemGroup>
            </Project>
            """);
        Write("target/Beta/Beta.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
            </Project>
            """);
        Write("target/Gamma/Gamma.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <RootNamespace>slow</RootNamespace>
              </PropertyGroup>
            </Project>
            """);
        foreach (var name in new[] { "Alpha", "Beta", "Gamma" })
            Write($"target/{name}/Input.cs", $"namespace Fixture; class {name} {{ }}");
        await AssertProgram($$"""
            import code
            import csharp
            let cb = csharp.parse()
            command MAIN = {
                foreach cb.Projects.Where({{query}}).OrderBy(item.Name) => '{item.Name}'
                0
            }
            """, names);
    }

    [TestCase("derivesFromAny(item, ['Parent'])", new[] { "Child" })]
    [TestCase("derivesFromAny(item, [])", new string[] { })]
    [TestCase("hasDecorator(item, 'Marker')", new[] { "Child" })]
    [TestCase("importsAny(item.File, ['Fixture.Dependency'])",
        new[] { "Child", "IAllowed", "IOther", "Parent" })]
    [TestCase("importsAny(item.File, ['Fixture'])", new string[] { })]
    [TestCase("hasNamespace(item)", new[] { "Child", "IAllowed", "IOther", "Parent" })]
    public async Task DeclarationQueriesUseCallerSuppliedNames(string query, string[] names)
    {
        Write("target/Input.cs", """
            using Fixture.Dependency;
            namespace Fixture;
            interface IAllowed { }
            interface IOther { }
            class Parent { }
            [Marker] class Child : Parent { }
            """);
        await AssertProgram($$"""
            import code
            import csharp
            let cb = csharp.parse()
            command MAIN = {
                foreach cb.Types.Where({{query}}).OrderBy(item.Name) => '{item.Name}'
                0
            }
            """, names);
    }

    [Test]
    public async Task CSharpFactQueriesKeepEnvironmentOverloadsAndNullableDirectivesDistinct()
    {
        Write("target/Input.cs", """
            #nullable disable
            namespace Fixture;
            class Example {
                async void Fire() { }
                void Run() {
                    System.Environment.GetEnvironmentVariable("one");
                    System.Environment.GetEnvironmentVariable("two", System.EnvironmentVariableTarget.Process);
                    System.Environment.SetEnvironmentVariable("three", "value");
                    System.Environment.GetEnvironmentVariables();
                }
            }
            """);
        await AssertProgram("""
            import code
            import csharp
            let cb = csharp.parse()
            command MAIN = {
                foreach cb.Calls:isEnvironmentVariableAccess => 'access:{item.Line}'
                foreach cb.Calls:isEnvironmentVariableRead => 'read:{item.Line}'
                foreach cb.Statements:disablesNullable => 'nullable:{item.Line}'
                foreach cb.Types.Methods:isAsyncVoid => 'async:{item.Name}'
                0
            }
            """, ["access:6", "access:7", "access:8", "read:6", "read:7",
                "nullable:1", "async:Fire"]);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task CSharpChecksKeepDiagnosticsAndHandlerSemantics(bool hasViolations)
    {
        Write("target/Input.cs", $$"""
            using System;
            using System.Threading;
            namespace Fixture;
            class Example {
                void Run() {
                    {{(hasViolations ? "Thread" : "Other")}}.Sleep(1);
                    {{(hasViolations ? "Console" : "Other")}}.WriteLine("example");
                    try { throw new Exception(); } catch (Exception) { {{(hasViolations ? "" : "throw;")}} }
                }
            }
            """);
        await AssertProgram("""
            import code
            import csharp-checks
            command MAIN = CHECK(
                thread-sleep-calls + console-calls + empty-catches + swallowed-exceptions)
            """, hasViolations ?
            [
                "Input.cs(6): error: Use Task.Delay instead of Thread.Sleep [thread-sleep-calls]",
                "Input.cs(7): warning: Don't use Console.WriteLine in library code [console-calls]",
                "Input.cs(8): error: Empty catch block swallows all exceptions silently [empty-catches]",
                "Input.cs(8): warning: Do not swallow Exception — rethrow or catch a specific type [swallowed-exceptions]"
            ] : [], hasViolations ? 4 : 0);
    }

    [Test]
    public async Task CSharpStyleQueriesUseParsedCommentsArgumentsAndExpressions()
    {
        Write("target/Input.cs", """
            using System.Threading.Tasks;
            //bad
            namespace Fixture.Example;
            class Example
            {
                bool Run(Task task, string value, bool flag)
                {
                    string url = "https://example.test/a";
                    string code = "value == \"\"; //not a comment";
                    task.ConfigureAwait(true);
                    task.ConfigureAwait(false);
                    task.ConfigureAwait(flag);
                    task.ConfigureAwait("true");
                    if (value == "") { }
                    ;
                    return value != "";
                }
            }
            """);
        await AssertProgram("""
            import code
            import csharp-checks
            command MAIN = print(
                '{configure-await-true-calls.Count} {comment-spacing.Count} ' +
                '{no-empty-statements.Count} {use-string-empty.Count} ' +
                '{file-header-required.Count}')
            """, ["1 1 1 2 1"], expectedExit: null);
    }

    [Test]
    public async Task CSharpVarCheckAllowsTypesThatContainAnonymousTypes()
    {
        Write("target/Input.cs", """
            using System.Linq;
            namespace Fixture.Example;
            class Example
            {
                void Run()
                {
                    var value = new { Name = "example" };
                    var items = new[] { value }.Select(item => item);
                    var count = 42;
                }
            }
            """);
        await AssertProgram("""
            import csharp-checks
            command MAIN = print('{var-declarations.Count}')
            """, ["1"], expectedExit: null);
    }

    [Test]
    public async Task CSharpConsoleCheckDistinguishesExecutableAndLibraryProjects()
    {
        Write("target/App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <OutputType>Exe</OutputType>
              </PropertyGroup>
            </Project>
            """);
        Write("target/App/Program.cs", """
            using System;
            namespace Fixture.App;
            class Program
            {
                static void Main() => Console.WriteLine("expected CLI output");
            }
            """);
        Write("target/Library/Library.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);
        Write("target/Library/Example.cs", """
            using System;
            namespace Fixture.Library;
            public class Example
            {
                public void Run() => Console.WriteLine("unexpected library output");
            }
            """);
        await AssertProgram("""
            import csharp-checks
            command MAIN = print('{console-calls.Count}')
            """, ["1"], expectedExit: null);
    }

    [Test]
    public async Task CSharpStyleChecksAllowElseIfAndLocalFunctionsInStaticClasses()
    {
        Write("target/Input.cs", """
            namespace Fixture.Example;
            static class Example
            {
                static void Run(int value)
                {
                    if (value == 1) { }
                    else if (value == 2) { }
                    else { }
                    if (value == 3) return;
                    int Local() => value;
                    Local();
                }
            }
            """);
        Write("target/InstanceState.cs", """
            namespace Fixture.Example;
            class State
            {
                private int _value;
                public static int Run() => 1;
            }
            class Constructed
            {
                public Constructed() { }
                public static int Run() => 1;
            }
            """);
        await AssertProgram("""
            import csharp-checks
            command MAIN = print(
                '{required-braces.Count} {static-no-instance.Count} {should-be-static.Count}')
            """, ["1 0 0"], expectedExit: null);
    }

    [TestCase("isEnabled", 0)]
    [TestCase("hasChanges", 0)]
    [TestCase("includeTests", 0)]
    [TestCase("recursive", 0)]
    [TestCase("value", 0)]
    [TestCase("flag", 1)]
    [TestCase("b", 1)]
    [TestCase("arg", 1)]
    [TestCase("island", 1)]
    [TestCase("value", 0, "System.Boolean")]
    [TestCase("flag", 1, "System.Boolean")]
    public async Task BooleanParametersAreCheckedForNamingRatherThanTheirType(
        string parameterName, int expectedCount, string parameterType = "bool")
    {
        Write("target/Input.cs", $$"""
            namespace Fixture.Example;
            public class Example
            {
                public void Run({{parameterType}} {{parameterName}}) { }
            }
            """);
        await AssertProgram("""
            import code
            import csharp
            import csharp-checks
            let cb = codebase(csharp.parse())
            command MAIN = print(avoidBoolParams(cb).Count)
            """, [expectedCount.ToString()], expectedExit: null);
    }

    [Test]
    public async Task StaticClassSuggestionConsidersBaseTypesAndOtherPartialDeclarations()
    {
        Write("target/State.cs", """
            namespace Fixture.Example;
            public partial class Stateful
            {
                private int _value;
            }
            public class Base { }
            public class Derived : Base
            {
                public static void Run() { }
            }
            """);
        Write("target/Helpers.cs", """
            namespace Fixture.Example;
            public partial class Stateful
            {
                public static void Run() { }
            }
            public class Utility
            {
                public static void Run() { }
            }
            """);
        await AssertProgram("""
            import code
            import csharp
            import csharp-checks
            let cb = codebase(csharp.parse())
            command MAIN = print(shouldBeStatic(cb).Count)
            """, ["1"], expectedExit: null);
    }

    [Test]
    public async Task BooleanDataPropertiesHaveDescriptiveNames()
    {
        Write("target/Input.cs", """
            namespace Fixture.Example;
            public class Data
            {
                public bool Value { get; set; }
                public bool? BooleanValue { get; set; }
                public bool IsEnabled { get; set; }
            }
            public class Vague
            {
                public bool Flag { get; set; }
            }
            """);
        await AssertProgram("""
            import code
            import csharp
            import csharp-checks
            let cb = codebase(csharp.parse())
            command MAIN = print(boolPropertyNaming(cb).Count)
            """, ["1"], expectedExit: null);
    }

    [Test]
    public void CSharpChecksPackageVerifiesItsOwnSource()
    {
        int exitCode = Cop.Cli.Commands.VerifyCommand.Execute(
            Path.Combine(_repo, "packages", "checks", "csharp-checks"));
        Assert.That(exitCode, Is.EqualTo(0));
    }

    [Test]
    public async Task CSharpMethodDocumentationCheckDoesNotRequireAccessorComments()
    {
        Write("target/Input.cs", """
            namespace Fixture.Example;
            /// <summary>A type with a documented property.</summary>
            public class Documented
            {
                /// <summary>Gets or sets the current value.</summary>
                public int Value { get; set; }
            }
            /// <summary>A type with an undocumented method.</summary>
            public class Undocumented
            {
                public void Run() { }
            }
            """);
        await AssertProgram("""
            import csharp-checks
            command MAIN = print('{public-method-documented.Count}')
            """, ["1"], expectedExit: null);
    }

    [Test]
    public async Task CSharpBindingResolvesDeclaredAspNetFrameworkReferences()
    {
        Write("target/Web/Web.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                <FrameworkReference Include="Microsoft.AspNetCore.App" />
              </ItemGroup>
            </Project>
            """);
        Write("target/Web/Input.cs", """
            using Microsoft.AspNetCore.Http;
            namespace Fixture.Example;
            public static class Example
            {
                public static void Send(HttpResponse response) => response.Redirect("/done");
            }
            """);
        await AssertProgram("""
            import code
            import csharp
            let cb = codebase(csharp.parse())
            let redirects = cb.Calls.Where(item.MemberName == 'Redirect')
            let receiver = redirects[0].ReceiverType
            command MAIN = print('{redirects.Count} {receiver.Namespace}.{receiver.Name}')
            """, ["1 Microsoft.AspNetCore.Http.HttpResponse"], expectedExit: null);
    }

    [Test]
    public async Task CSharpCompletionSourceCheckRequiresMissingContinuationOptions()
    {
        Write("target/Input.cs", """
            using System.Threading.Tasks;
            namespace Fixture.Example;
            internal static class Example
            {
                public static void Run()
                {
                    TaskCompletionSource<int> bare = new TaskCompletionSource<int>();
                    TaskCompletionSource<int> bareTargetTyped = new();
                    TaskCompletionSource<int> configured = new TaskCompletionSource<int>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    TaskCompletionSource<int> combined = new TaskCompletionSource<int>(
                        System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously |
                        TaskCreationOptions.AttachedToParent);
                    TaskCompletionSource<int> targetTyped = new(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    bare.TrySetResult(1);
                    configured.TrySetResult(2);
                }
            }
            """);
        await AssertProgram("""
            import csharp-checks
            command MAIN = print('{bare-task-completion-sources.Count}')
            """, ["2"], expectedExit: null);
    }

    [Test]
    public async Task CSharpDisposalCheckRecognizesOnlyTheActualUsingResource()
    {
        Write("target/Input.cs", """
            using System;
            using System.IO;
            using System.Threading.Tasks;
            namespace Fixture.Example;
            class Example
            {
                async Task Run()
                {
                    using MemoryStream declared = new MemoryStream();
                    using MemoryStream targetTyped = new();
                    await using MemoryStream asynchronous = new MemoryStream();
                    using MemoryStream first = new MemoryStream(), second = new MemoryStream();
                    using (MemoryStream resource = new MemoryStream())
                    {
                        MemoryStream leakedInsideUsing = new MemoryStream();
                    }
                    using (new MemoryStream()) { }
                    using MemoryStream parenthesized = (new MemoryStream());
                    using StreamReader wrapper = new StreamReader(new MemoryStream());
                    MemoryStream leaked = new MemoryStream();
                }
            }
            """);
        await AssertProgram("""
            import csharp-checks
            command MAIN = CHECK(undisposed-new)
            """,
            [
                "Input.cs(15): warning: CA2000: Review disposal or ownership of MemoryStream [undisposed-new]",
                "Input.cs(19): warning: CA2000: Review disposal or ownership of MemoryStream [undisposed-new]",
                "Input.cs(20): warning: CA2000: Review disposal or ownership of MemoryStream [undisposed-new]"
            ], expectedExit: 3);
    }

    [Test]
    public async Task CSharpUnusedInternalCheckRequiresTheAbsenceOfConstructorCalls()
    {
        Write("target/Input.cs", """
            namespace Fixture.Example;
            internal class Used { }
            internal class Used<T> { }
            internal class TargetTyped { }
            internal class Chained
            {
                public object Run() => this;
            }
            internal class Unused { }
            internal static class Factory
            {
                public static object Create() => new Used();
                public static object CreateGeneric() => new Used<int>();
                public static object CreateChained() => new Chained().Run();
                public static object CreateOther()
                {
                    TargetTyped value = new();
                    return value;
                }
            }
            """);
        Write("target/Other.cs", """
            namespace Fixture.Other;
            internal class Used { }
            """);
        await AssertProgram("""
            import csharp-checks
            command MAIN = print('{uninstantiated-internal.Count}')
            """, ["2"], expectedExit: null);
    }

    [Test]
    public async Task PrivateFieldNamingKeepsConstantsOnTheirOwnNamingRule()
    {
        Write("target/Input.cs", """
            namespace Fixture.Example;
            class Constants { private const int Answer = 42; }
            class Instance { private int count; }
            class Static { private static int Count; }
            class InvalidConstant { private const int wrong = 1; }
            class Correct
            {
                private int _count;
                private static int s_count;
                private const int Answer = 42;
            }
            """);
        await AssertProgram("""
            import csharp-checks
            command MAIN = CHECK(
                private-field-naming + static-field-naming + const-naming)
            """,
            [
                "Input.cs(3): warning: Instance has a private field that should start with " +
                    "underscore (_) [private-field-naming]",
                "Input.cs(4): warning: Static has a private static field that should start " +
                    "with s_ [static-field-naming]",
                "Input.cs(5): warning: InvalidConstant has a constant that should be " +
                    "PascalCase [const-naming]"
            ], expectedExit: 3);
    }

    [Test]
    public async Task ExceptionChecksDistinguishUnconditionalAndConditionalRethrows()
    {
        Write("target/Input.cs", """
            using System;
            using System.IO;
            namespace Demo;
            class Errors
            {
                void Always() { try { } catch (Exception) { throw; } }
                void Conditional(bool condition) { try { } catch (Exception) { if (condition) throw; } }
                void Typed() { try { } catch (Exception error) when (error is IOException) { } }
            }
            """);
        await AssertProgram("""
            import csharp-checks
            command MAIN = CHECK(base-exception-catches + swallowed-exceptions)
            """,
            [
                "Input.cs(7): warning: Catch a specific exception type instead of Exception " +
                    "[base-exception-catches]",
                "Input.cs(7): warning: Do not swallow Exception \u2014 " +
                    "rethrow or catch a specific type [swallowed-exceptions]"
            ], expectedExit: 2);
    }

    [Test]
    public async Task RecordsKeepTheirGeneratedInstanceSemantics()
    {
        Write("target/Input.cs", """
            namespace Demo;
            public record Request(string Name) { public static string Describe() => "request"; }
            public record Empty { public static string Describe() => "empty"; }
            internal class Helpers { public static string Describe() => "helper"; }
            """);
        await AssertProgram("""
            import csharp-checks
            command MAIN = CHECK(should-be-static)
            """,
            [
                "Input.cs(4): info: Helpers has only static members; " +
                    "consider a static class [should-be-static]"
            ], expectedExit: 1);
    }

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private async Task AssertProgram(string program, string[] expected, int? expectedExit = 0)
    {
        Write("main.cop", program);
        for (int run = 0; run < 2; run++)
        {
            var result = (await Engine.RunAsync(_root, Path.Combine(_root, "target"), new EngineRunOptions
            {
                AdditionalFeedPaths = [Path.Combine(_repo, "packages")],
            }));
            Assert.That(result.HasParseErrors, Is.False, string.Join("\n", result.ParseErrors));
            Assert.That(result.HasFatalErrors, Is.False, string.Join("\n", result.Errors));
            Assert.That(result.Outputs.Select(output => output.Message), Is.EqualTo(expected));
            Assert.That(result.ExitCode, Is.EqualTo(expectedExit));
        }
    }
}
