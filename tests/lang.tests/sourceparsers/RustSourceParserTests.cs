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
    public void Parse_SimpleUse()
    {
        var result = Parse("""
            use std::collections::HashMap;
            use std::io;
            """);
        Assert.That(result.Usings, Does.Contain("std::collections::HashMap"));
        Assert.That(result.Usings, Does.Contain("std::io"));
    }

    [Test]
    public void Parse_GroupedUse()
    {
        var result = Parse("""
            use std::collections::{HashMap, BTreeMap, HashSet};
            """);
        Assert.That(result.Usings, Does.Contain("std::collections::HashMap"));
        Assert.That(result.Usings, Does.Contain("std::collections::BTreeMap"));
        Assert.That(result.Usings, Does.Contain("std::collections::HashSet"));
    }

    [Test]
    public void Parse_UseWithAlias()
    {
        var result = Parse("""
            use std::fmt::Result as FmtResult;
            """);
        // Should capture the path (alias is not part of the module path)
        Assert.That(result.Usings.Count, Is.GreaterThan(0));
    }

    [Test]
    public void Parse_NoUse_EmptyUsings()
    {
        var result = Parse("""
            fn main() {
                println!("hello");
            }
            """);
        Assert.That(result.Usings, Is.Empty);
    }

    // ================================================================
    // Structs
    // ================================================================

    [Test]
    public void Parse_PublicStruct()
    {
        var result = Parse("""
            pub struct Config {
                pub name: String,
                pub timeout: u64,
            }
            """);
        Assert.That(result.Types, Has.Count.EqualTo(1));
        var t = result.Types[0];
        Assert.That(t.Name, Is.EqualTo("Config"));
        Assert.That(t.Kind, Is.EqualTo(TypeKind.Struct));
        Assert.That(t.IsPublic, Is.True);
    }

    [Test]
    public void Parse_StructFields()
    {
        var result = Parse("""
            pub struct Point {
                pub x: f64,
                pub y: f64,
                label: String,
            }
            """);
        var t = result.Types[0];
        Assert.That(t.Fields, Has.Count.EqualTo(3));
        var xField = t.Fields.First(f => f.Name == "x");
        Assert.That(xField.Type?.Name, Is.EqualTo("f64"));
        Assert.That(xField.IsPublic, Is.True);
        var labelField = t.Fields.First(f => f.Name == "label");
        Assert.That(labelField.IsPublic, Is.False);
    }

    [Test]
    public void Parse_PrivateStruct()
    {
        var result = Parse("""
            struct Internal {
                data: Vec<u8>,
            }
            """);
        var t = result.Types[0];
        Assert.That(t.Name, Is.EqualTo("Internal"));
        Assert.That(t.IsPublic, Is.False);
    }

    [Test]
    public void Parse_UnitStruct()
    {
        var result = Parse("""
            pub struct Marker;
            """);
        Assert.That(result.Types, Has.Count.EqualTo(1));
        Assert.That(result.Types[0].Name, Is.EqualTo("Marker"));
    }

    [Test]
    public void Parse_TupleStruct()
    {
        var result = Parse("""
            pub struct Wrapper(pub String, i32);
            """);
        Assert.That(result.Types, Has.Count.EqualTo(1));
        Assert.That(result.Types[0].Name, Is.EqualTo("Wrapper"));
    }

    [Test]
    public void Parse_GenericStruct()
    {
        var result = Parse("""
            pub struct Container<T: Clone> {
                inner: T,
            }
            """);
        Assert.That(result.Types, Has.Count.EqualTo(1));
        Assert.That(result.Types[0].Name, Is.EqualTo("Container"));
    }

    [Test]
    public void Parse_StructWithWhereClause()
    {
        var result = Parse("""
            pub struct Filter<T>
            where
                T: Send + Sync,
            {
                value: T,
            }
            """);
        Assert.That(result.Types, Has.Count.EqualTo(1));
        Assert.That(result.Types[0].Name, Is.EqualTo("Filter"));
    }

    // ================================================================
    // Enums
    // ================================================================

    [Test]
    public void Parse_SimpleEnum()
    {
        var result = Parse("""
            pub enum Color {
                Red,
                Green,
                Blue,
            }
            """);
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
    public void Parse_EnumWithData()
    {
        var result = Parse("""
            pub enum Shape {
                Circle(f64),
                Rectangle { width: f64, height: f64 },
                Point,
            }
            """);
        var t = result.Types[0];
        Assert.That(t.Name, Is.EqualTo("Shape"));
        Assert.That(t.EnumValues, Does.Contain("Circle"));
        Assert.That(t.EnumValues, Does.Contain("Rectangle"));
        Assert.That(t.EnumValues, Does.Contain("Point"));
    }

    [Test]
    public void Parse_EnumWithDiscriminant()
    {
        var result = Parse("""
            pub enum Status {
                Active = 1,
                Inactive = 0,
            }
            """);
        var t = result.Types[0];
        Assert.That(t.EnumValues, Does.Contain("Active"));
        Assert.That(t.EnumValues, Does.Contain("Inactive"));
    }

    // ================================================================
    // Traits
    // ================================================================

    [Test]
    public void Parse_Trait()
    {
        var result = Parse("""
            pub trait Drawable {
                fn draw(&self);
                fn area(&self) -> f64;
            }
            """);
        Assert.That(result.Types, Has.Count.EqualTo(1));
        var t = result.Types[0];
        Assert.That(t.Name, Is.EqualTo("Drawable"));
        Assert.That(t.Kind, Is.EqualTo(TypeKind.Interface));
        Assert.That(t.Methods, Has.Count.EqualTo(2));
        Assert.That(t.Methods.Select(m => m.Name), Does.Contain("draw"));
        Assert.That(t.Methods.Select(m => m.Name), Does.Contain("area"));
    }

    [Test]
    public void Parse_TraitWithSupertraits()
    {
        var result = Parse("""
            pub trait Serializable: Clone + Send {
                fn serialize(&self) -> Vec<u8>;
            }
            """);
        var t = result.Types[0];
        Assert.That(t.Name, Is.EqualTo("Serializable"));
        Assert.That(t.BaseTypes, Does.Contain("Clone"));
        Assert.That(t.BaseTypes, Does.Contain("Send"));
    }

    [Test]
    public void Parse_TraitWithDefaultMethod()
    {
        var result = Parse("""
            pub trait Logger {
                fn log(&self, msg: &str);
                fn warn(&self, msg: &str) {
                    self.log(msg);
                }
            }
            """);
        var t = result.Types[0];
        Assert.That(t.Methods, Has.Count.EqualTo(2));
    }

    // ================================================================
    // Impl blocks
    // ================================================================

    [Test]
    public void Parse_InherentImpl()
    {
        var result = Parse("""
            struct Foo;
            impl Foo {
                pub fn new() -> Self {
                    Foo
                }
                pub fn bar(&self) -> i32 {
                    42
                }
            }
            """);
        // Should produce the struct + impl type
        Assert.That(result.Types.Count, Is.GreaterThanOrEqualTo(2));
        var impl = result.Types.First(t => t.Name.Contains("impl"));
        Assert.That(impl.Constructors, Has.Count.EqualTo(1));
        Assert.That(impl.Constructors[0].Name, Is.EqualTo("new"));
        Assert.That(impl.Methods, Has.Count.EqualTo(1));
        Assert.That(impl.Methods[0].Name, Is.EqualTo("bar"));
    }

    [Test]
    public void Parse_TraitImpl()
    {
        var result = Parse("""
            struct Circle { radius: f64 }
            impl Drawable for Circle {
                fn draw(&self) {
                    println!("Drawing circle");
                }
            }
            """);
        var impl = result.Types.First(t => t.Name.Contains("impl"));
        Assert.That(impl.Name, Does.Contain("Drawable"));
        Assert.That(impl.BaseTypes, Does.Contain("Drawable"));
    }

    [Test]
    public void Parse_ImplWithGenericAndWhereClause()
    {
        var result = Parse("""
            impl<T> Container<T>
            where
                T: Clone + Send,
            {
                pub fn get(&self) -> &T {
                    &self.inner
                }
            }
            """);
        var impl = result.Types.First(t => t.Name.Contains("impl"));
        Assert.That(impl.Methods, Has.Count.EqualTo(1));
        Assert.That(impl.Methods[0].Name, Is.EqualTo("get"));
    }

    // ================================================================
    // Functions
    // ================================================================

    [Test]
    public void Parse_FnParameters()
    {
        var result = Parse("""
            struct S;
            impl S {
                pub fn process(&self, name: String, count: usize) -> bool {
                    true
                }
            }
            """);
        var impl = result.Types.First(t => t.Name.Contains("impl"));
        var m = impl.Methods.First(m => m.Name == "process");
        // &self is skipped, so we expect name and count
        Assert.That(m.Parameters, Has.Count.EqualTo(2));
        Assert.That(m.Parameters[0].Name, Is.EqualTo("name"));
        Assert.That(m.Parameters[0].Type?.Name, Is.EqualTo("String"));
        Assert.That(m.Parameters[1].Name, Is.EqualTo("count"));
        Assert.That(m.Parameters[1].Type?.Name, Is.EqualTo("usize"));
    }

    [Test]
    public void Parse_FnReturnType()
    {
        var result = Parse("""
            struct S;
            impl S {
                pub fn compute(&self) -> Vec<String> {
                    vec![]
                }
            }
            """);
        var impl = result.Types.First(t => t.Name.Contains("impl"));
        var m = impl.Methods.First(m => m.Name == "compute");
        Assert.That(m.ReturnType, Is.Not.Null);
        Assert.That(m.ReturnType!.Name, Does.Contain("Vec"));
    }

    [Test]
    public void Parse_AsyncFn()
    {
        var result = Parse("""
            struct Client;
            impl Client {
                pub async fn fetch(&self, url: &str) -> String {
                    String::new()
                }
            }
            """);
        var impl = result.Types.First(t => t.Name.Contains("impl"));
        var m = impl.Methods.First(m => m.Name == "fetch");
        Assert.That(m.IsAsync, Is.True);
    }

    [Test]
    public void Parse_FreeFn()
    {
        var result = Parse("""
            pub fn add(a: i32, b: i32) -> i32 {
                a + b
            }
            """);
        // Free functions are exposed as methods of a synthetic per-file container type
        // (so doc/naming checks can see a module's free-function API surface).
        var container = result.Types.FirstOrDefault(t => t.Name.Contains("(functions)"));
        Assert.That(container, Is.Not.Null, "free functions should be collected into a synthetic container type");
        Assert.That(container!.Methods.Select(m => m.Name), Does.Contain("add"));
        Assert.That(container.Methods.First(m => m.Name == "add").IsPublic, Is.True);
    }

    // ================================================================
    // Statements
    // ================================================================

    [Test]
    public void Parse_MethodCalls()
    {
        var result = Parse("""
            struct S;
            impl S {
                fn run(&self) {
                    self.setup();
                    self.execute();
                    println!("done");
                }
            }
            """);
        var stmts = result.Statements.Where(s => s.Kind == "call").ToList();
        Assert.That(stmts.Select(s => s.MemberName), Does.Contain("setup"));
        Assert.That(stmts.Select(s => s.MemberName), Does.Contain("execute"));
        Assert.That(stmts.Select(s => s.MemberName), Does.Contain("println!"));
    }

    [Test]
    public void Parse_QualifiedCalls()
    {
        var result = Parse("""
            struct S;
            impl S {
                fn run(&self) {
                    Vec::new();
                    HashMap::with_capacity(10);
                }
            }
            """);
        var stmts = result.Statements.Where(s => s.Kind == "call").ToList();
        var vecNew = stmts.FirstOrDefault(s => s.MemberName == "new");
        Assert.That(vecNew, Is.Not.Null);
        Assert.That(vecNew!.TypeName, Is.EqualTo("Vec"));
    }

    [Test]
    public void Parse_PanicAsThrow()
    {
        var result = Parse("""
            struct S;
            impl S {
                fn fail(&self) {
                    panic!("something went wrong");
                }
            }
            """);
        var throws = result.Statements.Where(s => s.Kind == "throw").ToList();
        Assert.That(throws, Has.Count.EqualTo(1));
        Assert.That(throws[0].MemberName, Is.EqualTo("panic"));
    }

    [Test]
    public void Parse_TodoAndUnimplemented()
    {
        var result = Parse("""
            struct S;
            impl S {
                fn wip(&self) {
                    todo!();
                }
                fn stub(&self) {
                    unimplemented!();
                }
            }
            """);
        var throws = result.Statements.Where(s => s.Kind == "throw").ToList();
        Assert.That(throws, Has.Count.EqualTo(2));
        Assert.That(throws.Select(s => s.MemberName), Does.Contain("todo"));
        Assert.That(throws.Select(s => s.MemberName), Does.Contain("unimplemented"));
    }

    // ================================================================
    // Doc comments
    // ================================================================

    [Test]
    public void Parse_DocComment_OnStruct()
    {
        var result = Parse("""
            /// A documented struct
            pub struct Documented {
                pub value: i32,
            }
            """);
        Assert.That(result.Types[0].HasDocComment, Is.True);
    }

    [Test]
    public void Parse_NoDocComment()
    {
        var result = Parse("""
            pub struct Undocumented {
                pub value: i32,
            }
            """);
        Assert.That(result.Types[0].HasDocComment, Is.False);
    }

    [Test]
    public void Parse_DocComment_OnMethod()
    {
        var result = Parse("""
            pub trait Foo {
                /// Documented method
                fn documented(&self);
                fn undocumented(&self);
            }
            """);
        var doc = result.Types[0].Methods.First(m => m.Name == "documented");
        var undoc = result.Types[0].Methods.First(m => m.Name == "undocumented");
        Assert.That(doc.HasDocComment, Is.True);
        Assert.That(undoc.HasDocComment, Is.False);
    }

    // ================================================================
    // Attributes
    // ================================================================

    [Test]
    public void Parse_DeriveAttribute()
    {
        var result = Parse("""
            #[derive(Debug, Clone)]
            pub struct Tagged {
                pub name: String,
            }
            """);
        var t = result.Types[0];
        Assert.That(t.Decorators, Has.Count.GreaterThan(0));
        Assert.That(t.Decorators[0], Does.Contain("derive"));
    }

    // ================================================================
    // Lines and language
    // ================================================================

    [Test]
    public void Parse_Language()
    {
        var result = Parse("fn main() {}");
        Assert.That(result.Language, Is.EqualTo("rust"));
    }

    [Test]
    public void Parse_CommentLines()
    {
        var result = Parse("""
            // a comment
            /// doc comment
            fn main() {}
            """);
        Assert.That(result.CommentLines, Is.Not.Empty);
    }

    // ================================================================
    // Edge cases
    // ================================================================

    [Test]
    public void Parse_EmptySource()
    {
        var result = Parse("");
        Assert.That(result.Types, Is.Empty);
        Assert.That(result.Statements, Is.Empty);
        Assert.That(result.Usings, Is.Empty);
    }

    [Test]
    public void Parse_RawStrings()
    {
        var result = Parse("""
            struct S;
            impl S {
                fn get_sql(&self) -> &str {
                    r#"SELECT * FROM "users" WHERE id = 1"#
                }
            }
            """);
        // Should not crash on raw string literals
        Assert.That(result.Types.Count, Is.GreaterThan(0));
    }

    [Test]
    public void Parse_NestedBlockComments()
    {
        var result = Parse("""
            /* outer /* inner */ still outer */
            pub struct AfterComment;
            """);
        Assert.That(result.Types, Has.Count.EqualTo(1));
        Assert.That(result.Types[0].Name, Is.EqualTo("AfterComment"));
    }

    [Test]
    public void Parse_Lifetimes()
    {
        var result = Parse("""
            pub struct Ref<'a> {
                data: &'a str,
            }
            """);
        Assert.That(result.Types, Has.Count.EqualTo(1));
        Assert.That(result.Types[0].Name, Is.EqualTo("Ref"));
    }

    [Test]
    public void Parse_PubCrateVisibility()
    {
        var result = Parse("""
            pub(crate) struct Internal {
                pub(crate) field: i32,
            }
            """);
        Assert.That(result.Types, Has.Count.EqualTo(1));
        Assert.That(result.Types[0].Name, Is.EqualTo("Internal"));
    }

    [Test]
    public void Parse_UnsafeTrait()
    {
        var result = Parse("""
            pub unsafe trait UnsafeMarker {
                fn check(&self) -> bool;
            }
            """);
        Assert.That(result.Types, Has.Count.EqualTo(1));
        Assert.That(result.Types[0].Name, Is.EqualTo("UnsafeMarker"));
        Assert.That(result.Types[0].Kind, Is.EqualTo(TypeKind.Interface));
    }

    [Test]
    public void Parse_MultipleTypesInOneFile()
    {
        var result = Parse("""
            use std::fmt;

            pub struct Foo;
            pub struct Bar;
            pub enum Baz { A, B }
            pub trait Qux {
                fn method(&self);
            }
            """);
        var names = result.Types.Select(t => t.Name).ToList();
        Assert.That(names, Does.Contain("Foo"));
        Assert.That(names, Does.Contain("Bar"));
        Assert.That(names, Does.Contain("Baz"));
        Assert.That(names, Does.Contain("Qux"));
    }

    [Test]
    public void Parse_RealWorldClientPattern()
    {
        var result = Parse("""
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
            """);

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
        var impl = result.Types.First(t => t.Name.Contains("FooClient") && t.Name.Contains("impl"));
        Assert.That(impl.Constructors, Has.Count.EqualTo(1));
        Assert.That(impl.Constructors[0].Name, Is.EqualTo("new"));
        Assert.That(impl.Constructors[0].HasDocComment, Is.True);

        // Async methods
        var asyncMethods = impl.Methods.Where(m => m.IsAsync).ToList();
        Assert.That(asyncMethods, Has.Count.EqualTo(2));
        Assert.That(asyncMethods.Select(m => m.Name), Does.Contain("list_items"));
        Assert.That(asyncMethods.Select(m => m.Name), Does.Contain("get_item"));

        // Non-public internal method
        var helper = impl.Methods.First(m => m.Name == "internal_helper");
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
    public void Parse_TupleDestructuredParam_DoesNotHang()
    {
        var result = ParseWithin("""
            impl<T1, T2> From<(T1, T2)> for PartitionKey
            where
                T1: Into<PartitionKeyValue>,
                T2: Into<PartitionKeyValue>,
            {
                fn from((v1, v2): (T1, T2)) -> Self {
                    PartitionKey(vec![v1.into(), v2.into()])
                }
            }
            """, 5000);
        var impl = result.Types.First(t => t.Name.Contains("impl"));
        Assert.That(impl.Methods.Select(m => m.Name).Concat(impl.Constructors.Select(c => c.Name)),
            Does.Contain("from"));
    }

    [Test]
    public void Parse_ClosureTypedParam_ParsesAsSingleType()
    {
        var result = ParseWithin("""
            struct S;
            impl S {
                pub fn apply(&self, f: impl Fn(i32) -> i32, x: i32) -> i32 {
                    f(x)
                }
            }
            """, 5000);
        var impl = result.Types.First(t => t.Name.Contains("impl"));
        var m = impl.Methods.First(x => x.Name == "apply");
        // 'f' (closure) and 'x' should both be recognized as parameters.
        Assert.That(m.Parameters.Select(p => p.Name), Does.Contain("x"));
    }

    [Test]
    public void Parse_TupleTypedParam_DoesNotHang()
    {
        var result = ParseWithin("""
            struct S;
            impl S {
                pub fn handle(&self, pair: (i32, String), flag: bool) -> bool {
                    flag
                }
            }
            """, 5000);
        var impl = result.Types.First(t => t.Name.Contains("impl"));
        var m = impl.Methods.First(x => x.Name == "handle");
        var pair = m.Parameters.First(p => p.Name == "pair");
        Assert.That(pair.Type?.Name, Does.Contain("i32"));
        Assert.That(m.Parameters.Select(p => p.Name), Does.Contain("flag"));
    }

    [Test]
    public void Parse_RefMutAndWildcardParams_DoNotHang()
    {
        var result = ParseWithin("""
            struct S;
            impl S {
                fn weird(&self, mut a: i32, ref b: String, _: u8) -> i32 {
                    a
                }
            }
            """, 5000);
        var impl = result.Types.First(t => t.Name.Contains("impl"));
        var m = impl.Methods.First(x => x.Name == "weird");
        Assert.That(m.Parameters.Select(p => p.Name), Does.Contain("a"));
    }

    // ================================================================
    // Free functions — no double-counting, correct visibility
    // ================================================================

    // Regression: a `pub fn` free function was parsed twice (once while probing for a
    // type, once by the function branch), doubling every statement it contained.
    [Test]
    public void Parse_PubFreeFn_StatementsCountedOnce()
    {
        var result = Parse("""
            pub fn run() {
                first();
                second();
                third();
            }
            """);
        var calls = result.Statements.Where(s => s.Kind == "call").ToList();
        Assert.That(calls.Count(s => s.MemberName == "first"), Is.EqualTo(1));
        Assert.That(calls.Count(s => s.MemberName == "second"), Is.EqualTo(1));
        Assert.That(calls.Count(s => s.MemberName == "third"), Is.EqualTo(1));
    }

    [Test]
    public void Parse_NonPubFreeFn_StatementsCountedOnce()
    {
        var result = Parse("""
            fn run() {
                only_once();
            }
            """);
        var calls = result.Statements.Where(s => s.Kind == "call").ToList();
        Assert.That(calls.Count(s => s.MemberName == "only_once"), Is.EqualTo(1));
    }

    // Regression: the stray trailing advance after a free function consumed the `pub`
    // of a following item, mis-classifying it as private. A pub struct after a pub fn
    // must remain public.
    [Test]
    public void Parse_PubStructAfterPubFn_IsPublic()
    {
        var result = Parse("""
            pub fn helper() {
                noop();
            }

            pub struct Config {
                pub value: i32,
            }
            """);
        var config = result.Types.First(t => t.Name == "Config");
        Assert.That(config.IsPublic, Is.True);
    }

    [Test]
    public void Parse_MultiplePubFreeFns_AllStatementsOnce()
    {
        var result = Parse("""
            pub fn a() {
                call_a();
            }
            pub fn b() {
                call_b();
            }
            pub fn c() {
                call_c();
            }
            """);
        var calls = result.Statements.Where(s => s.Kind == "call").ToList();
        Assert.That(calls.Count(s => s.MemberName == "call_a"), Is.EqualTo(1));
        Assert.That(calls.Count(s => s.MemberName == "call_b"), Is.EqualTo(1));
        Assert.That(calls.Count(s => s.MemberName == "call_c"), Is.EqualTo(1));
    }

    // ================================================================
    // Robustness regressions (review swarm)
    // ================================================================

    [Test]
    public void Parse_ManyBlankLines_DoesNotStackOverflow()
    {
        // SkipWhitespace used to recurse per newline -> uncatchable StackOverflow.
        var src = new string('\n', 30000) + "pub fn after() { work(); }";
        var result = ParseWithin(src, 5000);
        Assert.That(result.Statements.Any(s => s.MemberName == "work"), Is.True);
    }

    [Test]
    public void Parse_CharLiteral_NotMisLexedAsLifetime()
    {
        var result = Parse("""
            struct S;
            impl S {
                fn f(&self) {
                    helper('a');
                    after_call();
                }
            }
            """);
        var members = result.Statements.Where(s => s.Kind == "call").Select(s => s.MemberName).ToList();
        Assert.That(members, Does.Contain("helper"));
        Assert.That(members, Does.Contain("after_call"), "the char literal must not swallow the next call");
    }

    [Test]
    public void Parse_StructWithWhereClause_KeepsFields()
    {
        var result = Parse("""
            pub struct Filter<T> where T: Send {
                value: T,
                name: String,
            }
            """);
        var t = result.Types.First(x => x.Name == "Filter");
        Assert.That(t.Fields.Select(f => f.Name), Does.Contain("value"));
        Assert.That(t.Fields.Select(f => f.Name), Does.Contain("name"));
    }

    [Test]
    public void Parse_BodylessFnWithWhere_DoesNotDropFollowingItems()
    {
        var result = Parse("""
            pub trait T {
                fn foo() where Self: Sized;
                fn bar(&self);
            }
            """);
        var t = result.Types.First(x => x.Name == "T");
        Assert.That(t.Methods.Select(m => m.Name), Does.Contain("foo"));
        Assert.That(t.Methods.Select(m => m.Name), Does.Contain("bar"));
    }

    [Test]
    public void Parse_TraitWithLifetimeSupertrait_KeepsBody()
    {
        var result = Parse("""
            pub trait Foo: 'static {
                fn m(&self);
                fn n(&self);
            }
            """);
        var t = result.Types.First(x => x.Name == "Foo");
        Assert.That(t.Methods.Select(m => m.Name), Does.Contain("m"));
        Assert.That(t.Methods.Select(m => m.Name), Does.Contain("n"));
    }

    [Test]
    public void Parse_RawIdentifierStruct_IsCaptured()
    {
        var result = Parse("pub struct r#Match { pub x: i32 }");
        Assert.That(result.Types.Select(t => t.Name), Does.Contain("Match"));
    }

    [Test]
    public void Parse_Union_IsCaptured()
    {
        var result = Parse("pub union MyUnion { a: i32, b: f32 }");
        var u = result.Types.First(t => t.Name == "MyUnion");
        Assert.That(u.Fields, Has.Count.EqualTo(2));
    }

    [Test]
    public void Parse_PubCrate_IsNotPublic()
    {
        var result = Parse("pub(crate) struct CrateThing { pub x: i32 }");
        var t = result.Types.First(x => x.Name == "CrateThing");
        Assert.That(t.IsPublic, Is.False, "pub(crate) is restricted, not public API");
    }

    [Test]
    public void Parse_PublicTraitMethods_ArePublic()
    {
        var result = Parse("""
            pub trait Greeter {
                fn greet(&self) -> String;
            }
            """);
        var greet = result.Types.First(t => t.Name == "Greeter").Methods.First(m => m.Name == "greet");
        Assert.That(greet.IsPublic, Is.True, "a public trait's methods are public API");
    }

    [Test]
    public void Parse_NestedCallsInArguments_AreCaptured()
    {
        var result = Parse("""
            struct S;
            impl S {
                fn f(&self) {
                    log(process(data.unwrap()));
                }
            }
            """);
        var members = result.Statements.Where(s => s.Kind == "call").Select(s => s.MemberName).ToList();
        Assert.That(members, Does.Contain("log"));
        Assert.That(members, Does.Contain("process"));
        Assert.That(members, Does.Contain("unwrap"), "a call nested in arguments must still be captured");
    }

    [Test]
    public void Parse_PanicInClosureArgument_IsCaptured()
    {
        var result = Parse("""
            struct S;
            impl S {
                fn f(&self) {
                    items.for_each(|x| panic!("boom"));
                }
            }
            """);
        Assert.That(result.Statements.Any(s => s.Kind == "throw" && s.MemberName == "panic"), Is.True);
    }

    [Test]
    public void Parse_ImplForNonIdentifierTarget_KeepsNameAndMethods()
    {
        var result = Parse("""
            impl MyTrait for [u8] {
                fn go(&self) {}
            }
            """);
        var impl = result.Types.First(t => t.Name.Contains("(impl"));
        Assert.That(impl.Name.Trim(), Does.Not.StartWith("(impl"), "impl target name must not be empty");
        Assert.That(impl.Methods.Select(m => m.Name), Does.Contain("go"), "impl-on-slice methods must be captured");
        Assert.That(impl.BaseTypes, Does.Contain("MyTrait"));
    }

    [Test]
    public void Parse_ImplWithPathTrait_UsesTraitNotFirstSegment()
    {
        var result = Parse("""
            impl std::fmt::Debug for Foo {
                fn fmt(&self) {}
            }
            """);
        var impl = result.Types.First(t => t.Name.Contains("(impl"));
        Assert.That(impl.BaseTypes, Does.Contain("Debug"));
        Assert.That(impl.BaseTypes, Does.Not.Contain("std"));
        Assert.That(impl.Name, Does.StartWith("Foo"));
    }

    [Test]
    public void Parse_PubConstFnInImpl_IsPublic()
    {
        var result = Parse("""
            struct S;
            impl S {
                pub const fn make() -> i32 { 1 }
            }
            """);
        var impl = result.Types.First(t => t.Name.Contains("(impl"));
        var make = impl.Methods.Concat(impl.Constructors).First(m => m.Name == "make");
        Assert.That(make.IsPublic, Is.True);
    }

    [Test]
    public void Parse_MacroRulesBody_IsNotParsedAsCode()
    {
        var result = Parse("""
            macro_rules! make {
                () => { fn phantom_fn() { phantom_call(); } };
            }
            pub fn real() { actual_call(); }
            """);
        Assert.That(result.Statements.Any(s => s.MemberName == "phantom_call"), Is.False,
            "macro_rules! template tokens must not be parsed as real calls");
        Assert.That(result.Statements.Any(s => s.MemberName == "actual_call"), Is.True);
    }

    // ================================================================
    // Helper
    // ================================================================

    // Parses on a background task and fails (instead of hanging the suite) if the
    // parser does not terminate within the given budget — guards against the
    // non-advancing-loop class of bugs.
    private SourceFile ParseWithin(string source, int milliseconds)
    {
        SourceFile? result = null;
        var task = Task.Run(() => { result = _parser.Parse("test.rs", source); });
        Assert.That(task.Wait(milliseconds), Is.True,
            $"Parser did not terminate within {milliseconds}ms — likely an infinite loop.");
        Assert.That(result, Is.Not.Null, "Parser returned null");
        return result!;
    }

    private SourceFile Parse(string source)
    {
        var result = _parser.Parse("test.rs", source);
        Assert.That(result, Is.Not.Null, "Parser returned null");
        return result!;
    }
}
