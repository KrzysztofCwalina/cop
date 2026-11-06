using Cop.Providers.SourceModel;
using Cop.Providers.SourceParsers;
using NUnit.Framework;

namespace Cop.Tests.Lang.SourceParsers;

[TestFixture]
public class RustSourceParserTests
{
    private readonly RustSourceParser _parser = new();

    // ================================================================
    // Use / imports
    // ================================================================

    [Test]
    public async Task Parse_SimpleUse()
    {
        var result = (await ParseAsync("""
            use std::collections::HashMap;
            use std::io;
            """, cancellationToken: CancellationToken.None));
        Assert.That(result.Usings, Does.Contain("std::collections::HashMap"));
        Assert.That(result.Usings, Does.Contain("std::io"));
    }

    [Test]
    public async Task Parse_GroupedUse()
    {
        var result = (await ParseAsync("""
            use std::collections::{HashMap, BTreeMap, HashSet};
            """, cancellationToken: CancellationToken.None));
        Assert.That(result.Usings, Does.Contain("std::collections::HashMap"));
        Assert.That(result.Usings, Does.Contain("std::collections::BTreeMap"));
        Assert.That(result.Usings, Does.Contain("std::collections::HashSet"));
    }

    [Test]
    public async Task Parse_UseWithAlias()
    {
        var result = (await ParseAsync("""
            use std::fmt::Result as FmtResult;
            """, cancellationToken: CancellationToken.None));
        // Should capture the module path (the alias is not part of the path).
        Assert.That(result.Usings, Does.Contain("std::fmt::Result"));
    }

    [Test]
    public async Task Parse_NoUse_EmptyUsings()
    {
        var result = (await ParseAsync("""
            fn main() {
                println!("hello");
            }
            """, cancellationToken: CancellationToken.None));
        Assert.That(result.Usings, Is.Empty);
    }

    // ================================================================
    // Structs
    // ================================================================

    [Test]
    public async Task Parse_PublicStruct()
    {
        var result = (await ParseAsync("""
            pub struct Config {
                pub name: String,
                pub timeout: u64,
            }
            """, cancellationToken: CancellationToken.None));
        Assert.That(result.Types, Has.Count.EqualTo(1));
        var t = result.Types[0];
        Assert.That(t.Name, Is.EqualTo("Config"));
        Assert.That(t.Kind, Is.EqualTo(TypeKind.Struct));
        Assert.That(t.IsPublic, Is.True);
    }

    [Test]
    public async Task Parse_StructFields()
    {
        var result = (await ParseAsync("""
            pub struct Point {
                pub x: f64,
                pub y: f64,
                label: String,
            }
            """, cancellationToken: CancellationToken.None));
        var t = result.Types[0];
        Assert.That(t.Fields, Has.Count.EqualTo(3));
        var xField = t.Fields.First(f => f.Name == "x");
        Assert.That(xField.Type?.Name, Is.EqualTo("f64"));
        Assert.That(xField.IsPublic, Is.True);
        var labelField = t.Fields.First(f => f.Name == "label");
        Assert.That(labelField.IsPublic, Is.False);
    }

    [Test]
    public async Task Parse_PrivateStruct()
    {
        var result = (await ParseAsync("""
            struct Internal {
                data: Vec<u8>,
            }
            """, cancellationToken: CancellationToken.None));
        var t = result.Types[0];
        Assert.That(t.Name, Is.EqualTo("Internal"));
        Assert.That(t.IsPublic, Is.False);
    }

    [Test]
    public async Task Parse_UnitStruct()
    {
        var result = (await ParseAsync("""
            pub struct Marker;
            """, cancellationToken: CancellationToken.None));
        Assert.That(result.Types, Has.Count.EqualTo(1));
        Assert.That(result.Types[0].Name, Is.EqualTo("Marker"));
    }

    [Test]
    public async Task Parse_TupleStruct()
    {
        var result = (await ParseAsync("""
            pub struct Wrapper(pub String, i32);
            """, cancellationToken: CancellationToken.None));
        Assert.That(result.Types, Has.Count.EqualTo(1));
        Assert.That(result.Types[0].Name, Is.EqualTo("Wrapper"));
    }

    [Test]
    public async Task Parse_GenericStruct()
    {
        var result = (await ParseAsync("""
            pub struct Container<T: Clone> {
                inner: T,
            }
            """, cancellationToken: CancellationToken.None));
        Assert.That(result.Types, Has.Count.EqualTo(1));
        Assert.That(result.Types[0].Name, Is.EqualTo("Container"));
    }

    [Test]
    public async Task Parse_StructWithWhereClause()
    {
        var result = (await ParseAsync("""
            pub struct Filter<T>
            where
                T: Send + Sync,
            {
                value: T,
            }
            """, cancellationToken: CancellationToken.None));
        Assert.That(result.Types, Has.Count.EqualTo(1));
        Assert.That(result.Types[0].Name, Is.EqualTo("Filter"));
    }

    // ================================================================
    // Enums
    // ================================================================

    [Test]
    public async Task Parse_SimpleEnum()
    {
        var result = (await ParseAsync("""
            pub enum Color {
                Red,
                Green,
                Blue,
            }
            """, cancellationToken: CancellationToken.None));
        Assert.That(result.Types, Has.Count.EqualTo(1));
        var t = result.Types[0];
        Assert.That(t.Name, Is.EqualTo("Color"));
        Assert.That(t.Kind, Is.EqualTo(TypeKind.Enum));
        Assert.That(t.IsPublic, Is.True);
        Assert.That(t.EnumValues, Does.Contain("Red"));
        Assert.That(t.EnumValues, Does.Contain("Green"));
        Assert.That(t.EnumValues, Does.Contain("Blue"));
    }

