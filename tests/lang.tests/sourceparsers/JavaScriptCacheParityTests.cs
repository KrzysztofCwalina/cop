using Cop.Providers;
using Cop.Providers.SourceModel;
using Cop.Providers.SourceParsers;
using NUnit.Framework;

namespace Cop.Tests.Lang.SourceParsers;

[TestFixture]
public class JavaScriptCacheParityTests
{
    [TestCase(false)]
    [TestCase(true)]
    public void CompilerBackedQueries_DoNotWriteSourceOnlyDiskCaches(bool deferred)
    {
        var root = Path.Combine(Path.GetTempPath(), $"cop-js-native-cache-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var sourcePath = Path.Combine(root, "sample.ts");
            if (JavaScriptToolchain.Discover(sourcePath) is null)
                Assert.Ignore("Native cache tests require an installed TypeScript compiler.");
            File.WriteAllText(sourcePath, "export function run() { return log('ok'); }");
            var registry = new SourceParserRegistry();
            registry.Register(new JavaScriptSourceParser());
            var moduleId = typeof(JavaScriptSourceParser).Assembly.ManifestModule.ModuleVersionId;
            var scopeKey = !deferred ? root : OperatingSystem.IsWindows()
                ? root + "\n" + moduleId + "\n\ndeferred-bundle"
                : root + "\nfile\n" + sourcePath + "\n" + moduleId;
            var cachePath = (string)typeof(CodeCollectionBuilder).GetMethod("GetCachePath",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                .Invoke(null, [scopeKey])!;
            for (int run = 0; run < 2; run++)
            {
                using var scope = new Cop.Core.ToolchainExecutionScope();
                var files = deferred
                    ? new DeferredSourceSnapshot(registry, new() { RootPath = root })
                        .Query(new() { Collection = "Files" }).Collections["Files"].Cast<SourceFile>()
                    : CodeCollectionBuilder.CollectAndParse(registry, new() { RootPath = root })["Files"].Cast<SourceFile>();
                var file = files.Single();
                Assert.That(file.Methods.Select(m => m.Name), Is.EqualTo(new[] { "run" }));
                Assert.That(file.Statements.Where(s => s.Kind == "call").Select(s => s.MemberName),
                    Is.EqualTo(new[] { "log" }));
                Assert.That(File.Exists(cachePath), Is.False,
                    "Compiler semantics depend on imports, libraries and configuration, not just this source.");
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [Test]
    public void JavaScriptSubtypeCacheRoundTrip_RetainsParsedCallAndValueExpressionsAndSpans()
    {
        JavaScriptStatementInfo.RegisterCacheFactory();
        var call = new JavaScriptStatementInfo("call", [], null, "log", ["'ab'"], 2, false)
        {
            CallTarget = new("memberAccess")
            {
                Name = "log", Target = new("identifier") { Name = "console" }
            },
            ArgumentExpressions = [new("stringLiteral") { StringValue = "ab" }],
            ValueExpression = new("numericLiteral") { Name = "42" },
            Column = 3, EndLine = 2, EndColumn = 20
        };
        var file = new SourceFile("sample.js", "javascript", [], [call], "\n  console.log('ab');");
        var serializer = typeof(CodeCollectionBuilder).Assembly.GetType("Cop.Providers.SourceCacheSerializer", true)!;
        var path = Path.Combine(Path.GetTempPath(), $"cop-js-cache-{Guid.NewGuid():N}.bin");
        try
        {
            serializer.GetMethod("Save", [typeof(string), typeof(byte[]), typeof(List<SourceFile>)])!
                .Invoke(null, [path, new byte[] { 1, 2 }, new List<SourceFile> { file }]);
            var files = (List<SourceFile>?)serializer.GetMethod("TryLoad", [typeof(string), typeof(byte[])])!
                .Invoke(null, [path, new byte[] { 1, 2 }]);
            Assert.That(files, Has.Count.EqualTo(1));
            var restored = files!.Single().Statements.Single();
            Assert.That(restored, Is.TypeOf<JavaScriptStatementInfo>());
            Assert.That(restored.CallTarget!.Kind, Is.EqualTo("memberAccess"));
            Assert.That(restored.CallTarget!.Target!.Name, Is.EqualTo("console"));
            Assert.That(restored.ArgumentExpressions.Single().StringValue, Is.EqualTo("ab"));
            Assert.That(restored.ValueExpression!.Name, Is.EqualTo("42"));
            Assert.That((restored.Column, restored.EndLine, restored.EndColumn), Is.EqualTo((3, 2, 20)));
        }
        finally { File.Delete(path); }
    }
}
