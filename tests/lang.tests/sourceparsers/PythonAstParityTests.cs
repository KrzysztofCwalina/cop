using Cop.Core;
using Cop.Providers;
using Cop.Providers.SourceModel;
using Cop.Providers.SourceParsers;
using NUnit.Framework;

namespace Cop.Tests.Lang.SourceParsers;

[TestFixture]
public class PythonAstParityTests
{
    private static SourceFile ParseNative(string source, string path = "parity.py")
    {
        using var scope = new ToolchainExecutionScope();
        var parser = new PythonSourceParser();
        if (parser.GetCacheIdentity(path).EndsWith(":simpler", StringComparison.Ordinal))
            Assert.Ignore("This test exercises installed CPython; fallback coverage runs without Python.");
        return parser.Parse(path, source)!;
    }

    [Test]
    public void Calls_IncludeAllExpressionContextsWithOwnershipAndNesting()
    {
        var file = ParseNative("""
            def f():
                direct(nested())
                x = assigned()
                return returned()
                assert checked()
                with opened():
                    body()
            """);
        Assert.That(file.Methods.Select(m => m.Name), Is.EqualTo(new[] { "f" }));
        var method = file.Methods.Single();
        var calls = file.Statements.Where(s => s.Kind == "call").ToList();
        Assert.That(calls.Select(s => s.MemberName), Is.EqualTo(new[]
            { "direct", "nested", "assigned", "returned", "checked", "opened", "body" }));
        Assert.That(calls.Select(s => s.Line), Is.EqualTo(new[] { 2, 2, 3, 4, 5, 6, 7 }));
        Assert.That(calls.All(s => ReferenceEquals(s.Method, method)), Is.True);
        Assert.That(calls[1].Parent, Is.SameAs(calls[0]));
        Assert.That(calls[2].Parent!.Kind, Is.EqualTo("assignment"));
        Assert.That(calls[3].Parent!.Kind, Is.EqualTo("return"));
        Assert.That(calls[4].Parent!.Kind, Is.EqualTo("assert"));
        Assert.That(calls[5].Parent, Is.SameAs(calls[6].Parent));
        Assert.That(calls[5].Parent!.Kind, Is.EqualTo("with"));
        Assert.That(calls[0].Children, Is.EqualTo(new[] { calls[1] }));
    }

    [Test]
    public void Arguments_AreDecodedAndStructuredWithoutInventingDynamicTargets()
    {
        var file = ParseNative("""
            client.send("hello\nworld", nested('x'), label='value', **options)
            factory().run(b'bytes', f'{value}')
            1e999
            """);
        var calls = file.Statements.Where(s => s.Kind == "call").ToList();
        Assert.That(calls.Select(s => s.MemberName), Is.EqualTo(new[] { "send", "nested", "run", "factory" }));
        var send = calls[0];
        Assert.That(send.Arguments, Is.EqualTo(new[] { "hello\nworld", "nested('x')", "value", "options" }));
        Assert.That(send.CallTarget!.Kind, Is.EqualTo("memberAccess"));
        Assert.That(send.CallTarget.Name, Is.EqualTo("send"));
        Assert.That(send.CallTarget.Target!.Name, Is.EqualTo("client"));
        Assert.That(send.ArgumentExpressions.Select(e => e.Kind),
            Is.EqualTo(new[] { "stringLiteral", "invocation", "namedArgument", "spread" }));
        Assert.That(send.ArgumentExpressions[0].StringValue, Is.EqualTo("hello\nworld"));
        Assert.That(send.ArgumentExpressions[1].Target!.Name, Is.EqualTo("nested"));
        Assert.That(send.ArgumentExpressions[2].Name, Is.EqualTo("label"));
        Assert.That(send.ArgumentExpressions[2].Children.Single().StringValue, Is.EqualTo("value"));
        Assert.That(calls[2].CallTarget!.Target!.Kind, Is.EqualTo("invocation"));
        Assert.That(calls[2].ArgumentExpressions.Select(e => e.Kind), Is.EqualTo(new[] { "literal", "other" }));
        Assert.That(calls[2].ArgumentExpressions[0].StringValue, Is.Null, "Bytes are not decoded string literals.");
        Assert.That(file.ParseErrors, Is.Empty);
    }

