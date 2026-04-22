using Cop.Providers.SourceParsers;
using NUnit.Framework;

namespace Cop.Tests.Lang.SourceParsers;

[TestFixture]
public class PythonSourceParserTests
{
    private readonly PythonSourceParser _parser = new();

    [Test]
    public void Parse_ExtractsImports()
    {
        var source = """
            import os
            import sys
            from azure.core import PipelineClient
            from typing import Optional

            class MyClient:
                pass
            """;
        var result = _parser.Parse("test.py", source)!;
        Assert.That(result.Usings, Does.Contain("os"));
        Assert.That(result.Usings, Does.Contain("sys"));
        Assert.That(result.Usings, Does.Contain("azure.core"));
        Assert.That(result.Usings, Does.Contain("typing"));
    }

    [Test]
    public void Parse_MultipleImportsOnOneLine()
    {
        var source = """
            import os, sys, json
            """;
        var result = _parser.Parse("test.py", source)!;
        Assert.That(result.Usings, Does.Contain("os"));
        Assert.That(result.Usings, Does.Contain("sys"));
        Assert.That(result.Usings, Does.Contain("json"));
    }

    [Test]
    public void Parse_ImportWithAlias()
    {
        var source = """
            import numpy as np
            from collections import OrderedDict as OD
            """;
        var result = _parser.Parse("test.py", source)!;
        Assert.That(result.Usings, Does.Contain("numpy"));
        Assert.That(result.Usings, Does.Contain("collections"));
    }

    [Test]
    public void Parse_NoImports_EmptyUsings()
    {
        var source = """
            class Simple:
                pass
            """;
        var result = _parser.Parse("test.py", source)!;
        Assert.That(result.Usings, Is.Empty);
    }

    // ── Statement extraction tests ──

    [Test]
    public void Parse_ExtractsPrintCall()
    {
        var source = "class Foo:\n    def bar(self):\n        print('hello')\n";
        var result = _parser.Parse("test.py", source)!;
        var prints = result.Statements.Where(s => s.Kind == "call" && s.MemberName == "print").ToList();
        Assert.That(prints, Has.Count.EqualTo(1));
        Assert.That(prints[0].IsInMethod, Is.True);
    }

    [Test]
    public void Parse_ExtractsBareExcept()
    {
        var source = "class Foo:\n    def bar(self):\n        try:\n            pass\n        except:\n            pass\n";
        var result = _parser.Parse("test.py", source)!;
        var catches = result.Statements.Where(s => s.Kind == "catch").ToList();
        Assert.That(catches, Has.Count.EqualTo(1));
        Assert.That(catches[0].TypeName, Is.Null);
        Assert.That(catches[0].HasRethrow, Is.False);
        Assert.That(catches[0].IsErrorHandler, Is.True);
        Assert.That(catches[0].IsGenericErrorHandler, Is.True);
    }

    [Test]
    public void Parse_ExtractsExceptWithType()
    {
        var source = "class Foo:\n    def bar(self):\n        try:\n            pass\n        except ValueError:\n            pass\n";
        var result = _parser.Parse("test.py", source)!;
        var catches = result.Statements.Where(s => s.Kind == "catch").ToList();
        Assert.That(catches, Has.Count.EqualTo(1));
        Assert.That(catches[0].TypeName, Is.EqualTo("ValueError"));
        Assert.That(catches[0].IsErrorHandler, Is.True);
        Assert.That(catches[0].IsGenericErrorHandler, Is.False);
    }

    [Test]
    public void Parse_ExtractsExceptWithAsClause()
    {
        var source = "class Foo:\n    def bar(self):\n        try:\n            pass\n        except Exception as e:\n            pass\n";
        var result = _parser.Parse("test.py", source)!;
        var catches = result.Statements.Where(s => s.Kind == "catch").ToList();
        Assert.That(catches, Has.Count.EqualTo(1));
        Assert.That(catches[0].TypeName, Is.EqualTo("Exception"));
        Assert.That(catches[0].IsErrorHandler, Is.True);
        Assert.That(catches[0].IsGenericErrorHandler, Is.True);
    }

