using System.Reflection;
using Cop.Core;
using Cop.Providers;
using Cop.Providers.SourceModel;
using Cop.Providers.SourceParsers;
using NUnit.Framework;

namespace Cop.Tests.Lang.SourceParsers;

[TestFixture]
public class RustParityTests
{
    // These controls specify the built-in lexer/parser, including its recovery limits.
    private static async Task<SourceFile> ParseAsync(
        string text, CancellationToken cancellationToken = default) =>
        (await new RustSourceParser(bundleFactory: () => null)
            .ParseAsync("fixture.rs", text, cancellationToken: cancellationToken))!;

    [Test]
    public async Task NestedControlFlowAndInvocations_HaveExactStructureAndDecodedArguments()
    {
        var file = (await ParseAsync("""
            fn inspect(items: &[i32]) {
                if ready() {
                    for item in items.iter() {
                        while active(item) {
                            log(format!("line\n\u{41}\x42", read(r#"raw\text"#))).flush();
                        }
                    }
                } else {
                    return fallback();
                }
            }
            """, cancellationToken: CancellationToken.None));
        Assert.That(file.ParseErrors, Is.Empty);
        Assert.That(file.Statements.Select(s => s.Kind), Is.EqualTo(new[]
        { "if", "call", "for", "call", "while", "call", "call", "call", "call", "call", "else", "return", "call" }));
        Assert.That(file.Statements.Where(s => s.Kind == "call").Select(s => s.MemberName),
            Is.EqualTo(new[] { "ready", "iter", "active", "log", "flush", "format!", "read", "fallback" }));
        var log = file.Statements.Single(s => s.MemberName == "log");
        var format = file.Statements.Single(s => s.MemberName == "format!");
        var read = file.Statements.Single(s => s.MemberName == "read");
        var flush = file.Statements.Single(s => s.MemberName == "flush");
        Assert.That(format.Parent, Is.SameAs(log));
        Assert.That(read.Parent, Is.SameAs(format));
        Assert.That(log.Parent, Is.SameAs(flush));
        Assert.That(flush.GetAncestors().Select(s => s.Kind), Is.EqualTo(new[] { "while", "for", "if" }));
        Assert.That(log.ArgumentExpressions[0].Kind, Is.EqualTo("macroInvocation"));
        Assert.That(format.ArgumentExpressions[0].StringValue, Is.EqualTo("line\nAB"));
        Assert.That(read.ArgumentExpressions[0].StringValue, Is.EqualTo(@"raw\text"));
        Assert.That(flush.CallTarget?.Kind, Is.EqualTo("memberAccess"));
        Assert.That(flush.CallTarget?.Target?.Kind, Is.EqualTo("invocation"));
        Assert.That((await file.Statements.ToAsyncEnumerable().AllAsync(async (Cop.Providers.SourceModel.StatementInfo s, CancellationToken cancellationToken) => ReferenceEquals(s.Method, (await AsyncSourceFacts.MembersAsync(file.Types.Single(), CancellationToken.None)).Methods.Single()))), Is.True);
    }

