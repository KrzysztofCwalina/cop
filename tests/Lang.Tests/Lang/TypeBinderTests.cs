using Cop.Lang;
using Cop.Providers;
using NUnit.Framework;

namespace Cop.Tests.Lang;

[TestFixture]
public class TypeBinderTests
{
    private TypeRegistry CreateRegistry()
    {
        var registry = new TypeRegistry();
        CodeTypeRegistrar.Register(registry);
        return registry;
    }

    [Test]
    public void Bind_ValidPropertyChain_NoErrors()
    {
        var registry = CreateRegistry();
        var binder = new TypeBinder(registry);
        var file = ScriptParser.Parse(
            """
            import code
            predicate isPublic(Type) => Type.Public
            """, "test.cop");

        var errors = binder.Bind(file);
        Assert.That(errors, Is.Empty);
    }

    [Test]
    public void Bind_InvalidPropertyAccess_ReportsError()
    {
        var registry = CreateRegistry();
        var binder = new TypeBinder(registry);
        var file = ScriptParser.Parse(
            """
            import code
            predicate bad(Type) => Type.NonExistentProperty == 'foo'
            """, "test.cop");

        var errors = binder.Bind(file);
        Assert.That(errors, Has.Count.GreaterThan(0));
        Assert.That(errors[0], Does.Contain("NonExistentProperty"));
        Assert.That(errors[0], Does.Contain("Type"));
    }

    [Test]
    public void Bind_InvalidTemplateProperty_ReportsError()
    {
        var registry = CreateRegistry();
        var binder = new TypeBinder(registry);
        var file = ScriptParser.Parse(
            """
            import code
            foreach Types => PRINT('ERROR: {item.FakeProperty}')
            """, "test.cop");

        var errors = binder.Bind(file);
        Assert.That(errors, Has.Count.GreaterThan(0));
        Assert.That(errors[0], Does.Contain("FakeProperty"));
    }

    [Test]
    public void Bind_ValidTemplate_NoErrors()
    {
        var registry = CreateRegistry();
        var binder = new TypeBinder(registry);
        var file = ScriptParser.Parse(
            """
            import code
            foreach Types => PRINT('INFO: {item.Name} is public: {item.Public}')
            """, "test.cop");

        var errors = binder.Bind(file);
        Assert.That(errors, Is.Empty);
    }

    [Test]
    public void Bind_LegacyModeNoImport_SkipsValidation()
    {
        var registry = CreateRegistry();
        var binder = new TypeBinder(registry);
        // No import statement → legacy mode → no validation
        var file = ScriptParser.Parse(
            """
            predicate bad(Type) => Type.CompletelyWrong == 'foo'
            foreach Types => PRINT('ERROR: {item.BadProp}')
            """, "test.cop");

        var errors = binder.Bind(file);
        Assert.That(errors, Is.Empty, "Legacy mode should not perform type checking");
    }

    [Test]
    public void Bind_UnknownCollection_ReportsError()
    {
        var registry = CreateRegistry();
        var binder = new TypeBinder(registry);
        var file = ScriptParser.Parse(
            """
            import code
            foreach NonExistentCollection => PRINT('ERROR: something')
            """, "test.cop");

        var errors = binder.Bind(file);
        Assert.That(errors, Has.Count.GreaterThan(0));
        Assert.That(errors[0], Does.Contain("NonExistentCollection"));
    }

    [Test]
    public void Bind_NestedPropertyChain_ValidatesCorrectly()
    {
        var registry = CreateRegistry();
        var binder = new TypeBinder(registry);
        var file = ScriptParser.Parse(
            """
            import code
            predicate hasParams(Method) => Method.Parameters.Count > 0
            """, "test.cop");

        var errors = binder.Bind(file);
        Assert.That(errors, Is.Empty);
    }

    [Test]
    public void Bind_MethodOnKnownType_NoErrors()
    {
        var registry = CreateRegistry();
        var binder = new TypeBinder(registry);
        var file = ScriptParser.Parse(
            """
            import code
            predicate nameCheck(Type) => Type.Name:endsWith('Client')
            """, "test.cop");

        var errors = binder.Bind(file);
        Assert.That(errors, Is.Empty);
    }

    [Test]
    public void Bind_LetDeclarationWithUnknownBase_ReportsError()
    {
        var registry = CreateRegistry();
        var binder = new TypeBinder(registry);
        var file = ScriptParser.Parse(
            """
            import code
            predicate isPublic(Type) => Type.Public
            let PublicTypes = FakeCollection:isPublic
            foreach PublicTypes => PRINT('INFO: {item.Name}')
            """, "test.cop");

        var errors = binder.Bind(file);
        Assert.That(errors, Has.Count.GreaterThan(0));
        Assert.That(errors[0], Does.Contain("FakeCollection"));
    }

    [Test]
    public void Bind_ValidLetDeclaration_NoErrors()
    {
        var registry = CreateRegistry();
        var binder = new TypeBinder(registry);
        var file = ScriptParser.Parse(
            """
            import code
            predicate isPublic(Type) => Type.Public
            let PublicTypes = Types:isPublic
            foreach PublicTypes => PRINT('INFO: {item.Name}')
            """, "test.cop");

        var errors = binder.Bind(file);
        Assert.That(errors, Is.Empty);
    }
}
