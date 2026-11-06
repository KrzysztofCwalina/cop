using Cop.Providers.SourceModel;
using Cop.Providers.SourceParsers;
using NUnit.Framework;

namespace Cop.Tests.Lang.SourceParsers;

/// <summary>
/// Contract tests for <see cref="GoSourceParser"/>. Positive tests assert the exact model the
/// parser builds; negative tests assert that malformed Go yields specific ParseErrors.
/// </summary>
[TestFixture]
public class GoSourceParserTests
{
    private readonly GoSourceParser _parser = new();

    // ── Package / namespace ──────────────────────────────────────────────────

    [Test]
    public async Task Parse_ExtractsPackageName()
    {
        var src = "package main\n";
        var result = (await _parser.ParseAsync("main.go", src, cancellationToken: CancellationToken.None))!;
        Assert.That(result.Namespace, Is.EqualTo("main"));
    }

    // ── Import extraction ────────────────────────────────────────────────────

    [Test]
    public async Task Parse_SingleImport_ExtractsPath()
    {
        var src = """
            package main

            import "fmt"
            """;
        var result = (await _parser.ParseAsync("main.go", src, cancellationToken: CancellationToken.None))!;
        Assert.That(result.Usings, Is.EqualTo(new[] { "fmt" }));
    }

    [Test]
    public async Task Parse_ImportBlock_ExtractsAllPaths()
    {
        var src = """
            package server

            import (
                "fmt"
                "net/http"
                "os"
            )
            """;
        var result = (await _parser.ParseAsync("server.go", src, cancellationToken: CancellationToken.None))!;
        Assert.That(result.Usings, Does.Contain("fmt"));
        Assert.That(result.Usings, Does.Contain("net/http"));
        Assert.That(result.Usings, Does.Contain("os"));
        Assert.That(result.Usings, Has.Count.EqualTo(3));
    }

    [Test]
    public async Task Parse_ImportBlockWithAlias_ExtractsPath()
    {
        // The alias is ignored; the import path is stored.
        var src = """
            package main

            import (
                log "github.com/sirupsen/logrus"
            )
            """;
        var result = (await _parser.ParseAsync("main.go", src, cancellationToken: CancellationToken.None))!;
        Assert.That(result.Usings, Does.Contain("github.com/sirupsen/logrus"));
    }

    // ── Struct types ─────────────────────────────────────────────────────────

    [Test]
    public async Task Parse_StructDecl_ExtractsNameAndKind()
    {
        var src = """
            package shapes

            type Rectangle struct {
                Width  float64
                Height float64
            }
            """;
        var result = (await _parser.ParseAsync("shapes.go", src, cancellationToken: CancellationToken.None))!;
        var ty = result.Types.FirstOrDefault(t => t.Name == "Rectangle");
        Assert.That(ty, Is.Not.Null);
        Assert.That(ty!.Kind, Is.EqualTo(TypeKind.Struct));
        Assert.That(ty.IsPublic, Is.True);
        var goTy = ty as GoTypeDeclaration;
        Assert.That(goTy, Is.Not.Null);
        Assert.That(goTy!.IsStruct, Is.True);
        Assert.That(goTy.IsInterface, Is.False);
    }

    [Test]
    public async Task Parse_StructWithStructTags_SetsHasStructTags()
    {
        var src = """
            package model

            type User struct {
                Name  string `json:"name"`
                Email string `json:"email"`
            }
            """;
        var result = (await _parser.ParseAsync("model.go", src, cancellationToken: CancellationToken.None))!;
        var ty = result.Types.FirstOrDefault(t => t.Name == "User") as GoTypeDeclaration;
        Assert.That(ty, Is.Not.Null);
        Assert.That(ty!.HasStructTags, Is.True);
    }

    [Test]
    public async Task Parse_UnexportedStruct_IsPrivate()
    {
        var src = """
            package internal

            type worker struct{}
            """;
        var result = (await _parser.ParseAsync("internal.go", src, cancellationToken: CancellationToken.None))!;
        var ty = result.Types.FirstOrDefault(t => t.Name == "worker");
        Assert.That(ty, Is.Not.Null);
        Assert.That(ty!.IsPublic, Is.False);
    }

    // ── Interface types ──────────────────────────────────────────────────────

