using System.Text.Json;
using Cop.Core;
using Cop.Providers;
using Cop.Providers.SourceModel;
using Cop.Providers.SourceParsers;
using NUnit.Framework;

namespace Cop.Tests.Lang.SourceParsers;

[TestFixture]
public class GoJavaParityRegressionTests
{
    [TestCase("go")]
    [TestCase("java")]
    public void InstalledNativeToolchain_ProducesExactAstFacts(string language)
    {
        string tool = language == "go" ? "go" : "javac";
        if (ToolchainProcess.FindExecutable(tool) is null) Assert.Ignore($"{tool} is not installed; deterministic protocol and fallback tests still run.");
        ProviderWarnings.Clear();
        using var scope = new ToolchainExecutionScope();
        ISourceParser parser = language == "go" ? new GoSourceParser() : new JavaSourceParser();
        Assert.That(parser.GetCacheIdentity("fixture"), Does.Contain(language == "go" ? "go-ast:" : "jdk-tree:"),
            string.Join("\n", ProviderWarnings.Drain()));
        var source = language == "go" ? """
            package sample
            type Pair[T any] struct { A, B int }
            type Alias = Pair[int]
            func (p *Pair[T]) Run(value string) string {
                if ready(probe()) {
                    return outer(inner("line\nvalue"))
                }
                return value
            }
            """ : """
            class Pair<T extends Number> {
                int a, b;
                String Run(String value) {
                    if (ready(probe())) {
                        return outer(inner("line\nvalue"));
                    }
                    return value;
                }
            }
            """;
        var file = parser.Parse(language == "go" ? "fixture.go" : "Pair.java", source)!;
        Assert.That(file.ParseErrors, Is.Empty);
        var type = file.Types.First(t => t.Name == "Pair");
        Assert.That(type.Fields.Select(f => f.Name), Is.EqualTo(language == "go" ? new[] { "A", "B" } : new[] { "a", "b" }));
        var method = type.Methods.Single();
        Assert.That(method.Name, Is.EqualTo("Run"));
        Assert.That(method.Line, Is.EqualTo(language == "go" ? 4 : 3));
        Assert.That(method.Parameters.Single().Name, Is.EqualTo("value"));
        Assert.That(method.Parameters.Single().Line, Is.EqualTo(method.Line));
        Assert.That(file.Statements.Where(s => s.Kind == "call").Select(s => s.MemberName),
            Is.EqualTo(new[] { "ready", "probe", "outer", "inner" }));
        AssertCalls(file, method, "outer", "inner");
        Assert.That(file.Statements.Single(s => s.MemberName == "outer").Parent?.Kind, Is.EqualTo("return"));
        Assert.That(file.Statements.Single(s => s.MemberName == "outer").GetAncestors().Select(s => s.Kind),
            Is.EqualTo(new[] { "return", "if" }));
        Assert.That(file.Statements.Single(s => s.MemberName == "inner").Column, Is.Not.Null);
        Assert.That(ProviderWarnings.Drain(), Is.Empty);
    }

