using Cop.Core;
using Cop.Providers.SourceModel;

namespace Cop.Providers;

/// <summary>
/// Go-specific runtime bindings: the shared <see cref="CodeBindings"/> plus the <c>GoType</c>
/// accessor set, keyed to <see cref="GoTypeDeclaration"/>.
/// </summary>
public static class GoBindings
{
    public static RuntimeBindings Build()
    {
        var bindings = CodeBindings.Build();

        bindings.ClrTypeMappings[typeof(GoTypeDeclaration)] = "GoType";

        var goAccessors = new Dictionary<string, Func<object, object?>>(bindings.Accessors["Type"])
        {
            ["IsInterface"] = o => (object)((GoTypeDeclaration)o).IsInterface,
            ["IsStruct"] = o => (object)((GoTypeDeclaration)o).IsStruct,
        };
        bindings.Accessors["GoType"] = goAccessors;

        return bindings;
    }
}