    [Test]
    public async Task Parse_InterfaceDecl_ExtractsNameAndKind()
    {
        var src = """
            package io

            type Reader interface {
                Read(p []byte) (n int, err error)
            }
            """;
        var result = (await _parser.ParseAsync("reader.go", src, cancellationToken: CancellationToken.None))!;
        var ty = result.Types.FirstOrDefault(t => t.Name == "Reader");
        Assert.That(ty, Is.Not.Null);
        Assert.That(ty!.Kind, Is.EqualTo(TypeKind.Interface));
        var goTy = ty as GoTypeDeclaration;
        Assert.That(goTy!.IsInterface, Is.True);
        Assert.That(goTy.IsStruct, Is.False);
    }

    [Test]
    public async Task Parse_InterfaceWithMethods_ExtractsMethods()
    {
        var src = """
            package svc

            type Service interface {
                Start() error
                Stop() error
                Name() string
            }
            """;
        var result = (await _parser.ParseAsync("svc.go", src, cancellationToken: CancellationToken.None))!;
        var iface = result.Types.FirstOrDefault(t => t.Name == "Service")
            ?? throw new AssertionException("Expected the Service interface.");
        Assert.That(iface, Is.Not.Null);
        Assert.That((await AsyncSourceFacts.MembersAsync(iface!, CancellationToken.None)).Methods, Has.Count.EqualTo(3));
        Assert.That((await AsyncSourceFacts.MembersAsync(iface, CancellationToken.None)).Methods.Any(m => m.Name == "Start"), Is.True);
        Assert.That((await AsyncSourceFacts.MembersAsync(iface, CancellationToken.None)).Methods.Any(m => m.Name == "Stop"), Is.True);
        Assert.That((await AsyncSourceFacts.MembersAsync(iface, CancellationToken.None)).Methods.Any(m => m.Name == "Name"), Is.True);
    }

    [Test]
    public async Task Parse_GenericInterface_IsValidAndHasNoParseErrors()
    {
        var src = """
            package coll

            type Set[T any] interface {
                Add(v T)
                Contains(v T) bool
            }
            """;
        var result = (await _parser.ParseAsync("coll.go", src, cancellationToken: CancellationToken.None))!;
        Assert.That(result.ParseErrors, Is.Empty, string.Join("\n", result.ParseErrors));
        var ty = result.Types.FirstOrDefault(t => t.Name == "Set");
        Assert.That(ty, Is.Not.Null);
        Assert.That(ty!.Kind, Is.EqualTo(TypeKind.Interface));
    }

    // ── Function / method extraction ─────────────────────────────────────────

    [Test]
    public async Task Parse_FreeFunction_AppearsInSyntheticFunctionsType()
    {
        var src = """
            package util

            func Add(a, b int) int {
                return a + b
            }
            """;
        var result = (await _parser.ParseAsync("util.go", src, cancellationToken: CancellationToken.None))!;
        var funcType = result.Types.FirstOrDefault(t => t.Name.Contains("functions"));
        Assert.That(funcType, Is.Not.Null);
        Assert.That((await AsyncSourceFacts.MembersAsync(funcType!, CancellationToken.None)).Methods.Any(m => m.Name == "Add"), Is.True);
    }

    [Test]
    public async Task Parse_ExportedFunction_IsPublic()
    {
        var src = """
            package api

            func HandleRequest(w http.ResponseWriter, r *http.Request) {}
            """;
        var result = (await _parser.ParseAsync("api.go", src, cancellationToken: CancellationToken.None))!;
        var funcType = result.Types.FirstOrDefault(t => t.Name.Contains("functions"));
        var method = funcType?.Methods.FirstOrDefault(m => m.Name == "HandleRequest");
        Assert.That(method, Is.Not.Null);
        Assert.That(method!.IsPublic, Is.True);
    }

    [Test]
    public async Task Parse_MethodWithValueReceiver_AttachesToType()
    {
        var src = """
            package geo

            type Point struct { X, Y float64 }

            func (p Point) Distance() float64 {
                return 0
            }
            """;
        var result = (await _parser.ParseAsync("geo.go", src, cancellationToken: CancellationToken.None))!;
        var pointType = result.Types.FirstOrDefault(t => t.Name == "Point");
        Assert.That(pointType, Is.Not.Null);
        Assert.That((await AsyncSourceFacts.MembersAsync(pointType!, CancellationToken.None)).Methods.Any(m => m.Name == "Distance"), Is.True);
    }