    [TestCase("go")]
    [TestCase("java")]
    public void InstalledNativeToolchain_PreservesGenericImportsAndSameLineOwners(string language)
    {
        string tool = language == "go" ? "go" : "javac";
        if (ToolchainProcess.FindExecutable(tool) is null) Assert.Ignore($"{tool} is not installed.");
        using var scope = new ToolchainExecutionScope();
        ISourceParser parser = language == "go" ? new GoSourceParser() : new JavaSourceParser();
        Assert.That(parser.GetCacheIdentity("fixture"), Does.Contain(language == "go" ? "go-ast:" : "jdk-tree:"),
            string.Join("\n", ProviderWarnings.Drain()));
        var file = parser.Parse(language == "go" ? "fixture.go" : "Box.java", language == "go" ? """
            package sample
            import alias "example.org/library"
            type Box[T any] struct { Value T }
            func First(value Box[string]) Box[int] { return one() }; func Second() { two() }
            """ : """
            import static java.util.Collections.*;
            class Box<T> {
                java.util.List<String> First(java.util.Map<String, Box<Integer>> value) { return one(); } void Second() { two(); }
            }
            """)!;
        Assert.That(file.ParseErrors, Is.Empty);
        var methods = file.Types.SelectMany(t => t.Methods).ToList();
        var first = methods.Single(m => m.Name == "First");
        var second = methods.Single(m => m.Name == "Second");
        Assert.That(first.Line, Is.EqualTo(second.Line));
        Assert.That(first.Column, Is.Not.EqualTo(second.Column));
        Assert.That(file.Statements.Single(s => s.MemberName == "one").Method, Is.SameAs(first));
        Assert.That(file.Statements.Single(s => s.MemberName == "two").Method, Is.SameAs(second));
        Assert.That(first.ReturnType!.GenericArguments.Single().Name, Is.EqualTo(language == "go" ? "int" : "String"));
        var arguments = first.Parameters.Single().Type!.GenericArguments;
        Assert.That(arguments.Select(a => a.Name), Is.EqualTo(language == "go"
            ? new[] { "string" } : new[] { "String", "Box<Integer>" }));
        if (language == "java") Assert.That(arguments[1].GenericArguments.Single().Name, Is.EqualTo("Integer"));
        Assert.That(file.Imports.Single().Line, Is.EqualTo(language == "go" ? 2 : 1));
        Assert.That(file.Imports.Single().Column, Is.Not.Null);
        if (language == "go") Assert.That(file.Imports.Single().Alias, Is.EqualTo("alias"));
        else Assert.That(file.Imports.Single().IsStatic && file.Imports.Single().IsWildcard, Is.True);
    }

    [Test]
    public void GoFallback_GroupedTypesMultiFieldsAliasesAndSignatures()
    {
        var file = new GoSourceParser(toolchainSearchPath: "").Parse("types.go", """
            package sample
            type (
                Pair[T any] struct {
                    A, B int
                }
                Alias = Pair[int]
            )
            func Transform(
                left, right string,
                logger pkg.Logger,
                others ...int,
            ) *Pair[int] { return nil }
            """)!;
        Assert.That(file.ParseErrors, Is.Empty);
        Assert.That(file.Types.Select(t => t.Name), Is.EqualTo(new[] { "Pair", "Alias", "types (functions)" }));
        Assert.That(file.Types[0].Fields.Select(f => f.Name), Is.EqualTo(new[] { "A", "B" }));
        Assert.That(file.Types[0].Fields.Select(f => f.Type?.Name), Is.EqualTo(new[] { "int", "int" }));
        Assert.That(file.Types[0].TypeParameters, Is.EqualTo(new[] { "T any" }));
        Assert.That(file.Types[1].Kind, Is.EqualTo(TypeKind.Alias));
        Assert.That(file.Types[1].AliasTarget?.Name, Is.EqualTo("Pair[int]"));
        Assert.That(((GoTypeDeclaration)file.Types[1]).IsStruct, Is.False);
        var method = file.Types[2].Methods.Single();
        Assert.That(method.Line, Is.EqualTo(8));
        Assert.That(method.Parameters.Select(p => p.Name), Is.EqualTo(new[] { "left", "right", "logger", "others" }));
        Assert.That(method.Parameters.Select(p => p.Type?.Name), Is.EqualTo(new[] { "string", "string", "pkg.Logger", "int" }));
        Assert.That(method.Parameters.Select(p => p.Line), Is.EqualTo(new[] { 9, 9, 10, 11 }));
        Assert.That(method.Parameters[^1].IsVariadic, Is.True);
        Assert.That(method.ReturnType?.Name, Is.EqualTo("*Pair[int]"));
    }

