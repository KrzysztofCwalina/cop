using Cop.Core;
using Cop.Providers.SourceModel;

namespace Cop.Providers;

public static class CopBindings
{
    public static RuntimeBindings Build()
    {
        var bindings = CodeBindings.Build();
        bindings.ClrTypeMappings[typeof(CopFunctionDeclaration)] = "CopFunction";
        bindings.Accessors["CopFunction"] = new(bindings.Accessors["Method"])
        {
            ["IsIntrinsic"] = o => ((CopFunctionDeclaration)o).IsIntrinsic,
            ["IsPredicate"] = o => ((CopFunctionDeclaration)o).IsPredicate,
            ["IsCommand"] = o => ((CopFunctionDeclaration)o).IsCommand,
            ["IsExported"] = o => ((CopFunctionDeclaration)o).IsExported,
            ["HasGuard"] = o => ((CopFunctionDeclaration)o).HasGuard
        };
        return bindings;
    }
}