    [Test]
    public void Parse_ExtractsExceptTupleForm()
    {
        var source = "class Foo:\n    def bar(self):\n        try:\n            pass\n        except (ValueError, TypeError):\n            pass\n";
        var result = _parser.Parse("test.py", source)!;
        var catches = result.Statements.Where(s => s.Kind == "catch").ToList();
        Assert.That(catches, Has.Count.EqualTo(1));
        Assert.That(catches[0].TypeName, Is.EqualTo("ValueError"));
    }

    [Test]
    public void Parse_DetectsHasRethrow_BareRaise()
    {
        var source = "class Foo:\n    def bar(self):\n        try:\n            pass\n        except Exception:\n            raise\n";
        var result = _parser.Parse("test.py", source)!;
        var catches = result.Statements.Where(s => s.Kind == "catch").ToList();
        Assert.That(catches[0].HasRethrow, Is.True);
    }

    [Test]
    public void Parse_DoesNotTreatRaiseNewAsRethrow()
    {
        var source = "class Foo:\n    def bar(self):\n        try:\n            pass\n        except Exception:\n            raise ValueError('wrapped')\n";
        var result = _parser.Parse("test.py", source)!;
        var catches = result.Statements.Where(s => s.Kind == "catch").ToList();
        Assert.That(catches[0].HasRethrow, Is.False);
    }

    [Test]
    public void Parse_ExtractsRaiseStatement()
    {
        var source = "class Foo:\n    def bar(self):\n        raise ValueError('bad')\n";
        var result = _parser.Parse("test.py", source)!;
        var throws = result.Statements.Where(s => s.Kind == "throw").ToList();
        Assert.That(throws, Has.Count.EqualTo(1));
        Assert.That(throws[0].TypeName, Is.EqualTo("ValueError"));
    }

    [Test]
    public void Parse_ExtractsModuleLevelInvocation()
    {
        var source = "print('hello')\n";
        var result = _parser.Parse("test.py", source)!;
        var prints = result.Statements.Where(s => s.Kind == "call" && s.MemberName == "print").ToList();
        Assert.That(prints, Has.Count.EqualTo(1));
        Assert.That(prints[0].IsInMethod, Is.False);
    }

    [Test]
    public void Parse_ExtractsTopLevelFunctionStatements()
    {
        var source = "def my_func():\n    print('inside')\n";
        var result = _parser.Parse("test.py", source)!;
        var prints = result.Statements.Where(s => s.Kind == "call" && s.MemberName == "print").ToList();
        Assert.That(prints, Has.Count.EqualTo(1));
        Assert.That(prints[0].IsInMethod, Is.True);
    }

    [Test]
    public void Parse_ExtractsMethodCallWithModule()
    {
        var source = "class Foo:\n    def bar(self):\n        os.system('ls')\n";
        var result = _parser.Parse("test.py", source)!;
        var calls = result.Statements.Where(s => s.Kind == "call" && s.MemberName == "system").ToList();
        Assert.That(calls, Has.Count.EqualTo(1));
        Assert.That(calls[0].TypeName, Is.EqualTo("os"));
    }

    [Test]
    public void Parse_SkipsDocstrings()
    {
        var source = "class Foo:\n    \"\"\"This has print('hello') in a docstring.\"\"\"\n    def bar(self):\n        pass\n";
        var result = _parser.Parse("test.py", source)!;
        var prints = result.Statements.Where(s => s.MemberName == "print").ToList();
        Assert.That(prints, Is.Empty);
    }

    [Test]
    public void Parse_SkipsComments()
    {
        var source = "class Foo:\n    def bar(self):\n        # print('commented out')\n        pass\n";
        var result = _parser.Parse("test.py", source)!;
        var prints = result.Statements.Where(s => s.MemberName == "print").ToList();
        Assert.That(prints, Is.Empty);
    }
}
