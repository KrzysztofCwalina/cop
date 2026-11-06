using Cop.Providers.SourceParsers;
using NUnit.Framework;

namespace Cop.Tests.Lang.SourceParsers;

[TestFixture]
public class JavaScriptSourceParserTests
{
    private readonly JavaScriptSourceParser _parser = new();

    // ── Import tests ──

    [Test]
    public async Task Parse_ExtractsESModuleImports()
    {
        var source = """
            import { useState } from 'react';
            import axios from 'axios';
            import * as path from 'path';
            """;
        var result = (await _parser.ParseAsync("test.js", source, cancellationToken: CancellationToken.None))!;
        Assert.That(result.Usings, Does.Contain("react"));
        Assert.That(result.Usings, Does.Contain("axios"));
        Assert.That(result.Usings, Does.Contain("path"));
    }

    [Test]
    public async Task Parse_ExtractsSideEffectImport()
    {
        var source = "import './styles.css';\n";
        var result = (await _parser.ParseAsync("test.js", source, cancellationToken: CancellationToken.None))!;
        Assert.That(result.Usings, Does.Contain("./styles.css"));
    }

    [Test]
    public async Task Parse_ExtractsRequire()
    {
        var source = """
            const fs = require('fs');
            const { join } = require('path');
            """;
        var result = (await _parser.ParseAsync("test.js", source, cancellationToken: CancellationToken.None))!;
        Assert.That(result.Usings, Does.Contain("fs"));
        Assert.That(result.Usings, Does.Contain("path"));
    }

    [Test]
    public async Task Parse_NoImports_EmptyUsings()
    {
        var source = "console.log('hello');\n";
        var result = (await _parser.ParseAsync("test.js", source, cancellationToken: CancellationToken.None))!;
        Assert.That(result.Usings, Is.Empty);
    }

    // ── Class tests ──

    [Test]
    public async Task Parse_ExtractsClass()
    {
        var source = """
            class MyService {
                constructor(name) {
                    this.name = name;
                }
                getData() {
                    return this.data;
                }
            }
            """;
        var result = (await _parser.ParseAsync("test.js", source, cancellationToken: CancellationToken.None))!;
        Assert.That(result.Types, Has.Count.EqualTo(1));
        Assert.That(result.Types[0].Name, Is.EqualTo("MyService"));
        Assert.That(result.Types[0].Constructors, Has.Count.EqualTo(1));
        Assert.That((await AsyncSourceFacts.MembersAsync(result.Types[0], CancellationToken.None)).Methods, Has.Count.EqualTo(1));
        Assert.That((await AsyncSourceFacts.MembersAsync(result.Types[0], CancellationToken.None)).Methods[0].Name, Is.EqualTo("getData"));
    }

    [Test]
    public async Task Parse_ExportedClass()
    {
        var source = """
            export class ApiClient extends BaseClient {
                fetch() {}
            }
            """;
        var result = (await _parser.ParseAsync("test.js", source, cancellationToken: CancellationToken.None))!;
        Assert.That(result.Types, Has.Count.EqualTo(1));
        Assert.That(result.Types[0].IsPublic, Is.True);
        Assert.That(result.Types[0].BaseTypes, Does.Contain("BaseClient"));
    }

    [Test]
    public async Task Parse_StaticAndAsyncMethods()
    {
        var source = """
            class Util {
                static create() {}
                async fetchData() {}
            }
            """;
        var result = (await _parser.ParseAsync("test.js", source, cancellationToken: CancellationToken.None))!;
        var staticMethod = (await AsyncSourceFacts.MembersAsync(result.Types[0], CancellationToken.None)).Methods.First(m => m.Name == "create");
        var asyncMethod = (await AsyncSourceFacts.MembersAsync(result.Types[0], CancellationToken.None)).Methods.First(m => m.Name == "fetchData");
        Assert.That(staticMethod.IsStatic, Is.True);
        Assert.That(asyncMethod.IsAsync, Is.True);
    }

    // ── Statement tests ──

    [Test]
    public async Task Parse_ExtractsConsoleCall()
    {
        var source = """
            function main() {
                console.log('hello');
                console.error('fail');
            }
            """;
        var result = (await _parser.ParseAsync("test.js", source, cancellationToken: CancellationToken.None))!;
        var consoleCalls = result.Statements.Where(s => s.Kind == "call" &&
            s.CallTarget is { Kind: "memberAccess", Target: { Kind: "identifier", Name: "console" } }).ToList();
        Assert.That(consoleCalls, Has.Count.EqualTo(2));
        Assert.That(consoleCalls[0].MemberName, Is.EqualTo("log"));
        Assert.That(consoleCalls[1].MemberName, Is.EqualTo("error"));
    }