    [Test]
    public async Task Parse_EnumWithData()
    {
        var result = (await ParseAsync("""
            pub enum Shape {
                Circle(f64),
                Rectangle { width: f64, height: f64 },
                Point,
            }
            """, cancellationToken: CancellationToken.None));
        var t = result.Types[0];
        Assert.That(t.Name, Is.EqualTo("Shape"));
        Assert.That(t.EnumValues, Does.Contain("Circle"));
        Assert.That(t.EnumValues, Does.Contain("Rectangle"));
        Assert.That(t.EnumValues, Does.Contain("Point"));
    }

    [Test]
    public async Task Parse_EnumWithDiscriminant()
    {
        var result = (await ParseAsync("""
            pub enum Status {
                Active = 1,
                Inactive = 0,
            }
            """, cancellationToken: CancellationToken.None));
        var t = result.Types[0];
        Assert.That(t.EnumValues, Does.Contain("Active"));
        Assert.That(t.EnumValues, Does.Contain("Inactive"));
    }

    // ================================================================
    // Traits
    // ================================================================

    [Test]
    public async Task Parse_Trait()
    {
        var result = (await ParseAsync("""
            pub trait Drawable {
                fn draw(&self);
                fn area(&self) -> f64;
            }
            """, cancellationToken: CancellationToken.None));
        Assert.That(result.Types, Has.Count.EqualTo(1));
        var t = result.Types[0];
        Assert.That(t.Name, Is.EqualTo("Drawable"));
        Assert.That(t.Kind, Is.EqualTo(TypeKind.Interface));
        Assert.That((await AsyncSourceFacts.MembersAsync(t, CancellationToken.None)).Methods, Has.Count.EqualTo(2));
        Assert.That((await AsyncSourceFacts.MembersAsync(t, CancellationToken.None)).Methods.Select(m => m.Name), Does.Contain("draw"));
        Assert.That((await AsyncSourceFacts.MembersAsync(t, CancellationToken.None)).Methods.Select(m => m.Name), Does.Contain("area"));
    }

    [Test]
    public async Task Parse_TraitWithSupertraits()
    {
        var result = (await ParseAsync("""
            pub trait Serializable: Clone + Send {
                fn serialize(&self) -> Vec<u8>;
            }
            """, cancellationToken: CancellationToken.None));
        var t = result.Types[0];
        Assert.That(t.Name, Is.EqualTo("Serializable"));
        Assert.That(t.BaseTypes, Does.Contain("Clone"));
        Assert.That(t.BaseTypes, Does.Contain("Send"));
    }

    [Test]
    public async Task Parse_TraitWithDefaultMethod()
    {
        var result = (await ParseAsync("""
            pub trait Logger {
                fn log(&self, msg: &str);
                fn warn(&self, msg: &str) {
                    self.log(msg);
                }
            }
            """, cancellationToken: CancellationToken.None));
        var t = result.Types[0];
        Assert.That((await AsyncSourceFacts.MembersAsync(t, CancellationToken.None)).Methods, Has.Count.EqualTo(2));
    }

    // ================================================================
    // Impl blocks
    // ================================================================

    [Test]
    public async Task Parse_InherentImpl()
    {
        var result = (await ParseAsync("""
            struct Foo;
            impl Foo {
                pub fn new() -> Self {
                    Foo
                }
                pub fn bar(&self) -> i32 {
                    42
                }
            }
            """, cancellationToken: CancellationToken.None));
        Assert.That(result.Types.Count, Is.EqualTo(2));
        var owner = result.Types.Single(t => t.Name == "Foo");
        Assert.That(owner.Constructors, Is.Empty, "new is an ordinary associated function, not a Rust constructor");
        Assert.That((await AsyncSourceFacts.MembersAsync(owner, CancellationToken.None)).Methods.Select(m => m.Name), Is.EqualTo(new[] { "new", "bar" }));
        Assert.That((await AsyncSourceFacts.MembersAsync(result.Types.Single(t => t is RustTypeDeclaration { IsImpl: true }), CancellationToken.None)).Methods, Is.Empty);
    }

    [Test]
    public async Task Parse_TraitImpl()
    {
        var result = (await ParseAsync("""
            struct Circle { radius: f64 }
            impl Drawable for Circle {
                fn draw(&self) {
                    println!("Drawing circle");
                }
            }
            """, cancellationToken: CancellationToken.None));
        var impl = result.Types.First(t => t.Name.Contains("impl"));
        Assert.That(impl.Name, Does.Contain("Drawable"));
        Assert.That(impl.BaseTypes, Does.Contain("Drawable"));
    }

    [Test]
    public async Task Parse_ImplWithGenericAndWhereClause()
    {
        var result = (await ParseAsync("""
            impl<T> Container<T>
            where
                T: Clone + Send,
            {
                pub fn get(&self) -> &T {
                    &self.inner
                }
            }
            """, cancellationToken: CancellationToken.None));
        var impl = result.Types.First(t => t.Name.Contains("impl"));
        Assert.That((await AsyncSourceFacts.MembersAsync(impl, CancellationToken.None)).Methods, Has.Count.EqualTo(1));
        Assert.That((await AsyncSourceFacts.MembersAsync(impl, CancellationToken.None)).Methods[0].Name, Is.EqualTo("get"));
    }

    // ================================================================
    // Functions
    // ================================================================

    [Test]
    public async Task Parse_FnParameters()
    {
        var result = (await ParseAsync("""
            struct S;
            impl S {
                pub fn process(&self, name: String, count: usize) -> bool {
                    true
                }
            }
            """, cancellationToken: CancellationToken.None));
        var m = (await AsyncSourceFacts.MembersAsync(result.Types.Single(t => t.Name == "S"), CancellationToken.None)).Methods.Single(m => m.Name == "process");
        // &self is skipped, so we expect name and count
        Assert.That(m.Parameters, Has.Count.EqualTo(2));
        Assert.That(m.Parameters[0].Name, Is.EqualTo("name"));
        Assert.That(m.Parameters[0].Type?.Name, Is.EqualTo("String"));
        Assert.That(m.Parameters[1].Name, Is.EqualTo("count"));
        Assert.That(m.Parameters[1].Type?.Name, Is.EqualTo("usize"));
    }

