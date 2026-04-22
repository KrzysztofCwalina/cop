using Cop.Providers.SourceParsers;
using NUnit.Framework;

namespace Cop.Tests.Lang.SourceParsers;

[TestFixture]
public class CSharpSourceParserTests
{
    private readonly CSharpSourceParser _parser = new();

    [Test]
    public void Parse_GoodClient_ExtractsTypes()
    {
        var source = File.ReadAllText(SamplePath("GoodClient.cs"));
        var result = _parser.Parse("GoodClient.cs", source)!;
        var typeNames = result.Types.Select(t => t.Name).ToList();
        Assert.That(typeNames, Does.Contain("GoodClient"));
        Assert.That(typeNames, Does.Contain("GoodClientOptions"));
    }

    [Test]
    public void Parse_GoodClient_DetectsSealed()
    {
        var source = File.ReadAllText(SamplePath("GoodClient.cs"));
        var result = _parser.Parse("GoodClient.cs", source)!;
        var goodClient = result.Types.First(t => t.Name == "GoodClient");
        Assert.That(goodClient.IsSealed, Is.True);
        Assert.That(goodClient.IsPublic, Is.True);
    }

    [Test]
    public void Parse_GoodClient_ExtractsConstructorParameters()
    {
        var source = File.ReadAllText(SamplePath("GoodClient.cs"));
        var result = _parser.Parse("GoodClient.cs", source)!;
        var goodClient = result.Types.First(t => t.Name == "GoodClient");
        Assert.That(goodClient.Constructors, Has.Count.GreaterThanOrEqualTo(1));
        var mainCtor = goodClient.Constructors.First(c => c.Parameters.Count > 0);
        var paramTypes = mainCtor.Parameters.Select(p => p.Type?.Name).ToList();
        Assert.That(paramTypes, Does.Contain("GoodClientOptions"));
    }

    [Test]
    public void Parse_GoodClient_ExtractsAsyncMethods()
    {
        var source = File.ReadAllText(SamplePath("GoodClient.cs"));
        var result = _parser.Parse("GoodClient.cs", source)!;
        var goodClient = result.Types.First(t => t.Name == "GoodClient");
        var asyncMethods = goodClient.Methods.Where(m => m.IsAsync).ToList();
        Assert.That(asyncMethods, Has.Count.GreaterThanOrEqualTo(1));
        var getItem = asyncMethods.First(m => m.Name == "GetItemAsync");
        Assert.That(getItem.Parameters.Any(p => p.Type?.Name == "CancellationToken"), Is.True);
    }

    [Test]
    public void Parse_BadClient_NotSealed()
    {
        var source = File.ReadAllText(SamplePath("BadClient.cs"));
        var result = _parser.Parse("BadClient.cs", source)!;
        var badClient = result.Types.First(t => t.Name == "BadClient");
        Assert.That(badClient.IsSealed, Is.False);
        Assert.That(badClient.IsAbstract, Is.False);
    }

    [Test]
    public void Parse_GoodClient_DetectsBaseType()
    {
        var source = File.ReadAllText(SamplePath("GoodClient.cs"));
        var result = _parser.Parse("GoodClient.cs", source)!;
        var options = result.Types.First(t => t.Name == "GoodClientOptions");
        Assert.That(options.InheritsFrom("ClientOptions"), Is.True);
    }

    [Test]
    public void Parse_BadClient_ExtractsVarStatements()
    {
        var source = File.ReadAllText(SamplePath("BadClient.cs"));
        var result = _parser.Parse("BadClient.cs", source)!;
        var varStatements = result.Statements
            .Where(s => s.Kind == "declaration" && s.Keywords.Contains("var"))
            .ToList();
        Assert.That(varStatements, Has.Count.GreaterThanOrEqualTo(1));
    }

    [Test]
    public void Parse_BadClient_ExtractsInvocations()
    {
        var source = File.ReadAllText(SamplePath("BadClient.cs"));
        var result = _parser.Parse("BadClient.cs", source)!;
        var sleepCall = result.Statements.FirstOrDefault(
            s => s.Kind == "call" && s.MemberName == "Sleep");
        Assert.That(sleepCall, Is.Not.Null);
    }