    [Test]
    public async Task Parse_ExtractsAlertCall()
    {
        var source = "function warn() { alert('danger!'); }\n";
        var result = (await _parser.ParseAsync("test.js", source, cancellationToken: CancellationToken.None))!;
        var alerts = result.Statements.Where(s => s.Kind == "call" && s.MemberName == "alert").ToList();
        Assert.That(alerts, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Parse_ExtractsEvalCall()
    {
        var source = "function run() { eval('code'); }\n";
        var result = (await _parser.ParseAsync("test.js", source, cancellationToken: CancellationToken.None))!;
        var evals = result.Statements.Where(s => s.Kind == "call" && s.MemberName == "eval").ToList();
        Assert.That(evals, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Parse_ExtractsDebuggerStatement()
    {
        var source = "function test() { debugger; }\n";
        var result = (await _parser.ParseAsync("test.js", source, cancellationToken: CancellationToken.None))!;
        var debuggers = result.Statements.Where(s => s.MemberName == "debugger").ToList();
        Assert.That(debuggers, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Parse_ExtractsVarDeclaration()
    {
        var source = """
            function old() {
                var x = 1;
                let y = 2;
                const z = 3;
            }
            """;
        var result = (await _parser.ParseAsync("test.js", source, cancellationToken: CancellationToken.None))!;
        var declarations = result.Statements.Where(s => s.Kind == "declaration").ToList();
        Assert.That(declarations, Has.Count.EqualTo(3));
        var varDecl = declarations.First(d => d.MemberName == "x");
        Assert.That(varDecl.Keywords, Does.Contain("var"));
        var letDecl = declarations.First(d => d.MemberName == "y");
        Assert.That(letDecl.Keywords, Does.Contain("let"));
    }

    [Test]
    public async Task Parse_ExtractsCatch()
    {
        var source = """
            function risky() {
                try {
                    doSomething();
                } catch (e) {
                    log(e);
                }
            }
            """;
        var result = (await _parser.ParseAsync("test.js", source, cancellationToken: CancellationToken.None))!;
        var catches = result.Statements.Where(s => s.Kind == "catch").ToList();
        Assert.That(catches, Has.Count.EqualTo(1));
        Assert.That(catches[0].HasRethrow, Is.False);
        Assert.That(catches[0].IsErrorHandler, Is.True);
        Assert.That(catches[0].IsGenericErrorHandler, Is.True);
    }

    [Test]
    public async Task Parse_CatchWithRethrow()
    {
        var source = """
            function risky() {
                try {
                    doSomething();
                } catch (e) {
                    log(e);
                    throw e;
                }
            }
            """;
        var result = (await _parser.ParseAsync("test.js", source, cancellationToken: CancellationToken.None))!;
        var catches = result.Statements.Where(s => s.Kind == "catch").ToList();
        Assert.That(catches, Has.Count.EqualTo(1));
        Assert.That(catches[0].HasRethrow, Is.True);
        Assert.That(catches[0].IsErrorHandler, Is.True);
        Assert.That(catches[0].IsGenericErrorHandler, Is.True);
    }

    [Test]
    public async Task Parse_ExtractsThrow()
    {
        var source = "function fail() { throw new Error('boom'); }\n";
        var result = (await _parser.ParseAsync("test.js", source, cancellationToken: CancellationToken.None))!;
        var throws = result.Statements.Where(s => s.Kind == "throw").ToList();
        Assert.That(throws, Has.Count.EqualTo(1));
        Assert.That((await AsyncSourceFacts.TypeNameAsync(throws[0], CancellationToken.None)).TypeName, Is.EqualTo("Error"));
    }

    // ── Comment handling ──

    [Test]
    public async Task Parse_SkipsLineComments()
    {
        var source = """
            // console.log('this is a comment');
            function main() {
                console.log('real');
            }
            """;
        var result = (await _parser.ParseAsync("test.js", source, cancellationToken: CancellationToken.None))!;
        var consoleCalls = result.Statements.Where(s => s.Kind == "call" &&
            s.CallTarget is { Kind: "memberAccess", Target: { Kind: "identifier", Name: "console" } }).ToList();
        Assert.That(consoleCalls, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Parse_SkipsBlockComments()
    {
        var source = """
            /*
             * console.log('commented out');
             */
            function main() {
                console.log('real');
            }
            """;
        var result = (await _parser.ParseAsync("test.js", source, cancellationToken: CancellationToken.None))!;
        var consoleCalls = result.Statements.Where(s => s.Kind == "call" &&
            s.CallTarget is { Kind: "memberAccess", Target: { Kind: "identifier", Name: "console" } }).ToList();
        Assert.That(consoleCalls, Has.Count.EqualTo(1));
    }

    // ── TypeScript support ──

    [Test]
    public void Parse_TypeScriptExtension()
    {
        Assert.That(_parser.Extensions, Does.Contain(".ts"));
        Assert.That(_parser.Extensions, Does.Contain(".js"));
    }

    [Test]
    public async Task Parse_TypeScriptParameters()
    {
        var source = """
            class Api {
                fetch(url: string, timeout?: number) {
                    return url;
                }
            }
            """;
        var result = (await _parser.ParseAsync("test.ts", source, cancellationToken: CancellationToken.None))!;
        var method = (await AsyncSourceFacts.MembersAsync(result.Types[0], CancellationToken.None)).Methods[0];
        Assert.That(method.Parameters, Has.Count.EqualTo(2));
        Assert.That(method.Parameters[0].Name, Is.EqualTo("url"));
        Assert.That(method.Parameters[0].Type!.Name, Is.EqualTo("string"));
        Assert.That(method.Parameters[1].Name, Is.EqualTo("timeout"));
    }

    [Test]
    public async Task Parse_TopLevelFunction()
    {
        var source = """
            export async function fetchData(url) {
                const response = await fetch(url);
                return response.json();
            }
            """;
        var result = (await _parser.ParseAsync("test.js", source, cancellationToken: CancellationToken.None))!;
        // Should extract calls from the function body
        var calls = result.Statements.Where(s => s.Kind == "call").ToList();
        Assert.That(calls.Any(c => c.MemberName == "fetch"), Is.True);
    }

    [Test]
    public async Task Parse_LanguageIsJavaScript()
    {
        var result = (await _parser.ParseAsync("test.js", "const x = 1;\n", cancellationToken: CancellationToken.None))!;
        Assert.That(result.Language, Is.EqualTo("javascript"));
    }

    // ── Comment line detection ──

    [Test]
    public async Task Parse_CommentLines_SingleLine()
    {
        var source = "// comment\nconst x = 1;\n// another\n";
        var result = (await _parser.ParseAsync("test.js", source, cancellationToken: CancellationToken.None))!;
        Assert.That(result.CommentLines, Does.Contain(1));
        Assert.That(result.CommentLines, Does.Not.Contain(2));
        Assert.That(result.CommentLines, Does.Contain(3));
    }

    [Test]
    public async Task Parse_CommentLines_MultiLine()
    {
        var source = "/* multi\n   line */\nconst x = 1;\n";
        var result = (await _parser.ParseAsync("test.js", source, cancellationToken: CancellationToken.None))!;
        Assert.That(result.CommentLines, Does.Contain(1));
        Assert.That(result.CommentLines, Does.Contain(2));
        Assert.That(result.CommentLines, Does.Not.Contain(3));
    }

    // ── ParseErrors: negative tests ──

    [Test]
    public async Task Parse_ParseErrors_UnterminatedString()
    {
        var source = "const x = \"unterminated;\n";
        var result = (await _parser.ParseAsync("test.js", source, cancellationToken: CancellationToken.None))!;
        Assert.That(result.ParseErrors, Is.Not.Empty);
        Assert.That(result.ParseErrors.Any(e => e.Contains("Unterminated")), Is.True);
    }

    [Test]
    public async Task Parse_ParseErrors_UnbalancedBraces()
    {
        var source = "function foo() { if (true) { x(); }\n";
        var result = (await _parser.ParseAsync("test.js", source, cancellationToken: CancellationToken.None))!;
        Assert.That(result.ParseErrors, Is.Not.Empty);
        Assert.That(result.ParseErrors.Any(e => e.Contains("Unclosed") ||
            e.Contains("TS1005") && e.Contains("'}' expected")), Is.True);
    }

    [Test]
    public async Task Parse_ParseErrors_UnterminatedBlockComment()
    {
        var source = "const x = 1; /* unterminated comment\n";
        var result = (await _parser.ParseAsync("test.js", source, cancellationToken: CancellationToken.None))!;
        Assert.That(result.ParseErrors, Is.Not.Empty);
        Assert.That(result.ParseErrors.Any(e => e.Contains("Unterminated block comment") ||
            e.Contains("TS1010") && e.Contains("'*/' expected")), Is.True);
    }

    // ── ParseErrors: positive control (valid modern TS → zero errors) ──

    [Test]
    public async Task Parse_ValidModernTypeScript_NoParseErrors()
    {
        var source = """
            import { useState, useEffect } from 'react';
            import type { FC } from 'react';

            interface User {
                id: number;
                name: string;
                email?: string;
            }

            type ApiResponse<T> = {
                data: T;
                status: number;
            };

            const greet = (name: string = 'world'): string => `Hello, ${name}!`;

            async function fetchUser(id: number): Promise<User> {
                const url = `https://api.example.com/users/${id}`;
                const response = await fetch(url);
                if (!response.ok) {
                    throw new Error(`HTTP error: ${response.status}`);
                }
                return response.json();
            }

            class UserService {
                private users: Map<number, User> = new Map();

                async getUser(id: number): Promise<User | null> {
                    if (this.users.has(id)) {
                        return this.users.get(id) ?? null;
                    }
                    try {
                        const user = await fetchUser(id);
                        this.users.set(id, user);
                        return user;
                    } catch (err) {
                        console.error('Failed to fetch user', err);
                        return null;
                    }
                }

                deleteUser = async (id: number): Promise<void> => {
                    this.users.delete(id);
                };
            }

            const service = new UserService();
            const [count, setCount] = useState<number>(0);
            const result = await service.getUser(42);
            const name = result?.name ?? 'unknown';
            const upper = name?.toUpperCase?.();
            export default UserService;
            """;
        var result = (await _parser.ParseAsync("test.ts", source, cancellationToken: CancellationToken.None))!;
        Assert.That(result.ParseErrors, Is.Empty,
            $"Expected no parse errors but got: {string.Join("; ", result.ParseErrors)}");
    }
}