    [Test]
    public async Task PatternsPayloadsGenericsAliasesAndSpans_AreNotLost()
    {
        var file = (await ParseAsync("""
            pub struct Packet<T>(pub T, pub(crate) std::vec::Vec<String>);
            pub enum Message<T> { Data(T), Named { value: T }, Empty }
            pub type Messages<T> = std::vec::Vec<Message<T>>;
            pub extern "C" fn inspect<'a, T, const N: usize>(
                (left, right): (i32, i32),
                [first, ..]: [u8; 2],
                text: &'a str,
                _: T,
            ) -> std::option::Option<T> { None }
            """, cancellationToken: CancellationToken.None));
        Assert.That(file.ParseErrors, Is.Empty);
        var packet = file.Types.Single(t => t.Name == "Packet");
        Assert.That(packet.TypeParameters, Is.EqualTo(new[] { "T" }));
        Assert.That(packet.Fields.Select(f => f.Name), Is.EqualTo(new[] { "0", "1" }));
        Assert.That(packet.Fields.Select(f => f.Modifiers), Is.EqualTo(new[] { Modifier.Public, Modifier.Internal }));
        Assert.That(packet.Fields[1].Type?.Name, Is.EqualTo("Vec"));
        Assert.That(packet.Fields[1].Type?.Namespace, Is.EqualTo("std::vec"));
        Assert.That(packet.Fields[1].Type?.GenericArguments.Single().Name, Is.EqualTo("String"));
        var message = file.Types.Single(t => t.Name == "Message");
        Assert.That(message.NestedTypes.Select(t => t.Name), Is.EqualTo(new[] { "Data", "Named" }));
        Assert.That(message.NestedTypes.SelectMany(t => t.Fields).Select(f => f.Name), Is.EqualTo(new[] { "0", "value" }));
        var alias = file.Types.Single(t => t.Name == "Messages");
        Assert.That(alias.Kind, Is.EqualTo(TypeKind.Alias));
        Assert.That(alias.AliasTarget?.GenericArguments.Single().OriginalText, Is.EqualTo("Message<T>"));
        var method = (RustMethodDeclaration)(await AsyncSourceFacts.MembersAsync(file.Types.Single(t => t.Name.EndsWith("(functions)")), CancellationToken.None)).Methods.Single();
        Assert.That(method.IsExtern, Is.True);
        Assert.That(method.IsPublic, Is.True);
        Assert.That(method.IsPrivate, Is.False);
        Assert.That(method.TypeParameters, Is.EqualTo(new[] { "'a", "T", "N" }));
        Assert.That(method.Parameters.Select(p => p.Name), Is.EqualTo(new[] { "(left, right)", "[first, ..]", "text", "_" }));
        Assert.That(method.Parameters.Select(p => p.Line), Is.EqualTo(new[] { 5, 6, 7, 8 }));
        Assert.That(method.Parameters[0].Column, Is.EqualTo(5));
        Assert.That(method.ReturnType?.Name, Is.EqualTo("Option"));
        Assert.That(method.ReturnType?.GenericArguments.Single().Name, Is.EqualTo("T"));
        Assert.That(method.EndLine, Is.EqualTo(9));
    }

    [Test]
    public async Task ImplOwnership_IsModuleScopedAndNewRemainsAnOrdinaryMethod()
    {
        var file = (await ParseAsync("""
            mod a {
                pub struct Same;
                impl Same { pub fn new() -> Self { Same } }
            }
            mod b {
                pub struct Same;
                impl crate::b::Same { pub(crate) fn update(&self) { work(); } }
            }
            impl std::fmt::Debug for external::Same {
                fn fmt(&self) { other(); }
            }
            """, cancellationToken: CancellationToken.None));
        Assert.That(file.ParseErrors, Is.Empty);
        var declared = file.Types.Where(t => t.Name == "Same").ToList();
        Assert.That(declared, Has.Count.EqualTo(2));
        Assert.That(declared.Select(t => t.Namespace), Is.EqualTo(new[] { "a", "b" }));
        Assert.That((await AsyncSourceFacts.MembersAsync(declared[0], CancellationToken.None)).Methods.Select(m => m.Name), Is.EqualTo(new[] { "new" }));
        Assert.That((await AsyncSourceFacts.MembersAsync(declared[1], CancellationToken.None)).Methods.Select(m => m.Name), Is.EqualTo(new[] { "update" }));
        Assert.That((await AsyncSourceFacts.MembersAsync(declared[1], CancellationToken.None)).Methods[0].IsInternal, Is.True);
        Assert.That((await AsyncSourceFacts.MembersAsync(declared[1], CancellationToken.None)).Methods[0].IsPublic, Is.False);
        Assert.That(declared.SelectMany(t => t.Constructors), Is.Empty);
        var external = file.Types.Single(t => t.Name.StartsWith("external::Same"));
        Assert.That(external.BaseTypes, Is.EqualTo(new[] { "std::fmt::Debug" }));
        Assert.That((await AsyncSourceFacts.MembersAsync(external, CancellationToken.None)).Methods.Single().Name, Is.EqualTo("fmt"));
    }

