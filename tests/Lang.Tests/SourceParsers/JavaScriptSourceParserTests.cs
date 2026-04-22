using Cop.Providers.SourceParsers;
using NUnit.Framework;

namespace Cop.Tests.Lang.SourceParsers;

[TestFixture]
public class JavaScriptSourceParserTests
{
    private readonly JavaScriptSourceParser _parser = new();

    // ── Import tests ──

    [Test]
    public void Parse_ExtractsESModuleImports()
    {
        var source = """
            import { useState } from 'react';
            import axios from 'axios';
            import * as path from 'path';
            """;
        var result = _parser.Parse("test.js", source)!;
        Assert.That(result.Usings, Does.Contain("react"));
        Assert.That(result.Usings, Does.Contain("axios"));
        Assert.That(result.Usings, Does.Contain("path"));
    }

    [Test]
    public void Parse_ExtractsSideEffectImport()
    {
        var source = "import './styles.css';\n";
        var result = _parser.Parse("test.js", source)!;
        Assert.That(result.Usings, Does.Contain("./styles.css"));
    }

    [Test]
    public void Parse_ExtractsRequire()
    {
        var source = """
            const fs = require('fs');
            const { join } = require('path');
            """;
        var result = _parser.Parse("test.js", source)!;
        Assert.That(result.Usings, Does.Contain("fs"));
        Assert.That(result.Usings, Does.Contain("path"));
    }

    [Test]
    public void Parse_NoImports_EmptyUsings()
    {
        var source = "console.log('hello');\n";
        var result = _parser.Parse("test.js", source)!;
        Assert.That(result.Usings, Is.Empty);
    }

    // ── Class tests ──