    [Test]
    public void Parameters_PreserveAnnotationsDefaultsVariadicsAndLocations()
    {
        var file = ParseNative("""
            def transform(
                values: list[str],
                maybe: int | None = None,
                /, *args: str, enabled: bool = True, required, **kwargs
            ) -> "Result":
                pass
            """);
        var method = file.Methods.Single();
        Assert.That(method.ReturnType!.Name, Is.EqualTo("Result"));
        Assert.That(method.ReturnType.ToString(), Is.EqualTo("\"Result\""));
        Assert.That(method.Parameters.Select(p => p.Name),
            Is.EqualTo(new[] { "values", "maybe", "args", "enabled", "required", "kwargs" }));
        Assert.That(method.Parameters.Select(p => p.HasDefaultValue), Is.EqualTo(new[] { false, true, false, true, false, false }));
        Assert.That(method.Parameters.Select(p => p.DefaultValueText),
            Is.EqualTo(new string?[] { null, "None", null, "True", null, null }));
        Assert.That(method.Parameters.Select(p => p.Line), Is.EqualTo(new[] { 2, 3, 4, 4, 4, 4 }));
        Assert.That(method.Parameters[0].Type!.ToString(), Is.EqualTo("list[str]"));
        Assert.That(method.Parameters[0].Type!.GenericArguments.Single().Name, Is.EqualTo("str"));
        Assert.That(method.Parameters[1].Type!.ToString(), Is.EqualTo("int | None"));
        Assert.That(method.Parameters[2].IsVariadic, Is.True);
        Assert.That(method.Parameters[5].IsKwargs, Is.True);
        Assert.That(method.Parameters[1].IsOptional, Is.True);
        Assert.That(method.Column, Is.EqualTo(1));
        Assert.That(method.EndLine, Is.EqualTo(6));
    }

    [Test]
    public void Classes_ExposeMembersPropertiesNestingAndPythonFlags()
    {
        var file = ParseNative("""
            @dataclasses.dataclass
            class Container(typing.Protocol):
                '''Class documentation.'''
                __slots__ = ('value',)
                count: int = 0
                _hidden = False
                class Nested:
                    pass
                @property
                def value(self) -> str:
                    return 'value'
                @value.setter
                def value(self, new_value: str):
                    pass
                @staticmethod
                async def run(*args):
                    pass
            class _Private:
                pass
            """);
        var type = (PythonTypeDeclaration)file.Types[0];
        Assert.That(file.ParseErrors, Is.Empty);
        Assert.That(file.Types.Select(t => t.Name), Is.EqualTo(new[] { "Container", "_Private" }));
        Assert.That(type.NestedTypes.Select(t => t.Name), Is.EqualTo(new[] { "Nested" }));
        Assert.That(type.Fields.Select(f => f.Name), Is.EqualTo(new[] { "__slots__", "count", "_hidden" }));
        Assert.That(type.Fields[1].Type!.Name, Is.EqualTo("int"));
        Assert.That(type.Fields[2].IsPrivate, Is.True);
        Assert.That(type.IsDataclass, Is.True);
        Assert.That(type.IsProtocol, Is.True);
        Assert.That(type.HasSlots, Is.True);
        Assert.That(type.HasDocComment, Is.True);
        Assert.That(type.Properties.Single().Name, Is.EqualTo("value"));
        Assert.That(type.Properties.Single().HasGetter, Is.True);
        Assert.That(type.Properties.Single().HasSetter, Is.True);
        Assert.That(type.Methods.Select(m => m.Name), Is.EqualTo(new[] { "value", "value", "run" }));
        Assert.That(type.Methods[2].IsAsync, Is.True);
        Assert.That(type.Methods[2].IsStatic, Is.True);
        Assert.That(file.Types[1].IsPublic, Is.False);
    }

    [Test]
    public void NestedFunctions_DoNotContaminateOuterGeneratorOrStatements()
    {
        var file = ParseNative("""
            def outer():
                def inner():
                    yield make()
                return finish()
            """);
        var outer = (PythonMethodDeclaration)file.Methods.Single(m => m.Name == "outer");
        var inner = (PythonMethodDeclaration)file.Methods.Single(m => m.Name == "inner");
        Assert.That(outer.IsGenerator, Is.False);
        Assert.That(inner.IsGenerator, Is.True);
        Assert.That(outer.Statements.Where(s => s.Kind == "call").Select(s => s.MemberName), Is.EqualTo(new[] { "finish" }));
        Assert.That(inner.Statements.Where(s => s.Kind == "call").Select(s => s.MemberName), Is.EqualTo(new[] { "make" }));
    }