    [Test]
    public async Task MacroDistinctionAndTokenBoundaries_ArePreserved()
    {
        var file = (await ParseAsync("""
            /* first
               second
               third */
            #[doc = "contains ] brackets"]
            struct S;
            fn run() {
                for n in 0..10 { send(n); }
                for n in 0..=2 { send(n); }
                panic!("boom {}", nested());
                panic("ordinary");
                todo!();
                std::unreachable!();
                let c = '\u{1f600}';
                let bytes = b"not a Rust string";
                take(r"ends at closing quote", "escaped\\\"quote", b"bytes", c"c string");
            }
            """, cancellationToken: CancellationToken.None));
        Assert.That(file.ParseErrors, Is.Empty);
        Assert.That(file.CommentLines.Order(), Is.EqualTo(new[] { 1, 2, 3 }));
        Assert.That(file.Statements.Count(s => s.Kind == "for"), Is.EqualTo(2));
        Assert.That(file.Statements.Count(s => s.MemberName == "send"), Is.EqualTo(2));
        Assert.That(file.Statements.OfType<RustStatementInfo>().Where(s => s.IsPanic).Select(s => s.MemberName),
            Is.EqualTo(new[] { "panic!", "todo!", "unreachable!" }));
        var normal = (RustStatementInfo)file.Statements.Single(s => s.MemberName == "panic");
        Assert.That(normal.IsMacroCall, Is.False);
        Assert.That(normal.IsPanic, Is.False);
        var take = file.Statements.Single(s => s.MemberName == "take");
        Assert.That(take.ArgumentExpressions.Select(a => a.StringValue),
            Is.EqualTo(new string?[] { "ends at closing quote", "escaped\\\"quote", null, null }));
        Assert.That(file.Statements.Single(s => s.MemberName == "nested").Parent?.MemberName, Is.EqualTo("panic!"));
    }

    [Test]
    public async Task MatchGuardsLetElseClosuresAndTurbofish_ExposeOnlyRealCalls()
    {
        var file = (await ParseAsync("""
            fn inspect(input: Option<i32>) {
                let Some(value) = input else { return fallback(); };
                match value {
                    Some(x) if ready(x) => run::<i32>(x),
                    None => { stop(); },
                    _ => queue.iter().for_each(|item| consume(item)),
                }
                Vec::<String>::new().push(make::<String>());
            }
            """, cancellationToken: CancellationToken.None));
        Assert.That(file.ParseErrors, Is.Empty);
        Assert.That(file.Statements.Where(s => s.Kind == "call").Select(s => s.MemberName),
            Is.EqualTo(new[] { "fallback", "ready", "run", "stop", "iter", "for_each", "consume", "new", "push", "make" }));
        Assert.That(file.Statements.Count(s => s.Kind == "switch"), Is.EqualTo(1));
        Assert.That(file.Statements.Single(s => s.MemberName == "consume").Parent?.MemberName, Is.EqualTo("for_each"));
        Assert.That(file.Statements.Single(s => s.MemberName == "new").CallTarget?.Target?.Name, Is.EqualTo("Vec"));
        Assert.That(file.Statements.Any(s => s.MemberName == "Some"), Is.False);
    }

    [TestCase("fn f() { call([)]; }", 2)]
    [TestCase("fn f() { call({]); }", 1)]
    public async Task MismatchedBalancedDelimiters_AreReported(string text, int mismatches)
    {
        var file = (await ParseAsync(text, cancellationToken: CancellationToken.None));
        Assert.That(file.ParseErrors.Count(e => e.Contains("mismatched")), Is.EqualTo(mismatches));
        Assert.That((await ParseAsync("fn f() { call([1]); }", cancellationToken: CancellationToken.None)).ParseErrors, Is.Empty);
    }

    [Test]
    public async Task RawStringAtEof_IsNotReportedAsUnterminated()
    {
        Assert.That((await ParseAsync("const TEXT: &str = r#\"text\"#", cancellationToken: CancellationToken.None)).ParseErrors, Is.Empty);
        Assert.That((await ParseAsync("const TEXT: &str = r#\"text\"", cancellationToken: CancellationToken.None)).ParseErrors,
            Has.Count.EqualTo(1));
    }

    [Test]
    [CancelAfter(5000)]
    public async Task AdversarialNestingTerminatesAndReportsLimits()
    {
        var file = await ParseAsync("fn run() { " + new string('{', 2000) + "work();" + new string('}', 2000) + " after(); }",
            TestContext.CurrentContext.CancellationToken);
        TestContext.CurrentContext.CancellationToken.ThrowIfCancellationRequested();
        Assert.That(file.ParseErrors.Count(e => e.Contains("nesting limit")), Is.EqualTo(1));
        Assert.That(file.Statements.Count(s => s.MemberName == "after"), Is.EqualTo(1));
    }