    [Test]
    public void GoFallback_PreservesCrossFileReceiverAndUnnamedQualifiedParameters()
    {
        var file = new GoSourceParser(toolchainSearchPath: "").Parse("methods.go", """
            package sample
            func (p *Pair[T]) Read(pkg.Logger, *Other, []byte) {
                sink(inside("line\nvalue"), `raw`)
            }
            """)!;
        var method = file.Methods.Single();
        Assert.That(method.Name, Is.EqualTo("Read"));
        Assert.That(method.ReceiverType, Is.EqualTo("Pair"));
        Assert.That(((GoMethodDeclaration)method).IsPointerReceiver, Is.True);
        Assert.That(method.Parameters.Select(p => p.Type?.Name), Is.EqualTo(new[] { "pkg.Logger", "*Other", "[]byte" }));
        Assert.That(method.Parameters.Select(p => p.Name), Is.EqualTo(new[] { "", "", "" }));
        AssertCalls(file, method, "sink", "inside");
    }

    [Test]
    public void GoFallback_UnnamedGenericParametersAreNotNames()
    {
        var file = new GoSourceParser(toolchainSearchPath: "").Parse("generic.go",
            "package sample\nfunc Run(Box[T], Map[string, Box[int]], values []Box[T]) {}")!;
        var parameters = file.Types.Single().Methods.Single().Parameters;
        Assert.That(parameters.Select(p => p.Name), Is.EqualTo(new[] { "", "", "values" }));
        Assert.That(parameters.Select(p => p.Type!.Name), Is.EqualTo(new[] { "Box[T]", "Map[string,Box[int]]", "[]Box[T]" }));
    }

    [TestCase("go")]
    [TestCase("java")]
    public void Fallback_FluentCallTargetsRetainEveryInvocation(string language)
    {
        var file = language == "go"
            ? new GoSourceParser(toolchainSearchPath: "").Parse("fluent.go",
                "package sample\nfunc Run() { factory().Next(inner(\"value\")).End() }")!
            : new JavaSourceParser(toolchainSearchPath: "").Parse("Fluent.java",
                "class Fluent { void Run() { factory().Next(inner(\"value\")).End(); } }")!;
        Assert.That(file.Statements.Where(s => s.Kind == "call").Select(s => s.MemberName),
            Is.EquivalentTo(new[] { "factory", "Next", "inner", "End" }));
        var next = file.Statements.Single(s => s.MemberName == "Next");
        Assert.That(next.CallTarget!.Kind, Is.EqualTo("memberAccess"));
        Assert.That(next.CallTarget.Target!.Kind, Is.EqualTo("invocation"));
        Assert.That(next.CallTarget.Target.Target!.Name, Is.EqualTo("factory"));
    }

    [Test]
    public void JavaFallback_AnnotationStringParenthesesDoNotConsumeDeclarations()
    {
        var file = new JavaSourceParser(toolchainSearchPath: "").Parse("Annotated.java", """
            @Tag ("(")
            class Annotated {
                @Tag(")")
                void run() { call(); }
            }
            """)!;
        Assert.That(file.ParseErrors, Is.Empty);
        Assert.That(file.Types.Single().Name, Is.EqualTo("Annotated"));
        Assert.That(file.Types.Single().Decorators, Is.EqualTo(new[] { "Tag" }));
        Assert.That(file.Types.Single().Methods.Single().Name, Is.EqualTo("run"));
        Assert.That(file.Statements.Single(s => s.Kind == "call").MemberName, Is.EqualTo("call"));
    }

    [Test]
    public void GoFallback_InitializerAndHeaderCallsAreNotSkipped()
    {
        var file = new GoSourceParser(toolchainSearchPath: "").Parse("calls.go", """
            package sample
            var value = outside(initial())
            func Run() {
                if ready(probe()) {
                    finish(wrap("line\nvalue"))
                }
            }
            """)!;
        Assert.That(file.Statements.Where(s => s.Kind == "call").Select(s => s.MemberName),
            Is.EquivalentTo(new[] { "outside", "initial", "ready", "probe", "finish", "wrap" }));
        Assert.That(file.Statements.Single(s => s.MemberName == "outside").IsInMethod, Is.False);
        var finish = file.Statements.Single(s => s.MemberName == "finish");
        Assert.That(finish.Parent?.Kind, Is.EqualTo("if"));
        Assert.That(finish.Parent!.Children, Does.Contain(finish));
        Assert.That(finish.Method?.Name, Is.EqualTo("Run"));
    }

