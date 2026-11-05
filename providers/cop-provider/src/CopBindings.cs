// Maps Cop-specific declarations to runtime-accessible source-model fields.

using Cop.Core;
using Cop.Providers.SourceModel;

namespace Cop.Providers;

/// <summary>Builds runtime accessors for Cop-specific source declarations.</summary>
public static class CopBindings
{
    /// <summary>Extends shared code bindings with Cop function metadata.</summary>
    public static RuntimeBindings Build()
    {
        RuntimeBindings bindings = CodeBindings.Build();
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