    [Test]
    public async Task Parse_MethodWithPointerReceiver_IsPointerReceiver()
    {
        var src = """
            package stack

            type Stack struct{ items []int }

            func (s *Stack) Push(v int) {
                s.items = append(s.items, v)
            }
            """;
        var result = (await _parser.ParseAsync("stack.go", src, cancellationToken: CancellationToken.None))!;
        var stackType = result.Types.FirstOrDefault(t => t.Name == "Stack");
        Assert.That(stackType, Is.Not.Null);
        var push = (await AsyncSourceFacts.MembersAsync(stackType!, CancellationToken.None)).Methods.FirstOrDefault(m => m.Name == "Push") as GoMethodDeclaration;
        Assert.That(push, Is.Not.Null);
        Assert.That(push!.IsPointerReceiver, Is.True);
    }

    [Test]
    public async Task Parse_GenericFunction_IsGeneric()
    {
        var src = """
            package maps

            func Map[T, U any](slice []T, f func(T) U) []U {
                return nil
            }
            """;
        var result = (await _parser.ParseAsync("maps.go", src, cancellationToken: CancellationToken.None))!;
        Assert.That(result.ParseErrors, Is.Empty, string.Join("\n", result.ParseErrors));
        var funcType = result.Types.FirstOrDefault(t => t.Name.Contains("functions"));
        var method = funcType?.Methods.FirstOrDefault(m => m.Name == "Map") as GoMethodDeclaration;
        Assert.That(method, Is.Not.Null);
        Assert.That(method!.IsGeneric, Is.True);
    }

    [Test]
    public async Task Parse_VariadicFunction_IsVariadic()
    {
        var src = """
            package log

            func Printf(format string, args ...interface{}) {}
            """;
        var result = (await _parser.ParseAsync("log.go", src, cancellationToken: CancellationToken.None))!;
        var funcType = result.Types.FirstOrDefault(t => t.Name.Contains("functions"));
        var method = funcType?.Methods.FirstOrDefault(m => m.Name == "Printf") as GoMethodDeclaration;
        Assert.That(method, Is.Not.Null);
        Assert.That(method!.IsVariadic, Is.True);
    }

    // ── Statement detection (calls, control flow, error handling) ────────────

    [Test]
    public async Task Parse_FuncCallStatement_ExtractedAsCallKind()
    {
        var src = """
            package main

            import "fmt"

            func main() {
                fmt.Println("hello")
            }
            """;
        var result = (await _parser.ParseAsync("main.go", src, cancellationToken: CancellationToken.None))!;
        var call = result.Statements.FirstOrDefault(s => s.Kind == "call" && s.MemberName == "Println")
            ?? throw new AssertionException("Expected the Println call.");
        Assert.That(call, Is.Not.Null);
        Assert.That((await AsyncSourceFacts.TypeNameAsync(call!, CancellationToken.None)).TypeName, Is.EqualTo("fmt"));
        Assert.That(call.IsInMethod, Is.True);
    }

    [Test]
    public async Task Parse_BareCallStatement_NoTypeName()
    {
        var src = """
            package main

            func main() {
                doWork()
            }

            func doWork() {}
            """;
        var result = (await _parser.ParseAsync("main.go", src, cancellationToken: CancellationToken.None))!;
        var call = result.Statements.FirstOrDefault(s => s.Kind == "call" && s.MemberName == "doWork");
        Assert.That(call, Is.Not.Null);
        Assert.That((await AsyncSourceFacts.TypeNameAsync(call!, CancellationToken.None)).TypeName, Is.Null);
    }

    [Test]
    public async Task Parse_DeferStatement_KindIsDefer()
    {
        var src = """
            package main

            func openFile() {
                defer close()
            }
            """;
        var result = (await _parser.ParseAsync("main.go", src, cancellationToken: CancellationToken.None))!;
        var defer_ = result.Statements.FirstOrDefault(s => s.Kind == "defer");
        Assert.That(defer_, Is.Not.Null);
        Assert.That(defer_!.IsInMethod, Is.True);
        var goStmt = defer_ as GoStatementInfo;
        Assert.That(goStmt!.IsDefer, Is.True);
    }