    [Test]
    public async Task CachedRustSubtypeCopiesKeepExpressionFactsAndSpans()
    {
        var original = (await ParseAsync("fn f() { log(read(\"decoded\\nvalue\")); }", cancellationToken: CancellationToken.None)).Statements.Single(s => s.MemberName == "log");
        var copy = new RustStatementInfo(original);
        Assert.That(copy.CallTarget, Is.SameAs(original.CallTarget));
        Assert.That(copy.ArgumentExpressions, Is.SameAs(original.ArgumentExpressions));
        Assert.That(copy.Column, Is.EqualTo(10));
        Assert.That(copy.EndColumn, Is.EqualTo(37));
    }

    [Test]
    public async Task ExternBlocksNestedFunctionsAndGroupedImports_HaveNoPhantomCalls()
    {
        var file = (await ParseAsync("""
            use std::{io::{self, Read as Input, Write}, fmt::Debug};
            unsafe extern "C" { pub fn foreign(value: i32) -> i32; }
            fn outer() {
                fn inner(value: i32) { inside(value); }
                inner(1);
            }
            """, cancellationToken: CancellationToken.None));
        Assert.That(file.ParseErrors, Is.Empty);
        Assert.That(file.Usings, Is.EqualTo(new[] { "std::io", "std::io::Read", "std::io::Write", "std::fmt::Debug" }));
        Assert.That(file.Imports.Select(i => i.Module), Is.EqualTo(new[] { "std", "std::io", "std::io", "std::fmt" }));
        Assert.That(file.Imports.Single(i => i.Name == "Read").Alias, Is.EqualTo("Input"));
        var methods = await file.Types.ToAsyncEnumerable().SelectMany(AsyncSourceFacts.MethodsAsync).ToListAsync();
        Assert.That(methods.Select(m => m.Name).Order(), Is.EqualTo(new[] { "foreign", "inner", "outer" }));
        Assert.That(((RustMethodDeclaration)methods.Single(m => m.Name == "foreign")).IsExtern, Is.True);
        Assert.That(methods.Single(m => m.Name == "foreign").IsPublic, Is.True);
        Assert.That(((RustMethodDeclaration)methods.Single(m => m.Name == "outer")).IsExtern, Is.False);
        Assert.That(file.Statements.Select(s => s.MemberName), Is.EqualTo(new[] { "inside", "inner" }));
        Assert.That(file.Statements.Single(s => s.MemberName == "inside").Method?.Name, Is.EqualTo("inner"));
        Assert.That(file.Statements.Single(s => s.MemberName == "inner").Method?.Name, Is.EqualTo("outer"));
    }

    [Test]
    public async Task SourceCacheRoundTrip_PreservesRustGraphSubtypesSignaturesAndLocations()
    {
        var file = (await ParseAsync("""
            use std::io::Read as Input;
            pub struct Packet<T>(pub T);
            impl<T> Packet<T> {
                pub unsafe extern "C" fn inspect<'a>(&self, text: &'a str) {
                    if ready() { log(read("decoded\nvalue")); panic!("failed"); }
                }
            }
            """, cancellationToken: CancellationToken.None));
        RustTypeDeclaration.RegisterCacheFactory();
        RustMethodDeclaration.RegisterCacheFactory();
        RustStatementInfo.RegisterCacheFactory();
        var serializer = typeof(CodeCollectionBuilder).Assembly.GetType("Cop.Providers.SourceCacheSerializer", true)!;
        var save = serializer.GetMethod("Save", [typeof(string), typeof(byte[]), typeof(List<SourceFile>)])!;
        var load = serializer.GetMethod("LoadOrDefault", [typeof(string), typeof(byte[]), typeof(Assembly)])!;
        string cache = Path.Combine(Path.GetTempPath(), "cop-rust-roundtrip-" + Guid.NewGuid().ToString("N") + ".bin");
        try
        {
            byte[] fingerprint = [1, 2, 3];
            save.Invoke(null, [cache, fingerprint, new List<SourceFile> { file }]);
            var loaded = (List<SourceFile>)load.Invoke(null, [cache, fingerprint, typeof(RustSourceParser).Assembly])!;
            var warm = loaded.Single();
            Assert.That((await Snapshot(warm)), Is.EqualTo((await Snapshot(file))));
            Assert.That(warm.Imports, Is.EqualTo(file.Imports));
            var method = (RustMethodDeclaration)(await AsyncSourceFacts.MembersAsync(warm.Types.Single(t => t.Name == "Packet"), CancellationToken.None)).Methods.Single();
            Assert.That(method.IsExtern && method.IsUnsafe && method.IsPublic, Is.True);
            Assert.That(warm.Statements.All(s => s is RustStatementInfo), Is.True);
            Assert.That(warm.Statements.All(s => ReferenceEquals(s.Method, method)), Is.True);
            var log = warm.Statements.Single(s => s.MemberName == "log");
            var read = warm.Statements.Single(s => s.MemberName == "read");
            Assert.That(read.Parent, Is.SameAs(log));
            Assert.That(log.Children.Single(), Is.SameAs(read));
            Assert.That(read.ArgumentExpressions.Single().StringValue, Is.EqualTo("decoded\nvalue"));
            Assert.That(((RustStatementInfo)warm.Statements.Single(s => s.MemberName == "panic!")).IsMacroCall, Is.True);
        }
        finally { File.Delete(cache); }
    }

