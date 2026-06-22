using Cop.Core;
using Cop.Providers.SourceModel;

namespace Cop.Providers;

/// <summary>
/// Python-specific runtime bindings: the shared <see cref="CodeBindings"/> plus the
/// <c>PythonType</c> accessor set, keyed to <see cref="PythonTypeDeclaration"/>.
/// </summary>
public static class PythonBindings
{
    public static RuntimeBindings Build()
    {
        var bindings = CodeBindings.Build();

        bindings.ClrTypeMappings[typeof(PythonTypeDeclaration)] = "PythonType";

        var pythonAccessors = new Dictionary<string, Func<object, object?>>(bindings.Accessors["Type"])
        {
            ["IsDataclass"] = o => (object)((PythonTypeDeclaration)o).IsDataclass,
            ["IsEnum"] = o => (object)((PythonTypeDeclaration)o).IsEnum,
            ["IsAbstract"] = o => (object)((PythonTypeDeclaration)o).IsAbstract,
            ["IsNamedTuple"] = o => (object)((PythonTypeDeclaration)o).IsNamedTuple,
            ["IsProtocol"] = o => (object)((PythonTypeDeclaration)o).IsProtocol,
            ["IsException"] = o => (object)((PythonTypeDeclaration)o).IsException,
            ["HasSlots"] = o => (object)((PythonTypeDeclaration)o).HasSlots,
        };
        bindings.Accessors["PythonType"] = pythonAccessors;

        bindings.ClrTypeMappings[typeof(PythonMethodDeclaration)] = "PythonMethod";

        var pythonMethodAccessors = new Dictionary<string, Func<object, object?>>(bindings.Accessors["Method"])
        {
            ["IsStaticmethod"] = o => (object)((PythonMethodDeclaration)o).IsStaticmethod,
            ["IsClassmethod"] = o => (object)((PythonMethodDeclaration)o).IsClassmethod,
            ["IsProperty"] = o => (object)((PythonMethodDeclaration)o).IsProperty,
            ["IsGenerator"] = o => (object)((PythonMethodDeclaration)o).IsGenerator,
            ["IsAbstractMethod"] = o => (object)((PythonMethodDeclaration)o).IsAbstractMethod,
            ["IsDunder"] = o => (object)((PythonMethodDeclaration)o).IsDunder,
        };
        bindings.Accessors["PythonMethod"] = pythonMethodAccessors;

        bindings.ClrTypeMappings[typeof(PythonStatementInfo)] = "PythonStatement";

        var pythonStatementAccessors = new Dictionary<string, Func<object, object?>>(bindings.Accessors["Statement"])
        {
            ["IsWith"] = o => (object)((PythonStatementInfo)o).IsWith,
            ["IsAsyncWith"] = o => (object)((PythonStatementInfo)o).IsAsyncWith,
            ["IsRaise"] = o => (object)((PythonStatementInfo)o).IsRaise,
            ["IsAssert"] = o => (object)((PythonStatementInfo)o).IsAssert,
            ["IsComprehension"] = o => (object)((PythonStatementInfo)o).IsComprehension,
            ["IsGlobal"] = o => (object)((PythonStatementInfo)o).IsGlobal,
            ["IsNonlocal"] = o => (object)((PythonStatementInfo)o).IsNonlocal,
        };
        bindings.Accessors["PythonStatement"] = pythonStatementAccessors;

        return bindings;
    }
}