    [Test]
    public async Task Parse_GoStatement_KindIsGo()
    {
        var src = """
            package main

            func start() {
                go worker()
            }
            """;
        var result = (await _parser.ParseAsync("main.go", src, cancellationToken: CancellationToken.None))!;
        var goroutine = result.Statements.FirstOrDefault(s => s.Kind == "go");
        Assert.That(goroutine, Is.Not.Null);
        var goStmt = goroutine as GoStatementInfo;
        Assert.That(goStmt!.IsGoroutine, Is.True);
    }

    [Test]
    public async Task Parse_SelectStatement_KindIsSelect()
    {
        var src = """
            package main

            func multiplex(a, b <-chan int) {
                select {
                case v := <-a:
                    _ = v
                case v := <-b:
                    _ = v
                }
            }
            """;
        var result = (await _parser.ParseAsync("main.go", src, cancellationToken: CancellationToken.None))!;
        var sel = result.Statements.FirstOrDefault(s => s.Kind == "select");
        Assert.That(sel, Is.Not.Null);
        var goStmt = sel as GoStatementInfo;
        Assert.That(goStmt!.IsSelect, Is.True);
    }

    [Test]
    public async Task Parse_ForRangeLoop_KindIsRange()
    {
        var src = """
            package main

            func process(items []string) {
                for i, v := range items {
                    _ = i
                    _ = v
                }
            }
            """;
        var result = (await _parser.ParseAsync("main.go", src, cancellationToken: CancellationToken.None))!;
        var rangeStmt = result.Statements.FirstOrDefault(s => s.Kind == "range");
        Assert.That(rangeStmt, Is.Not.Null);
        var goStmt = rangeStmt as GoStatementInfo;
        Assert.That(goStmt!.IsRangeLoop, Is.True);
    }

    [Test]
    public async Task Parse_PlainForLoop_KindIsFor()
    {
        var src = """
            package main

            func countdown() {
                for i := 10; i > 0; i-- {
                }
            }
            """;
        var result = (await _parser.ParseAsync("main.go", src, cancellationToken: CancellationToken.None))!;
        var forStmt = result.Statements.FirstOrDefault(s => s.Kind == "for");
        Assert.That(forStmt, Is.Not.Null);
    }

    [Test]
    public async Task Parse_TypeSwitch_KindIsTypeSwitch()
    {
        var src = """
            package main

            func describe(i interface{}) {
                switch v := i.(type) {
                case int:
                    _ = v
                }
            }
            """;
        var result = (await _parser.ParseAsync("main.go", src, cancellationToken: CancellationToken.None))!;
        var ts = result.Statements.FirstOrDefault(s => s.Kind == "type-switch");
        Assert.That(ts, Is.Not.Null);
        var goStmt = ts as GoStatementInfo;
        Assert.That(goStmt!.IsTypeSwitch, Is.True);
    }

    [Test]
    public async Task Parse_PlainSwitch_KindIsSwitch()
    {
        var src = """
            package main

            func grade(score int) string {
                switch {
                case score >= 90:
                    return "A"
                default:
                    return "F"
                }
            }
            """;
        var result = (await _parser.ParseAsync("main.go", src, cancellationToken: CancellationToken.None))!;
        var sw = result.Statements.FirstOrDefault(s => s.Kind == "switch");
        Assert.That(sw, Is.Not.Null);
    }

    [Test]
    public async Task Parse_PanicCall_KindIsThrow()
    {
        var src = """
            package main

            func mustPositive(n int) {
                if n <= 0 {
                    panic("must be positive")
                }
            }
            """;
        var result = (await _parser.ParseAsync("main.go", src, cancellationToken: CancellationToken.None))!;
        var throwStmt = result.Statements.FirstOrDefault(s => s.Kind == "throw");
        Assert.That(throwStmt, Is.Not.Null);
        Assert.That(throwStmt!.MemberName, Is.EqualTo("panic"));
    }

    [Test]
    public async Task Parse_RecoverCall_KindIsCatch_IsGenericErrorHandler()
    {
        var src = """
            package main

            func safe() {
                defer func() {
                    if r := recover(); r != nil {
                    }
                }()
            }
            """;
        var result = (await _parser.ParseAsync("main.go", src, cancellationToken: CancellationToken.None))!;
        var catchStmt = result.Statements.FirstOrDefault(s => s.Kind == "catch");
        Assert.That(catchStmt, Is.Not.Null);
        Assert.That(catchStmt!.MemberName, Is.EqualTo("recover"));
        Assert.That(catchStmt.IsErrorHandler, Is.True);
        Assert.That(catchStmt.IsGenericErrorHandler, Is.True);
    }

