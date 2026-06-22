using Cop.Core;
using Cop.Providers.SourceModel;

namespace Cop.Providers;

/// <summary>
/// JavaScript-specific runtime bindings: the shared <see cref="CodeBindings"/> plus the
/// <c>JavaScriptType</c> accessor set, keyed to <see cref="JavaScriptTypeDeclaration"/>.
/// </summary>
public static class JavaScriptBindings
{
    public static RuntimeBindings Build()
    {
        var bindings = CodeBindings.Build();

        bindings.ClrTypeMappings[typeof(JavaScriptTypeDeclaration)] = "JavaScriptType";

        var jsAccessors = new Dictionary<string, Func<object, object?>>(bindings.Accessors["Type"])
        {
            ["IsExported"] = o => (object)((JavaScriptTypeDeclaration)o).IsExported,
            ["HasBaseClass"] = o => (object)((JavaScriptTypeDeclaration)o).HasBaseClass,
            ["IsAbstract"] = o => (object)((JavaScriptTypeDeclaration)o).IsAbstract,
            ["IsGeneric"] = o => (object)((JavaScriptTypeDeclaration)o).IsGeneric,
            ["HasImplements"] = o => (object)((JavaScriptTypeDeclaration)o).HasImplements,
        };
        bindings.Accessors["JavaScriptType"] = jsAccessors;

        bindings.ClrTypeMappings[typeof(JavaScriptMethodDeclaration)] = "JavaScriptMethod";

        var jsMethodAccessors = new Dictionary<string, Func<object, object?>>(bindings.Accessors["Method"])
        {
            ["IsGenerator"] = o => (object)((JavaScriptMethodDeclaration)o).IsGenerator,
            ["IsArrow"] = o => (object)((JavaScriptMethodDeclaration)o).IsArrow,
            ["IsGetter"] = o => (object)((JavaScriptMethodDeclaration)o).IsGetter,
            ["IsSetter"] = o => (object)((JavaScriptMethodDeclaration)o).IsSetter,
            ["IsConstructor"] = o => (object)((JavaScriptMethodDeclaration)o).IsConstructor,
        };
        bindings.Accessors["JavaScriptMethod"] = jsMethodAccessors;

        bindings.ClrTypeMappings[typeof(JavaScriptStatementInfo)] = "JavaScriptStatement";

        var jsStatementAccessors = new Dictionary<string, Func<object, object?>>(bindings.Accessors["Statement"])
        {
            ["IsForOf"] = o => (object)((JavaScriptStatementInfo)o).IsForOf,
            ["IsForIn"] = o => (object)((JavaScriptStatementInfo)o).IsForIn,
            ["IsThrow"] = o => (object)((JavaScriptStatementInfo)o).IsThrow,
            ["IsAwait"] = o => (object)((JavaScriptStatementInfo)o).IsAwait,
            ["IsTryCatch"] = o => (object)((JavaScriptStatementInfo)o).IsTryCatch,
        };
        bindings.Accessors["JavaScriptStatement"] = jsStatementAccessors;

        return bindings;
    }
}