    [Test]
    public async Task Parse_FnReturnType()
    {
        var result = (await ParseAsync("""
            struct S;
            impl S {
                pub fn compute(&self) -> Vec<String> {
                    vec![]
                }
            }
            """, cancellationToken: CancellationToken.None));
        var m = (await AsyncSourceFacts.MembersAsync(result.Types.Single(t => t.Name == "S"), CancellationToken.None)).Methods.Single(m => m.Name == "compute");
        Assert.That(m.ReturnType, Is.Not.Null);
        Assert.That(m.ReturnType!.Name, Does.Contain("Vec"));
    }

    [Test]
    public async Task Parse_AsyncFn()
    {
        var result = (await ParseAsync("""
            struct Client;
            impl Client {
                pub async fn fetch(&self, url: &str) -> String {
                    String::new()
                }
            }
            """, cancellationToken: CancellationToken.None));
        var m = (await AsyncSourceFacts.MembersAsync(result.Types.Single(t => t.Name == "Client"), CancellationToken.None)).Methods.Single(m => m.Name == "fetch");
        Assert.That(m.IsAsync, Is.True);
    }

    [Test]
    public async Task Parse_FreeFn()
    {
        var result = (await ParseAsync("""
            pub fn add(a: i32, b: i32) -> i32 {
                a + b
            }
            """, cancellationToken: CancellationToken.None));
        // Free functions are exposed as methods of a synthetic per-file container type
        // (so doc/naming checks can see a module's free-function API surface).
        var container = result.Types.FirstOrDefault(t => t.Name.Contains("(functions)"))
            ?? throw new AssertionException("Expected a synthetic free-function container.");
        Assert.That(container, Is.Not.Null, "free functions should be collected into a synthetic container type");
        Assert.That((await AsyncSourceFacts.MembersAsync(container!, CancellationToken.None)).Methods.Select(m => m.Name), Does.Contain("add"));
        Assert.That((await AsyncSourceFacts.MembersAsync(container, CancellationToken.None)).Methods.First(m => m.Name == "add").IsPublic, Is.True);
    }

    [Test]
    public async Task Parse_FreeUnsafeFn_IsMarkedUnsafe()
    {
        // Regression: the item-level dispatch consumes `unsafe` (for unsafe trait/impl) and
        // previously failed to apply it to a free `unsafe fn`, so RustMethod.IsUnsafe was
        // false and rust-checks' missing-safety-doc rule could never fire on free functions.
        var result = (await ParseAsync("""
            pub unsafe fn danger() {
                let _ = 1;
            }

            pub fn safe_fn() {
                let _ = 2;
            }
            """, cancellationToken: CancellationToken.None));
        var container = result.Types.First(t => t.Name.Contains("(functions)"));
        var danger = (RustMethodDeclaration)(await AsyncSourceFacts.MembersAsync(container, CancellationToken.None)).Methods.First(m => m.Name == "danger");
        var safe = (RustMethodDeclaration)(await AsyncSourceFacts.MembersAsync(container, CancellationToken.None)).Methods.First(m => m.Name == "safe_fn");
        Assert.That(danger.IsUnsafe, Is.True, "free `unsafe fn` must be marked unsafe");
        Assert.That(safe.IsUnsafe, Is.False, "a non-unsafe free fn must not be marked unsafe");
    }

    // ================================================================
    // Statements
    // ================================================================

    [Test]
    public async Task Parse_MethodCalls()
    {
        var result = (await ParseAsync("""
            struct S;
            impl S {
                fn run(&self) {
                    self.setup();
                    self.execute();
                    println!("done");
                }
            }
            """, cancellationToken: CancellationToken.None));
        var stmts = result.Statements.Where(s => s.Kind == "call").ToList();
        Assert.That(stmts.Select(s => s.MemberName), Does.Contain("setup"));
        Assert.That(stmts.Select(s => s.MemberName), Does.Contain("execute"));
        Assert.That(stmts.Select(s => s.MemberName), Does.Contain("println!"));
    }

    [Test]
    public async Task Parse_QualifiedCalls()
    {
        var result = (await ParseAsync("""
            struct S;
            impl S {
                fn run(&self) {
                    Vec::new();
                    HashMap::with_capacity(10);
                }
            }
            """, cancellationToken: CancellationToken.None));
        var stmts = result.Statements.Where(s => s.Kind == "call").ToList();
        var vecNew = stmts.FirstOrDefault(s => s.MemberName == "new");
        Assert.That(vecNew, Is.Not.Null);
        Assert.That((await AsyncSourceFacts.TypeNameAsync(vecNew!, CancellationToken.None)).TypeName, Is.EqualTo("Vec"));
    }

    [Test]
    public async Task Parse_PanicAsThrow()
    {
        var result = (await ParseAsync("""
            struct S;
            impl S {
                fn fail(&self) {
                    panic!("something went wrong");
                }
            }
            """, cancellationToken: CancellationToken.None));
        var throws = result.Statements.Where(s => s.Kind == "throw").ToList();
        Assert.That(throws, Has.Count.EqualTo(1));
        Assert.That(throws[0].MemberName, Is.EqualTo("panic!"));
        Assert.That(((RustStatementInfo)throws[0]).IsMacroCall, Is.True);
    }

