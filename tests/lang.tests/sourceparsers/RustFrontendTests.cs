using System.IO.Compression;
using System.Diagnostics;
using Cop.Core;
using Cop.Providers;
using Cop.Providers.SourceModel;
using Cop.Providers.SourceParsers;
using NUnit.Framework;

namespace Cop.Tests.Lang.SourceParsers;

[TestFixture]
[NonParallelizable]
public class RustFrontendTests
{
    private string _root = "";
    private string _frontend = "";
    private string _packages = "";

    [SetUp]
    public void SetUp()
    {
        string? repository = TestContext.CurrentContext.TestDirectory;
        while (repository is not null && !File.Exists(Path.Combine(repository, "cop.sln")))
            repository = Path.GetDirectoryName(repository);
        Assert.That(repository, Is.Not.Null);
        _packages = Path.Combine(repository!, "packages");
        string name = OperatingSystem.IsWindows() ? "cop-rust-frontend.exe" : "cop-rust-frontend";
        _frontend = Path.Combine(repository!, "providers", "rust-provider", "native", "target", "release", name);
        if (!File.Exists(_frontend))
            _frontend = Path.Combine(repository!, "providers", "rust-provider", "native", "target", "debug", name);
        if (!File.Exists(_frontend))
            Assert.Ignore("Native Rust frontend build required: cargo build --locked --manifest-path providers/rust-provider/native/Cargo.toml");
        _root = Path.Combine(Path.GetTempPath(), "cop-rust-semantics-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        ProviderErrors.Clear();
        ProviderWarnings.Clear();
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
        ProviderErrors.Clear();
        ProviderWarnings.Clear();
    }

    private string Write(string relative, string text)
    {
        string path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private async Task<SourceFile> ParseAsync(string path, CancellationToken cancellationToken = default) => (await new RustSourceParser(_frontend, _root,
        Path.Combine(_root, "no-standard-library")).ParseAsync(path, File.ReadAllText(path), cancellationToken: cancellationToken))!;
    private static async Task<CallableReference> Bound(SourceFile file, string name) =>
        (await AsyncSourceFacts.SemanticAsync(file.Statements.Single(s => s.MemberName == name), CancellationToken.None)).ReferencedCallable!;

    [Test]
    public async System.Threading.Tasks.Task MissingRustCompiler_BindsLocalCallsWithoutPreparingUnusableStandardLibrary()
    {
        string path = Write("src/lib.rs",
            "pub struct Alpha; impl Alpha { pub fn get(&self, value: u32) -> u32 { value } } pub fn run() { let a = Alpha; a.get(1); }");
        Write("Cargo.toml", "[package]\nname='app'\nversion='1.0.0'\nedition='2024'");
        string? previous = Environment.GetEnvironmentVariable("PATH");
        int preparations = 0;
        try
        {
            Environment.SetEnvironmentVariable("PATH", "");
            await using var scope = new ToolchainExecutionScope();
            var frontend = new RustFrontend(_frontend, _root, prepareStandardLibrary: () =>
            {
                preparations++;
                throw new AssertionException("Sources cannot be used without a compatible compiler identity.");
            });
            var file = (await frontend.ParseAsync(path, File.ReadAllText(path), cancellationToken: CancellationToken.None));
            var call = file.Statements.Single(s => s.MemberName == "get");
            Assert.That((await AsyncSourceFacts.SemanticAsync(call, CancellationToken.None)).ReceiverType?.Name, Is.EqualTo("Alpha"));
            Assert.That((await AsyncSourceFacts.SemanticAsync(call, CancellationToken.None)).ValueType?.Name, Is.EqualTo("u32"));
            Assert.That((await AsyncSourceFacts.SemanticAsync(call, CancellationToken.None)).ReferencedCallable?.Signature, Is.EqualTo("pub fn get(&self, value: u32) -> u32"));
            Assert.That(preparations, Is.Zero);
            Assert.That(ProviderWarnings.Drain(), Has.Some.Contains("compiler identity is unavailable"));
            Assert.That(ProviderErrors.Drain(), Is.Empty);
        }
        finally { Environment.SetEnvironmentVariable("PATH", previous); }
    }

    [Test]
    public async System.Threading.Tasks.Task MaintainedAst_DeclarationSpansExcludeDocumentationAndRetainAttributes()
    {
        string path = Write("src/lib.rs", """
            /// Type docs.
            pub struct Alpha;
            impl Alpha {
                /// Call docs.
                #[inline]
                pub fn get(&self, value: u32) -> u32 { value }
            }
            /// Run docs.
            pub fn run() { Alpha.get(1); }
            """);
        await using var scope = new ToolchainExecutionScope();
        var file = (await ParseAsync(path, cancellationToken: CancellationToken.None));
        Assert.That(file.ParseErrors, Is.Empty);
        var type = file.Types.Single(t => t.Name == "Alpha");
        var method = (await AsyncSourceFacts.MembersAsync(type, CancellationToken.None)).Methods.Single(m => m.Name == "get");
        var run = await file.Types.ToAsyncEnumerable().SelectMany(AsyncSourceFacts.MethodsAsync).SingleAsync(m => m.Name == "run");
        Assert.Multiple(() =>
        {
            Assert.That((type.Line, type.Column), Is.EqualTo((2, 1)));
            Assert.That(type.HasDocComment, Is.True);
            Assert.That(type.DocComment, Does.Contain("Type docs."));
            Assert.That((method.Line, method.Column, method.EndLine), Is.EqualTo((5, 5, 6)));
            Assert.That(method.Decorators, Is.EqualTo(new[] { "#[inline]" }));
            Assert.That(method.HasDocComment, Is.True);
            Assert.That(method.DocComment, Does.Contain("Call docs."));
            Assert.That((run.Line, run.Column), Is.EqualTo((9, 1)));
            Assert.That(run.HasDocComment, Is.True);
        });
        var callable = (await Bound(file, "get"));
        Assert.That(callable, Is.Not.Null);
        Assert.That((callable.Line, callable.Column, callable.EndLine), Is.EqualTo((5, 5, 6)));
        Assert.That(callable.Signature, Is.EqualTo("pub fn get(&self, value: u32) -> u32"));
    }

    [Test]
    public async System.Threading.Tasks.Task MaintainedAstAndHir_BindAliasesFactoriesTraitMethodsAndGenericSignaturesAcrossFiles()
    {
        Write("Cargo.toml", "[package]\nname='app'\nversion='1.0.0'\nedition='2024'");
        string modelPath = Write("src/model.rs", """
            pub struct Packet<T> { pub value: T }
            pub trait Inspect { fn inspect(&self) -> u32; }
            impl<T> Inspect for Packet<T> { fn inspect(&self) -> u32 { 7 } }
            impl<T> Packet<T> { pub fn value(&self) -> &T { &self.value } }
            pub fn factory() -> Packet<u32> { Packet { value: 7 } }
            """);
        Write("src/unrelated.rs", """
            pub struct Packet;
            impl Packet { pub fn inspect(&self) -> bool { false } }
            pub fn factory() -> Packet { Packet }
            """);
        string path = Write("src/lib.rs", """
            mod model;
            mod unrelated;
            use model::{factory as make, Inspect};
            use unrelated::factory as other;
            fn run() {
                let packet = make();
                packet.inspect();
                packet.value();
                other().inspect();
            }
            """);
        await using var scope = new ToolchainExecutionScope();
        var file = (await ParseAsync(path, cancellationToken: CancellationToken.None));
        Assert.That(file.ParseErrors, Is.Empty);
        Assert.That(file.Statements.Where(s => s.Kind == "call").Select(s => s.MemberName),
            Is.EqualTo(new[] { "make", "inspect", "value", "inspect", "other" }));
        var factory = (await Bound(file, "make"));
        Assert.That(factory, Is.Not.Null);
        Assert.That(factory.Name, Is.EqualTo("factory"));
        Assert.That(factory.Namespace, Is.EqualTo("app::model"));
        Assert.That(factory.ReturnType?.OriginalText, Is.EqualTo("Packet<u32>"));
        var inspections = file.Statements.Where(s => s.MemberName == "inspect").ToList();
        Assert.That((await inspections.ToAsyncEnumerable().Select(async (Cop.Providers.SourceModel.StatementInfo s, CancellationToken cancellationToken) => (await AsyncSourceFacts.SemanticAsync(s, CancellationToken.None)).ReceiverType?.OriginalText).ToArrayAsync()),
            Is.EqualTo(new[] { "Packet<u32>", "Packet" }));
        Assert.That((await inspections.ToAsyncEnumerable().Select(async (Cop.Providers.SourceModel.StatementInfo s, CancellationToken cancellationToken) => (await AsyncSourceFacts.SemanticAsync(s, CancellationToken.None)).ValueType?.OriginalText).ToArrayAsync()), Is.EqualTo(new[] { "u32", "bool" }));
        Assert.That((await inspections.ToAsyncEnumerable().Select(async (Cop.Providers.SourceModel.StatementInfo s, CancellationToken cancellationToken) => (await AsyncSourceFacts.SemanticAsync(s, CancellationToken.None)).ReferencedCallable?.Namespace).ToArrayAsync()),
            Is.EqualTo(new[] { "app::model", "app::unrelated" }));
        Assert.That((await inspections.ToAsyncEnumerable().Select(async (Cop.Providers.SourceModel.StatementInfo s, CancellationToken cancellationToken) => (await AsyncSourceFacts.SemanticAsync(s, CancellationToken.None)).ReferencedCallable?.SourceFile).ToArrayAsync()),
            Is.EqualTo(new[] { Path.Combine(_root, "src", "model.rs"), Path.Combine(_root, "src", "unrelated.rs") }));
        Assert.That((await AsyncSourceFacts.SemanticAsync(inspections[0], CancellationToken.None)).ReferencedCallable?.Line, Is.EqualTo(3), "select the impl, not merely the trait declaration on line 2");
        Assert.That((await AsyncSourceFacts.SemanticAsync(inspections[0], CancellationToken.None)).ReferencedCallable?.DeclaringType, Is.EqualTo("Packet<T>"));
        Assert.That((await Bound(file, "value")).Signature, Is.EqualTo("pub fn value(&self) -> &T"));
        Assert.That((await Bound(file, "value")).DeclaringType, Is.EqualTo("Packet<T>"));
        Assert.That((await AsyncSourceFacts.MembersAsync((await ParseAsync(modelPath, cancellationToken: CancellationToken.None)).Types.Single(t => t.Kind == TypeKind.Struct && t.Name == "Packet"), CancellationToken.None))
            .Methods.Select(m => m.Name), Is.EqualTo(new[] { "inspect", "value" }));
        Assert.That(ProviderErrors.Drain(), Is.Empty);
    }

    [Test]
    public async System.Threading.Tasks.Task DependencyEditsInvalidateSemanticContextWithoutRunningBuildScriptsOrApplications()
    {
        Write("Cargo.toml", """
            [package]
            name='app'
            version='1.0.0'
            edition='2024'
            [dependencies]
            renamed={package='dependency',path='dependency'}
            """);
        Write("build.rs", "compile_error!(\"BUILD SCRIPT MUST NEVER RUN\");");
        Write("dependency/Cargo.toml", "[package]\nname='dependency'\nversion='1.0.0'\nedition='2024'");
        string dependency = Write("dependency/src/lib.rs", """
            pub struct Item;
            impl Item { pub fn result(&self)->u32 { 1 } }
            pub fn create()->Item { Item }
            """);
        string path = Write("src/lib.rs", "fn run() { let item=renamed::create(); item.result(); }\n");
        await using var scope = new ToolchainExecutionScope();
        var cold = (await ParseAsync(path, cancellationToken: CancellationToken.None));
        Assert.That((await Bound(cold, "result")).ReturnType?.Name, Is.EqualTo("u32"));
        Assert.That((await Bound(cold, "result")).Namespace, Is.EqualTo("dependency"));
        File.WriteAllText(dependency, File.ReadAllText(dependency).Replace("->u32 { 1 }", "->bool { true }"));
        var warm = (await ParseAsync(path, cancellationToken: CancellationToken.None));
        Assert.That((await Bound(warm, "result")).ReturnType?.Name, Is.EqualTo("bool"));
        Assert.That((await AsyncSourceFacts.SemanticAsync(warm.Statements.Single(s => s.MemberName == "result"), CancellationToken.None)).ValueType?.Name, Is.EqualTo("bool"));
        Assert.That(Directory.Exists(Path.Combine(_root, "target")), Is.False);
        Assert.That(ProviderErrors.Drain(), Is.Empty);
        Assert.That(ProviderWarnings.Drain(), Has.Some.Contains("build script is disabled"));
    }

    [Test]
    public async System.Threading.Tasks.Task MissingDependencyAndMalformedSyntaxAreExplicitWithoutInventingBindings()
    {
        Write("Cargo.toml", "[package]\nname='app'\nversion='1.0.0'\nedition='2024'\n[dependencies]\nmissing='1.0'");
        string path = Write("src/lib.rs", "fn run(){ missing::create().inspect(); }\n");
        await using var scope = new ToolchainExecutionScope();
        var file = (await ParseAsync(path, cancellationToken: CancellationToken.None));
        Assert.That(file.Statements.Select(s => s.MemberName), Is.EqualTo(new[] { "inspect", "create" }));
        Assert.That((await file.Statements.ToAsyncEnumerable().Select(async (Cop.Providers.SourceModel.StatementInfo s, CancellationToken cancellationToken) => (await AsyncSourceFacts.SemanticAsync(s, CancellationToken.None)).ReferencedCallable).ToArrayAsync()), Is.EqualTo(new CallableReference?[] { null, null }));
        Assert.That(ProviderWarnings.Drain(), Has.Some.Contains("missing unavailable on disk"));
        File.WriteAllText(path, "fn run( { let x = ; }");
        Assert.That((await ParseAsync(path, cancellationToken: CancellationToken.None)).ParseErrors, Is.Not.Empty);
    }

    [Test]
    public async System.Threading.Tasks.Task StructureIsDemandDrivenAndBackendFailuresDoNotMasqueradeAsFallbackSuccess()
    {
        Write("Cargo.toml", "invalid TOML");
        string path = Write("src/lib.rs", "fn run(){ work(\"line\\nvalue\"); }");
        await using var scope = new ToolchainExecutionScope();
        var file = (await ParseAsync(path, cancellationToken: CancellationToken.None));
        Assert.That(file.ParseErrors, Is.Empty);
        Assert.That(file.Statements.Single().ArgumentExpressions.Single().StringValue, Is.EqualTo("line\nvalue"));
        Assert.That(ProviderErrors.Drain(), Is.Empty, "structural demand must not load a semantic project");
        Assert.That((await AsyncSourceFacts.SemanticAsync(file.Statements.Single(), CancellationToken.None)).ReferencedCallable, Is.Null);
        Assert.That(ProviderErrors.Drain(), Has.Count.EqualTo(1));
        var unavailable = (await new RustSourceParser(Path.Combine(_root, "missing-frontend"), _root)
            .ParseAsync(path, File.ReadAllText(path), cancellationToken: CancellationToken.None))!;
        Assert.That(unavailable.ParseErrors.Single(), Does.Contain("Rust frontend Unavailable"));
        Assert.That(unavailable.Types, Is.Empty, "configured failed backend is not silent fallback");
    }

    [Test]
    public async System.Threading.Tasks.Task CargoDefaultFeaturesAndCompilerCfgSelectRealDeclarations()
    {
        Write("Cargo.toml", """
            [package]
            name='app'
            version='1.0.0'
            edition='2024'
            [features]
            default=['selected']
            selected=[]
            """);
        string path = Write("src/lib.rs", """
            #[cfg(feature="selected")]
            fn choose()->u32 { 1 }
            #[cfg(not(feature="selected"))]
            fn choose()->bool { false }
            #[cfg(windows)]
            fn platform()->u16 { 1 }
            #[cfg(not(windows))]
            fn platform()->u64 { 1 }
            fn run(){ choose(); platform(); }
            """);
        await using var scope = new ToolchainExecutionScope();
        var file = (await ParseAsync(path, cancellationToken: CancellationToken.None));
        Assert.That((await Bound(file, "choose")).ReturnType?.Name, Is.EqualTo("u32"));
        Assert.That((await Bound(file, "choose")).Line, Is.EqualTo(1), "declaration span includes its cfg attribute, not the disabled declaration on line 3");
        Assert.That((await Bound(file, "platform")).ReturnType?.Name,
            Is.EqualTo(OperatingSystem.IsWindows() ? "u16" : "u64"));
        Write(".cargo/config.toml", "[build]\ntarget='unsupported-custom-target'");
        var unsupported = (await ParseAsync(path, cancellationToken: CancellationToken.None));
        Assert.That((await unsupported.Statements.ToAsyncEnumerable().Select(async (Cop.Providers.SourceModel.StatementInfo s, CancellationToken cancellationToken) => (await AsyncSourceFacts.SemanticAsync(s, CancellationToken.None)).ReferencedCallable).ToArrayAsync()),
            Is.EqualTo(new CallableReference?[] { null, null }));
        Assert.That(ProviderWarnings.Drain(), Has.Some.Contains("rather than assuming host defaults"));
    }

    [Test]
    public async System.Threading.Tasks.Task LockedRenamedRegistrySourceAlreadyOnDiskIsBoundWithoutCargoOrNetwork()
    {
        Write("Cargo.toml", """
            [package]
            name='app'
            version='1.0.0'
            edition='2024'
            [dependencies]
            renamed={package='cached',version='1'}
            """);
        Write("Cargo.lock", """
            version=4
            [[package]]
            name='app'
            version='1.0.0'
            dependencies=['cached']
            [[package]]
            name='cached'
            version='1.2.3'
            source='registry+https://github.com/rust-lang/crates.io-index'
            checksum='known-package-identity'
            """);
        Write("cargo-home/registry/src/test-index/cached-1.2.3/Cargo.toml",
            "[package]\nname='cached'\nversion='1.2.3'\nedition='2024'");
        Write("cargo-home/registry/src/test-index/cached-1.2.3/.cargo-checksum.json",
            "{\"package\":\"known-package-identity\",\"files\":{}}");
        Write("cargo-home/registry/src/test-index/cached-1.2.3/src/lib.rs", "pub fn answer()->u64 { 42 }");
        string path = Write("src/lib.rs", "fn run(){ renamed::answer(); }");
        string? previous = Environment.GetEnvironmentVariable("CARGO_HOME");
        try
        {
            Environment.SetEnvironmentVariable("CARGO_HOME", Path.Combine(_root, "cargo-home"));
            await using var scope = new ToolchainExecutionScope();
            var file = (await ParseAsync(path, cancellationToken: CancellationToken.None));
            Assert.That((await Bound(file, "answer")).Namespace, Is.EqualTo("cached"));
            Assert.That((await Bound(file, "answer")).ReturnType?.Name, Is.EqualTo("u64"));
            Assert.That(ProviderErrors.Drain(), Is.Empty);
            Assert.That(Directory.Exists(Path.Combine(_root, "target")), Is.False);
            Assert.That(File.ReadAllText(Path.Combine(_root, "Cargo.lock")), Does.Contain("known-package-identity"));
        }
        finally { Environment.SetEnvironmentVariable("CARGO_HOME", previous); }
    }

    [Test]
    public async System.Threading.Tasks.Task UnknownParameterSlotIsNotDroppedAndUnicodeSpansMatchTheSelectedCall()
    {
        Write("Cargo.toml", "[package]\nname='app'\nversion='1.0.0'\nedition='2024'");
        string path = Write("src/lib.rs", """
            fn partial(value: missing::Type) {}
            fn known()->u32 { 1 }
            fn run(){ let text="😀"; known(); partial(unknown()); }
            """);
        await using var scope = new ToolchainExecutionScope();
        var file = (await ParseAsync(path, cancellationToken: CancellationToken.None));
        Assert.That((await Bound(file, "known")).Line, Is.EqualTo(2));
        Assert.That((await AsyncSourceFacts.SemanticAsync(file.Statements.Single(s => s.MemberName == "known"), CancellationToken.None)).ValueType?.Name, Is.EqualTo("u32"));
        Assert.That((await AsyncSourceFacts.SemanticAsync(file.Statements.Single(s => s.MemberName == "partial"), CancellationToken.None)).ReferencedCallable, Is.Null);
        Assert.That(ProviderWarnings.Drain(), Has.Some.Contains("parameter type unavailable"));
        Assert.That(ProviderErrors.Drain(), Is.Empty);
    }

    [Test]
    public async Task PublicSemanticQueriesHaveExactColdAndWarmResultsAndPreserveNegativeControls()
    {
        Write("Cargo.toml", "[package]\nname='app'\nversion='1.0.0'\nedition='2024'");
        Write("src/model.rs", """
            pub struct Packet<T> { value: T }
            pub trait Inspect { fn inspect(&self) -> u32; }
            impl<T> Inspect for Packet<T> { fn inspect(&self) -> u32 { 7 } }
            pub fn factory() -> Packet<u32> { Packet { value: 7 } }
            """);
        Write("src/shadow.rs", """
            pub struct Packet;
            impl Packet { pub fn inspect(&self) -> bool { false } }
            pub fn factory() -> Packet { Packet }
            """);
        Write("src/lib.rs", """
            mod model;
            mod shadow;
            use model::{factory as make, Inspect};
            use shadow::factory as other;
            fn run(){ let packet=make(); packet.inspect(); other().inspect(); }
            """);
        string program = Path.GetDirectoryName(Write("cop-checks/main.cop", """
            import rust
            let cb = rust.parse()
            predicate isInspect(Statement) => Statement.MemberName == 'inspect'
            predicate isModel(Statement) => Statement.ReferencedCallable.Namespace == 'app::model'
            predicate isShadow(Statement) => Statement.ReferencedCallable.Namespace == 'app::shadow'
            command MAIN = foreach cb.Calls:isInspect => 'model:{cb.Calls:isInspect:isModel.Count};shadow:{cb.Calls:isInspect:isShadow.Count};{item.ReferencedCallable.Namespace}:{item.ReceiverType.Name}:{item.ReceiverType.GenericArguments.Count}:{item.ValueType.Name}:{item.ReferencedCallable.Signature}'
            """))!;
        string deployed = Path.Combine(AppContext.BaseDirectory, Path.GetFileName(_frontend));
        bool copied = !File.Exists(deployed);
        try
        {
            if (copied) File.Copy(_frontend, deployed);
            await using (ToolchainExecutionScope execution = new())
            {
                for (int pass = 0; pass < 2; pass++)
                {
                    var result = (await Engine.RunAsync(program, _root, new EngineRunOptions
                    {
                        AdditionalFeedPaths = [_packages],
                    }));
                    Assert.That(result.HasFatalErrors, Is.False, string.Join("; ", result.Errors));
                    Assert.That(result.Outputs.Select(o => o.Message), Is.EqualTo(new[]
                    {
                        "model:1;shadow:1;app::model:Packet:1:u32:fn inspect(&self) -> u32",
                        "model:1;shadow:1;app::shadow:Packet:0:bool:pub fn inspect(&self) -> bool"
                    }), $"execution {pass}: public semantic fields must survive repeat queries without stale binding");
                }
            }
        }
        finally
        {
            if (copied)
            {
                await DeleteOwnedFrontendAsync(deployed);
            }
        }
    }

    private static async Task DeleteOwnedFrontendAsync(string path)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                File.Delete(path);
                Assert.That(File.Exists(path), Is.False, "The fixture-owned frontend must be removed.");
                return;
            }
            catch (Exception ex) when (OperatingSystem.IsWindows()
                && (ex is UnauthorizedAccessException
                    || ex is IOException && (ex.HResult & 0xFFFF) is 32 or 33)
                && elapsed.Elapsed < TimeSpan.FromSeconds(5)
                && (File.GetAttributes(path) & FileAttributes.ReadOnly) == 0)
            {
                // Windows may briefly retain the image mapping after worker termination.
                await Task.Delay(25).ConfigureAwait(false);
            }
        }
    }

    [Test]
    public async System.Threading.Tasks.Task BinaryTargetBindsSamePackageLibraryWithoutRunningItsMain()
    {
        Write("Cargo.toml", "[package]\nname='app'\nversion='1.0.0'\nedition='2024'");
        Write("src/lib.rs", "pub fn answer()->u16 { 7 }");
        string path = Write("src/main.rs", "fn main(){ app::answer(); panic!(\"MUST NOT RUN\"); }");
        await using var scope = new ToolchainExecutionScope();
        var file = (await ParseAsync(path, cancellationToken: CancellationToken.None));
        Assert.That((await Bound(file, "answer")).Namespace, Is.EqualTo("app"));
        Assert.That((await Bound(file, "answer")).ReturnType?.Name, Is.EqualTo("u16"));
        Assert.That(Directory.Exists(Path.Combine(_root, "target")), Is.False);
        Assert.That(ProviderErrors.Drain(), Is.Empty);
    }

    [Test]
    public async System.Threading.Tasks.Task LockedGitSourceRequiresExactCheckoutRevisionAndNeverRunsGit()
    {
        const string revision = "1234567890123456789012345678901234567890";
        Write("Cargo.toml", """
            [package]
            name='app'
            version='1.0.0'
            edition='2024'
            [dependencies]
            renamed={package='cached',git='https://invalid.example/cached'}
            """);
        Write("Cargo.lock", $$"""
            version=4
            [[package]]
            name='app'
            version='1.0.0'
            dependencies=['cached']
            [[package]]
            name='cached'
            version='1.2.3'
            source='git+https://invalid.example/cached#{{revision}}'
            """);
        string checkout = "cargo-home/git/checkouts/cached-identity/revision";
        string head = Write(checkout + "/.git/HEAD", revision);
        Write(checkout + "/Cargo.toml", "[package]\nname='cached'\nversion='1.2.3'\nedition='2024'");
        Write(checkout + "/src/lib.rs", "pub fn answer()->u64 { 42 }");
        string path = Write("src/lib.rs", "fn run(){ renamed::answer(); }");
        string? previous = Environment.GetEnvironmentVariable("CARGO_HOME");
        try
        {
            Environment.SetEnvironmentVariable("CARGO_HOME", Path.Combine(_root, "cargo-home"));
            await using var scope = new ToolchainExecutionScope();
            Assert.That((await Bound((await ParseAsync(path, cancellationToken: CancellationToken.None)), "answer")).ReturnType?.Name, Is.EqualTo("u64"));
            File.WriteAllText(head, "0000000000000000000000000000000000000000");
            Assert.That((await AsyncSourceFacts.SemanticAsync((await ParseAsync(path, cancellationToken: CancellationToken.None)).Statements.Single(s => s.MemberName == "answer"), CancellationToken.None)).ReferencedCallable, Is.Null);
            Assert.That(ProviderWarnings.Drain(), Has.Some.Contains("renamed unavailable on disk"));
            Assert.That(ProviderErrors.Drain(), Is.Empty);
        }
        finally { Environment.SetEnvironmentVariable("CARGO_HOME", previous); }
    }

    [Test]
    public async System.Threading.Tasks.Task BundledStandardLibraryBindsGenericPreludeFactoriesAndMethods()
    {
        string sources = Path.Combine(Path.GetDirectoryName(_frontend)!, "rust-stdlib");
        if (!File.Exists(Path.Combine(sources, "manifest.json")))
            Assert.Ignore("Prepare the checksum-pinned standard library beside the test frontend with native/prepare-stdlib.ps1.");
        Write("Cargo.toml", "[package]\nname='app'\nversion='1.0.0'\nedition='2024'");
        string path = Write("src/lib.rs", """
            fn run() {
                let mut values = Vec::<u32>::new();
                values.push(7);
                values.len();
            }
            """);
        await using var scope = new ToolchainExecutionScope();
        for (int pass = 0; pass < 2; pass++)
        {
            var file = (await new RustSourceParser(_frontend, _root, sources).ParseAsync(path, File.ReadAllText(path), cancellationToken: CancellationToken.None))!;
            Assert.That((await AsyncSourceFacts.SemanticAsync(file.Statements.Single(s => s.MemberName == "new"), CancellationToken.None)).ValueType?.OriginalText, Is.EqualTo("Vec<u32, Global>"));
            var count = file.Statements.Single(s => s.MemberName == "len");
            Assert.That((await AsyncSourceFacts.SemanticAsync(count, CancellationToken.None)).ReceiverType?.OriginalText, Is.EqualTo("Vec<u32, Global>"));
            Assert.That((await AsyncSourceFacts.SemanticAsync(count, CancellationToken.None)).ValueType?.Name, Is.EqualTo("usize"));
            Assert.That((await AsyncSourceFacts.SemanticAsync(count, CancellationToken.None)).ReferencedCallable?.Namespace, Is.EqualTo("alloc::vec"));
            Assert.That((await AsyncSourceFacts.SemanticAsync(count, CancellationToken.None)).ReferencedCallable?.Signature, Is.EqualTo("pub const fn len(&self) -> usize"));
            Assert.That((await AsyncSourceFacts.SemanticAsync(count, CancellationToken.None)).ReferencedCallable?.SourceFile, Does.EndWith(Path.Combine("alloc", "src", "vec", "mod.rs")));
            Assert.That((await AsyncSourceFacts.SemanticAsync(file.Statements.Single(s => s.MemberName == "push"), CancellationToken.None)).ReferencedCallable?.Name, Is.EqualTo("push"));
        }
        Assert.That(ProviderErrors.Drain(), Is.Empty);
    }

    [Test]
    public async System.Threading.Tasks.Task Issue091_ArchiveSourcesRemainDeferredUntilRealSemanticDemand()
    {
        string sources = Path.Combine(Path.GetDirectoryName(_frontend)!, "rust-stdlib");
        if (!File.Exists(Path.Combine(sources, "manifest.json")))
            Assert.Ignore("Prepare the checksum-pinned standard library beside the test frontend with native/prepare-stdlib.ps1.");
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            zip.CreateEntryFromFile(_frontend, Path.GetFileName(_frontend), CompressionLevel.Fastest);
            foreach (string source in Directory.GetFiles(sources, "*", SearchOption.AllDirectories))
                zip.CreateEntryFromFile(source, "rust-stdlib/" + Path.GetRelativePath(sources, source).Replace('\\', '/'),
                    CompressionLevel.Fastest);
            using var notice = new StreamWriter(zip.CreateEntry("rust-frontend-licenses/manifest.json").Open());
            notice.Write("{}");
        }
        byte[] bytes = buffer.ToArray();
        string cache = Path.Combine(_root, ".cop", "frontend-cache");
        Write("Cargo.toml", "[package]\nname='app'\nversion='1.0.0'\nedition='2024'");
        string path = Write("src/lib.rs", "fn run(){ let mut values=Vec::<u32>::new(); values.push(7); values.len(); }");
        await using var scope = new ToolchainExecutionScope();
        for (int pass = 0; pass < 2; pass++)
        {
            int opened = 0;
            var parser = new RustSourceParser(projectRoot: _root,
                bundleFactory: () => { opened++; return new MemoryStream(bytes); }, cacheDirectory: cache);
            var file = (await parser.ParseAsync(path, File.ReadAllText(path), cancellationToken: CancellationToken.None))!;
            Assert.That(file.ParseErrors, Is.Empty);
            Assert.That(file.Statements.Where(s => s.Kind == "call").Select(s => s.MemberName),
                Is.EqualTo(new[] { "new", "push", "len" }));
            if (pass == 0)
                Assert.That(Directory.GetFiles(cache, "lib.rs", SearchOption.AllDirectories), Is.Empty,
                    "structural demand must not deploy the bundled source tree");
            var count = file.Statements.Single(s => s.MemberName == "len");
            Assert.That((await AsyncSourceFacts.SemanticAsync(count, CancellationToken.None)).ReceiverType?.OriginalText, Is.EqualTo("Vec<u32, Global>"));
            Assert.That((await AsyncSourceFacts.SemanticAsync(count, CancellationToken.None)).ValueType?.Name, Is.EqualTo("usize"));
            Assert.That((await AsyncSourceFacts.SemanticAsync(count, CancellationToken.None)).ReferencedCallable?.Signature, Is.EqualTo("pub const fn len(&self) -> usize"));
            string declaration = (await AsyncSourceFacts.SemanticAsync(count, CancellationToken.None)).ReferencedCallable!.SourceFile!;
            if (declaration.StartsWith(cache, StringComparison.Ordinal))
            {
                Assert.That(File.ReadAllText(declaration),
                    Is.EqualTo(File.ReadAllText(Path.Combine(sources, "library", "alloc", "src", "vec", "mod.rs"))));
                string manifest = Directory.GetFiles(cache, "manifest.json", SearchOption.AllDirectories)
                    .Single(filePath => Path.GetFileName(Path.GetDirectoryName(filePath)) == "rust-stdlib");
                Assert.That(File.ReadAllText(manifest), Is.EqualTo(File.ReadAllText(Path.Combine(sources, "manifest.json"))));
            }
            else
            {
                Assert.That(File.Exists(declaration), Is.True, "matching installed rust-src takes precedence");
                Assert.That(Directory.GetFiles(cache, "lib.rs", SearchOption.AllDirectories), Is.Empty);
            }
            Assert.That(opened, Is.EqualTo(1), "semantic deployment must reuse the trusted original archive bytes");
        }
        Assert.That(ProviderErrors.Drain(), Is.Empty);
    }

    [Test]
    public async System.Threading.Tasks.Task Issue091_LocalMethodWithFullStandardLibraryKeepsExactSelectedDeclaration()
    {
        string sources = Path.Combine(Path.GetDirectoryName(_frontend)!, "rust-stdlib");
        if (!File.Exists(Path.Combine(sources, "manifest.json")))
            Assert.Ignore("Prepare the checksum-pinned standard library beside the test frontend with native/prepare-stdlib.ps1.");
        Write("Cargo.toml", "[package]\nname='cop-publish-smoke'\nversion='0.1.0'\nedition='2024'\n[lib]\npath='sample.rs'");
        string path = Write("sample.rs", "pub struct Alpha; impl Alpha { pub fn get(&self, value: u32) -> u32 { value } } pub fn run() { let a = Alpha; a.get(1); }");
        await using var scope = new ToolchainExecutionScope();
        for (int pass = 0; pass < 2; pass++)
        {
            var file = (await new RustSourceParser(_frontend, _root, sources).ParseAsync(path, File.ReadAllText(path), cancellationToken: CancellationToken.None))!;
            var call = file.Statements.Single(statement => statement.MemberName == "get");
            Assert.That((await AsyncSourceFacts.SemanticAsync(call, CancellationToken.None)).ReceiverType?.OriginalText, Is.EqualTo("Alpha"));
            Assert.That((await AsyncSourceFacts.SemanticAsync(call, CancellationToken.None)).ValueType?.Name, Is.EqualTo("u32"));
            Assert.That((await AsyncSourceFacts.SemanticAsync(call, CancellationToken.None)).ReferencedCallable?.Signature, Is.EqualTo("pub fn get(&self, value: u32) -> u32"));
            Assert.That((await AsyncSourceFacts.SemanticAsync(call, CancellationToken.None)).ReferencedCallable?.Parameters.Select(type => type.Name), Is.EqualTo(new[] { "u32" }));
            Assert.That((await AsyncSourceFacts.SemanticAsync(call, CancellationToken.None)).ReferencedCallable?.SourceFile, Is.EqualTo(path));
            Assert.That((await AsyncSourceFacts.SemanticAsync(call, CancellationToken.None)).ReferencedCallable?.Line, Is.EqualTo(1));
            Assert.That((await AsyncSourceFacts.SemanticAsync(call, CancellationToken.None)).ReferencedCallable?.Column, Is.EqualTo(32));
        }
        Assert.That(ProviderErrors.Drain(), Is.Empty);
    }

    [Test]
    public async System.Threading.Tasks.Task IncompatibleBundledSourcesAndChangedDemandedFileNeverInventSemanticBindings()
    {
        string manifest = Write("incompatible/manifest.json", "{\"release\":\"0.0.0\",\"compilerCommit\":\"incompatible\"}");
        Write("Cargo.toml", "[package]\nname='app'\nversion='1.0.0'\nedition='2024'");
        string path = Write("src/lib.rs", "fn run(){ Vec::<u32>::new(); }");
        await using var scope = new ToolchainExecutionScope();
        var file = (await new RustSourceParser(_frontend, _root, Path.GetDirectoryName(manifest)).ParseAsync(path, File.ReadAllText(path), cancellationToken: CancellationToken.None))!;
        Assert.That((await AsyncSourceFacts.SemanticAsync(file.Statements.Single(), CancellationToken.None)).ReferencedCallable, Is.Null);
        Assert.That(ProviderWarnings.Drain(), Has.Some.Contains("do not match installed compiler"));
        File.WriteAllText(manifest, "{}");
        var corrupt = (await new RustSourceParser(_frontend, _root, Path.GetDirectoryName(manifest)).ParseAsync(path, File.ReadAllText(path), cancellationToken: CancellationToken.None))!;
        Assert.That((await AsyncSourceFacts.SemanticAsync(corrupt.Statements.Single(), CancellationToken.None)).ReferencedCallable, Is.Null);
        Assert.That(ProviderErrors.Drain(), Has.Some.Contains("standard-library manifest is malformed"));
        File.WriteAllText(path, "fn known()->u32 { 1 } fn run(){ known(); }");
        var changed = (await ParseAsync(path, cancellationToken: CancellationToken.None));
        File.WriteAllText(path, "fn known()->bool { true } fn run(){ known(); }");
        Assert.That((await AsyncSourceFacts.SemanticAsync(changed.Statements.Single(), CancellationToken.None)).ReferencedCallable, Is.Null);
        Assert.That(ProviderErrors.Drain(), Has.Some.Contains("source changed since its syntax parse"));
    }

    [Test]
    public async Task ParserEnvironmentOverride_ReportsExplicitPartialContextWithoutChangingProcessEnvironment()
    {
        Write("Cargo.toml", "[package]\nname='app'\nversion='1.0.0'\nedition='2024'");
        string path = Write("src/lib.rs", "fn known()->u32 { 1 } fn run(){ known(); }");
        string? inheritedFlags = Environment.GetEnvironmentVariable("RUSTFLAGS");
        var parser = new RustSourceParser(_frontend, _root, Path.Combine(_root, "no-standard-library"))
        {
            ToolchainEnvironment = new Dictionary<string, string?>
            {
                ["RUSTFLAGS"] = "--cfg=custom"
            }
        };
        await using var scope = new ToolchainExecutionScope();
        var file = await parser.ParseAsync(path, File.ReadAllText(path))
            ?? throw new AssertionException("Expected Rust syntax facts in an explicit partial context.");
        var call = file.Statements.Single(s => s.Kind == "call" && s.MemberName == "known");
        await call.EnsureSemanticAnalysisAsync();
        Assert.That(file.ParseErrors, Is.Empty);
        Assert.That(call.ReferencedCallable, Is.Null);
        Assert.That(call.ValueType, Is.Null);
        Assert.That(ProviderWarnings.Drain(), Has.Some.Contains("Unsupported Cargo environment context"));
        Assert.That(ProviderErrors.Drain(), Is.Empty);
        Assert.That(Environment.GetEnvironmentVariable("RUSTFLAGS"), Is.EqualTo(inheritedFlags));
    }

    [Test]
    public async System.Threading.Tasks.Task CargoEnvironmentGlobalConfigurationAndCustomToolchainPathsAreExplicitPartialContexts()
    {
        Write("Cargo.toml", "[package]\nname='app'\nversion='1.0.0'\nedition='2024'");
        string path = Write("src/lib.rs", "fn known()->u32 { 1 } fn run(){ known(); }");
        string? flags = Environment.GetEnvironmentVariable("RUSTFLAGS");
        string? home = Environment.GetEnvironmentVariable("CARGO_HOME");
        try
        {
            await using var scope = new ToolchainExecutionScope();
            Environment.SetEnvironmentVariable("RUSTFLAGS", "--cfg=custom");
            Assert.That((await AsyncSourceFacts.SemanticAsync((await ParseAsync(path, cancellationToken: CancellationToken.None)).Statements.Single(), CancellationToken.None)).ReferencedCallable, Is.Null);
            Assert.That(ProviderWarnings.Drain(), Has.Some.Contains("Unsupported Cargo environment context"));
            Environment.SetEnvironmentVariable("RUSTFLAGS", null);
            Write("cargo-home/config.toml", "[build]\nrustflags=['--cfg=custom']");
            Environment.SetEnvironmentVariable("CARGO_HOME", Path.Combine(_root, "cargo-home"));
            Assert.That((await AsyncSourceFacts.SemanticAsync((await ParseAsync(path, cancellationToken: CancellationToken.None)).Statements.Single(), CancellationToken.None)).ReferencedCallable, Is.Null);
            Assert.That(ProviderWarnings.Drain(), Has.Some.Contains("rather than assuming host defaults"));
            Write("rust-toolchain.toml", "[toolchain]\npath='untrusted-target-toolchain'");
            Assert.That((await AsyncSourceFacts.SemanticAsync((await ParseAsync(path, cancellationToken: CancellationToken.None)).Statements.Single(), CancellationToken.None)).ReferencedCallable, Is.Null);
            Assert.That(ProviderWarnings.Drain(), Has.Some.Contains("Custom path Rust toolchain is unsupported"));
            Assert.That(Directory.Exists(Path.Combine(_root, "target")), Is.False);
            Assert.That(ProviderErrors.Drain(), Is.Empty);
        }
        finally
        {
            Environment.SetEnvironmentVariable("RUSTFLAGS", flags);
            Environment.SetEnvironmentVariable("CARGO_HOME", home);
        }
    }

    [Test]
    public async System.Threading.Tasks.Task MaintainedAstControlConditionsAndElseBranchesKeepExactCallOwnership()
    {
        string path = Write("src/lib.rs", """
            fn run(value: bool) {
                let mut answer=choose();
                if value { yes(); }
                else if other() { fallback(); }
                else { no(); }
                while ready() { tick(); }
                let Some(item) = lookup() else { return; };
            }
            """);
        await using var scope = new ToolchainExecutionScope();
        var file = (await ParseAsync(path, cancellationToken: CancellationToken.None));
        Assert.That(file.ParseErrors, Is.Empty);
        StatementInfo Call(string name) => file.Statements.Single(s => s.MemberName == name);
        var declaration = file.Statements.Single(s => s.Kind == "declaration" && s.MemberName == "answer");
        Assert.That(declaration.MemberName, Is.EqualTo("answer"));
        Assert.That(Call("choose").Parent, Is.SameAs(declaration));
        Assert.That(Call("yes").Parent?.Condition, Is.EqualTo("value"));
        Assert.That(Call("yes").Parent?.ValueExpression?.Name, Is.EqualTo("value"));
        Assert.That(Call("other").Parent?.Parent?.Kind, Is.EqualTo("else"));
        Assert.That(Call("fallback").Parent, Is.SameAs(Call("other").Parent));
        Assert.That(Call("no").Parent?.Kind, Is.EqualTo("else"));
        Assert.That(Call("no").Parent?.Parent, Is.SameAs(Call("other").Parent?.Parent));
        Assert.That(Call("ready").Parent?.Condition, Is.EqualTo("ready()"));
        Assert.That(Call("ready").Parent?.ValueExpression?.Kind, Is.EqualTo("invocation"));
        Assert.That(Call("tick").Parent, Is.SameAs(Call("ready").Parent));
        Assert.That(file.Statements.Single(s => s.Kind == "return").Parent?.Kind, Is.EqualTo("else"));
        Assert.That(file.Statements.Single(s => s.Kind == "return").Parent?.Parent, Is.SameAs(Call("lookup").Parent));
        Assert.That(file.Statements.Count(s => s.Kind == "else"), Is.EqualTo(3));
        Assert.That(file.Statements.All(s => s.Method?.Name == "run"), Is.True);
    }
}