    [Test]
    public void JavaFallback_FieldsAnnotationsGenericsPermitsAndLocations()
    {
        var file = new JavaSourceParser(toolchainSearchPath: "").Parse("Example.java", """
            @interface Tag { String value(); }
            sealed class Example<T extends Number> extends Base<T> implements Face<T> permits Child {
                int a = factory(first()), b = factory(second());
                @Tag("method")
                public java.util.List<T> run(
                    final java.util.Map<String, T> values) {
                    return outer(inner("line\nvalue"));
                }
            }
            """)!;
        Assert.That(file.Types.Select(t => t.Name), Is.EqualTo(new[] { "Tag", "Example" }));
        Assert.That(file.Types[0].Kind, Is.EqualTo(TypeKind.Interface));
        var type = file.Types[1];
        Assert.That(type.BaseTypes, Is.EqualTo(new[] { "Base<T>", "Face<T>" }));
        Assert.That(type.TypeParameters, Is.EqualTo(new[] { "T extends Number" }));
        Assert.That(type.Fields.Select(f => f.Name), Is.EqualTo(new[] { "a", "b" }));
        var method = type.Methods.Single();
        Assert.That(method.Line, Is.EqualTo(4));
        Assert.That(method.Decorators, Is.EqualTo(new[] { "Tag" }));
        Assert.That(method.ReturnType?.Name, Is.EqualTo("java.util.List<T>"));
        Assert.That(method.Parameters.Single().Type?.Name, Is.EqualTo("java.util.Map<String,T>"));
        Assert.That(method.Parameters.Single().Line, Is.EqualTo(6));
        Assert.That(file.Statements.Where(s => s.Kind == "call" && !s.IsInMethod).Select(s => s.MemberName),
            Is.EquivalentTo(new[] { "factory", "first", "factory", "second" }));
        AssertCalls(file, method, "outer", "inner");
    }

    [Test]
    public void JavaFallback_NestedHeaderConstructorAndResourceCalls()
    {
        var file = new JavaSourceParser(toolchainSearchPath: "").Parse("Calls.java", """
            class Calls {
                void run() {
                    if (ready(probe())) {
                        while (keepGoing()) {
                            new Box(wrap("text"));
                        }
                    }
                    try (Resource r = open(config())) { consume(r); }
                }
            }
            """)!;
        Assert.That(file.Statements.Where(s => s.Kind == "call").Select(s => s.MemberName),
            Is.EquivalentTo(new[] { "ready", "probe", "keepGoing", "<init>", "wrap", "open", "config", "consume" }));
        Assert.That(file.Statements.Single(s => s.MemberName == "<init>").Parent?.Kind, Is.EqualTo("while"));
        Assert.That(file.Statements.Single(s => s.MemberName == "probe").Parent?.MemberName, Is.EqualTo("ready"));
    }

    [TestCase("go")]
    [TestCase("java")]
    public void Fallback_TracksEveryBlockCommentLineAndImportFacts(string language)
    {
        var file = language == "go"
            ? new GoSourceParser(toolchainSearchPath: "").Parse("source.go", "package sample\n/* first\nmiddle\nlast */\nimport alias \"example.org/lib\"\n")!
            : new JavaSourceParser(toolchainSearchPath: "").Parse("Source.java", "package sample;\n/* first\nmiddle\nlast */\nimport static java.util.Collections.*;\nclass Source {}")!;
        Assert.That(file.CommentLines.Order(), Is.EqualTo(new[] { 2, 3, 4 }));
        Assert.That(file.Imports, Has.Count.EqualTo(1));
        if (language == "go") Assert.That(file.Imports[0].Alias, Is.EqualTo("alias"));
        else
        {
            Assert.That(file.Imports[0].IsStatic, Is.True);
            Assert.That(file.Imports[0].IsWildcard, Is.True);
        }
    }

