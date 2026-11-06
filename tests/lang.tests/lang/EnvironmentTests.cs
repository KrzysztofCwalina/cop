using Cop.Lang.Interpreter;
using NUnit.Framework;
using CopEnvironment = Cop.Lang.Interpreter.Environment;

namespace Cop.Tests.Lang;

[TestFixture]
public sealed class EnvironmentTests
{
    [Test]
    public void SingleBindingScopesAvoidDictionaryAllocation()
    {
        const int count = 1_000;
        CopEnvironment[] scopes = new CopEnvironment[count];
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < count; index++)
        {
            scopes[index] = new CopEnvironment();
            scopes[index].Define("item", CopNull.Instance);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.That(scopes.Select(scope => scope.Lookup("item")),
            Is.EqualTo(Enumerable.Repeat(CopNull.Instance, count)));
        Assert.That(allocated, Is.LessThan(128L * count));
    }

    [Test]
    public void BindingsPreserveCaseSensitiveLookupReplacementAndInsertionOrder()
    {
        CopEnvironment scope = new();
        scope.Define("item", new CopInt(1));
        scope.Define("item", new CopInt(2));
        Assert.That(((CopInt)scope.Lookup("item")).Value, Is.EqualTo(2));
        Assert.That(scope.TryLookup("Item", out CopValue missing), Is.False);
        Assert.That(missing, Is.SameAs(CopNull.Instance));

        scope.Define("Item", new CopInt(3));
        scope.Define("other", new CopInt(4));
        scope.Define("item", new CopInt(5));

        Assert.That(scope.AllBindings().Select(pair => pair.Key),
            Is.EqualTo(new[] { "item", "Item", "other" }));
        Assert.That(scope.AllBindings().Select(pair => ((CopInt)pair.Value).Value),
            Is.EqualTo(new[] { 5, 3, 4 }));
    }

    [Test]
    public void ChildBindingsShadowParentsWithoutHidingOtherInheritedValues()
    {
        CopEnvironment parent = new();
        parent.Define("item", new CopInt(1));
        parent.Define("inherited", new CopInt(2));
        CopEnvironment child = parent.Extend();
        child.Define("item", new CopInt(3));

        Assert.That(((CopInt)parent.Lookup("item")).Value, Is.EqualTo(1));
        Assert.That(((CopInt)child.Lookup("item")).Value, Is.EqualTo(3));
        Assert.That(((CopInt)child.Lookup("inherited")).Value, Is.EqualTo(2));
        Assert.That(child.Lookup("absent"), Is.SameAs(CopNull.Instance));
        Assert.That(child.AllBindings().Select(pair => pair.Key),
            Is.EqualTo(new[] { "item" }));
    }

    [Test]
    public void NullNamesRemainInvalidInEmptyAndPopulatedScopes()
    {
        CopEnvironment scope = new();
        Assert.Throws<ArgumentNullException>(() => scope.Define(null!, CopNull.Instance));
        Assert.Throws<ArgumentNullException>(() => scope.Lookup(null!));
        scope.Define("item", CopNull.Instance);
        Assert.Throws<ArgumentNullException>(() => scope.TryLookup(null!, out _));
    }
}