    [Test]
    public async Task Parse_IfErrNilPattern_IsErrorHandler()
    {
        var src = """
            package main

            import "os"

            func readFile(path string) {
                f, err := os.Open(path)
                if err != nil {
                    return
                }
                _ = f
            }
            """;
        var result = (await _parser.ParseAsync("main.go", src, cancellationToken: CancellationToken.None))!;
        var ifStmt = result.Statements.FirstOrDefault(s => s.Kind == "if" && s.IsErrorHandler);
        Assert.That(ifStmt, Is.Not.Null, "Expected an 'if' statement with IsErrorHandler=true for 'if err != nil'");
        Assert.That(ifStmt!.IsGenericErrorHandler, Is.False);
    }

    [Test]
    public async Task Parse_IfWithoutErrNil_IsNotErrorHandler()
    {
        var src = """
            package main

            func check(x int) {
                if x > 0 {
                }
            }
            """;
        var result = (await _parser.ParseAsync("main.go", src, cancellationToken: CancellationToken.None))!;
        var ifStmt = result.Statements.FirstOrDefault(s => s.Kind == "if");
        Assert.That(ifStmt, Is.Not.Null);
        Assert.That(ifStmt!.IsErrorHandler, Is.False);
    }

    // ── Comment-line tracking ────────────────────────────────────────────────

    [Test]
    public async Task Parse_LineComments_TrackedByLineNumber()
    {
        var src = "// This is a comment\npackage main\n// Another comment\n";
        var result = (await _parser.ParseAsync("main.go", src, cancellationToken: CancellationToken.None))!;
        Assert.That(result.CommentLines, Does.Contain(1));
        Assert.That(result.CommentLines, Does.Not.Contain(2));
        Assert.That(result.CommentLines, Does.Contain(3));
    }

    [Test]
    public async Task Parse_BlockComment_TrackedAsCommentLine()
    {
        var src = "package main\n/* block */\n";
        var result = (await _parser.ParseAsync("main.go", src, cancellationToken: CancellationToken.None))!;
        Assert.That(result.CommentLines, Does.Contain(2));
    }

    [Test]
    public async Task Parse_CodeInsideCommentNotParsed()
    {
        var src = """
            package main

            func main() {
                // fmt.Println("commented out")
            }
            """;
        var result = (await _parser.ParseAsync("main.go", src, cancellationToken: CancellationToken.None))!;
        var calls = result.Statements.Where(s => s.Kind == "call" && s.MemberName == "Println").ToList();
        Assert.That(calls, Is.Empty);
    }

    // ── Multiline raw strings (backtick) ─────────────────────────────────────

    [Test]
    public async Task Parse_MultilineRawString_ValidAndNoErrors()
    {
        var src = """
            package tmpl

            var tmpl = `
            Hello {{.Name}},
            func notAFunction() {}
            `
            """;
        var result = (await _parser.ParseAsync("tmpl.go", src, cancellationToken: CancellationToken.None))!;
        Assert.That(result.ParseErrors, Is.Empty, string.Join("\n", result.ParseErrors));
    }

    // ── ParseErrors (positive control — valid Go must yield empty errors) ────

    [Test]
    public async Task Parse_ValidGoFile_HasNoParseErrors()
    {
        var src = """
            package server

            import (
                "fmt"
                "net/http"
            )

            type Handler struct {
                Prefix string
            }

            func (h *Handler) ServeHTTP(w http.ResponseWriter, r *http.Request) {
                f, err := openFile(r.URL.Path)
                if err != nil {
                    http.Error(w, err.Error(), http.StatusNotFound)
                    return
                }
                defer f.Close()
                fmt.Fprintln(w, "ok")
            }

            func openFile(path string) (*file, error) {
                return nil, nil
            }

            type file struct{}

            func (f *file) Close() {}
            """;
        var result = (await _parser.ParseAsync("server.go", src, cancellationToken: CancellationToken.None))!;
        Assert.That(result.ParseErrors, Is.Empty, string.Join("\n", result.ParseErrors));
    }