    [Test]
    public async Task Parse_TodoAndUnimplemented()
    {
        var result = (await ParseAsync("""
            struct S;
            impl S {
                fn wip(&self) {
                    todo!();
                }
                fn stub(&self) {
                    unimplemented!();
                }
            }
            """, cancellationToken: CancellationToken.None));
        var throws = result.Statements.Where(s => s.Kind == "throw").ToList();
        Assert.That(throws, Has.Count.EqualTo(2));
        Assert.That(throws.Select(s => s.MemberName), Is.EqualTo(new[] { "todo!", "unimplemented!" }));
    }

    // ================================================================
    // Doc comments
    // ================================================================

    [Test]
    public async Task Parse_DocComment_OnStruct()
    {
        var result = (await ParseAsync("""
            /// A documented struct
            pub struct Documented {
                pub value: i32,
            }
            """, cancellationToken: CancellationToken.None));
        Assert.That(result.Types[0].HasDocComment, Is.True);
    }

    [Test]
    public async Task Parse_NoDocComment()
    {
        var result = (await ParseAsync("""
            pub struct Undocumented {
                pub value: i32,
            }
            """, cancellationToken: CancellationToken.None));
        Assert.That(result.Types[0].HasDocComment, Is.False);
    }

    [Test]
    public async Task Parse_DocComment_OnMethod()
    {
        var result = (await ParseAsync("""
            pub trait Foo {
                /// Documented method
                fn documented(&self);
                fn undocumented(&self);
            }
            """, cancellationToken: CancellationToken.None));
        var doc = (await AsyncSourceFacts.MembersAsync(result.Types[0], CancellationToken.None)).Methods.First(m => m.Name == "documented");
        var undoc = (await AsyncSourceFacts.MembersAsync(result.Types[0], CancellationToken.None)).Methods.First(m => m.Name == "undocumented");
        Assert.That(doc.HasDocComment, Is.True);
        Assert.That(undoc.HasDocComment, Is.False);
    }

    // ================================================================
    // Attributes
    // ================================================================

    [Test]
    public async Task Parse_DeriveAttribute()
    {
        var result = (await ParseAsync("""
            #[derive(Debug, Clone)]
            pub struct Tagged {
                pub name: String,
            }
            """, cancellationToken: CancellationToken.None));
        var t = result.Types[0];
        var allDecorators = string.Join(" ", t.Decorators);
        Assert.That(allDecorators, Does.Contain("derive"));
        Assert.That(allDecorators, Does.Contain("Debug"));
        Assert.That(allDecorators, Does.Contain("Clone"));
    }

    // ================================================================
    // Lines and language
    // ================================================================

    [Test]
    public async Task Parse_Language()
    {
        var result = (await ParseAsync("fn main() {}", cancellationToken: CancellationToken.None));
        Assert.That(result.Language, Is.EqualTo("rust"));
    }

    [Test]
    public async Task Parse_CommentLines()
    {
        var result = (await ParseAsync("""
            // a comment
            /// doc comment
            fn main() {}
            """, cancellationToken: CancellationToken.None));
        // Lines 1 (// comment) and 2 (/// doc comment) are both comment lines.
        Assert.That(result.CommentLines, Has.Count.EqualTo(2));
        Assert.That(result.CommentLines, Does.Contain(1));
        Assert.That(result.CommentLines, Does.Contain(2));
    }

    // ================================================================
    // Edge cases
    // ================================================================

    [Test]
    public async Task Parse_EmptySource()
    {
        var result = (await ParseAsync("", cancellationToken: CancellationToken.None));
        Assert.That(result.Types, Is.Empty);
        Assert.That(result.Statements, Is.Empty);
        Assert.That(result.Usings, Is.Empty);
    }

    [Test]
    public async Task Parse_RawStrings()
    {
        var result = (await ParseAsync("""
            struct S;
            impl S {
                fn get_sql(&self) -> &str {
                    r#"SELECT * FROM "users" WHERE id = 1"#
                }
            }
            """, cancellationToken: CancellationToken.None));
        // Raw string literals must not break parsing — the struct and its method are still captured.
        Assert.That(result.Types.Any(t => t.Name == "S"), Is.True, "the struct S must be parsed");
        var methodNames = await result.Types.ToAsyncEnumerable().SelectMany(AsyncSourceFacts.MethodsAsync).Select(m => m.Name).ToArrayAsync();
        Assert.That(methodNames, Does.Contain("get_sql"),
            "the method whose body contains a raw string must be captured");
    }

    [Test]
    public async Task Parse_NestedBlockComments()
    {
        var result = (await ParseAsync("""
            /* outer /* inner */ still outer */
            pub struct AfterComment;
            """, cancellationToken: CancellationToken.None));
        Assert.That(result.Types, Has.Count.EqualTo(1));
        Assert.That(result.Types[0].Name, Is.EqualTo("AfterComment"));
    }

    [Test]
    public async Task Parse_Lifetimes()
    {
        var result = (await ParseAsync("""
            pub struct Ref<'a> {
                data: &'a str,
            }
            """, cancellationToken: CancellationToken.None));
        Assert.That(result.Types, Has.Count.EqualTo(1));
        Assert.That(result.Types[0].Name, Is.EqualTo("Ref"));
    }

    [Test]
    public async Task Parse_PubCrateVisibility()
    {
        var result = (await ParseAsync("""
            pub(crate) struct Internal {
                pub(crate) field: i32,
            }
            """, cancellationToken: CancellationToken.None));
        Assert.That(result.Types, Has.Count.EqualTo(1));
        Assert.That(result.Types[0].Name, Is.EqualTo("Internal"));
    }

    [Test]
    public async Task Parse_UnsafeTrait()
    {
        var result = (await ParseAsync("""
            pub unsafe trait UnsafeMarker {
                fn check(&self) -> bool;
            }
            """, cancellationToken: CancellationToken.None));
        Assert.That(result.Types, Has.Count.EqualTo(1));
        Assert.That(result.Types[0].Name, Is.EqualTo("UnsafeMarker"));
        Assert.That(result.Types[0].Kind, Is.EqualTo(TypeKind.Interface));
    }