    [Test]
    public void ReceiverAssignments_ExposeInstanceFieldsButNotOtherObjectsOrNestedFunctions()
    {
        var file = ParseNative("""
            class C:
                def __init__(self):
                    self.value: int = 7
                    other.not_a_member = 1
                    def nested():
                        self.also_not_declared_here = 1
                @classmethod
                def configure(cls):
                    cls.shared = True
                @staticmethod
                def helper(value):
                    value.not_a_member = 1
            """);
        var fields = file.Types.Single().Fields;
        Assert.That(fields.Select(f => f.Name), Is.EqualTo(new[] { "value", "shared" }));
        Assert.That(fields[0].Type!.Name, Is.EqualTo("int"));
        Assert.That(fields[0].IsStatic, Is.False);
        Assert.That(fields[1].IsStatic, Is.True);
    }

    [Test]
    public void ConditionalClassMembers_StayInTheirDeclaringClass()
    {
        var file = ParseNative("""
            class Container:
                if enabled():
                    value: int = 1
                    def method(self):
                        invoked()
                    class Nested:
                        pass
            """);
        var type = file.Types.Single();
        Assert.That(file.Methods, Is.Empty, "A conditional class member is not a module function.");
        Assert.That(type.Methods.Select(m => m.Name), Is.EqualTo(new[] { "method" }));
        Assert.That(type.NestedTypes.Select(t => t.Name), Is.EqualTo(new[] { "Nested" }));
        Assert.That(type.Fields.Select(f => f.Name), Is.EqualTo(new[] { "value" }));
        Assert.That(file.Statements.Single(s => s.MemberName == "invoked").Method, Is.SameAs(type.Methods.Single()));
        Assert.That(file.Statements.Single(s => s.MemberName == "enabled").Parent!.Kind, Is.EqualTo("if"));
    }

    [Test]
    public void Imports_PreserveAliasRelativeLevelAndMemberNames()
    {
        var file = ParseNative("""
            import numpy as np
            from ..tools import Reader as R, Writer
            from . import *
            """);
        Assert.That(file.Usings, Is.EqualTo(new[] { "numpy", "..tools", "." }));
        Assert.That(file.Imports.Select(i => i.Module), Is.EqualTo(new[] { "numpy", "tools", "tools", "" }));
        Assert.That(file.Imports.Select(i => i.Name), Is.EqualTo(new string?[] { null, "Reader", "Writer", "*" }));
        Assert.That(file.Imports.Select(i => i.Alias), Is.EqualTo(new string?[] { "np", "R", null, null }));
        Assert.That(file.Imports.Select(i => i.RelativeLevel), Is.EqualTo(new[] { 0, 2, 2, 1 }));
        Assert.That(file.Imports[3].IsWildcard, Is.True);
    }

    [Test]
    public void Calls_InDecoratorsDefaultsAndLambdasAreNotDiscarded()
    {
        var file = ParseNative("""
            @decorate(config())
            def f(value=default()):
                result = lambda: compute()
            class C(base()):
                @decorate(other())
                def method(self, value=another()):
                    pass
                @nested_decorator()
                class Nested(nested_base(), metaclass=metaclass_factory()):
                    pass
            """);
        Assert.That(file.Statements.Where(s => s.Kind == "call").Select(s => s.MemberName).Order(),
            Is.EqualTo(new[] { "another", "base", "compute", "config", "decorate", "decorate", "default",
                "metaclass_factory", "nested_base", "nested_decorator", "other" }));
        Assert.That(file.Statements.Single(s => s.MemberName == "default").Method, Is.Null);
        Assert.That(file.Statements.Single(s => s.MemberName == "compute").Method!.Name, Is.EqualTo("f"));
    }