    [Test]
    public async Task Parse_GoroutineAndChannel_ValidNoErrors()
    {
        var src = """
            package main

            func pipeline() {
                ch := make(chan int, 10)
                go func() {
                    for i := 0; i < 10; i++ {
                        ch <- i
                    }
                    close(ch)
                }()
                for v := range ch {
                    _ = v
                }
            }
            """;
        var result = (await _parser.ParseAsync("pipeline.go", src, cancellationToken: CancellationToken.None))!;
        Assert.That(result.ParseErrors, Is.Empty, string.Join("\n", result.ParseErrors));
    }

    // ── ParseErrors (negative tests — malformed Go → specific errors) ────────

    [Test]
    public async Task Parse_MissingPackageClause_ReportsError()
    {
        var src = "func main() {}\n"; // no package declaration
        var result = (await _parser.ParseAsync("main.go", src, cancellationToken: CancellationToken.None))!;
        Assert.That(result.ParseErrors, Is.Not.Empty);
        Assert.That(result.ParseErrors.Any(e => e.Contains("missing package clause")), Is.True,
            $"Expected 'missing package clause' error, got: {string.Join(", ", result.ParseErrors)}");
    }

    [Test]
    public async Task Parse_UnterminatedInterpretedString_ReportsError()
    {
        var src = "package main\n\nfunc f() { s := \"unterminated\n}\n";
        var result = (await _parser.ParseAsync("main.go", src, cancellationToken: CancellationToken.None))!;
        Assert.That(result.ParseErrors, Is.Not.Empty);
        Assert.That(result.ParseErrors.Any(e => e.Contains("unterminated string")), Is.True,
            $"Expected 'unterminated string' error, got: {string.Join(", ", result.ParseErrors)}");
    }

    [Test]
    public async Task Parse_UnterminatedBlockComment_ReportsError()
    {
        var src = "package main\n\n/* this comment never closes\n";
        var result = (await _parser.ParseAsync("main.go", src, cancellationToken: CancellationToken.None))!;
        Assert.That(result.ParseErrors, Is.Not.Empty);
        Assert.That(result.ParseErrors.Any(e => e.Contains("unterminated block comment")), Is.True,
            $"Expected 'unterminated block comment' error, got: {string.Join(", ", result.ParseErrors)}");
    }

    [Test]
    public async Task Parse_UnterminatedRawString_ReportsError()
    {
        var src = "package main\n\nvar s = `never closed\n";
        var result = (await _parser.ParseAsync("main.go", src, cancellationToken: CancellationToken.None))!;
        Assert.That(result.ParseErrors, Is.Not.Empty);
        Assert.That(result.ParseErrors.Any(e => e.Contains("unterminated raw string")), Is.True,
            $"Expected 'unterminated raw string literal' error, got: {string.Join(", ", result.ParseErrors)}");
    }

    [Test]
    public async Task Parse_UnbalancedOpenBrace_ReportsError()
    {
        var src = "package main\n\nfunc f() {\n"; // missing closing }
        var result = (await _parser.ParseAsync("main.go", src, cancellationToken: CancellationToken.None))!;
        Assert.That(result.ParseErrors, Is.Not.Empty);
        Assert.That(result.ParseErrors.Any(e => e.Contains("expected closing '}'") || e.Contains("}")), Is.True,
            $"Expected unclosed brace error, got: {string.Join(", ", result.ParseErrors)}");
    }

    [Test]
    public async Task Parse_ErrorFormat_ContainsFilePathAndLineCol()
    {
        var src = "func main() {}\n"; // missing package
        var result = (await _parser.ParseAsync("myfile.go", src, cancellationToken: CancellationToken.None))!;
        Assert.That(result.ParseErrors, Is.Not.Empty);
        // Format: myfile.go(line,col): error: ...
        Assert.That(result.ParseErrors[0], Does.StartWith("myfile.go("));
        Assert.That(result.ParseErrors[0], Does.Contain("): error: "));
    }

    [Test]
    public async Task Parse_MultipleErrors_ReturnsAllErrors()
    {
        // Missing package + unterminated string — two distinct errors
        var src = "func f() { s := \"oops\n}\n";
        var result = (await _parser.ParseAsync("bad.go", src, cancellationToken: CancellationToken.None))!;
        Assert.That(result.ParseErrors, Has.Count.GreaterThanOrEqualTo(2),
            $"Expected ≥2 errors, got: {string.Join(", ", result.ParseErrors)}");
    }
}
