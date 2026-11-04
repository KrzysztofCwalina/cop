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
    public void CallQueriesUseParsedReceiversAndExactArgumentPositions(string query, int[] lines)
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
        AssertProgram($$"""
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
    public void ReceiverQueriesPreserveSyntacticDepth(string query, int[] lines)
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
        AssertProgram($$"""
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
    public void ProjectQueriesDoNotInventMissingPropertiesOrDependencies(
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
        AssertProgram($$"""
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
    public void DeclarationQueriesUseCallerSuppliedNames(string query, string[] names)
    {
        Write("target/Input.cs", """
            using Fixture.Dependency;
            namespace Fixture;
            interface IAllowed { }
            interface IOther { }
            class Parent { }
            [Marker] class Child : Parent { }
            """);
        AssertProgram($$"""
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
    public void CSharpFactQueriesKeepEnvironmentOverloadsAndNullableDirectivesDistinct()
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
        AssertProgram("""
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
    public void CSharpChecksKeepDiagnosticsAndHandlerSemantics(bool hasViolations)
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
        AssertProgram("""
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

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private void AssertProgram(string program, string[] expected, int expectedExit = 0)
    {
        Write("main.cop", program);
        for (int run = 0; run < 2; run++)
        {
            var result = Engine.Run(_root, Path.Combine(_root, "target"),
                additionalFeedPaths: [Path.Combine(_repo, "packages")]);
            Assert.That(result.HasParseErrors, Is.False, string.Join("\n", result.ParseErrors));
            Assert.That(result.HasFatalErrors, Is.False, string.Join("\n", result.Errors));
            Assert.That(result.Outputs.Select(output => output.Message), Is.EqualTo(expected));
            Assert.That(result.ExitCode, Is.EqualTo(expectedExit));
        }
    }
}
