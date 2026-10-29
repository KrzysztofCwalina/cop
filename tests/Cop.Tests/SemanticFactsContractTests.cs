using Cop.Providers;
using Cop.Providers.SourceModel;
using NUnit.Framework;

namespace Cop.Tests;

[TestFixture]
public class SemanticFactsContractTests
{
    [Test]
    public void SemanticAccessors_ShareOneDemand_AndKeepReceiverDistinctFromDeclaration()
    {
        var call = new StatementInfo("call", [], null, "Run", [], 1, true);
        var file = new SourceFile("caller.test", "test", [], [call], "factory().Run(1)");
        var runs = 0;
        file.DeferSemanticAnalysis(() =>
        {
            runs++;
            call.ReceiverType = new("Derived", "example", [], "example.Derived");
            var result = new TypeReference("string", null, [], "string");
            call.ValueType = result;
            call.ReferencedCallable = new("Run", "example.Base.Run(int): string")
            {
                Namespace = "example", DeclaringType = "Base",
                ReturnType = result,
                Parameters = [new("int", null, [], "int")],
                SourceFile = "base.test", Line = 5, Column = 3, EndLine = 5, EndColumn = 30
            };
        });
        call.DeferTypeNameAnalysis(null);
        var accessors = CodeBindings.Build().Accessors;
        Assert.That(accessors["Statement"]["MemberName"](call), Is.EqualTo("Run"));
        Assert.That(accessors["Statement"]["CallTarget"](call), Is.Null);
        Assert.That(call.TypeName, Is.Null);
        Assert.That(runs, Is.Zero);
        Assert.That(((TypeReference)accessors["Statement"]["ReceiverType"](call)!).Name, Is.EqualTo("Derived"));
        var selected = (CallableReference)accessors["Statement"]["ReferencedCallable"](call)!;
        Assert.That(accessors["CallableReference"]["DeclaringType"](selected), Is.EqualTo("Base"));
        Assert.That(accessors["CallableReference"]["Signature"](selected), Is.EqualTo("example.Base.Run(int): string"));
        Assert.That(((TypeReference)accessors["Statement"]["ValueType"](call)!).Name, Is.EqualTo("string"));
        Assert.That(selected.Parameters.Single().Name, Is.EqualTo("int"));
        Assert.That((selected.SourceFile, selected.Line, selected.Column, selected.EndLine, selected.EndColumn),
            Is.EqualTo(("base.test", 5, 3, 5, 30)));
        Assert.That(runs, Is.EqualTo(1));
    }

    [Test]
    public void SyntaxCache_DoesNotDemandOrPersistDependencyBoundFacts()
    {
        var call = new StatementInfo("call", [], "SyntaxOwner", "Run", [], 1, true);
        var file = new SourceFile("caller.test", "test", [], [call], "factory().Run()");
        var runs = 0;
        file.DeferSemanticAnalysis(() =>
        {
            runs++;
            call.TypeName = "CompilerOwner";
            call.ReceiverType = new("Receiver", null, [], "Receiver");
            call.ValueType = new("Result", null, [], "Result");
            call.ReferencedCallable = new("Run", "CompilerOwner.Run(): Result");
        });
        var cold = SourceGraphIdentityTests.RoundTrip(file).Statements.Single();
        Assert.That(runs, Is.Zero);
        Assert.That(cold.TypeName, Is.EqualTo("SyntaxOwner"));
        Assert.That(call.ReferencedCallable?.Signature, Is.EqualTo("CompilerOwner.Run(): Result"));
        Assert.That(runs, Is.EqualTo(1));
        var warm = SourceGraphIdentityTests.RoundTrip(file).Statements.Single();
        Assert.That(warm.TypeName, Is.EqualTo("SyntaxOwner"));
        Assert.That(warm.ReceiverType, Is.Null);
        Assert.That(warm.ValueType, Is.Null);
        Assert.That(warm.ReferencedCallable, Is.Null,
            "File-only syntax caches must never persist dependency-bound answers, even after demand.");
        Assert.That(runs, Is.EqualTo(1));
    }

    [Test]
    public void UnknownFacts_RemainNull_AndBindingFailureIsNotRetriedOrHidden()
    {
        var call = new StatementInfo("call", [], null, "Missing", [], 1, true);
        Assert.That(call.ReceiverType, Is.Null);
        Assert.That(call.ValueType, Is.Null);
        Assert.That(call.ReferencedCallable, Is.Null);
        var runs = 0;
        call.DeferSemanticAnalysis(new(() => { runs++; throw new InvalidOperationException("missing compiler context"); }));
        Assert.That(call.MemberName, Is.EqualTo("Missing"));
        Assert.That(runs, Is.Zero);
        Assert.That(() => _ = call.ReferencedCallable, Throws.InvalidOperationException.With.Message.EqualTo("missing compiler context"));
        Assert.That(() => _ = call.ValueType, Throws.InvalidOperationException.With.Message.EqualTo("missing compiler context"));
        Assert.That(runs, Is.EqualTo(1));
    }
}