    [Test]
    public void Parse_ExtractsClass()
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
        var result = _parser.Parse("test.js", source)!;
        Assert.That(result.Types, Has.Count.EqualTo(1));
        Assert.That(result.Types[0].Name, Is.EqualTo("MyService"));
        Assert.That(result.Types[0].Constructors, Has.Count.EqualTo(1));
        Assert.That(result.Types[0].Methods, Has.Count.EqualTo(1));
        Assert.That(result.Types[0].Methods[0].Name, Is.EqualTo("getData"));
    }

    [Test]
    public void Parse_ExportedClass()
    {
        var source = """
            export class ApiClient extends BaseClient {
                fetch() {}
            }
            """;
        var result = _parser.Parse("test.js", source)!;
        Assert.That(result.Types, Has.Count.EqualTo(1));
        Assert.That(result.Types[0].IsPublic, Is.True);
        Assert.That(result.Types[0].BaseTypes, Does.Contain("BaseClient"));
    }

    [Test]
    public void Parse_StaticAndAsyncMethods()
    {
        var source = """
            class Util {
                static create() {}
                async fetchData() {}
            }
            """;
        var result = _parser.Parse("test.js", source)!;
        var staticMethod = result.Types[0].Methods.First(m => m.Name == "create");
        var asyncMethod = result.Types[0].Methods.First(m => m.Name == "fetchData");
        Assert.That(staticMethod.IsStatic, Is.True);
        Assert.That(asyncMethod.IsAsync, Is.True);
    }

    // ── Statement tests ──

    [Test]
    public void Parse_ExtractsConsoleCall()
    {
        var source = """
            function main() {
                console.log('hello');
                console.error('fail');
            }
            """;
        var result = _parser.Parse("test.js", source)!;
        var consoleCalls = result.Statements.Where(s => s.Kind == "call" && s.TypeName == "console").ToList();
        Assert.That(consoleCalls, Has.Count.EqualTo(2));
        Assert.That(consoleCalls[0].MemberName, Is.EqualTo("log"));
        Assert.That(consoleCalls[1].MemberName, Is.EqualTo("error"));
    }

    [Test]
    public void Parse_ExtractsAlertCall()
    {
        var source = "function warn() { alert('danger!'); }\n";
        var result = _parser.Parse("test.js", source)!;
        var alerts = result.Statements.Where(s => s.Kind == "call" && s.MemberName == "alert").ToList();
        Assert.That(alerts, Has.Count.EqualTo(1));
    }

    [Test]
    public void Parse_ExtractsEvalCall()
    {
        var source = "function run() { eval('code'); }\n";
        var result = _parser.Parse("test.js", source)!;
        var evals = result.Statements.Where(s => s.Kind == "call" && s.MemberName == "eval").ToList();
        Assert.That(evals, Has.Count.EqualTo(1));
    }

    [Test]
    public void Parse_ExtractsDebuggerStatement()
    {
        var source = "function test() { debugger; }\n";
        var result = _parser.Parse("test.js", source)!;
        var debuggers = result.Statements.Where(s => s.MemberName == "debugger").ToList();
        Assert.That(debuggers, Has.Count.EqualTo(1));
    }

    [Test]
    public void Parse_ExtractsVarDeclaration()
    {
        var source = """
            function old() {
                var x = 1;
                let y = 2;
                const z = 3;
            }
            """;
        var result = _parser.Parse("test.js", source)!;
        var declarations = result.Statements.Where(s => s.Kind == "declaration").ToList();
        Assert.That(declarations, Has.Count.EqualTo(3));
        var varDecl = declarations.First(d => d.MemberName == "x");
        Assert.That(varDecl.Keywords, Does.Contain("var"));
        var letDecl = declarations.First(d => d.MemberName == "y");
        Assert.That(letDecl.Keywords, Does.Contain("let"));
    }

    [Test]
    public void Parse_ExtractsCatch()
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
        var result = _parser.Parse("test.js", source)!;
        var catches = result.Statements.Where(s => s.Kind == "catch").ToList();
        Assert.That(catches, Has.Count.EqualTo(1));
        Assert.That(catches[0].HasRethrow, Is.False);
        Assert.That(catches[0].IsErrorHandler, Is.True);
        Assert.That(catches[0].IsGenericErrorHandler, Is.True);
    }

    [Test]
    public void Parse_CatchWithRethrow()
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
        var result = _parser.Parse("test.js", source)!;
        var catches = result.Statements.Where(s => s.Kind == "catch").ToList();
        Assert.That(catches, Has.Count.EqualTo(1));
        Assert.That(catches[0].HasRethrow, Is.True);
        Assert.That(catches[0].IsErrorHandler, Is.True);
        Assert.That(catches[0].IsGenericErrorHandler, Is.True);
    }

    [Test]
    public void Parse_ExtractsThrow()
    {
        var source = "function fail() { throw new Error('boom'); }\n";
        var result = _parser.Parse("test.js", source)!;
        var throws = result.Statements.Where(s => s.Kind == "throw").ToList();
        Assert.That(throws, Has.Count.EqualTo(1));
        Assert.That(throws[0].TypeName, Is.EqualTo("Error"));
    }

    // ── Comment handling ──

    [Test]
    public void Parse_SkipsLineComments()
    {
        var source = """
            // console.log('this is a comment');
            function main() {
                console.log('real');
            }
            """;
        var result = _parser.Parse("test.js", source)!;
        var consoleCalls = result.Statements.Where(s => s.Kind == "call" && s.TypeName == "console").ToList();
        Assert.That(consoleCalls, Has.Count.EqualTo(1));
    }

    [Test]
    public void Parse_SkipsBlockComments()
    {
        var source = """
            /*
             * console.log('commented out');
             */
            function main() {
                console.log('real');
            }
            """;
        var result = _parser.Parse("test.js", source)!;
        var consoleCalls = result.Statements.Where(s => s.Kind == "call" && s.TypeName == "console").ToList();
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
    public void Parse_TypeScriptParameters()
    {
        var source = """
            class Api {
                fetch(url: string, timeout?: number) {
                    return url;
                }
            }
            """;
        var result = _parser.Parse("test.ts", source)!;
        var method = result.Types[0].Methods[0];
        Assert.That(method.Parameters, Has.Count.EqualTo(2));
        Assert.That(method.Parameters[0].Name, Is.EqualTo("url"));
        Assert.That(method.Parameters[0].Type!.Name, Is.EqualTo("string"));
        Assert.That(method.Parameters[1].Name, Is.EqualTo("timeout"));
    }

    [Test]
    public void Parse_TopLevelFunction()
    {
        var source = """
            export async function fetchData(url) {
                const response = await fetch(url);
                return response.json();
            }
            """;
        var result = _parser.Parse("test.js", source)!;
        // Should extract calls from the function body
        var calls = result.Statements.Where(s => s.Kind == "call").ToList();
        Assert.That(calls.Any(c => c.MemberName == "fetch"), Is.True);
    }

    [Test]
    public void Parse_LanguageIsJavaScript()
    {
        var result = _parser.Parse("test.js", "const x = 1;\n")!;
        Assert.That(result.Language, Is.EqualTo("javascript"));
    }
}