    [Test]
    public async Task Parse_MultipleTypesInOneFile()
    {
        var result = (await ParseAsync("""
            use std::fmt;

            pub struct Foo;
            pub struct Bar;
            pub enum Baz { A, B }
            pub trait Qux {
                fn method(&self);
            }
            """, cancellationToken: CancellationToken.None));
        var names = result.Types.Select(t => t.Name).ToList();
        Assert.That(names, Does.Contain("Foo"));
        Assert.That(names, Does.Contain("Bar"));
        Assert.That(names, Does.Contain("Baz"));
        Assert.That(names, Does.Contain("Qux"));
    }

    [Test]
    public async Task Parse_RealWorldClientPattern()
    {
        var result = (await ParseAsync("""
            use reqwest::Client;
            use serde::{Deserialize, Serialize};

            /// HTTP client for the Foo service.
            #[derive(Debug, Clone)]
            pub struct FooClient {
                inner: Client,
                base_url: String,
            }

            impl FooClient {
                /// Creates a new FooClient.
                pub fn new(base_url: &str) -> Self {
                    FooClient {
                        inner: Client::new(),
                        base_url: base_url.to_string(),
                    }
                }

                /// Lists all items.
                pub async fn list_items(&self) -> Vec<Item> {
                    let resp = self.inner.get(&self.base_url).send().await.unwrap();
                    resp.json().await.unwrap()
                }

                /// Gets an item by ID.
                pub async fn get_item(&self, id: u64) -> Item {
                    let url = format!("{}/{}", self.base_url, id);
                    let resp = self.inner.get(&url).send().await.unwrap();
                    resp.json().await.unwrap()
                }

                fn internal_helper(&self) {
                    println!("helper");
                }
            }

            #[derive(Debug, Serialize, Deserialize)]
            pub struct Item {
                pub id: u64,
                pub name: String,
            }
            """, cancellationToken: CancellationToken.None));

        // Check usings
        Assert.That(result.Usings, Does.Contain("reqwest::Client"));
        Assert.That(result.Usings, Does.Contain("serde::Deserialize"));
        Assert.That(result.Usings, Does.Contain("serde::Serialize"));

        // Check struct types
        var fooClient = result.Types.First(t => t.Name == "FooClient");
        Assert.That(fooClient.IsPublic, Is.True);
        Assert.That(fooClient.HasDocComment, Is.True);

        var item = result.Types.First(t => t.Name == "Item");
        Assert.That(item.Fields, Has.Count.EqualTo(2));

        // Check impl
        var impl = fooClient;
        Assert.That(impl.Constructors, Is.Empty);
        Assert.That((await AsyncSourceFacts.MembersAsync(impl, CancellationToken.None)).Methods.Single(m => m.Name == "new").HasDocComment, Is.True);

        // Async methods
        var asyncMethods = (await AsyncSourceFacts.MembersAsync(impl, CancellationToken.None)).Methods.Where(m => m.IsAsync).ToList();
        Assert.That(asyncMethods, Has.Count.EqualTo(2));
        Assert.That(asyncMethods.Select(m => m.Name), Does.Contain("list_items"));
        Assert.That(asyncMethods.Select(m => m.Name), Does.Contain("get_item"));

        // Non-public internal method
        var helper = (await AsyncSourceFacts.MembersAsync(impl, CancellationToken.None)).Methods.First(m => m.Name == "internal_helper");
        Assert.That(helper.IsPublic, Is.False);
    }

    // ================================================================
    // Parameter patterns / hang regressions
    // ================================================================

    // Regression: a tuple-destructured parameter `(v1, v2): (T1, T2)` previously
    // sent the parameter loop into an infinite loop (it never advanced past '(').
    // This hung cop on azure-sdk-for-rust (cosmos partition_key.rs). The parse must
    // terminate and still recognize the method.
    [Test]
    [CancelAfter(5000)]
    public async Task Parse_TupleDestructuredParam_DoesNotHang()
    {
        var result = (await ParseWithin("""
            impl<T1, T2> From<(T1, T2)> for PartitionKey
            where
                T1: Into<PartitionKeyValue>,
                T2: Into<PartitionKeyValue>,
            {
                fn from((v1, v2): (T1, T2)) -> Self {
                    PartitionKey(vec![v1.into(), v2.into()])
                }
            }
            """, 5000));
        var impl = result.Types.First(t => t.Name.Contains("impl"));
        Assert.That((await AsyncSourceFacts.MembersAsync(impl, CancellationToken.None)).Methods.Select(m => m.Name).Concat(impl.Constructors.Select(c => c.Name)),
            Does.Contain("from"));
    }

    [Test]
    [CancelAfter(5000)]
    public async Task Parse_ClosureTypedParam_ParsesAsSingleType()
    {
        var result = (await ParseWithin("""
            struct S;
            impl S {
                pub fn apply(&self, f: impl Fn(i32) -> i32, x: i32) -> i32 {
                    f(x)
                }
            }
            """, 5000));
        var m = (await AsyncSourceFacts.MembersAsync(result.Types.Single(t => t.Name == "S"), CancellationToken.None)).Methods.Single(x => x.Name == "apply");
        // 'f' (closure) and 'x' should both be recognized as parameters.
        Assert.That(m.Parameters.Select(p => p.Name), Does.Contain("x"));
    }

    [Test]
    [CancelAfter(5000)]
    public async Task Parse_TupleTypedParam_DoesNotHang()
    {
        var result = (await ParseWithin("""
            struct S;
            impl S {
                pub fn handle(&self, pair: (i32, String), flag: bool) -> bool {
                    flag
                }
            }
            """, 5000));
        var m = (await AsyncSourceFacts.MembersAsync(result.Types.Single(t => t.Name == "S"), CancellationToken.None)).Methods.Single(x => x.Name == "handle");
        var pair = m.Parameters.First(p => p.Name == "pair");
        Assert.That(pair.Type?.Name, Does.Contain("i32"));
        Assert.That(m.Parameters.Select(p => p.Name), Does.Contain("flag"));
    }

