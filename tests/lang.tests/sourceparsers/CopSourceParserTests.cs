using Cop.Core;
using Cop.Providers;
using Cop.Providers.SourceModel;
using Cop.Providers.SourceParsers;
using NUnit.Framework;

namespace Cop.Tests.SourceParsers;

[TestFixture]
public class CopSourceParserTests
{
    private const string Source = """
        ## Native function.
        export function native(values : [string]) : [string] => intrinsic
        function ordinary(value : string) : string => value
        predicate positive(value : int) => value > 0
        function guarded(value : int) : int : (value > 0) => value
        command MAIN = print('ok')
        """;

    [Test]
    public void Parse_ExposesCopFunctionsFromAst()
    {
        var file = new CopSourceParser().Parse("sample.cop", Source)!;
        Assert.That(file.Methods.Select(m => m.Name),
            Is.EqualTo(new[] { "native", "ordinary", "positive", "guarded", "MAIN" }));
        Assert.That(file.Methods, Has.All.TypeOf<CopFunctionDeclaration>());
        var functions = file.Methods.Cast<CopFunctionDeclaration>().ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(functions.Select(f => f.IsIntrinsic),
                Is.EqualTo(new[] { true, false, false, false, false }));
            Assert.That(functions.Select(f => f.IsPredicate),
                Is.EqualTo(new[] { false, false, true, false, false }));
            Assert.That(functions.Select(f => f.IsCommand),
                Is.EqualTo(new[] { false, false, false, false, true }));
            Assert.That(functions.Select(f => f.HasGuard),
                Is.EqualTo(new[] { false, false, false, true, false }));
            Assert.That(functions.Select(f => f.IsExported),
                Is.EqualTo(new[] { true, false, false, false, false }));
            Assert.That(functions[0].Line, Is.EqualTo(2));
            Assert.That(functions[0].HasDocComment, Is.True);
            Assert.That(functions[0].DocComment, Is.EqualTo("Native function."));
            Assert.That(functions[0].Parameters.Single().Name, Is.EqualTo("values"));
            Assert.That(functions[0].Parameters.Single().Type?.OriginalText, Is.EqualTo("[string]"));
            Assert.That(functions[0].ReturnType?.OriginalText, Is.EqualTo("[string]"));
            Assert.That(functions[2].ReturnType?.Name, Is.EqualTo("bool"));
            Assert.That(functions[4].Statements.Single().MemberName, Is.EqualTo("print"));
            Assert.That(functions[4].Statements.Single().Method, Is.SameAs(functions[4]));
        });

        var collections = CodeCollectionBuilder.ExtractCollections([file], "Methods");
        Assert.That(collections["Methods"], Is.EqualTo(file.Methods));
        Assert.That(file.Statements.Count(s => s.Kind == "declaration"), Is.EqualTo(4),
            "Keep the existing declaration statement surface for compatibility.");
    }

    [Test]
    public void SourceCache_RoundTripsCopFactsAndFunctionCallOwnership()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cop-ast-cache-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "cache.bin");
        try
        {
            var source = new CopSourceParser().Parse("sample.cop", Source)!;
            SourceCacheSerializer.Save(path, [1, 2, 3], [source]);
            var loaded = SourceCacheSerializer.TryLoad(path, [1, 2, 3])!;

            Assert.That(loaded, Has.Count.EqualTo(1));
            Assert.That(loaded[0].Methods, Has.Count.EqualTo(5));
            Assert.That(loaded[0].Methods, Has.All.TypeOf<CopFunctionDeclaration>());
            var functions = loaded[0].Methods.Cast<CopFunctionDeclaration>().ToArray();
            Assert.Multiple(() =>
            {
                Assert.That(functions[0].IsIntrinsic, Is.True);
                Assert.That(functions[0].IsExported, Is.True);
                Assert.That(functions[0].DocComment, Is.EqualTo("Native function."));
                Assert.That(functions[0].ReturnType?.OriginalText, Is.EqualTo("[string]"));
                Assert.That(functions[2].IsPredicate, Is.True);
                Assert.That(functions[3].HasGuard, Is.True);
                Assert.That(functions[4].IsCommand, Is.True);
                Assert.That(functions[4].Statements, Has.Count.EqualTo(1));
                Assert.That(functions[4].Statements[0].Method, Is.SameAs(functions[4]));
                Assert.That(loaded[0].Statements.Single(s => s.Kind == "call"),
                    Is.SameAs(functions[4].Statements[0]));
            });
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void Provider_LinksFunctionAndCallLocations_OnColdAndCachedQueries()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cop-ast-query-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "sample.cop"), Source);
            var provider = new CopProvider();
            for (int run = 0; run < 2; run++)
            {
                var collections = (Dictionary<string, List<object>>)provider.Query(
                    new ProviderQuery { RootPath = directory })!;
                var functions = collections["Methods"].Cast<CopFunctionDeclaration>().ToArray();
                Assert.That(functions, Has.Length.EqualTo(5));
                Assert.That(functions.Select(f => f.File?.Path),
                    Is.EqualTo(Enumerable.Repeat("sample.cop", 5)));
                Assert.That(functions[0].File?.FullPath, Is.EqualTo(Path.Combine(directory, "sample.cop")));
                Assert.That(functions[4].Statements[0].File, Is.SameAs(functions[4].File));
                Assert.That(collections["Calls"].Cast<StatementInfo>().Single().Method,
                    Is.SameAs(functions[4]));
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
