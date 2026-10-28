using Cop.Core;
using System.Reflection;
using System.Text.Json;
using Cop.Providers;
using Cop.Providers.SourceModel;
using Cop.Providers.SourceParsers;
using NUnit.Framework;

namespace Cop.Tests.Lang.SourceParsers;

[TestFixture]
public class PythonSemanticBindingTests
{
    private string _root = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "cop-python-binding-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        ProviderWarnings.Clear();
    }

    [TearDown]
    public void TearDown()
    {
        ProviderWarnings.Clear();
        Directory.Delete(_root, recursive: true);
    }

    private string Write(string name, string text)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private static SourceFile Parse(string path)
    {
        var parser = new PythonSourceParser();
        if (parser.GetCacheIdentity(path).EndsWith(":simpler", StringComparison.Ordinal))
            Assert.Ignore("Native source binding requires installed CPython; fallback is tested separately.");
        return parser.Parse(path, File.ReadAllText(path))!;
    }

    [Test]
    public void AnnotatedAndFactoryCalls_SelectActualInheritedDeclarationAcrossImports()
    {
        using var scope = new ToolchainExecutionScope();
        var dependency = Write("models.py", """
            class Base:
                def Run(self, value: int) -> str:
                    return str(value)
            class Derived(Base):
                pass
            def create() -> Derived:
                return Derived()
            """);
        var caller = Write("caller.py", """
            from models import Derived, create as factory
            def invoke(client: Derived) -> str:
                client.Run(1)
                return factory().Run(2)
            """);
        foreach (var run in Enumerable.Range(0, 2))
        {
            var file = Parse(caller);
            Assert.That(file.Statements.Where(s => s.Kind == "call").Select(s => s.MemberName),
                Is.EqualTo(new[] { "Run", "Run", "factory" }));
            Assert.That(ProviderWarnings.Drain(), Is.Empty);
            foreach (var call in file.Statements.Where(s => s.MemberName == "Run"))
            {
                Assert.That(call.ReceiverType!.Name, Is.EqualTo("Derived"));
                Assert.That(call.ReceiverType.Namespace, Is.EqualTo("models"));
                Assert.That(call.ValueType!.Name, Is.EqualTo("str"));
                var selected = call.ReferencedCallable!;
                Assert.That(selected.Name, Is.EqualTo("Run"));
                Assert.That(selected.DeclaringType, Is.EqualTo("Base"));
                Assert.That(selected.Namespace, Is.EqualTo("models"));
                Assert.That(selected.Signature, Is.EqualTo("models.Base.Run(int): str"));
                Assert.That(selected.Parameters.Select(p => p.Name), Is.EqualTo(new[] { "int" }));
                Assert.That(selected.ReturnType!.Name, Is.EqualTo("str"));
                Assert.That(selected.SourceFile, Is.EqualTo(dependency));
                Assert.That(selected.Line, Is.EqualTo(2));
            }
            Assert.That(file.Statements.Single(s => s.MemberName == "factory").ReferencedCallable!.Name, Is.EqualTo("create"));
            Assert.That(ProviderWarnings.Drain(), Is.Empty);
        }
    }

    [Test]
    public void LocalAssignmentsConstructorsModuleFunctionsAndRelativeImports_BindWithoutExecution()
    {
        using var scope = new ToolchainExecutionScope();
        Write("pkg\\__init__.py", "");
        var dependency = Write("pkg\\models.py", """
            raise RuntimeError('must not execute')
            class Service:
                def __init__(self, value: int) -> None:
                    pass
                def Run(self, value: list[str]) -> bool:
                    return True
            def convert(value: str) -> int:
                return 1
            """);
        var caller = Write("pkg\\caller.py", """
            from .models import Service
            from . import models as api
            def invoke() -> None:
                service = Service(1)
                service.Run(['a'])
                api.convert('a')
            """);
        var calls = Parse(caller).Statements.Where(s => s.Kind == "call").ToList();
        Assert.That(calls.Select(s => s.ValueType?.Name), Is.EqualTo(new[] { "Service", "bool", "int" }));
        Assert.That(calls[0].ReferencedCallable!.Name, Is.EqualTo("__init__"));
        Assert.That(calls[0].ReferencedCallable!.ReturnType!.Name, Is.EqualTo("None"));
        Assert.That(calls[1].ReceiverType!.Name, Is.EqualTo("Service"));
        Assert.That(calls[1].ReferencedCallable!.Parameters.Single().GenericArguments.Single().Name, Is.EqualTo("str"));
        Assert.That(calls[2].ReceiverType, Is.Null, "A module qualifier is not an instance receiver.");
        Assert.That(calls[2].ReferencedCallable!.SourceFile, Is.EqualTo(dependency));
    }

    [Test]
    public void UnchangedCaller_DependencyEditIsReboundOnNextAnalysis()
    {
        using var scope = new ToolchainExecutionScope();
        var dependency = Write("dep.py", "def convert(value: int) -> str:\n    return str(value)\n");
        var caller = Write("caller.py", "from dep import convert\nconvert(1)\n");
        Assert.That(Parse(caller).Statements.Single().ValueType!.Name, Is.EqualTo("str"));
        var timestamp = File.GetLastWriteTimeUtc(dependency);
        File.WriteAllText(dependency, "def convert(value: int) -> int:\n    return int(value)\n");
        File.SetLastWriteTimeUtc(dependency, timestamp);
        var next = Parse(caller).Statements.Single();
        Assert.That(next.ValueType!.Name, Is.EqualTo("int"));
        Assert.That(next.ReferencedCallable!.ReturnType!.Name, Is.EqualTo("int"));
    }

    [Test]
    public void DynamicMissingAndSameNameTargets_StayUnknownWithDemandOnlyDiagnostics()
    {
        using var scope = new ToolchainExecutionScope();
        Write("other.py", "def Run(value: int) -> str:\n    return str(value)\n");
        var caller = Write("caller.py", """
            from missing import absent
            def Run(value: int) -> str:
                return str(value)
            def invoke(unknown):
                unknown.Run(1)
                absent(1)
                Run = unknown
                Run(1)
                getattr(unknown, 'Run')(1)
            """);
        var file = Parse(caller);
        var calls = file.Statements.Where(s => s.Kind == "call" && s.IsInMethod && s.Method!.Name == "invoke").ToList();
        Assert.That(calls.Select(s => s.MemberName), Is.EqualTo(new string?[] { "Run", "absent", "Run", null, "getattr" }));
        Assert.That(ProviderWarnings.Drain(), Is.Empty);
        foreach (var call in calls)
        {
            Assert.That(call.ReferencedCallable, Is.Null);
            Assert.That(call.ReceiverType, Is.Null);
            Assert.That(call.ValueType, Is.Null);
        }
        Assert.That(ProviderWarnings.Drain(), Has.Count.EqualTo(5));
        Assert.That(calls[0].ReferencedCallable, Is.Null);
        Assert.That(ProviderWarnings.Drain(), Is.Empty, "A getter must not retry or repeat diagnostics.");
    }

    [Test]
    public void UntypedParameter_RejectsWholeCallableButKeepsIndependentReceiverAndResult()
    {
        using var scope = new ToolchainExecutionScope();
        var caller = Write("caller.py", """
            class Service:
                def Run(self, value) -> str:
                    return 'result'
            def invoke(service: Service):
                service.Run(1)
            """);
        var call = Parse(caller).Statements.Single(s => s.Kind == "call");
        Assert.That(call.ReceiverType!.Name, Is.EqualTo("Service"));
        Assert.That(call.ValueType!.Name, Is.EqualTo("str"));
        Assert.That(call.ReferencedCallable, Is.Null);
        Assert.That(ProviderWarnings.Drain().Single(), Does.Contain("unannotated or unresolved parameter"));
    }

    [Test]
    public void MissingReturnAnnotation_KeepsTypedCallableButReportsUnknownResult()
    {
        using var scope = new ToolchainExecutionScope();
        var caller = Write("caller.py", "def convert(value: int):\n    return value\nconvert(1)\n");
        var call = Parse(caller).Statements.Single(s => s.Kind == "call");
        Assert.That(call.ReferencedCallable!.Parameters.Single().Name, Is.EqualTo("int"));
        Assert.That(call.ReferencedCallable.ReturnType, Is.Null);
        Assert.That(call.ValueType, Is.Null);
        Assert.That(ProviderWarnings.Drain().Single(), Does.Contain("result type is unresolved or unannotated"));
    }

    [Test]
    public void SameLineCallsAndUnicodePositions_IdentifyEachInvocationAndUtf16DeclarationSpan()
    {
        using var scope = new ToolchainExecutionScope();
        const string declaration = "def convert(value: int) -> str: return '\u00e9\U0001F600'";
        var path = Write("caller.py", declaration + "\n'\u00e9'; convert(1); convert(2)\n");
        var calls = Parse(path).Statements.Where(s => s.Kind == "call").ToList();
        Assert.That(calls.Select(s => s.Column), Is.EqualTo(new[] { 6, 18 }));
        foreach (var call in calls)
        {
            Assert.That(call.ValueType!.Name, Is.EqualTo("str"));
            Assert.That(call.ReferencedCallable!.Name, Is.EqualTo("convert"));
            Assert.That(call.ReferencedCallable.Line, Is.EqualTo(1));
            Assert.That(call.ReferencedCallable.Column, Is.EqualTo(1));
            Assert.That(call.ReferencedCallable.EndLine, Is.EqualTo(1));
            Assert.That(call.ReferencedCallable.EndColumn, Is.EqualTo(declaration.Length + 1));
        }
        Assert.That(ProviderWarnings.Drain(), Is.Empty);
    }

    [Test]
    public void VariadicAndKeywordOnlyParameters_KeepDeclarationOrderAndExactTypes()
    {
        using var scope = new ToolchainExecutionScope();
        var caller = Write("caller.py", """
            def collect(first: int, /, *items: str, flag: bool = True, **options: float) -> str:
                return 'result'
            collect(1, 'x', flag=True, extra=1.5)
            """);
        var call = Parse(caller).Statements.Single(s => s.Kind == "call");
        Assert.That(call.ReferencedCallable!.Parameters.Select(p => p.Name),
            Is.EqualTo(new[] { "int", "str", "bool", "float" }));
        Assert.That(call.ValueType!.Name, Is.EqualTo("str"));
        Assert.That(ProviderWarnings.Drain(), Is.Empty);
    }

    [Test]
    public void DefinitionDefaultExpressions_UseEnclosingClassScopeNotSameNameModuleFunction()
    {
        using var scope = new ToolchainExecutionScope();
        var dependency = Write("dep.py", "class Result:\n    pass\ndef make() -> Result:\n    return Result()\n");
        var caller = Write("caller.py", """
            class Wrong:
                pass
            def make() -> Wrong:
                return Wrong()
            class Owner:
                from dep import make
                def configure(self, value=make()):
                    pass
            """);
        var call = Parse(caller).Statements.Single(s => s.MemberName == "make");
        Assert.That(call.ValueType!.Name, Is.EqualTo("Result"));
        Assert.That(call.ReferencedCallable!.Namespace, Is.EqualTo("dep"));
        Assert.That(call.ReferencedCallable.SourceFile, Is.EqualTo(dependency));
    }

    [Test]
    public void StructuralAndSyntaxCacheAccess_DoNotReadOrBindDependencies()
    {
        using var scope = new ToolchainExecutionScope();
        var caller = Write("caller.py", "from dep import convert\nconvert(1)\n");
        var file = Parse(caller);
        Assert.That(file.Statements.Single().MemberName, Is.EqualTo("convert"));
        Assert.That(file.Statements.Single().CallTarget!.Name, Is.EqualTo("convert"));
        var cache = Path.Combine(_root, "syntax.bin");
        SourceCacheSerializer.Save(cache, [1, 2], [file]);
        Assert.That(ProviderWarnings.Drain(), Is.Empty);
        var dependency = Write("dep.py", "def convert(value: int) -> str:\n    return str(value)\n");
        Assert.That(file.Statements.Single().ReferencedCallable!.SourceFile, Is.EqualTo(dependency),
            "The dependency must be loaded on getter demand, not during syntax parse or serialization.");
        var warm = SourceCacheSerializer.TryLoad(cache, [1, 2], typeof(PythonSourceParser).Assembly)!.Single();
        Assert.That(warm.Statements.Single().ReferencedCallable, Is.Null, "File-only caches contain no bound dependency answers.");
        Assert.That(new PythonSourceParser().CanCache(caller), Is.False);
    }

    [Test]
    public void TypedReceiverFieldsPropertiesSelfAndStaticMembers_BindFromAstRelationships()
    {
        using var scope = new ToolchainExecutionScope();
        var path = Write("caller.py", """
            class Service:
                def Run(self, value: int) -> str:
                    return 'result'
                @staticmethod
                def Static(value: int) -> bool:
                    return True
            class Owner:
                def __init__(self) -> None:
                    self.service: Service
                @property
                def property_service(self) -> Service:
                    return Service()
                def invoke(self) -> None:
                    self.service.Run(1)
                    self.property_service.Run(2)
                    Service.Static(3)
            """);
        var calls = Parse(path).Statements.Where(s => s.Kind == "call" && s.Method!.Name == "invoke").ToList();
        Assert.That(calls.Select(s => s.ReceiverType?.Name), Is.EqualTo(new[] { "Service", "Service", "Service" }));
        Assert.That(calls.Select(s => s.ValueType?.Name), Is.EqualTo(new[] { "str", "str", "bool" }));
        Assert.That(calls.Select(s => s.ReferencedCallable?.DeclaringType), Is.EqualTo(new[] { "Service", "Service", "Service" }));
        Assert.That(ProviderWarnings.Drain(), Is.Empty);
    }

    [TestCase("def invoke():\n    Run = lambda value: value\n    Run(1)\n")]
    [TestCase("def invoke():\n    [Run(1) for Run in []]\n")]
    [TestCase("def invoke():\n    Run(1)\n    Run = other\n")]
    [TestCase("def invoke():\n    Run(1, 2)\n")]
    [TestCase("def invoke():\n    Run(unknown=1)\n")]
    [TestCase("def invoke():\n    Run()\n")]
    public void ShadowedOrInvalidCalls_DoNotSelectSameNameGlobalFunction(string body)
    {
        using var scope = new ToolchainExecutionScope();
        var path = Write("caller.py", "def Run(value: int) -> str:\n    return 'result'\n" + body);
        var call = Parse(path).Statements.Single(s => s.Kind == "call" && s.MemberName == "Run");
        Assert.That(call.ReferencedCallable, Is.Null);
        Assert.That(ProviderWarnings.Drain(), Has.Count.EqualTo(1));
    }

    [Test]
    public void AsyncResultAndDecoratedOrAmbiguousMembers_AreNotInvented()
    {
        using var scope = new ToolchainExecutionScope();
        var path = Write("caller.py", """
            async def async_run(value: int) -> str:
                return 'result'
            def decorate(value):
                return value
            @decorate
            def decorated(value: int) -> str:
                return 'result'
            class First:
                def Run(self, value: int) -> str:
                    return 'result'
            class Second:
                def Run(self, value: int) -> int:
                    return value
            class Ambiguous(First, Second):
                pass
            def invoke(client: Ambiguous):
                async_run(1)
                decorated(2)
                client.Run(3)
            """);
        var calls = Parse(path).Statements.Where(s => s.Kind == "call").ToList();
        Assert.That(calls.Select(s => s.MemberName), Is.EqualTo(new[] { "async_run", "decorated", "Run" }));
        Assert.That(calls[0].ReferencedCallable!.Name, Is.EqualTo("async_run"));
        Assert.That(calls[0].ValueType, Is.Null, "A coroutine is not its annotated awaited result.");
        Assert.That(calls[1].ReferencedCallable, Is.Null);
        Assert.That(calls[1].ValueType, Is.Null);
        Assert.That(calls[2].ReferencedCallable, Is.Null);
        Assert.That(calls[2].ValueType, Is.Null);
        Assert.That(calls[2].ReceiverType!.Name, Is.EqualTo("Ambiguous"));
        Assert.That(ProviderWarnings.Drain(), Has.Count.EqualTo(3));
    }

    [Test]
    public void NamespacePackageWithSrcLayout_UsesProjectContextWithoutExecutingIt()
    {
        using var scope = new ToolchainExecutionScope();
        Write("pyproject.toml", "[project]\nname='sample'\n");
        var dependency = Write("src\\pkg\\model.py", "def Run(value: int) -> str:\n    return 'result'\n");
        var path = Write("src\\pkg\\caller.py", "from pkg.model import Run\nRun(1)\n");
        var call = Parse(path).Statements.Single();
        Assert.That(call.ValueType!.Name, Is.EqualTo("str"));
        Assert.That(call.ReferencedCallable!.Namespace, Is.EqualTo("pkg.model"));
        Assert.That(call.ReferencedCallable.SourceFile, Is.EqualTo(dependency));
    }

    [Test]
    public void SourceVisibleMemberReplacement_DoesNotSelectOriginalMethod()
    {
        using var scope = new ToolchainExecutionScope();
        var path = Write("caller.py", """
            class Service:
                def Run(self, value: int) -> str:
                    return 'result'
            def invoke(service: Service, replacement):
                service.Run = replacement
                service.Run(1)
            """);
        var call = Parse(path).Statements.Single(s => s.Kind == "call");
        Assert.That(call.ReceiverType!.Name, Is.EqualTo("Service"));
        Assert.That(call.ValueType, Is.Null);
        Assert.That(call.ReferencedCallable, Is.Null);
        Assert.That(ProviderWarnings.Drain().Single(), Does.Contain("member replacement"));
    }

    [TestCase("service.Run = lambda value: value", "", "member replacement")]
    [TestCase("Service.Run = other", "", "member replacement")]
    [TestCase("", "    def __getattribute__(self, name: str):\n        return lambda value: value\n", "__getattribute__")]
    public void ReplacedOrInterceptedMethods_DoNotSelectOriginalDeclaration(string replacement, string interception, string diagnostic)
    {
        using var scope = new ToolchainExecutionScope();
        var path = Write("caller.py", "class Service:\n"
            + "    def Run(self, value: int) -> str:\n        return 'result'\n"
            + interception
            + "def invoke(service: Service, other):\n"
            + (replacement.Length == 0 ? "" : "    " + replacement + "\n")
            + "    service.Run(1)\n");
        var call = Parse(path).Statements.Single(s => s.Kind == "call");
        Assert.That(call.ReceiverType!.Name, Is.EqualTo("Service"));
        Assert.That(call.ValueType, Is.Null);
        Assert.That(call.ReferencedCallable, Is.Null);
        Assert.That(ProviderWarnings.Drain().Single(), Does.Contain(diagnostic));
    }

    [Test]
    public void InheritedAttributeInterception_PreventsSelectingDerivedMember()
    {
        using var scope = new ToolchainExecutionScope();
        var path = Write("caller.py", """
            class Base:
                def __getattribute__(self, name: str):
                    return lambda value: value
            class Derived(Base):
                def Run(self, value: int) -> str:
                    return 'result'
            def invoke(service: Derived):
                service.Run(1)
            """);
        var call = Parse(path).Statements.Single(s => s.Kind == "call");
        Assert.That(call.ReceiverType!.Name, Is.EqualTo("Derived"));
        Assert.That(call.ValueType, Is.Null);
        Assert.That(call.ReferencedCallable, Is.Null);
        Assert.That(ProviderWarnings.Drain().Single(), Does.Contain("__getattribute__"));
    }

    [Test]
    public void SiblingModuleInNestedTarget_RemainsResolvableWithAncestorProjectMarker()
    {
        using var scope = new ToolchainExecutionScope();
        Write("pyproject.toml", "[project]\nname='sample'\n");
        var dependency = Write("nested\\model.py", "def Run(value: int) -> str:\n    return 'result'\n");
        var path = Write("nested\\caller.py", "from model import Run\nRun(1)\n");
        var call = Parse(path).Statements.Single();
        Assert.That(call.ValueType!.Name, Is.EqualTo("str"));
        Assert.That(call.ReferencedCallable!.SourceFile, Is.EqualTo(dependency));
    }

    [Test]
    public void ActualIsolatedHelperLaunch_StaysWellBelowWindowsCommandLineLimit()
    {
        Parse(Write("probe.py", "def probe():\n    pass\n"));
        var toolchain = typeof(PythonSourceParser).Assembly.GetType("Cop.Providers.SourceParsers.PythonToolchain")!;
        var script = (string)toolchain.GetField("Script", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        const string wrapper = """
            import json,subprocess,sys
            script=json.loads(sys.stdin.read())["script"]
            args=[sys.executable,"-I","-S","-c",script]
            request=json.dumps({"path":"probe.py","source":"def probe():\n    pass\n","mode":"source"})+"\n"
            child=subprocess.run(args,input=request,text=True,capture_output=True,timeout=5)
            print(json.dumps({"length":len(subprocess.list2cmdline(args)),"exit":child.returncode,"output":child.stdout,"error":child.stderr}))
            """;
        var attempts = new List<string>();
        foreach (var name in new[] { "python", "python3", "py" })
        {
            var executable = ToolchainProcess.FindExecutable(name);
            if (executable is null) continue;
            string[] prefix = name == "py" ? ["-3"] : [];
            var result = ToolchainProcess.RunAsync(executable, [.. prefix, "-I", "-S", "-c", wrapper], _root,
                JsonSerializer.Serialize(new { script }), timeout: TimeSpan.FromSeconds(8)).GetAwaiter().GetResult();
            if (result.Status != ToolchainStatus.Success)
            {
                attempts.Add(result.Diagnostic ?? result.StandardError);
                continue;
            }
            using var response = JsonDocument.Parse(result.StandardOutput);
            var root = response.RootElement;
            Assert.That(root.GetProperty("exit").GetInt32(), Is.Zero, root.GetProperty("error").GetString());
            Assert.That(root.GetProperty("length").GetInt32(), Is.LessThan(16_000), "Leave ample room for long executable paths and launcher prefixes.");
            using var ast = JsonDocument.Parse(root.GetProperty("output").GetString()!);
            Assert.That(ast.RootElement.GetProperty("tree").GetProperty("body")[0].GetProperty("name").GetString(), Is.EqualTo("probe"));
            TestContext.Out.WriteLine($"Actual isolated helper command line: {root.GetProperty("length")} characters.");
            return;
        }
        Assert.Fail("Native parser succeeded but no wrapper could launch its helper: " + string.Join("; ", attempts));
    }

    [Test]
    public void Fallback_LeavesSemanticsUnknownAndWarnsOnlyWhenDemanded()
    {
        var path = Write("fallback.py", "def invoke(value: int) -> str:\n    return str(value)\ninvoke(1)\n");
        var file = new PythonSourceParser(useToolchain: false).Parse(path, File.ReadAllText(path))!;
        ProviderWarnings.Drain();
        Assert.That(file.Statements.Select(s => s.MemberName), Is.EqualTo(new[] { "str", "invoke" }));
        Assert.That(ProviderWarnings.Drain(), Is.Empty);
        Assert.That(file.Statements.Last().ReferencedCallable, Is.Null);
        Assert.That(ProviderWarnings.Drain().Single(), Does.Contain("semantic facts are unavailable"));
    }

    [Test]
    public void Engine_RepeatedRunsAndUnchangedCallerDependencyEdit_ExposeFreshBoundFacts()
    {
        var caller = Write("target\\caller.py", """
            from models import Derived, create
            create().Run(1)
            """);
        Parse(caller);
        const string dependencyText = """
            class Base:
                def Run(self, value: int) -> str:
                    return 'result'
            class Derived(Base):
                pass
            def create() -> Derived:
                return Derived()
            """;
        var dependency = Write("target\\models.py", dependencyText);
        Write("cop-checks\\main.cop", """
            import python
            let cb = python.parse()
            let call = cb.Calls.Where(item.MemberName == 'Run')[0]
            command MAIN = print('{call.ReceiverType.Name}:{call.ValueType.Name}:{call.ReferencedCallable.DeclaringType}:{call.ReferencedCallable.Parameters[0].Name}')
            """);
        var repo = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", ".."));
        var checks = Path.Combine(_root, "cop-checks");
        var target = Path.Combine(_root, "target");
        foreach (var run in Enumerable.Range(0, 3))
        {
            if (run == 2)
            {
                var timestamp = File.GetLastWriteTimeUtc(dependency);
                File.WriteAllText(dependency, dependencyText.Replace("-> str", "-> int"));
                File.SetLastWriteTimeUtc(dependency, timestamp);
            }
            var result = Engine.Run(checks, target, additionalFeedPaths: [Path.Combine(repo, "packages")]);
            Assert.That(result.ParseErrors, Is.Empty);
            Assert.That(result.Errors, Is.Empty);
            Assert.That(result.Warnings, Is.Empty);
            Assert.That(result.Outputs.Select(o => o.Message),
                Is.EqualTo(new[] { run == 2 ? "Derived:int:Base:int" : "Derived:str:Base:int" }));
        }
    }
}