    [Test]
    public async Task ShiftExpressionsAndInitializerValues_KeepNestedCalls()
    {
        var file = (await ParseAsync("""
            fn compute() {
                let shifted = input() << shift();
                flags |= mask();
                return shifted >> amount();
            }
            """, cancellationToken: CancellationToken.None));
        Assert.That(file.ParseErrors, Is.Empty);
        Assert.That(file.Statements.Where(s => s.Kind == "call").Select(s => s.MemberName),
            Is.EqualTo(new[] { "input", "shift", "mask", "amount" }));
        var declaration = file.Statements.Single(s => s.Kind == "declaration");
        Assert.That(declaration.MemberName, Is.EqualTo("shifted"));
        Assert.That(declaration.ValueExpression?.Kind, Is.EqualTo("binary"));
        Assert.That(declaration.ValueExpression?.Name, Is.EqualTo("<<"));
        Assert.That(declaration.Children.Select(s => s.MemberName), Is.EqualTo(new[] { "input", "shift" }));
        var returned = file.Statements.Single(s => s.Kind == "return");
        Assert.That(returned.ValueExpression?.Name, Is.EqualTo(">>"));
        Assert.That(returned.Children.Single().MemberName, Is.EqualTo("amount"));
    }

    [TestCase("type Deep = ", "V<", ">", ";", "type nesting limit")]
    [TestCase("fn f() { root()", ".then()", "", "; after(); }", "nesting limit")]
    [CancelAfter(5000)]
    public async Task AdversarialTypeAndPostfixNesting_IsBounded(string prefix, string open, string close,
        string suffix, string diagnostic)
    {
        string source = prefix + string.Concat(Enumerable.Repeat(open, 2000)) + "T"
            + string.Concat(Enumerable.Repeat(close, 2000)) + suffix;
        if (open == ".then()") source = prefix + string.Concat(Enumerable.Repeat(open, 2000)) + suffix;
        var file = await ParseAsync(source, TestContext.CurrentContext.CancellationToken);
        TestContext.CurrentContext.CancellationToken.ThrowIfCancellationRequested();
        Assert.That(file.ParseErrors.Count(e => e.Contains(diagnostic)), Is.EqualTo(1));
        if (open == ".then()")
            Assert.That(file.Statements.Count(s => s.MemberName == "after"), Is.EqualTo(1));
    }

    [Test]
    public async Task RawIdentifiersUnicodeCharsHexSubtractionAndSelfPaths_AreNotMisclassified()
    {
        var file = (await ParseAsync("#[r#match]\nfn inspect(value: self::Thing) { take('" +
            "\U0001F600" + "', 0x1e - amount()); }", cancellationToken: CancellationToken.None));
        Assert.That(file.ParseErrors, Is.Empty);
        var method = (await AsyncSourceFacts.MembersAsync(file.Types.Single(), CancellationToken.None)).Methods.Single();
        Assert.That(method.Parameters.Single().Name, Is.EqualTo("value"));
        Assert.That(method.Parameters.Single().Type?.OriginalText, Is.EqualTo("self::Thing"));
        Assert.That(file.Statements.Select(s => s.MemberName), Is.EqualTo(new[] { "take", "amount" }));
        var take = file.Statements[0];
        Assert.That(take.ArgumentExpressions, Has.Count.EqualTo(2));
        Assert.That(take.ArgumentExpressions[0].Kind, Is.EqualTo("literal"));
        Assert.That(take.ArgumentExpressions[1].Name, Is.EqualTo("-"));
        Assert.That(file.Statements[1].Parent, Is.SameAs(take));
    }

