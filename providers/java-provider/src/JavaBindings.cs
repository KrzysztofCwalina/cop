using Cop.Core;
using Cop.Providers.SourceModel;

namespace Cop.Providers;

/// <summary>
/// Java-specific runtime bindings: the shared <see cref="CodeBindings"/> plus the
/// <c>JavaType</c> accessor set (base Type accessors + Java-only fields), keyed to the
/// <see cref="JavaTypeDeclaration"/> CLR type. The shared CodeBindings stay untouched.
/// </summary>
public static class JavaBindings
{
    public static RuntimeBindings Build()
    {
        var bindings = CodeBindings.Build();

        bindings.ClrTypeMappings[typeof(JavaTypeDeclaration)] = "JavaType";

        var javaAccessors = new Dictionary<string, Func<object, object?>>(bindings.Accessors["Type"])
        {
            ["IsRecord"] = o => (object)((JavaTypeDeclaration)o).IsRecord,
            ["IsEnum"] = o => (object)((JavaTypeDeclaration)o).IsEnum,
        };
        bindings.Accessors["JavaType"] = javaAccessors;

        return bindings;
    }
}
