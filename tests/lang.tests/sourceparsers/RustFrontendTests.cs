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

    private SourceFile Parse(string path) => new RustSourceParser(_frontend, _root,
        Path.Combine(_root, "no-standard-library")).Parse(path, File.ReadAllText(path))!;
    private static CallableReference Bound(SourceFile file, string name) =>
        file.Statements.Single(s => s.MemberName == name).ReferencedCallable!;

    [Test]
    public void MaintainedAstAndHir_BindAliasesFactoriesTraitMethodsAndGenericSignaturesAcrossFiles()
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
        using var scope = new ToolchainExecutionScope();
        var file = Parse(path);
        Assert.That(file.ParseErrors, Is.Empty);
        Assert.That(file.Statements.Where(s => s.Kind == "call").Select(s => s.MemberName),
            Is.EqualTo(new[] { "make", "inspect", "value", "inspect", "other" }));
        var factory = Bound(file, "make");
        Assert.That(factory, Is.Not.Null);
        Assert.That(factory.Name, Is.EqualTo("factory"));
        Assert.That(factory.Namespace, Is.EqualTo("app::model"));
        Assert.That(factory.ReturnType?.OriginalText, Is.EqualTo("Packet<u32>"));
        var inspections = file.Statements.Where(s => s.MemberName == "inspect").ToList();
        Assert.That(inspections.Select(s => s.ReceiverType?.OriginalText),
            Is.EqualTo(new[] { "Packet<u32>", "Packet" }));
        Assert.That(inspections.Select(s => s.ValueType?.OriginalText), Is.EqualTo(new[] { "u32", "bool" }));
        Assert.That(inspections.Select(s => s.ReferencedCallable?.Namespace),
            Is.EqualTo(new[] { "app::model", "app::unrelated" }));
        Assert.That(inspections.Select(s => s.ReferencedCallable?.SourceFile),
            Is.EqualTo(new[] { Path.Combine(_root, "src", "model.rs"), Path.Combine(_root, "src", "unrelated.rs") }));
        Assert.That(inspections[0].ReferencedCallable?.Line, Is.EqualTo(3), "select the impl, not merely the trait declaration on line 2");
        Assert.That(inspections[0].ReferencedCallable?.DeclaringType, Is.EqualTo("Packet<T>"));
        Assert.That(Bound(file, "value").Signature, Is.EqualTo("pub fn value(&self) -> &T"));
        Assert.That(Bound(file, "value").DeclaringType, Is.EqualTo("Packet<T>"));
        Assert.That(Parse(modelPath).Types.Single(t => t.Kind == TypeKind.Struct && t.Name == "Packet")
            .Methods.Select(m => m.Name), Is.EqualTo(new[] { "inspect", "value" }));
        Assert.That(ProviderErrors.Drain(), Is.Empty);
    }

    [Test]
    public void DependencyEditsInvalidateSemanticContextWithoutRunningBuildScriptsOrApplications()
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
        using var scope = new ToolchainExecutionScope();
        var cold = Parse(path);
        Assert.That(Bound(cold, "result").ReturnType?.Name, Is.EqualTo("u32"));
        Assert.That(Bound(cold, "result").Namespace, Is.EqualTo("dependency"));
        File.WriteAllText(dependency, File.ReadAllText(dependency).Replace("->u32 { 1 }", "->bool { true }"));
        var warm = Parse(path);
        Assert.That(Bound(warm, "result").ReturnType?.Name, Is.EqualTo("bool"));
        Assert.That(warm.Statements.Single(s => s.MemberName == "result").ValueType?.Name, Is.EqualTo("bool"));
        Assert.That(Directory.Exists(Path.Combine(_root, "target")), Is.False);
        Assert.That(ProviderErrors.Drain(), Is.Empty);
        Assert.That(ProviderWarnings.Drain(), Has.Some.Contains("build script is disabled"));
    }

    [Test]
    public void MissingDependencyAndMalformedSyntaxAreExplicitWithoutInventingBindings()
    {
        Write("Cargo.toml", "[package]\nname='app'\nversion='1.0.0'\nedition='2024'\n[dependencies]\nmissing='1.0'");
        string path = Write("src/lib.rs", "fn run(){ missing::create().inspect(); }\n");
        using var scope = new ToolchainExecutionScope();
        var file = Parse(path);
        Assert.That(file.Statements.Select(s => s.MemberName), Is.EqualTo(new[] { "inspect", "create" }));
        Assert.That(file.Statements.Select(s => s.ReferencedCallable), Is.EqualTo(new CallableReference?[] { null, null }));
        Assert.That(ProviderWarnings.Drain(), Has.Some.Contains("missing unavailable on disk"));
        File.WriteAllText(path, "fn run( { let x = ; }");
        Assert.That(Parse(path).ParseErrors, Is.Not.Empty);
    }

    [Test]
    public void StructureIsDemandDrivenAndBackendFailuresDoNotMasqueradeAsFallbackSuccess()
    {
        Write("Cargo.toml", "invalid TOML");
        string path = Write("src/lib.rs", "fn run(){ work(\"line\\nvalue\"); }");
        using var scope = new ToolchainExecutionScope();
        var file = Parse(path);
        Assert.That(file.ParseErrors, Is.Empty);
        Assert.That(file.Statements.Single().ArgumentExpressions.Single().StringValue, Is.EqualTo("line\nvalue"));
        Assert.That(ProviderErrors.Drain(), Is.Empty, "structural demand must not load a semantic project");
        Assert.That(file.Statements.Single().ReferencedCallable, Is.Null);
        Assert.That(ProviderErrors.Drain(), Has.Count.EqualTo(1));
        var unavailable = new RustSourceParser(Path.Combine(_root, "missing-frontend"), _root)
            .Parse(path, File.ReadAllText(path))!;
        Assert.That(unavailable.ParseErrors.Single(), Does.Contain("Rust frontend Unavailable"));
        Assert.That(unavailable.Types, Is.Empty, "configured failed backend is not silent fallback");
    }

    [Test]
    public void CargoDefaultFeaturesAndCompilerCfgSelectRealDeclarations()
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
        using var scope = new ToolchainExecutionScope();
        var file = Parse(path);
        Assert.That(Bound(file, "choose").ReturnType?.Name, Is.EqualTo("u32"));
        Assert.That(Bound(file, "choose").Line, Is.EqualTo(1), "declaration span includes its cfg attribute, not the disabled declaration on line 3");
        Assert.That(Bound(file, "platform").ReturnType?.Name,
            Is.EqualTo(OperatingSystem.IsWindows() ? "u16" : "u64"));
        Write(".cargo/config.toml", "[build]\ntarget='unsupported-custom-target'");
        var unsupported = Parse(path);
        Assert.That(unsupported.Statements.Select(s => s.ReferencedCallable),
            Is.EqualTo(new CallableReference?[] { null, null }));
        Assert.That(ProviderWarnings.Drain(), Has.Some.Contains("rather than assuming host defaults"));
    }

    [Test]
    public void LockedRenamedRegistrySourceAlreadyOnDiskIsBoundWithoutCargoOrNetwork()
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
            using var scope = new ToolchainExecutionScope();
            var file = Parse(path);
            Assert.That(Bound(file, "answer").Namespace, Is.EqualTo("cached"));
            Assert.That(Bound(file, "answer").ReturnType?.Name, Is.EqualTo("u64"));
            Assert.That(ProviderErrors.Drain(), Is.Empty);
            Assert.That(Directory.Exists(Path.Combine(_root, "target")), Is.False);
            Assert.That(File.ReadAllText(Path.Combine(_root, "Cargo.lock")), Does.Contain("known-package-identity"));
        }
        finally { Environment.SetEnvironmentVariable("CARGO_HOME", previous); }
    }

    [Test]
    public void UnknownParameterSlotIsNotDroppedAndUnicodeSpansMatchTheSelectedCall()
    {
        Write("Cargo.toml", "[package]\nname='app'\nversion='1.0.0'\nedition='2024'");
        string path = Write("src/lib.rs", """
            fn partial(value: missing::Type) {}
            fn known()->u32 { 1 }
            fn run(){ let text="😀"; known(); partial(unknown()); }
            """);
        using var scope = new ToolchainExecutionScope();
        var file = Parse(path);
        Assert.That(Bound(file, "known").Line, Is.EqualTo(2));
        Assert.That(file.Statements.Single(s => s.MemberName == "known").ValueType?.Name, Is.EqualTo("u32"));
        Assert.That(file.Statements.Single(s => s.MemberName == "partial").ReferencedCallable, Is.Null);
        Assert.That(ProviderWarnings.Drain(), Has.Some.Contains("parameter type unavailable"));
        Assert.That(ProviderErrors.Drain(), Is.Empty);
    }

    [Test]
    public void PublicSemanticQueriesHaveExactColdAndWarmResultsAndPreserveNegativeControls()
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
            for (int pass = 0; pass < 2; pass++)
            {
                var result = Engine.Run(program, _root, additionalFeedPaths: [_packages]);
                Assert.That(result.HasFatalErrors, Is.False, string.Join("; ", result.Errors));
                Assert.That(result.Outputs.Select(o => o.Message), Is.EqualTo(new[]
                {
                    "model:1;shadow:1;app::model:Packet:1:u32:fn inspect(&self) -> u32",
                    "model:1;shadow:1;app::shadow:Packet:0:bool:pub fn inspect(&self) -> bool"
                }), $"execution {pass}: public semantic fields must survive repeat queries without stale binding");
            }
        }
        finally { if (copied) File.Delete(deployed); }
    }

    [Test]
    public void BinaryTargetBindsSamePackageLibraryWithoutRunningItsMain()
    {
        Write("Cargo.toml", "[package]\nname='app'\nversion='1.0.0'\nedition='2024'");
        Write("src/lib.rs", "pub fn answer()->u16 { 7 }");
        string path = Write("src/main.rs", "fn main(){ app::answer(); panic!(\"MUST NOT RUN\"); }");
        using var scope = new ToolchainExecutionScope();
        var file = Parse(path);
        Assert.That(Bound(file, "answer").Namespace, Is.EqualTo("app"));
        Assert.That(Bound(file, "answer").ReturnType?.Name, Is.EqualTo("u16"));
        Assert.That(Directory.Exists(Path.Combine(_root, "target")), Is.False);
        Assert.That(ProviderErrors.Drain(), Is.Empty);
    }

    [Test]
    public void LockedGitSourceRequiresExactCheckoutRevisionAndNeverRunsGit()
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
            using var scope = new ToolchainExecutionScope();
            Assert.That(Bound(Parse(path), "answer").ReturnType?.Name, Is.EqualTo("u64"));
            File.WriteAllText(head, "0000000000000000000000000000000000000000");
            Assert.That(Parse(path).Statements.Single(s => s.MemberName == "answer").ReferencedCallable, Is.Null);
            Assert.That(ProviderWarnings.Drain(), Has.Some.Contains("renamed unavailable on disk"));
            Assert.That(ProviderErrors.Drain(), Is.Empty);
        }
        finally { Environment.SetEnvironmentVariable("CARGO_HOME", previous); }
    }

    [Test]
    public void BundledStandardLibraryBindsGenericPreludeFactoriesAndMethods()
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
        using var scope = new ToolchainExecutionScope();
        for (int pass = 0; pass < 2; pass++)
        {
            var file = new RustSourceParser(_frontend, _root, sources).Parse(path, File.ReadAllText(path))!;
            Assert.That(file.Statements.Single(s => s.MemberName == "new").ValueType?.OriginalText, Is.EqualTo("Vec<u32, Global>"));
            var count = file.Statements.Single(s => s.MemberName == "len");
            Assert.That(count.ReceiverType?.OriginalText, Is.EqualTo("Vec<u32, Global>"));
            Assert.That(count.ValueType?.Name, Is.EqualTo("usize"));
            Assert.That(count.ReferencedCallable?.Namespace, Is.EqualTo("alloc::vec"));
            Assert.That(count.ReferencedCallable?.Signature, Is.EqualTo("pub const fn len(&self) -> usize"));
            Assert.That(count.ReferencedCallable?.SourceFile, Does.EndWith(Path.Combine("alloc", "src", "vec", "mod.rs")));
            Assert.That(file.Statements.Single(s => s.MemberName == "push").ReferencedCallable?.Name, Is.EqualTo("push"));
        }
        Assert.That(ProviderErrors.Drain(), Is.Empty);
    }

    [Test]
    public void IncompatibleBundledSourcesAndChangedDemandedFileNeverInventSemanticBindings()
    {
        string manifest = Write("incompatible/manifest.json", "{\"release\":\"0.0.0\",\"compilerCommit\":\"incompatible\"}");
        Write("Cargo.toml", "[package]\nname='app'\nversion='1.0.0'\nedition='2024'");
        string path = Write("src/lib.rs", "fn run(){ Vec::<u32>::new(); }");
        using var scope = new ToolchainExecutionScope();
        var file = new RustSourceParser(_frontend, _root, Path.GetDirectoryName(manifest)).Parse(path, File.ReadAllText(path))!;
        Assert.That(file.Statements.Single().ReferencedCallable, Is.Null);
        Assert.That(ProviderWarnings.Drain(), Has.Some.Contains("do not match installed compiler"));
        File.WriteAllText(manifest, "{}");
        var corrupt = new RustSourceParser(_frontend, _root, Path.GetDirectoryName(manifest)).Parse(path, File.ReadAllText(path))!;
        Assert.That(corrupt.Statements.Single().ReferencedCallable, Is.Null);
        Assert.That(ProviderErrors.Drain(), Has.Some.Contains("standard-library manifest is malformed"));
        File.WriteAllText(path, "fn known()->u32 { 1 } fn run(){ known(); }");
        var changed = Parse(path);
        File.WriteAllText(path, "fn known()->bool { true } fn run(){ known(); }");
        Assert.That(changed.Statements.Single().ReferencedCallable, Is.Null);
        Assert.That(ProviderErrors.Drain(), Has.Some.Contains("source changed since its syntax parse"));
    }

    [Test]
    public void CargoEnvironmentGlobalConfigurationAndCustomToolchainPathsAreExplicitPartialContexts()
    {
        Write("Cargo.toml", "[package]\nname='app'\nversion='1.0.0'\nedition='2024'");
        string path = Write("src/lib.rs", "fn known()->u32 { 1 } fn run(){ known(); }");
        string? flags = Environment.GetEnvironmentVariable("RUSTFLAGS");
        string? home = Environment.GetEnvironmentVariable("CARGO_HOME");
        try
        {
            using var scope = new ToolchainExecutionScope();
            Environment.SetEnvironmentVariable("RUSTFLAGS", "--cfg=custom");
            Assert.That(Parse(path).Statements.Single().ReferencedCallable, Is.Null);
            Assert.That(ProviderWarnings.Drain(), Has.Some.Contains("Unsupported Cargo environment context"));
            Environment.SetEnvironmentVariable("RUSTFLAGS", null);
            Write("cargo-home/config.toml", "[build]\nrustflags=['--cfg=custom']");
            Environment.SetEnvironmentVariable("CARGO_HOME", Path.Combine(_root, "cargo-home"));
            Assert.That(Parse(path).Statements.Single().ReferencedCallable, Is.Null);
            Assert.That(ProviderWarnings.Drain(), Has.Some.Contains("rather than assuming host defaults"));
            Write("rust-toolchain.toml", "[toolchain]\npath='untrusted-target-toolchain'");
            Assert.That(Parse(path).Statements.Single().ReferencedCallable, Is.Null);
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
    public void MaintainedAstControlConditionsAndElseBranchesKeepExactCallOwnership()
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
        using var scope = new ToolchainExecutionScope();
        var file = Parse(path);
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