    [TestCase("x = [")]
    [TestCase("if :\n    pass\n")]
    [TestCase("def f():\nreturn 1\n")]
    [TestCase("return 1\n")]
    [TestCase("yield 1\n")]
    public void MalformedSource_ReportsCompilerDiagnostics(string source)
    {
        var file = ParseNative(source);
        Assert.That(file.ParseErrors, Has.Count.EqualTo(1));
        Assert.That(file.ParseErrors[0], Does.StartWith("parity.py(").And.Contain("): error:"));
        Assert.That(file.Types, Is.Empty);
    }

    [Test]
    public void StubsAndUnicodeColumns_AreSupportedWithoutExecutingTarget()
    {
        var file = ParseNative("""
            import definitely_not_an_installed_module
            def identity(value: str = 'x') -> str: ...
            x = '😀'; client.send('ok')
            raise RuntimeError('must never execute')
            """, "api.pyi");
        Assert.That(file.ParseErrors, Is.Empty);
        Assert.That(new PythonSourceParser().Extensions, Does.Contain(".pyi"));
        Assert.That(file.Methods.Single().Name, Is.EqualTo("identity"));
        var call = file.Statements.Single(s => s.MemberName == "send");
        Assert.That(call.Column, Is.EqualTo(11));
        Assert.That(call.EndColumn, Is.EqualTo(28));
    }

    [Test]
    public void UnavailableInterpreter_WarnsOnceAndUsesDistinctCacheIdentity()
    {
        ProviderWarnings.Clear();
        var parser = new PythonSourceParser(searchPath: "");
        var identity = parser.GetCacheIdentity("fallback.py");
        var first = parser.Parse("fallback.py", "def useful(value: int = 7) -> 'Result':\n    return called()\n")!;
        parser.Parse("another.py", "class Other:\n    pass\n");
        Assert.That(identity, Does.EndWith(":simpler"));
        Assert.That(first.Methods.Single().Name, Is.EqualTo("useful"));
        Assert.That(first.Methods.Single().ReturnType!.Name, Is.EqualTo("Result"));
        Assert.That(first.Methods.Single().Parameters.Single().DefaultValueText, Is.EqualTo("7"));
        Assert.That(first.Methods.Single().Parameters.Single().Line, Is.EqualTo(1));
        Assert.That(first.Statements.Single().MemberName, Is.EqualTo("called"));
        var warnings = ProviderWarnings.Drain();
        Assert.That(warnings, Has.Count.EqualTo(1));
        Assert.That(warnings[0], Does.Contain("simpler Python analysis"));
    }

    [Test]
    public void Fallback_DetectsCallsAcrossContextsAndDoesNotLeakNestedYield()
    {
        var parser = new PythonSourceParser(useToolchain: false);
        var file = parser.Parse("fallback.py", """
            def outer():
                def generator():
                    yield nested()
                direct(inner())
                x = assigned()
                return returned()
                assert checked()
                with opened():
                    body()
            """)!;
        Assert.That(file.Statements.Where(s => s.Kind == "call").Select(s => s.MemberName),
            Is.EqualTo(new[] { "nested", "direct", "inner", "assigned", "returned", "checked", "opened", "body" }));
        Assert.That(((PythonMethodDeclaration)file.Methods.Single(m => m.Name == "outer")).IsGenerator, Is.False);
    }

    [TestCase("x = [")]
    [TestCase("if :\n    pass\n")]
    public void Fallback_ReportsBasicMalformedSource(string source)
    {
        var file = new PythonSourceParser(useToolchain: false).Parse("fallback.py", source)!;
        Assert.That(file.ParseErrors, Has.Count.EqualTo(1));
        Assert.That(file.ParseErrors[0], Does.Contain("fallback.py(1,"));
    }

    [Test]
    public void Fallback_PreservesImportsNestingAndVisibilityConventions()
    {
        var file = new PythonSourceParser(useToolchain: false).Parse("fallback.py", """
            import numpy as np
            from ..tools import Reader as R
            class _Private:
                class Nested:
                    def __str__(self):
                        return 'value'
            """)!;
        Assert.That(file.Usings, Is.EqualTo(new[] { "numpy", "..tools" }));
        Assert.That(file.Imports.Select(i => i.Alias), Is.EqualTo(new[] { "np", "R" }));
        Assert.That(file.Imports[1].RelativeLevel, Is.EqualTo(2));
        Assert.That(file.Types.Single().IsPublic, Is.False);
        Assert.That(file.Types.Single().NestedTypes.Single().Name, Is.EqualTo("Nested"));
        Assert.That(file.Types.Single().NestedTypes.Single().Methods.Single().IsPublic, Is.True);
    }

