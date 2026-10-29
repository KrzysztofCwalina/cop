using Cop.Core;
using Cop.Providers.SourceModel;
using Cop.Providers.SourceParsers;
using NUnit.Framework;

namespace Cop.Tests.Lang.SourceParsers;

[TestFixture]
public class GoJavaSemanticTests
{
    private string _root = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "cop-semantic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        ProviderErrors.Clear();
        ProviderWarnings.Clear();
    }

    [TearDown]
    public void TearDown()
    {
        Directory.Delete(_root, true);
        ProviderErrors.Clear();
        ProviderWarnings.Clear();
    }

    private string Write(string relative, string source)
    {
        string path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, source);
        return path;
    }

    private static void Native(string language)
    {
        string executable = language == "go" ? "go" : "javac";
        if (ToolchainProcess.FindExecutable(executable) is null) Assert.Ignore(executable + " is not installed.");
    }

    [Test]
    public void Java_AttributionSelectsImportedTypeFactoryAndOverload()
    {
        Native("java");
        Write("library\\Service.java", """
            package library;
            public class Service {
                public static Service create() { return new Service(); }
                public String select(String value) { return value; }
                public int select(int value) { return value; }
            }
            """);
        Write("control\\Service.java", "package control; public class Service { public boolean select(String value) { return true; } }");
        string source = """
            import library.Service;
            class Caller {
                void run() {
                    var service = Service.create();
                    var assigned = service;
                    assigned.select("value");
                    assigned.select(42);
                    new control.Service().select("control");
                }
            }
            """;
        string path = Write("Caller.java", source);
        using var scope = new ToolchainExecutionScope();
        var parser = new JavaSourceParser();
        Assert.That(parser.CanCache(path), Is.False);
        var file = parser.Parse(path, source)!;
        Assert.That(ProviderErrors.Drain(), Is.Empty);
        var calls = file.Statements.Where(s => s.MemberName == "select").ToList();
        Assert.That(calls, Has.Count.EqualTo(3));
        Assert.That(calls[0].ReceiverType!.Name, Is.EqualTo("library.Service"));
        Assert.That(calls[0].ReferencedCallable!.DeclaringType, Is.EqualTo("library.Service"));
        Assert.That(calls[0].ReferencedCallable!.Parameters.Select(t => t.Name), Is.EqualTo(new[] { "java.lang.String" }));
        Assert.That(calls[0].ReferencedCallable!.Signature, Is.EqualTo("library.Service.select(java.lang.String)java.lang.String"));
        Assert.That(calls[0].ValueType!.Name, Is.EqualTo("java.lang.String"));
        Assert.That(calls[0].ReferencedCallable!.SourceFile, Is.EqualTo(Path.Combine(_root, "library", "Service.java")));
        Assert.That(calls[0].ReferencedCallable!.Line, Is.EqualTo(4));
        Assert.That(calls[1].ReferencedCallable!.Parameters.Select(t => t.Name), Is.EqualTo(new[] { "int" }));
        Assert.That(calls[1].ValueType!.Name, Is.EqualTo("int"));
        Assert.That(calls[2].ReferencedCallable!.DeclaringType, Is.EqualTo("control.Service"));
        Assert.That(calls[2].ValueType!.Name, Is.EqualTo("boolean"));
        Assert.That(ProviderErrors.Drain(), Is.Empty);
        Assert.That(Directory.EnumerateFiles(_root, "*.class", SearchOption.AllDirectories), Is.Empty);
    }

    [Test]
    public async Task Java_LocalJarAttributionDoesNotExecuteAnnotationProcessors()
    {
        Native("java");
        string compiler = ToolchainProcess.FindExecutable("javac")!;
        string library = Write("dependency-source\\api\\Service.java",
            "package api; public class Service { public static long value() { return 7; } }");
        string marker = Path.Combine(_root, "processor-executed.txt");
        string processor = Write("dependency-source\\Processor.java", $$"""
            import java.util.Set;
            import javax.annotation.processing.*;
            import javax.lang.model.SourceVersion;
            import javax.lang.model.element.TypeElement;
            @SupportedAnnotationTypes("*")
            public class Processor extends AbstractProcessor {
                public SourceVersion getSupportedSourceVersion() { return SourceVersion.latestSupported(); }
                public boolean process(Set<? extends TypeElement> annotations, RoundEnvironment round) {
                    try { java.nio.file.Files.writeString(java.nio.file.Path.of("{{marker.Replace('\\', '/')}}"), "executed"); }
                    catch (java.io.IOException error) { throw new RuntimeException(error); }
                    return false;
                }
            }
            """);
        string classes = Path.Combine(_root, "dependency-classes");
        Directory.CreateDirectory(classes);
        var compile = await ToolchainProcess.RunAsync(compiler, ["-proc:none", "-d", classes, library, processor], _root,
            timeout: TimeSpan.FromSeconds(30), environment: new Dictionary<string, string?>
            { ["JAVA_TOOL_OPTIONS"] = null, ["JDK_JAVA_OPTIONS"] = null, ["_JAVA_OPTIONS"] = null, ["JDK_JAVAC_OPTIONS"] = null });
        Assert.That(compile.Status, Is.EqualTo(ToolchainStatus.Success), compile.StandardError);
        Directory.CreateDirectory(Path.Combine(classes, "META-INF", "services"));
        File.WriteAllText(Path.Combine(classes, "META-INF", "services", "javax.annotation.processing.Processor"), "Processor\n");
        Directory.CreateDirectory(Path.Combine(_root, "target", "libs"));
        System.IO.Compression.ZipFile.CreateFromDirectory(classes, Path.Combine(_root, "target", "libs", "library.jar"));
        string source = "class Caller { void run() { var value = api.Service.value(); } }";
        string path = Write("target\\Caller.java", source);
        var file = new JavaSourceParser().Parse(path, source)!;
        var call = file.Statements.Single(s => s.Kind == "call");
        Assert.That(call.ReferencedCallable!.DeclaringType, Is.EqualTo("api.Service"));
        Assert.That(call.ValueType!.Name, Is.EqualTo("long"));
        Assert.That(ProviderErrors.Drain(), Is.Empty);
        Assert.That(File.Exists(marker), Is.False, "Annotation processors must never execute.");
        Assert.That(Directory.EnumerateFiles(Path.Combine(_root, "target"), "*.class", SearchOption.AllDirectories), Is.Empty);
    }

    [Test]
    public void Java_NamedModuleSourceContextIsAttributedWithoutGeneratingClasses()
    {
        Native("java");
        Write("module-info.java", "module fixture.application { exports library; }");
        Write("library\\Service.java", "package library; public class Service { public static long value() { return 7; } }");
        string source = "package application; import library.Service; public class Caller { void run() { var result = Service.value(); } }";
        string path = Write("application\\Caller.java", source);
        var call = new JavaSourceParser().Parse(path, source)!.Statements.Single(s => s.Kind == "call");
        Assert.That(call.ReferencedCallable!.DeclaringType, Is.EqualTo("library.Service"));
        Assert.That(call.ValueType!.Name, Is.EqualTo("long"));
        Assert.That(ProviderErrors.Drain(), Is.Empty);
        Assert.That(Directory.EnumerateFiles(_root, "*.class", SearchOption.AllDirectories), Is.Empty);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Java_ProductionContextDoesNotAnalyzeOrRequireTestDependencies(bool gradle)
    {
        Native("java");
        Write(gradle ? "build.gradle.kts" : "pom.xml", gradle
            ? "dependencies { testImplementation(\"missing.test:missing:1.0\") }\n"
            : "<project><dependencies><dependency><groupId>missing.test</groupId><artifactId>missing</artifactId>"
                + "<version>1.0</version><scope>test</scope></dependency></dependencies></project>");
        Write("src\\test\\java\\Broken.java", "class Broken { void run() { missing(); } }");
        Write("src\\main\\java\\library\\Service.java",
            "package library; public class Service { public static long value() { return 7; } }");
        string source = "package application; import library.Service; public class Caller { void run() { var result = Service.value(); } }";
        string path = Write("src\\main\\java\\application\\Caller.java", source);
        var call = new JavaSourceParser().Parse(path, source)!.Statements.Single(s => s.Kind == "call");
        Assert.That(call.ReferencedCallable!.DeclaringType, Is.EqualTo("library.Service"));
        Assert.That(call.ValueType!.Name, Is.EqualTo("long"));
        Assert.That(ProviderErrors.Drain(), Is.Empty);
    }

    [Test]
    public void Java_EmptyProjectClasspathCannotBindCopHelperClasses()
    {
        Native("java");
        string source = "class Caller { void run() { CopJavaAnalysis.quote(\"value\"); } }";
        string path = Write("Caller.java", source);
        var call = new JavaSourceParser().Parse(path, source)!.Statements.Single(s => s.Kind == "call");
        Assert.That(call.ReferencedCallable, Is.Null);
        var errors = ProviderErrors.Drain();
        Assert.That(errors, Has.Count.EqualTo(1));
        Assert.That(errors.Single(), Does.Contain("CopJavaAnalysis"));
    }

    [Test]
    public void Go_TypeCheckingSelectsImportedTypeFactoryAssignmentAndCrossFileMethod()
    {
        Native("go");
        Write("go.mod", "module example.test/semantic\n\ngo 1.18\n");
        Write("library\\service.go", """
            package library
            type Service struct {}
            func Create() *Service { return &Service{} }
            """);
        Write("library\\methods.go", "package library\nfunc (s *Service) Select(value string) string { return value }\n");
        Write("control\\service.go", "package control\ntype Service struct {}\nfunc (s Service) Select(value string) bool { return true }\n");
        string source = """
            package caller
            import "example.test/semantic/library"
            import "example.test/semantic/control"
            func Run() {
                service := library.Create()
                assigned := service
                _ = assigned.Select("value")
                other := control.Service{}
                _ = other.Select("control")
            }
            """;
        string path = Write("caller.go", source);
        using var scope = new ToolchainExecutionScope();
        var parser = new GoSourceParser();
        Assert.That(parser.CanCache(path), Is.False);
        var file = parser.Parse(path, source)!;
        Assert.That(ProviderErrors.Drain(), Is.Empty);
        var calls = file.Statements.Where(s => s.MemberName == "Select").ToList();
        Assert.That(calls, Has.Count.EqualTo(2));
        Assert.That(calls[0].ReceiverType!.Name, Is.EqualTo("*example.test/semantic/library.Service"));
        Assert.That(calls[0].ReferencedCallable!.Namespace, Is.EqualTo("example.test/semantic/library"));
        Assert.That(calls[0].ReferencedCallable!.Parameters.Select(t => t.Name), Is.EqualTo(new[] { "string" }));
        Assert.That(calls[0].ReferencedCallable!.Signature,
            Is.EqualTo("(*example.test/semantic/library.Service).Selectfunc(value string) string"));
        Assert.That(calls[0].ValueType!.Name, Is.EqualTo("string"));
        Assert.That(calls[0].ReferencedCallable!.SourceFile, Is.EqualTo(Path.Combine(_root, "library", "methods.go")));
        Assert.That(calls[0].ReferencedCallable!.Line, Is.EqualTo(2));
        Assert.That(calls[1].ReferencedCallable!.Namespace, Is.EqualTo("example.test/semantic/control"));
        Assert.That(calls[1].ValueType!.Name, Is.EqualTo("bool"));
        Assert.That(ProviderErrors.Drain(), Is.Empty);
    }

    [Test]
    public void Go_WorkspaceAndBuildContextResolveLocalModuleWithoutDownloads()
    {
        Native("go");
        Write("go.work", "go 1.18\nuse (\n ./app\n ./dependency\n)\n");
        Write("app\\go.mod", "module fixture.test/app\n\ngo 1.18\nrequire fixture.test/dependency v0.0.0\n");
        Write("dependency\\go.mod", "module fixture.test/dependency\n\ngo 1.18\n");
        Write("dependency\\value.go", "package dependency\nfunc Value() string { return \"local\" }\n");
        Write("dependency\\excluded.go", "//go:build cop_never_enabled\n\npackage dependency\nfunc Value() int { return 1 }\n");
        string source = "package app\nimport \"fixture.test/dependency\"\nfunc Run() { _ = dependency.Value() }\n";
        string path = Write("app\\caller.go", source);
        var file = new GoSourceParser().Parse(path, source)!;
        var call = file.Statements.Single(s => s.Kind == "call");
        Assert.That(call.ReferencedCallable!.Namespace, Is.EqualTo("fixture.test/dependency"));
        Assert.That(call.ValueType!.Name, Is.EqualTo("string"));
        Assert.That(ProviderErrors.Drain(), Is.Empty);
        Assert.That(File.Exists(Path.Combine(_root, "go.work.sum")), Is.False);
        Assert.That(File.Exists(Path.Combine(_root, "app", "go.sum")), Is.False);
    }

    [Test]
    public void Go_StandardLibraryIsCheckedFromInstalledSources()
    {
        Native("go");
        Write("go.mod", "module fixture.test/stdlib\n\ngo 1.18\n");
        string source = "package fixture\nimport \"strings\"\nfunc Run() { _ = strings.TrimSpace(\"value\") }\n";
        string path = Write("caller.go", source);
        var file = new GoSourceParser().Parse(path, source)!;
        var call = file.Statements.Single(s => s.Kind == "call");
        Assert.That(call.ReferencedCallable!.Namespace, Is.EqualTo("strings"));
        Assert.That(call.ReferencedCallable!.Parameters.Select(t => t.Name), Is.EqualTo(new[] { "string" }));
        Assert.That(call.ValueType!.Name, Is.EqualTo("string"));
        Assert.That(ProviderErrors.Drain(), Is.Empty);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Go_LocalReplacementAndVendorDependenciesAreResolvedOffline(bool vendor)
    {
        Native("go");
        Write("app\\go.mod", "module fixture.test/app\n\ngo 1.18\nrequire fixture.test/dependency v1.0.0\n"
            + (vendor ? "" : "replace fixture.test/dependency => ../dependency\n"));
        string prefix = vendor ? "app\\vendor\\fixture.test\\dependency" : "dependency";
        Write(Path.Combine(prefix, "go.mod"), "module fixture.test/dependency\n\ngo 1.18\n");
        Write(Path.Combine(prefix, "value.go"), "package dependency\nfunc Value() int { return 7 }\n");
        if (vendor) Write("app\\vendor\\modules.txt", "# fixture.test/dependency v1.0.0\n## explicit; go 1.18\nfixture.test/dependency\n");
        string source = "package app\nimport \"fixture.test/dependency\"\nfunc Run() { _ = dependency.Value() }\n";
        string path = Write("app\\caller.go", source);
        var call = new GoSourceParser().Parse(path, source)!.Statements.Single(s => s.Kind == "call");
        Assert.That(call.ReferencedCallable!.Namespace, Is.EqualTo("fixture.test/dependency"));
        Assert.That(call.ValueType!.Name, Is.EqualTo("int"));
        Assert.That(ProviderErrors.Drain(), Is.Empty);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Go_InternalAndExternalTestPackagesUseTheirOwnImportContext(bool external)
    {
        Native("go");
        Write("go.mod", "module fixture.test/testing\n\ngo 1.18\n");
        Write("value.go", "package fixture\nfunc Value() int { return 7 }\n");
        string source = external
            ? "package fixture_test\nimport \"fixture.test/testing\"\nfunc Run() { _ = fixture.Value() }\n"
            : "package fixture\nfunc Run() { _ = Value() }\n";
        string path = Write("caller_test.go", source);
        var call = new GoSourceParser().Parse(path, source)!.Statements.Single(s => s.Kind == "call");
        Assert.That(call.ReferencedCallable!.Name, Is.EqualTo("Value"));
        Assert.That(call.ValueType!.Name, Is.EqualTo("int"));
        Assert.That(ProviderErrors.Drain(), Is.Empty);
    }

    [TestCase("go")]
    [TestCase("java")]
    public void Native_GenericCallsExposeInstantiatedParameterAndResultArguments(string language)
    {
        Native(language);
        if (language == "go") Write("go.mod", "module fixture.test/generic\n\ngo 1.18\n");
        string source = language == "go" ? """
            package fixture
            type Box[T any] struct { Value T }
            func Make[T any](value T) Box[T] { return Box[T]{Value: value} }
            func Run() { _ = Make("hello") }
            """ : """
            class Box<T> { }
            class Caller {
                static <T> Box<T> make(T value) { return new Box<T>(); }
                void run() { var result = make("hello"); }
            }
            """;
        string path = Write(language == "go" ? "caller.go" : "Caller.java", source);
        ISourceParser parser = language == "go" ? new GoSourceParser() : new JavaSourceParser();
        var call = parser.Parse(path, source)!.Statements.Single(s => s.MemberName == (language == "go" ? "Make" : "make"));
        string argument = language == "go" ? "string" : "java.lang.String";
        Assert.That(call.ReferencedCallable!.Parameters.Select(t => t.Name), Is.EqualTo(new[] { argument }));
        Assert.That(call.ValueType!.Name, Is.EqualTo(language == "go" ? "fixture.test/generic.Box[string]" : "Box<java.lang.String>"));
        Assert.That(call.ValueType.GenericArguments.Select(t => t.Name), Is.EqualTo(new[] { argument }));
        Assert.That(call.ReferencedCallable.ReturnType!.GenericArguments.Select(t => t.Name), Is.EqualTo(new[] { argument }));
        Assert.That(ProviderErrors.Drain(), Is.Empty);
    }

    [TestCase("go")]
    [TestCase("java")]
    public void Native_DependencyEditChangesCallerFactsWithoutCallerEdit(string language)
    {
        Native(language);
        string dependency = language == "go" ? "dependency.go" : "Dependency.java";
        string caller = language == "go" ? "caller.go" : "Caller.java";
        string source = language == "go" ? "package fixture\nfunc Run() { _ = Value() }\n"
            : "class Caller { void run() { var value = Dependency.value(); } }";
        if (language == "go") Write("go.mod", "module fixture.test/edit\n\ngo 1.18\n");
        string path = Write(caller, source);
        using var scope = new ToolchainExecutionScope();
        ISourceParser parser = language == "go" ? new GoSourceParser() : new JavaSourceParser();
        for (int version = 0; version < 2; version++)
        {
            Write(dependency, language == "go" ? version == 0
                ? "package fixture\nfunc Value() string { return \"first\" }\n"
                : "package fixture\nfunc Value() int { return 42 }\n"
                : version == 0 ? "class Dependency { static String value() { return \"first\"; } }"
                : "class Dependency { static int value() { return 42; } }");
            var file = parser.Parse(path, source)!;
            var call = file.Statements.Single(s => s.Kind == "call");
            Assert.That(call.ValueType!.Name, Is.EqualTo(version == 1 ? "int" : language == "go" ? "string" : "java.lang.String"));
            Assert.That(ProviderErrors.Drain(), Is.Empty);
        }
    }

    [TestCase("go")]
    [TestCase("java")]
    public void Native_MissingSymbolsAreDiagnosedOnlyOnSemanticDemand(string language)
    {
        Native(language);
        string source = language == "go" ? "package fixture\nfunc Run() { missing() }\n"
            : "class Caller { void run() { missing(); } }";
        if (language == "go") Write("go.mod", "module fixture.test/missing\n\ngo 1.18\n");
        string path = Write(language == "go" ? "caller.go" : "Caller.java", source);
        ISourceParser parser = language == "go" ? new GoSourceParser() : new JavaSourceParser();
        var file = parser.Parse(path, source)!;
        Assert.That(file.ParseErrors, Is.Empty);
        Assert.That(file.Statements.Single(s => s.Kind == "call").CallTarget!.Name, Is.EqualTo("missing"));
        Assert.That(ProviderErrors.Drain(), Is.Empty);
        Assert.That(file.Statements.Single(s => s.Kind == "call").ReferencedCallable, Is.Null);
        var errors = ProviderErrors.Drain();
        Assert.That(errors, Has.Count.EqualTo(1));
        Assert.That(errors.Single(), Does.Contain("missing"));
    }

    [TestCase("go")]
    [TestCase("java")]
    public void MissingToolchain_LeavesSemanticFactsUnknownWithVisibleFallback(string language)
    {
        ISourceParser parser = language == "go" ? new GoSourceParser(toolchainSearchPath: "")
            : new JavaSourceParser(toolchainSearchPath: "");
        var file = parser.Parse("fixture", language == "go" ? "package fixture\nfunc Run(){ call() }"
            : "class Fixture { void run(){ call(); } }")!;
        var call = file.Statements.Single(s => s.Kind == "call");
        Assert.That(call.ReceiverType, Is.Null);
        Assert.That(call.ValueType, Is.Null);
        Assert.That(call.ReferencedCallable, Is.Null);
        Assert.That(ProviderWarnings.Drain(), Has.Count.EqualTo(1));
    }

    [TestCase("go")]
    [TestCase("java")]
    public async Task RealCli_SemanticPositiveNegativeDependencyEditAndFallbackAreExact(string language)
    {
        Native(language);
        string dotnet = ToolchainProcess.FindExecutable("dotnet")!;
        string tool = ToolchainProcess.FindExecutable(language == "go" ? "go" : "javac")!;
        var root = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "cop.sln"))) root = root.Parent;
        Assert.That(root, Is.Not.Null);
        string configuration = new DirectoryInfo(TestContext.CurrentContext.TestDirectory).Parent!.Name;
        string cli = Path.Combine(root!.FullName, "cop", "cli", "bin", configuration, "net10.0", "cop.dll");
        string script = Write("cop-checks\\main.cop", $$"""
            feed '{{Path.Combine(root.FullName, "packages").Replace('\\', '/')}}'
            import code
            import {{language}}
            let cb = {{language}}.parse()
            predicate isBound(Statement) => Statement.ReferencedCallable != nic
            predicate isString(Statement) => Statement.ValueType != nic && Statement.ValueType.Name == '{{(language == "go" ? "string" : "java.lang.String")}}'
            let violations = cb.Calls:isString:toWarning('Compiler selected string result')
            command MAIN = CHECK(violations)
            command COUNTS = foreach [0] => 'bound={cb.Calls:isBound.Count};string={violations.Count}'
            """);
        string target = Path.Combine(_root, "target");
        if (language == "go") Write("target\\go.mod", "module fixture.test/cli\n\ngo 1.18\n");
        string caller = Write(language == "go" ? "target\\caller.go" : "target\\Caller.java",
            language == "go" ? "package fixture\nfunc Run() { _ = Value() }\n"
                : "class Caller { void run() { var result = Dependency.value(); } }");
        string dependency = language == "go" ? "target\\dependency.go" : "target\\Dependency.java";
        var environment = new Dictionary<string, string?> { ["PATH"] = Path.GetDirectoryName(tool) };
        var verify = await ToolchainProcess.RunAsync(dotnet, [cli, "verify", Path.GetDirectoryName(script)!], root.FullName,
            timeout: TimeSpan.FromSeconds(30), environment: environment);
        Assert.That(verify.ExitCode, Is.EqualTo(0), verify.StandardOutput + verify.StandardError);
        async Task<ToolchainResult> Run() => await ToolchainProcess.RunAsync(dotnet,
            [cli, script, "-t", target, "-c", "MAIN,COUNTS"], root.FullName,
            timeout: TimeSpan.FromMinutes(2), environment: environment);
        for (int version = 0; version < 2; version++)
        {
            Write(dependency, language == "go" ? version == 0
                ? "package fixture\nfunc Value() string { return \"first\" }\n"
                : "package fixture\nfunc Value() int { return 42 }\n"
                : version == 0 ? "class Dependency { static String value() { return \"first\"; } }"
                : "class Dependency { static int value() { return 42; } }");
            string? first = null;
            for (int repeat = 0; repeat < 2; repeat++)
            {
                var result = await Run();
                string output = result.StandardOutput + result.StandardError;
                Assert.That(result.ExitCode, Is.EqualTo(version == 0 ? 1 : 0), output);
                Assert.That(output, Does.Contain($"bound=1;string={(version == 0 ? 1 : 0)}"));
                Assert.That(output.Contains("Compiler selected string result"), Is.EqualTo(version == 0));
                Assert.That(output, Does.Not.Contain("using simpler"));
                if (first is not null) Assert.That(output, Is.EqualTo(first));
                first = output;
            }
        }
        environment["PATH"] = "";
        var fallback = await Run();
        Assert.That(fallback.ExitCode, Is.EqualTo(0), fallback.StandardOutput + fallback.StandardError);
        Assert.That(fallback.StandardOutput + fallback.StandardError, Does.Contain("bound=0;string=0").And.Contain("using simpler"));
        environment["PATH"] = Path.GetDirectoryName(tool);
        File.WriteAllText(caller, language == "go" ? "package fixture\nfunc Run() { missing() }\n"
            : "class Caller { void run() { missing(); } }");
        var missing = await Run();
        Assert.That(missing.ExitCode, Is.EqualTo(1), missing.StandardOutput + missing.StandardError);
        Assert.That(missing.StandardOutput + missing.StandardError, Does.Contain("source analysis error(s)"));
        Assert.That(missing.StandardOutput + missing.StandardError, Does.Contain("semantic context"));
    }
}