    [Test]
    [CancelAfter(5000)]
    public async Task Parse_RefMutAndWildcardParams_DoNotHang()
    {
        var result = (await ParseWithin("""
            struct S;
            impl S {
                fn weird(&self, mut a: i32, ref b: String, _: u8) -> i32 {
                    a
                }
            }
            """, 5000));
        var m = (await AsyncSourceFacts.MembersAsync(result.Types.Single(t => t.Name == "S"), CancellationToken.None)).Methods.Single(x => x.Name == "weird");
        Assert.That(m.Parameters.Select(p => p.Name), Does.Contain("a"));
    }

    // ================================================================
    // Free functions — no double-counting, correct visibility
    // ================================================================

    // Regression: a `pub fn` free function was parsed twice (once while probing for a
    // type, once by the function branch), doubling every statement it contained.
    [Test]
    public async Task Parse_PubFreeFn_StatementsCountedOnce()
    {
        var result = (await ParseAsync("""
            pub fn run() {
                first();
                second();
                third();
            }
            """, cancellationToken: CancellationToken.None));
        var calls = result.Statements.Where(s => s.Kind == "call").ToList();
        Assert.That(calls.Count(s => s.MemberName == "first"), Is.EqualTo(1));
        Assert.That(calls.Count(s => s.MemberName == "second"), Is.EqualTo(1));
        Assert.That(calls.Count(s => s.MemberName == "third"), Is.EqualTo(1));
    }

    [Test]
    public async Task Parse_NonPubFreeFn_StatementsCountedOnce()
    {
        var result = (await ParseAsync("""
            fn run() {
                only_once();
            }
            """, cancellationToken: CancellationToken.None));
        var calls = result.Statements.Where(s => s.Kind == "call").ToList();
        Assert.That(calls.Count(s => s.MemberName == "only_once"), Is.EqualTo(1));
    }

    // Regression: the stray trailing advance after a free function consumed the `pub`
    // of a following item, mis-classifying it as private. A pub struct after a pub fn
    // must remain public.
    [Test]
    public async Task Parse_PubStructAfterPubFn_IsPublic()
    {
        var result = (await ParseAsync("""
            pub fn helper() {
                noop();
            }

            pub struct Config {
                pub value: i32,
            }
            """, cancellationToken: CancellationToken.None));
        var config = result.Types.First(t => t.Name == "Config");
        Assert.That(config.IsPublic, Is.True);
    }

    [Test]
    public async Task Parse_MultiplePubFreeFns_AllStatementsOnce()
    {
        var result = (await ParseAsync("""
            pub fn a() {
                call_a();
            }
            pub fn b() {
                call_b();
            }
            pub fn c() {
                call_c();
            }
            """, cancellationToken: CancellationToken.None));
        var calls = result.Statements.Where(s => s.Kind == "call").ToList();
        Assert.That(calls.Count(s => s.MemberName == "call_a"), Is.EqualTo(1));
        Assert.That(calls.Count(s => s.MemberName == "call_b"), Is.EqualTo(1));
        Assert.That(calls.Count(s => s.MemberName == "call_c"), Is.EqualTo(1));
    }

    // ================================================================
    // Robustness regressions (review swarm)
    // ================================================================

    [Test]
    [CancelAfter(5000)]
    public async Task Parse_ManyBlankLines_DoesNotStackOverflow()
    {
        // SkipWhitespace used to recurse per newline -> uncatchable StackOverflow.
        var src = new string('\n', 30000) + "pub fn after() { work(); }";
        var result = (await ParseWithin(src, 5000));
        Assert.That(result.Statements.Any(s => s.MemberName == "work"), Is.True);
    }

    [Test]
    public async Task Parse_CharLiteral_NotMisLexedAsLifetime()
    {
        var result = (await ParseAsync("""
            struct S;
            impl S {
                fn f(&self) {
                    helper('a');
                    after_call();
                }
            }
            """, cancellationToken: CancellationToken.None));
        var members = result.Statements.Where(s => s.Kind == "call").Select(s => s.MemberName).ToList();
        Assert.That(members, Does.Contain("helper"));
        Assert.That(members, Does.Contain("after_call"), "the char literal must not swallow the next call");
    }

    [Test]
    public async Task Parse_StructWithWhereClause_KeepsFields()
    {
        var result = (await ParseAsync("""
            pub struct Filter<T> where T: Send {
                value: T,
                name: String,
            }
            """, cancellationToken: CancellationToken.None));
        var t = result.Types.First(x => x.Name == "Filter");
        Assert.That(t.Fields.Select(f => f.Name), Does.Contain("value"));
        Assert.That(t.Fields.Select(f => f.Name), Does.Contain("name"));
    }

    [Test]
    public async Task Parse_BodylessFnWithWhere_DoesNotDropFollowingItems()
    {
        var result = (await ParseAsync("""
            pub trait T {
                fn foo() where Self: Sized;
                fn bar(&self);
            }
            """, cancellationToken: CancellationToken.None));
        var t = result.Types.First(x => x.Name == "T");
        Assert.That((await AsyncSourceFacts.MembersAsync(t, CancellationToken.None)).Methods.Select(m => m.Name), Does.Contain("foo"));
        Assert.That((await AsyncSourceFacts.MembersAsync(t, CancellationToken.None)).Methods.Select(m => m.Name), Does.Contain("bar"));
    }