    [Test]
    public void Fallback_Issue083_ClassCompoundStatementTerminatesAndKeepsFollowingMethods()
    {
        var parse = Task.Run(() => new PythonSourceParser(useToolchain: false).Parse("fallback.py", """
            class C:
                if True:
                    value = 1
                def method(self):
                    useful()
            """)!);
        Assert.That(parse.Wait(TimeSpan.FromSeconds(3)), Is.True, "The simpler parser must advance past class-level compound statements.");
        Assert.That(parse.Result.Types.Select(t => t.Name), Is.EqualTo(new[] { "C" }));
        Assert.That(parse.Result.Types.Single().Methods.Select(m => m.Name), Is.EqualTo(new[] { "method" }));
        Assert.That(parse.Result.Statements.Select(s => s.MemberName), Is.EqualTo(new[] { "useful" }));
    }

    [Test]
    public void CacheRoundTrip_PreservesPythonFactsStructuredExpressionsAndCanonicalOwnership()
    {
        var source = ParseNative("""
            from ..tools import Reader as R
            def f(value: list[str] = ['default']):
                if value:
                    direct(nested('decoded\ntext')); direct(nested('second'))
            class Container:
                class Nested:
                    @staticmethod
                    def method():
                        called()
            """);
        PythonTypeDeclaration.RegisterCacheFactory();
        PythonMethodDeclaration.RegisterCacheFactory();
        PythonStatementInfo.RegisterCacheFactory();
        var path = Path.Combine(Path.GetTempPath(), "cop-python-cache-" + Guid.NewGuid().ToString("N") + ".bin");
        try
        {
            SourceCacheSerializer.Save(path, [1, 2, 3], [source]);
            var warm = SourceCacheSerializer.TryLoad(path, [1, 2, 3], typeof(PythonSourceParser).Assembly)!.Single();
            foreach (var file in new[] { source, warm })
            {
                CodeCollectionBuilder.LinkReferences(file);
                var function = (PythonMethodDeclaration)file.Methods.Single();
                var calls = file.Statements.Where(s => s.Kind == "call").ToList();
                Assert.That(calls.Select(s => s.MemberName), Is.EqualTo(new[] { "direct", "nested", "direct", "nested", "called" }));
                Assert.That(calls[0], Is.Not.SameAs(calls[2]), "Distinct same-line calls must not collapse.");
                Assert.That(calls[0].Method, Is.SameAs(function));
                Assert.That(calls[1].Parent, Is.SameAs(calls[0]));
                Assert.That(calls[3].Parent, Is.SameAs(calls[2]));
                Assert.That(calls[0].Parent, Is.SameAs(calls[2].Parent));
                Assert.That(function.Statements.Where(s => s.Kind == "call"), Is.EqualTo(calls.Take(4)));
                Assert.That(calls[1].ArgumentExpressions.Single().StringValue, Is.EqualTo("decoded\ntext"));
                Assert.That(calls[0].ArgumentExpressions.Single().Kind, Is.EqualTo("invocation"));
                Assert.That(calls[0].CallTarget!.Name, Is.EqualTo("direct"));
                Assert.That(calls[0].Column, Is.EqualTo(9));
                Assert.That(function.Parameters.Single().DefaultValueText, Is.EqualTo("['default']"));
                Assert.That(function.Parameters.Single().IsOptional, Is.True);
                Assert.That(file.Imports.Single().Alias, Is.EqualTo("R"));
                Assert.That(file.Imports.Single().RelativeLevel, Is.EqualTo(2));
                Assert.That(file.Imports.Single().Column, Is.EqualTo(1));
                var nested = file.Types.Single().NestedTypes.Single();
                Assert.That(nested.File, Is.SameAs(file));
                Assert.That(((PythonMethodDeclaration)nested.Methods.Single()).IsStaticmethod, Is.True);
                Assert.That(calls[4].Method, Is.SameAs(nested.Methods.Single()));
                Assert.That(calls.All(c => ReferenceEquals(c.File, file)), Is.True);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }
}