    private static async Task<string> Snapshot(SourceFile file) => string.Join("\n",
        await file.Types.Select(t => $"{t.GetType().Name}|{t.Name}|{t.Namespace}|{string.Join(",", t.TypeParameters)}|{t.Line}:{t.Column}-{t.EndLine}:{t.EndColumn}").ToAsyncEnumerable()
        .Concat(file.Types.ToAsyncEnumerable().SelectMany(AsyncSourceFacts.MethodsAsync).Select(m =>
            $"{m.GetType().Name}|{m.Name}|{m.Modifiers}|{string.Join(",", m.TypeParameters)}|{m.Line}:{m.Column}-{m.EndLine}:{m.EndColumn}|" +
            string.Join(",", m.Parameters.Select(p => $"{p.Name}:{p.Type}:{p.Line}:{p.Column}"))))
        .Concat(file.Statements.Select(s =>
            $"{s.GetType().Name}|{s.Kind}|{s.MemberName}|{s.Method?.Name}|{s.Parent?.Kind}:{s.Parent?.MemberName}|" +
            $"{s.Line}:{s.Column}-{s.EndLine}:{s.EndColumn}|{s.CallTarget?.Kind}:{s.CallTarget?.Name}|" +
            string.Join(",", s.ArgumentExpressions.Select(a => $"{a.Kind}:{a.StringValue}"))).ToAsyncEnumerable()).ToArrayAsync());
}

[TestFixture, NonParallelizable]
public class RustAnalysisCapabilityTests
{
    [Test]
    public async Task MalformedSourceDiagnosticsAreNotHiddenByRepeatedQueriesOrAnalysisNotices()
    {
        string root = Path.Combine(Path.GetTempPath(), "cop-rust-malformed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "fixture.rs"), "fn f() { work();");
            for (int run = 0; run < 2; run++)
            {
                ProviderErrors.Clear();
                ProviderWarnings.Clear();
                await new RustProvider().QueryAsync(new ProviderQuery { RootPath = root }, cancellationToken: CancellationToken.None);
                Assert.That(ProviderErrors.Drain().Single(), Does.Contain("unclosed '{'"));
                Assert.That(ProviderWarnings.Drain().Single(), Does.Contain("syntax-only"));
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ToolchainPresenceDoesNotPretendToEnableSemanticAnalysis(bool launcherPresent)
    {
        string root = Path.Combine(Path.GetTempPath(), "cop-rust-capabilities-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string? path = Environment.GetEnvironmentVariable("PATH");
        try
        {
            File.WriteAllText(Path.Combine(root, "fixture.rs"), "pub fn inspect() { read(\"known\"); }");
            if (launcherPresent)
            {
                string launcher = Path.Combine(root, OperatingSystem.IsWindows() ? "rustc.exe" : "rustc");
                File.WriteAllText(launcher, "This is a nonfunctional launcher that must never be executed.");
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(launcher, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            }
            Environment.SetEnvironmentVariable("PATH", launcherPresent ? root : "");
            ProviderWarnings.Clear();
            ProviderErrors.Clear();
            var provider = new RustProvider();
            var result = (Dictionary<string, List<object>>)(await provider.QueryAsync(new ProviderQuery { RootPath = root }, cancellationToken: CancellationToken.None))!;
            Assert.That(result["Calls"].Cast<StatementInfo>().Select(s => s.MemberName), Is.EqualTo(new[] { "read" }));
            var notices = ProviderWarnings.Drain();
            Assert.That(notices, Has.Count.EqualTo(1));
            Assert.That(notices[0], Does.Contain("syntax-only"));
            Assert.That(notices[0], Does.Contain("maintained Rust frontend is unavailable"));
            Assert.That(notices[0], Does.Not.Contain("stable Rust tooling"));
            Assert.That(notices[0], Does.Contain("macro expansion, name/type resolution"));
            Assert.That(ProviderErrors.Drain(), Is.Empty);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", path);
            Directory.Delete(root, true);
        }
    }
}
