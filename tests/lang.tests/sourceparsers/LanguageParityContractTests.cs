using System.Text.Json;
using Cop.Core;
using Cop.Providers;
using Cop.Providers.SourceModel;
using Cop.Providers.SourceParsers;
using NUnit.Framework;

namespace Cop.Tests.SourceParsers;

[TestFixture]
public class LanguageParityContractTests
{
    private string _root = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "cop-parity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        ProviderErrors.Clear();
        ProviderWarnings.Clear();
    }

    [TearDown]
    public void TearDown()
    {
        ProviderErrors.Clear();
        ProviderWarnings.Clear();
        Directory.Delete(_root, true);
    }

    public static IEnumerable<TestCaseData> Corpus()
    {
        yield return Case("csharp", ".cs", """
            class Alpha
            {
                string Run(string value = "default")
                {
                    return outer(inner("line\nvalue"));
                }
            }
            """, 1, 3, 5, "class Alpha {");
        yield return Case("python", ".py", """
            class Alpha:
                def Run(self, value='default'):
                    return outer(inner('line\nvalue'))
            """, 1, 2, 3, "class Alpha:\n    def Run(");
        yield return Case("javascript", ".js", """
            class Alpha {
                Run(value = 'default') {
                    return outer(inner('line\nvalue'));
                }
            }
            """, 1, 2, 3, "class Alpha { Run(");
        yield return Case("go", ".go", """
            package fixture
            type Alpha struct {}
            func (a Alpha) Run(value string) string {
                return outer(inner("line\nvalue"))
            }
            """, 2, 3, 4, "package fixture\nfunc Run(");
        yield return Case("java", ".java", """
            class Alpha {
                String Run(String value) {
                    return outer(inner("line\nvalue"));
                }
            }
            """, 1, 2, 3, "class Alpha { void Run(");
        yield return Case("rust", ".rs", """
            pub struct Alpha;
            impl Alpha {
                fn Run(&self, value: &str) -> String {
                    outer(inner("line\nvalue"))
                }
            }
            """, 1, 3, 4, "pub struct Alpha {");
        yield return Case("cop", ".cop", """
            type Alpha = { Value : string }
            function Run(value : string) : string => outer(inner('line\nvalue'))
            """, 1, 2, 2, "type Alpha = { Value :");
    }

    private static TestCaseData Case(string language, string extension, string source,
        int typeLine, int methodLine, int callLine, string malformed) =>
        new TestCaseData(language, extension, source, typeLine, methodLine, callLine, malformed)
            .SetName("{m}_" + language);

    [TestCaseSource(nameof(Corpus))]
    public void CommonFacts_AreExactOnColdAndWarmQueries(string language, string extension, string source,
        int typeLine, int methodLine, int callLine, string malformed)
    {
        File.WriteAllText(Path.Combine(_root, "sample" + extension), source);
        string? cold = null;
        using var scope = new ToolchainExecutionScope();
        for (int run = 0; run < 2; run++)
        {
            ProviderErrors.Clear();
            var file = Read(language);
            Assert.That(file.ParseErrors, Is.Empty);
            Assert.That(ProviderErrors.Drain(), Is.Empty);
            Assert.That(file.Types.Select(type => (type.Name, type.Line)),
                Is.EqualTo(language == "rust"
                    ? new[] { ("Alpha", typeLine), ("Alpha (impl)", 2) }
                    : new[] { ("Alpha", typeLine) }));
            var methods = CodeCollectionBuilder.ExtractCollections([file], "Methods")["Methods"]
                .Cast<MethodDeclaration>().ToArray();
            Assert.That(methods.Select(method => (method.Name, method.Line)),
                Is.EqualTo(new[] { ("Run", methodLine) }), "Functions must not be dropped or duplicated.");
            var method = methods.Single();
            var value = method.Parameters.Single(parameter => parameter.Name == "value");
            if (language is "csharp" or "python" or "javascript")
            {
                Assert.That(value.HasDefaultValue, Is.True);
                Assert.That(value.DefaultValueText, Is.EqualTo(language == "csharp" ? "\"default\"" : "'default'"),
                    "Ordinary parameter defaults must be retained exactly.");
            }
            Assert.That(method.File, Is.SameAs(file));
            var calls = file.Statements.Where(statement => statement.Kind == "call")
                .OrderBy(statement => statement.MemberName, StringComparer.Ordinal).ToArray();
            Assert.That(calls.Select(call => (call.MemberName, call.Line)),
                Is.EqualTo(new[] { ("inner", callLine), ("outer", callLine) }),
                "Both nested expression call sites must be retained, even on the same line.");
            foreach (var call in calls)
            {
                Assert.That(call.File, Is.SameAs(file));
                Assert.That(call.Method, Is.SameAs(method));
                Assert.That(call.IsInMethod, Is.True);
                Assert.That(Descendants(method.Statements).Any(statement => ReferenceEquals(statement, call)), Is.True,
                    "Flat queries and owner navigation must point to the same statement objects.");
                if (call.Parent is not null)
                    Assert.That(call.Parent.Children.Any(child => ReferenceEquals(child, call)), Is.True,
                        "Parent and Children must be inverse edges, not separately deserialized copies.");
                Assert.That(call.CallTarget?.Kind, Is.EqualTo("identifier"));
                Assert.That(call.CallTarget?.Name, Is.EqualTo(call.MemberName));
            }
            var inner = calls[0];
            var outer = calls[1];
            Assert.That(inner.ArgumentExpressions, Has.Count.EqualTo(1));
            Assert.That(inner.ArgumentExpressions[0].Kind, Is.EqualTo("stringLiteral"));
            Assert.That(inner.ArgumentExpressions[0].StringValue, Is.EqualTo("line\nvalue"));
            Assert.That(outer.ArgumentExpressions, Has.Count.EqualTo(1));
            Assert.That(outer.ArgumentExpressions[0].Kind, Is.EqualTo("invocation"));
            Assert.That(outer.ArgumentExpressions[0].Target?.Name, Is.EqualTo("inner"));

            var snapshot = JsonSerializer.Serialize(new
            {
                Types = file.Types.Select(type => new { type.Name, type.Line }),
                Methods = methods.Select(item => new { item.Name, item.Line, item.Parameters, item.ReturnType }),
                Calls = calls.Select(call => new
                {
                    call.Kind, call.MemberName, call.Line, call.Arguments, call.CallTarget,
                    call.ArgumentExpressions, Owner = call.Method?.Name,
                    Ancestors = call.GetAncestors().Select(parent => new { parent.Kind, parent.Line })
                })
            });
            if (run == 0) cold = snapshot;
            else Assert.That(snapshot, Is.EqualTo(cold), "Warm cache must not erase or change any common facts.");
        }
    }

    [TestCaseSource(nameof(Corpus))]
    public void MalformedSource_IsVisibleOnEveryQuery_NotCachedAsSuccess(string language, string extension, string source,
        int typeLine, int methodLine, int callLine, string malformed)
    {
        File.WriteAllText(Path.Combine(_root, "sample" + extension), malformed);
        using var scope = new ToolchainExecutionScope();
        for (int run = 0; run < 2; run++)
        {
            ProviderErrors.Clear();
            _ = Read(language);
            var diagnostics = ProviderErrors.Drain();
            Assert.That(diagnostics, Is.Not.Empty,
                $"{language}: malformed source must not silently produce an empty/partial model.");
            Assert.That(string.Join("\n", diagnostics), Does.Contain("sample" + extension));
        }
    }

    private SourceFile Read(string language)
    {
        DataProvider provider = language switch
        {
            "csharp" => new CSharpProvider(),
            "python" => new PythonProvider(),
            "javascript" => new JavaScriptProvider(),
            "go" => new GoProvider(),
            "java" => new JavaProvider(),
            "rust" => new RustProvider(),
            "cop" => new CopProvider(),
            _ => throw new ArgumentOutOfRangeException(nameof(language))
        };
        var result = (DeferredProviderResult)provider.QueryDeferred(new ProviderQuery { RootPath = _root, Collection = "Files" })!;
        return result.Collections["Files"].Cast<SourceFile>().Single();
    }

    private static IEnumerable<StatementInfo> Descendants(IEnumerable<StatementInfo> roots)
    {
        var seen = new HashSet<StatementInfo>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<StatementInfo>(roots);
        while (pending.TryPop(out var statement))
        {
            if (!seen.Add(statement)) continue;
            yield return statement;
            foreach (var child in statement.Children) pending.Push(child);
        }
    }
}