    [Test]
    public void GoProjects_IncludeNestedModulesAndWorkspaceReplaceExcludeContext()
    {
        string root = Path.Combine(Path.GetTempPath(), "cop-go-project-parity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "nested"));
        try
        {
            File.WriteAllText(Path.Combine(root, "go.mod"), """
                    module example.org/root
                    go 1.22
                    require example.org/dependency v1.2.0
                    replace example.org/dependency => ./replacement
                    exclude example.org/dependency v1.1.0
                    """);
            File.WriteAllText(Path.Combine(root, "nested", "go.mod"), "module example.org/nested\n");
            File.WriteAllText(Path.Combine(root, "go.work"), """
                    go 1.22
                    use (
                        .
                        ./nested
                    )
                    replace example.org/other => ./other
                    """);
            var projects = GoProjectDiscovery.Discover(root, null).OrderBy(p => p.Name).ToList();
            Assert.That(projects.Select(p => p.Name), Is.EqualTo(new[] { "nested", "root" }));
            Assert.That(projects[1].References, Is.EqualTo(new[] { "example.org/dependency" }));
            Assert.That(projects[1].Properties["Replacements"], Is.EqualTo("example.org/dependency => ./replacement"));
            Assert.That(projects[1].Properties["Exclusions"], Is.EqualTo("example.org/dependency v1.1.0"));
            Assert.That(projects[0].Properties["Workspace"], Is.EqualTo("go.work"));
            Assert.That(projects[0].Properties["WorkspaceUses"], Is.EqualTo(".\n./nested"));
            Assert.That(projects[0].Properties["WorkspaceReplacements"], Is.EqualTo("example.org/other => ./other"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Test]
    public void JavaProjects_UseOwnMavenArtifactUnorderedDependenciesAndNestedKotlinGradle()
    {
        string root = Path.Combine(Path.GetTempPath(), "cop-java-project-parity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "nested"));
        try
        {
            File.WriteAllText(Path.Combine(root, "pom.xml"), """
                    <project xmlns="http://maven.apache.org/POM/4.0.0">
                      <parent><artifactId>parent</artifactId></parent>
                      <artifactId>actual</artifactId>
                      <dependencies><dependency><artifactId>library</artifactId><version>1</version><groupId>example</groupId></dependency></dependencies>
                      <dependencyManagement><dependencies><dependency><groupId>managed</groupId><artifactId>not-a-dependency</artifactId></dependency></dependencies></dependencyManagement>
                      <build><plugins><plugin><dependencies><dependency><groupId>plugin</groupId><artifactId>not-a-project-dependency</artifactId></dependency></dependencies></plugin></plugins></build>
                    </project>
                    """);
            File.WriteAllText(Path.Combine(root, "nested", "build.gradle.kts"), """
                    dependencies {
                        implementation("example:library:1")
                        api("example:api:2")
                        // implementation("comment:fake:1")
                        val documentation = "implementation('string:fake:1')"
                    }
                    """);
            var projects = JavaProjectDiscovery.Discover(root, null);
            Assert.That(projects.Select(p => p.Name), Is.EquivalentTo(new[] { "actual", "nested" }));
            Assert.That(projects.Single(p => p.Name == "actual").References, Is.EqualTo(new[] { "example:library" }));
            Assert.That(projects.Single(p => p.Name == "nested").References, Is.EqualTo(new[] { "example:library", "example:api" }));
            ProviderErrors.Clear();
            File.WriteAllText(Path.Combine(root, "pom.xml"), "<project>");
            JavaProjectDiscovery.Discover(root, null);
            Assert.That(ProviderErrors.Drain(), Has.Count.EqualTo(1));
        }
        finally { Directory.Delete(root, true); }
    }
    [TestCase("go", ToolchainStatus.TimedOut)]
    [TestCase("go", ToolchainStatus.Failed)]
    [TestCase("java", ToolchainStatus.OutputLimitExceeded)]
    [TestCase("java", ToolchainStatus.Failed)]
    public void NativeRequestFailure_IsExplicitAndNeverFallsBack(string language, ToolchainStatus status)
    {
        ToolchainResult Request(string _) => new(status, "fixture", null, "", "failure", "fixture failure");
        ISourceParser parser = language == "go" ? new GoSourceParser(nativeRequest: Request) : new JavaSourceParser(nativeRequest: Request);
        Assert.That(() => parser.Parse("fixture", ""), Throws.InvalidOperationException.With.Message.Contains(status.ToString()));
    }

    [TestCase("go", "{}")]
    [TestCase("java", "not-json")]
    public void NativeProtocolFailure_IsExplicit(string language, string json)
    {
        ToolchainResult Request(string _) => new(ToolchainStatus.Success, "fixture", 0, json, "", null);
        ISourceParser parser = language == "go" ? new GoSourceParser(nativeRequest: Request) : new JavaSourceParser(nativeRequest: Request);
        Assert.That(() => parser.Parse("fixture", ""), Throws.InvalidOperationException);
    }

    [TestCase("go")]
    [TestCase("java")]
    public void NativeProtocol_ProjectsDeclarationsNotFallbackGuesses(string language)
    {
        var response = language == "go" ? """
            {"namespace":"fixture","imports":[],"errors":[],"calls":[],"statements":[],
             "methods":[],"types":[{"name":"FromNative","kind":"struct","line":2,"column":1,"endLine":3,"endColumn":2,
             "baseTypes":[],"typeParameters":["T any"],"fields":[{"name":"Value","type":"T","line":2,"column":9,"endLine":2,"endColumn":14}],"methods":[]}]}
            """ : """
            {"namespace":"fixture","imports":[],"errors":[],"calls":[],"statements":[],
             "types":[{"name":"FromNative","kind":"class","modifiers":["public"],"decorators":[],"line":2,"column":1,"endLine":3,"endColumn":2,
             "baseTypes":[],"typeParameters":["T"],"fields":[{"name":"value","type":"T","modifiers":["private"],"line":2,"column":9,"endLine":2,"endColumn":14}],
             "methods":[],"nestedTypes":[],"enumValues":[]}]}
            """;
        ToolchainResult Request(string input)
        {
            using var request = JsonDocument.Parse(input);
            Assert.That(request.RootElement.GetProperty("Source").GetString(), Is.Not.Empty);
            return new(ToolchainStatus.Success, "fixture", 0, response, "", null);
        }
        ISourceParser parser = language == "go" ? new GoSourceParser(nativeRequest: Request) : new JavaSourceParser(nativeRequest: Request);
        var file = parser.Parse("fixture", language == "go" ? "package fixture" : "class Ignored {}")!;
        Assert.That(file.Types.Select(t => t.Name), Is.EqualTo(new[] { "FromNative" }));
        Assert.That(file.Types[0].Fields.Single().Type?.Name, Is.EqualTo("T"));
        Assert.That(file.Types[0].Column, Is.EqualTo(1));
        Assert.That(file.Types[0].EndLine, Is.EqualTo(3));
    }

    private static void AssertCalls(SourceFile file, MethodDeclaration method, string outerName, string innerName)
    {
        var outer = file.Statements.Single(s => s.MemberName == outerName);
        var inner = file.Statements.Single(s => s.MemberName == innerName);
        Assert.That(outer.CallTarget?.Kind, Is.EqualTo("identifier"));
        Assert.That(outer.ArgumentExpressions[0].Kind, Is.EqualTo("invocation"));
        Assert.That(outer.ArgumentExpressions[0].Target?.Name, Is.EqualTo(innerName));
        Assert.That(inner.ArgumentExpressions[0].Kind, Is.EqualTo("stringLiteral"));
        Assert.That(inner.ArgumentExpressions[0].StringValue, Is.EqualTo("line\nvalue"));
        Assert.That(inner.Parent, Is.SameAs(outer));
        Assert.That(outer.Method, Is.SameAs(method));
        Assert.That(inner.Method, Is.SameAs(method));
    }
}