    [Test]
    public void Parse_BadClient_ExtractsCatchClauses()
    {
        var source = File.ReadAllText(SamplePath("BadClient.cs"));
        var result = _parser.Parse("BadClient.cs", source)!;
        var catches = result.Statements.Where(s => s.Kind == "catch").ToList();
        Assert.That(catches, Has.Count.GreaterThanOrEqualTo(1));
        Assert.That(catches[0].TypeName, Is.EqualTo("Exception"));
    }

    [Test]
    public void Parse_CatchWithRethrow_HasRethrowIsTrue()
    {
        var source = File.ReadAllText(SamplePath("BadClient.cs"));
        var result = _parser.Parse("BadClient.cs", source)!;
        var catchStmt = result.Statements.First(s => s.Kind == "catch" && s.TypeName == "Exception");
        Assert.That(catchStmt.HasRethrow, Is.True);
    }

    [Test]
    public void Parse_CatchWithoutRethrow_HasRethrowIsFalse()
    {
        var source = """
            class Foo {
                void Bar() {
                    try { }
                    catch (Exception ex) {
                        Console.WriteLine(ex);
                    }
                }
            }
            """;
        var result = _parser.Parse("test.cs", source)!;
        var catchStmt = result.Statements.First(s => s.Kind == "catch");
        Assert.That(catchStmt.HasRethrow, Is.False);
    }

    [Test]
    public void Parse_MultipleCatches_RethrowDetectedCorrectly()
    {
        var source = """
            class Foo {
                void Bar() {
                    try { }
                    catch (RequestFailedException ex) {
                        return;
                    }
                    catch (Exception ex) {
                        Console.WriteLine(ex.Message);
                        throw;
                    }
                }
            }
            """;
        var result = _parser.Parse("test.cs", source)!;
        var catches = result.Statements.Where(s => s.Kind == "catch").ToList();
        Assert.That(catches, Has.Count.EqualTo(2));
        Assert.That(catches[0].TypeName, Is.EqualTo("RequestFailedException"));
        Assert.That(catches[0].HasRethrow, Is.False);
        Assert.That(catches[0].IsErrorHandler, Is.True);
        Assert.That(catches[0].IsGenericErrorHandler, Is.False);
        Assert.That(catches[1].TypeName, Is.EqualTo("Exception"));
        Assert.That(catches[1].HasRethrow, Is.True);
        Assert.That(catches[1].IsErrorHandler, Is.True);
        Assert.That(catches[1].IsGenericErrorHandler, Is.True);
    }

    [Test]
    public void Parse_BareCatch_IsGenericErrorHandler()
    {
        var source = """
            class Foo {
                void Bar() {
                    try { }
                    catch {
                        Console.WriteLine("error");
                    }
                }
            }
            """;
        var result = _parser.Parse("test.cs", source)!;
        var catchStmt = result.Statements.First(s => s.Kind == "catch");
        Assert.That(catchStmt.TypeName, Is.Null);
        Assert.That(catchStmt.IsErrorHandler, Is.True);
        Assert.That(catchStmt.IsGenericErrorHandler, Is.True);
    }

    [Test]
    public void Parse_ExtractsUsings()
    {
        var source = """
            using System;
            using System.IO;
            using Microsoft.Extensions.Logging;

            namespace MyApp;

            public class Foo { }
            """;
        var result = _parser.Parse("test.cs", source)!;
        Assert.That(result.Usings, Does.Contain("System"));
        Assert.That(result.Usings, Does.Contain("System.IO"));
        Assert.That(result.Usings, Does.Contain("Microsoft.Extensions.Logging"));
    }

    [Test]
    public void Parse_ExtractsNamespace()
    {
        var source = """
            namespace MyApp.Controllers;

            public class FooController { }
            """;
        var result = _parser.Parse("test.cs", source)!;
        Assert.That(result.Namespace, Is.EqualTo("MyApp.Controllers"));
    }

    [Test]
    public void Parse_ExtractsBlockNamespace()
    {
        var source = """
            namespace MyApp.Domain
            {
                public class Entity { }
            }
            """;
        var result = _parser.Parse("test.cs", source)!;
        Assert.That(result.Namespace, Is.EqualTo("MyApp.Domain"));
    }

    [Test]
    public void Parse_NoNamespace_ReturnsNull()
    {
        var source = """
            public class TopLevel { }
            """;
        var result = _parser.Parse("test.cs", source)!;
        Assert.That(result.Namespace, Is.Null);
    }

    private static string SamplePath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "Samples", fileName);
}