    [Test]
    public async Task Parse_TraitWithLifetimeSupertrait_KeepsBody()
    {
        var result = (await ParseAsync("""
            pub trait Foo: 'static {
                fn m(&self);
                fn n(&self);
            }
            """, cancellationToken: CancellationToken.None));
        var t = result.Types.First(x => x.Name == "Foo");
        Assert.That((await AsyncSourceFacts.MembersAsync(t, CancellationToken.None)).Methods.Select(m => m.Name), Does.Contain("m"));
        Assert.That((await AsyncSourceFacts.MembersAsync(t, CancellationToken.None)).Methods.Select(m => m.Name), Does.Contain("n"));
    }

    [Test]
    public async Task Parse_RawIdentifierStruct_IsCaptured()
    {
        var result = (await ParseAsync("pub struct r#Match { pub x: i32 }", cancellationToken: CancellationToken.None));
        Assert.That(result.Types.Select(t => t.Name), Does.Contain("Match"));
    }

    [Test]
    public async Task Parse_Union_IsCaptured()
    {
        var result = (await ParseAsync("pub union MyUnion { a: i32, b: f32 }", cancellationToken: CancellationToken.None));
        var u = result.Types.First(t => t.Name == "MyUnion");
        Assert.That(u.Fields, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task Parse_PubCrate_IsNotPublic()
    {
        var result = (await ParseAsync("pub(crate) struct CrateThing { pub x: i32 }", cancellationToken: CancellationToken.None));
        var t = result.Types.First(x => x.Name == "CrateThing");
        Assert.That(t.IsPublic, Is.False, "pub(crate) is restricted, not public API");
    }

    [Test]
    public async Task Parse_PublicTraitMethods_ArePublic()
    {
        var result = (await ParseAsync("""
            pub trait Greeter {
                fn greet(&self) -> String;
            }
            """, cancellationToken: CancellationToken.None));
        var greet = (await AsyncSourceFacts.MembersAsync(result.Types.First(t => t.Name == "Greeter"), CancellationToken.None)).Methods.First(m => m.Name == "greet");
        Assert.That(greet.IsPublic, Is.True, "a public trait's methods are public API");
    }

    [Test]
    public async Task Parse_NestedCallsInArguments_AreCaptured()
    {
        var result = (await ParseAsync("""
            struct S;
            impl S {
                fn f(&self) {
                    log(process(data.unwrap()));
                }
            }
            """, cancellationToken: CancellationToken.None));
        var members = result.Statements.Where(s => s.Kind == "call").Select(s => s.MemberName).ToList();
        Assert.That(members, Does.Contain("log"));
        Assert.That(members, Does.Contain("process"));
        Assert.That(members, Does.Contain("unwrap"), "a call nested in arguments must still be captured");
    }

    [Test]
    public async Task Parse_PanicInClosureArgument_IsCaptured()
    {
        var result = (await ParseAsync("""
            struct S;
            impl S {
                fn f(&self) {
                    items.for_each(|x| panic!("boom"));
                }
            }
            """, cancellationToken: CancellationToken.None));
        Assert.That(result.Statements.Count(s => s.Kind == "throw" && s.MemberName == "panic!"), Is.EqualTo(1));
    }

    [Test]
    public async Task Parse_ImplForNonIdentifierTarget_KeepsNameAndMethods()
    {
        var result = (await ParseAsync("""
            impl MyTrait for [u8] {
                fn go(&self) {}
            }
            """, cancellationToken: CancellationToken.None));
        var impl = result.Types.First(t => t.Name.Contains("(impl"));
        Assert.That(impl.Name.Trim(), Does.Not.StartWith("(impl"), "impl target name must not be empty");
        Assert.That((await AsyncSourceFacts.MembersAsync(impl, CancellationToken.None)).Methods.Select(m => m.Name), Does.Contain("go"), "impl-on-slice methods must be captured");
        Assert.That(impl.BaseTypes, Does.Contain("MyTrait"));
    }

    [Test]
    public async Task Parse_ImplWithPathTrait_UsesTraitNotFirstSegment()
    {
        var result = (await ParseAsync("""
            impl std::fmt::Debug for Foo {
                fn fmt(&self) {}
            }
            """, cancellationToken: CancellationToken.None));
        var impl = result.Types.First(t => t.Name.Contains("(impl"));
        Assert.That(impl.BaseTypes, Is.EqualTo(new[] { "std::fmt::Debug" }));
        Assert.That(impl.BaseTypes, Does.Not.Contain("std"));
        Assert.That(impl.Name, Does.StartWith("Foo"));
    }

    [Test]
    public async Task Parse_PubConstFnInImpl_IsPublic()
    {
        var result = (await ParseAsync("""
            struct S;
            impl S {
                pub const fn make() -> i32 { 1 }
            }
            """, cancellationToken: CancellationToken.None));
        var make = (await AsyncSourceFacts.MembersAsync(result.Types.Single(t => t.Name == "S"), CancellationToken.None)).Methods.Single(m => m.Name == "make");
        Assert.That(make.IsPublic, Is.True);
    }

    [Test]
    public async Task Parse_MacroRulesBody_IsNotParsedAsCode()
    {
        var result = (await ParseAsync("""
            macro_rules! make {
                () => { fn phantom_fn() { phantom_call(); } };
            }
            pub fn real() { actual_call(); }
            """, cancellationToken: CancellationToken.None));
        Assert.That(result.Statements.Any(s => s.MemberName == "phantom_call"), Is.False,
            "macro_rules! template tokens must not be parsed as real calls");
        Assert.That(result.Statements.Any(s => s.MemberName == "actual_call"), Is.True);
    }

    [Test]
    public async Task Parse_BlockDocComment_IsRecognized()
    {
        var result = (await ParseAsync("""
            /** A documented struct */
            pub struct Documented { x: i32 }
            """, cancellationToken: CancellationToken.None));
        Assert.That(result.Types.First(t => t.Name == "Documented").HasDocComment, Is.True);
    }

    [Test]
    public async Task Parse_NegativeImpl_HasTargetName()
    {
        var result = (await ParseAsync("impl !Send for Foo {}", cancellationToken: CancellationToken.None));
        var impl = result.Types.First(t => t.Name.Contains("(impl"));
        Assert.That(impl.Name, Does.StartWith("Foo"), "negative-impl target must be named, not '?'");
        Assert.That(impl.BaseTypes, Does.Contain("Send"));
    }

    // ================================================================
    // ParseErrors — negative tests and positive control
    // ================================================================

    [Test]
    public async Task ParseErrors_MissingStructName_ReportsError()
    {
        var result = (await ParseAsync("struct { x: i32 }", cancellationToken: CancellationToken.None));
        Assert.That(result.ParseErrors, Is.Not.Empty,
            "a struct with no name should produce at least one parse error");
        Assert.That(result.ParseErrors.Any(e => e.Contains("struct name") || e.Contains("expected a name")), Is.True,
            "the error message should mention 'struct name'");
    }

    [Test]
    public async Task ParseErrors_MissingFnName_ReportsError()
    {
        var result = (await ParseAsync("fn () {}", cancellationToken: CancellationToken.None));
        Assert.That(result.ParseErrors, Is.Not.Empty,
            "a fn with no name should produce at least one parse error");
        Assert.That(result.ParseErrors.Any(e => e.Contains("function name") || e.Contains("expected a name")), Is.True,
            "the error message should mention 'function name'");
    }

    [Test]
    public async Task ParseErrors_UnterminatedBlockComment_ReportsError()
    {
        var result = (await ParseAsync("/* this comment never ends\npub struct Foo {}", cancellationToken: CancellationToken.None));
        Assert.That(result.ParseErrors, Is.Not.Empty,
            "an unterminated block comment should produce a parse error");
        Assert.That(result.ParseErrors.Any(e => e.Contains("unterminated") || e.Contains("terminate the block comment")), Is.True,
            "the error message should mention 'unterminated'");
    }

    [Test]
    public async Task ParseErrors_UnterminatedRawString_ReportsError()
    {
        var result = (await ParseAsync("fn f() {\n    let s = r#\"never ends;\n}", cancellationToken: CancellationToken.None));
        Assert.That(result.ParseErrors, Is.Not.Empty,
            "an unterminated raw string should produce a parse error");
        Assert.That(result.ParseErrors.Any(e => e.Contains("unterminated") || e.Contains("Invalid raw string literal")), Is.True,
            "the error message should mention 'unterminated'");
    }

    [Test]
    public async Task ParseErrors_UnclosedBrace_ReportsError()
    {
        // A fn body that is never closed — the structural parser tolerates this, so the
        // delimiter-balance check must catch it (parity with the other language parsers).
        var result = (await ParseAsync("fn f() {\n    let x = 1;\n", cancellationToken: CancellationToken.None));
        Assert.That(result.ParseErrors, Is.Not.Empty,
            "an unclosed '{' should produce a parse error");
        Assert.That(result.ParseErrors.Any(e => e.Contains("unclosed") || e.Contains("expected R_CURLY")), Is.True,
            "the error message should mention 'unclosed'");
    }

    [Test]
    public async Task ParseErrors_StrayClosingBrace_ReportsError()
    {
        var result = (await ParseAsync("fn f() {}\n}", cancellationToken: CancellationToken.None));
        Assert.That(result.ParseErrors.Any(e => e.Contains("unmatched")), Is.True,
            "a stray '}' should be reported as unmatched");
    }

    [Test]
    public async Task ParseErrors_ValidRust_IsEmpty()
    {
        // A realistic, modern Rust snippet — generics, lifetimes, closures, macros,
        // async fn, trait bounds, raw strings.  Must produce ZERO ParseErrors.
        var result = (await ParseAsync("""
            use std::collections::HashMap;
            use std::fmt;

            /// A generic container.
            #[derive(Debug, Clone)]
            pub struct Container<T: Clone + Send + Sync> {
                inner: Vec<T>,
                meta: HashMap<String, String>,
            }

            impl<T: Clone + Send + Sync> Container<T> {
                pub fn new() -> Self {
                    Container { inner: Vec::new(), meta: HashMap::new() }
                }
                pub fn add(&mut self, item: T) {
                    self.inner.push(item);
                }
                pub fn get(&self, idx: usize) -> Option<&T> {
                    self.inner.get(idx)
                }
                pub async fn fetch(&self, key: &str) -> Option<String> {
                    self.meta.get(key).cloned()
                }
            }

            pub trait Printable: fmt::Display + fmt::Debug {
                fn print(&self) {
                    println!("{}", self);
                }
            }

            pub fn process<'a>(s: &'a str, items: &[u32]) -> Vec<String> {
                let prefix = r#"item"#;
                items.iter()
                    .filter(|&&x| x > 0)
                    .map(|&x| format!("{}{}: {}", prefix, s, x))
                    .collect()
            }
            """, cancellationToken: CancellationToken.None));
        Assert.That(result.ParseErrors, Is.Empty,
            "valid modern Rust must produce zero parse errors");
    }

    // ================================================================
    // Helper
    // ================================================================

    private async Task<SourceFile> ParseWithin(string source, int milliseconds)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.CurrentContext.CancellationToken);
        deadline.CancelAfter(milliseconds);
        var result = await _parser.ParseAsync("test.rs", source, deadline.Token).AsTask().WaitAsync(deadline.Token);
        deadline.Token.ThrowIfCancellationRequested();
        Assert.That(result, Is.Not.Null, "Parser returned null");
        return result!;
    }

    private async Task<SourceFile> ParseAsync(string source, CancellationToken cancellationToken = default)
    {
        var result = (await _parser.ParseAsync("test.rs", source, cancellationToken: cancellationToken));
        Assert.That(result, Is.Not.Null, "Parser returned null");
        return result!;
    }
}
