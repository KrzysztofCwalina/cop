using Cop.Providers;
using Cop.Providers.SourceModel;
using Cop.Providers.SourceParsers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NUnit.Framework;

namespace Cop.Tests.Lang.SourceParsers;

[TestFixture]
public sealed class CSharpCatchFilterFactsTests
{
    [TestCase("ex is IOException", true)]
    [TestCase("ex is IOException io", true)]
    [TestCase("ex is IOException { HResult: 1 }", true)]
    [TestCase("(ex) is IOException", true)]
    [TestCase("ex is global::System.IO.IOException", true)]
    [TestCase("ex is Narrow", true)]
    [TestCase("ex is Derived", true)]
    [TestCase("ex is IOException or UnauthorizedAccessException", true)]
    [TestCase("ex is (IOException or UnauthorizedAccessException)", true)]
    [TestCase("ex is IOException and { Message: not null }", true)]
    [TestCase("ex is Exception and IOException", true)]
    [TestCase("ex is IOException && Arbitrary(ex)", true)]
    [TestCase("Arbitrary(ex) && ex is IOException", true)]
    [TestCase("ex is IOException || ex is UnauthorizedAccessException", true)]
    [TestCase("(ex is IOException || Arbitrary(ex)) && ex is UnauthorizedAccessException", true)]
    [TestCase("ex is Exception", false)]
    [TestCase("ex is SystemException", false)]
    [TestCase("ex is Broad", false)]
    [TestCase("ex is BroadSystem", false)]
    [TestCase("ex is N.Exception", false)]
    [TestCase("ex is N.SystemException", false)]
    [TestCase("ex is object", false)]
    [TestCase("ex is not IOException", false)]
    [TestCase("!(ex is not IOException)", false)]
    [TestCase("ex is IOException or Exception", false)]
    [TestCase("ex is IOException or BroadSystem", false)]
    [TestCase("ex is IOException or not null", false)]
    [TestCase("ex is IOException || Arbitrary(ex)", false)]
    [TestCase("Arbitrary(ex) || ex is IOException", false)]
    [TestCase("ex is IOException || ex is Broad", false)]
    [TestCase("ex.InnerException is IOException", false)]
    [TestCase("other is IOException", false)]
    [TestCase("Arbitrary(ex)", false)]
    [TestCase("ex.GetType() == typeof(IOException)", false)]
    [TestCase("ex is { InnerException: IOException }", false)]
    public async Task RestrictedFilterFactUsesBoundPositiveTests(string filter, bool expected)
    {
        string source = $$"""
            using System;
            using System.IO;
            using Broad = System.Exception;
            using BroadSystem = System.SystemException;
            using Narrow = System.IO.IOException;
            using N = System;
            class Derived : Exception { }
            class Example
            {
                static Exception other = new Exception();
                static bool Arbitrary(Exception value) => true;
                void Run()
                {
                    try { }
                    catch (Exception ex) when ({{filter}}) { }
                }
            }
            """;
        SyntaxTree tree = CSharpSyntaxTree.ParseText(source);
        MetadataReference[] references = [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)];
        CSharpCompilation compilation = CSharpCompilation.Create(
            "CatchFilterControls", [tree], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.That(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error),
            Is.Empty, source);
        SourceFile file = new CSharpSourceParser().ParseWithSemantics(
            "Example.cs", source, tree, compilation.GetSemanticModel(tree))!;
        CSharpStatementInfo statement =
            file.Statements.OfType<CSharpStatementInfo>().Single(item => item.Kind == "catch");

        Assert.That((await AsyncSourceFacts.SemanticAsync(statement, CancellationToken.None)).HasTypeRestrictedCatchFilter, Is.EqualTo(expected), filter);
        Assert.That((await AsyncSourceFacts.SemanticAsync(statement, CancellationToken.None)).HasTypeRestrictedCatchFilter, Is.EqualTo(expected), "repeated demand");
        Assert.That(CSharpBindings.Build().Accessors["CSharpStatement"]
            ["HasTypeRestrictedCatchFilter"](statement), Is.EqualTo(expected), "runtime binding");
    }

    [TestCase("System.Exception", false)]
    [TestCase("System.SystemException", false)]
    [TestCase("System.IO.IOException", true)]
    public async Task GlobalAliasesAreResolvedFromTheActualCompilation(string type, bool expected)
    {
        SyntaxTree aliases = CSharpSyntaxTree.ParseText("global using Hidden = " + type + ";");
        const string source = """
            class Example
            {
                void Run()
                {
                    try { }
                    catch (System.Exception ex) when (ex is Hidden) { }
                }
            }
            """;
        SyntaxTree tree = CSharpSyntaxTree.ParseText(source);
        CSharpCompilation compilation = CSharpCompilation.Create(
            "GlobalCatchFilterAliases", [tree, aliases],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.That(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error),
            Is.Empty, type);
        SourceFile file = new CSharpSourceParser().ParseWithSemantics(
            "Example.cs", source, tree, compilation.GetSemanticModel(tree))!;
        CSharpStatementInfo statement =
            file.Statements.OfType<CSharpStatementInfo>().Single(item => item.Kind == "catch");

        Assert.That((await AsyncSourceFacts.SemanticAsync(statement, CancellationToken.None)).HasTypeRestrictedCatchFilter, Is.EqualTo(expected), type);
    }

    [TestCase("IOException or UnauthorizedAccessException", true, 2)]
    [TestCase("IOException or InvalidDataException or FormatException or UnauthorizedAccessException",
        true, 4)]
    [TestCase("Narrow or UnauthorizedAccessException", true, 2)]
    [TestCase("IOException and { Message: not null }", true, 2)]
    [TestCase("Exception or IOException", false, 2)]
    [TestCase("SystemException or IOException", false, 2)]
    [TestCase("Nothing or IOException", false, 2)]
    [TestCase("IOException or Broad", false, 2)]
    public async Task CompoundPatternsDistinguishBoundTypesFromConstantsWithGlobalUsings(
        string pattern, bool expected, int constantSyntaxCount)
    {
        SyntaxTree imports = CSharpSyntaxTree.ParseText("""
            global using System;
            global using System.IO;
            global using Narrow = System.IO.IOException;
            global using Broad = System.Exception;
            """);
        string source = $$"""
            class Example
            {
                const Exception Nothing = null;
                void Run()
                {
                    try { }
                    catch (Exception ex) when (ex is {{pattern}}) { }
                }
            }
            """;
        SyntaxTree tree = CSharpSyntaxTree.ParseText(source);
        CSharpCompilation compilation = CSharpCompilation.Create(
            "CompoundCatchPatterns", [tree, imports],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.That(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error),
            Is.Empty, source);
        Assert.That(tree.GetRoot().DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ConstantPatternSyntax>().Count(),
            Is.EqualTo(constantSyntaxCount), "Roslyn retains ambiguous constant-pattern syntax");
        SourceFile file = new CSharpSourceParser().ParseWithSemantics(
            "Example.cs", source, tree, compilation.GetSemanticModel(tree))
            ?? throw new AssertionException("Expected the C# source file to parse.");
        CSharpStatementInfo statement = file.Statements.OfType<CSharpStatementInfo>()
            .Single(item => item.Kind == "catch");

        Assert.That((await AsyncSourceFacts.SemanticAsync(statement, CancellationToken.None))
            .HasTypeRestrictedCatchFilter, Is.EqualTo(expected), pattern);
        Assert.That(CSharpBindings.Build().Accessors["CSharpStatement"]
            ["HasTypeRestrictedCatchFilter"](statement), Is.EqualTo(expected), "runtime binding");
    }
}
