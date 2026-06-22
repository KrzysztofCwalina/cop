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
            ["IsSealed"] = o => (object)((JavaTypeDeclaration)o).IsSealed,
            ["IsNonSealed"] = o => (object)((JavaTypeDeclaration)o).IsNonSealed,
            ["IsFinal"] = o => (object)((JavaTypeDeclaration)o).IsFinal,
            ["IsGeneric"] = o => (object)((JavaTypeDeclaration)o).IsGeneric,
        };
        bindings.Accessors["JavaType"] = javaAccessors;

        bindings.ClrTypeMappings[typeof(JavaMethodDeclaration)] = "JavaMethod";

        var javaMethodAccessors = new Dictionary<string, Func<object, object?>>(bindings.Accessors["Method"])
        {
            ["IsSynchronized"] = o => (object)((JavaMethodDeclaration)o).IsSynchronized,
            ["IsNative"] = o => (object)((JavaMethodDeclaration)o).IsNative,
            ["IsDefault"] = o => (object)((JavaMethodDeclaration)o).IsDefault,
            ["IsStrictfp"] = o => (object)((JavaMethodDeclaration)o).IsStrictfp,
            ["IsGeneric"] = o => (object)((JavaMethodDeclaration)o).IsGeneric,
        };
        bindings.Accessors["JavaMethod"] = javaMethodAccessors;

        bindings.ClrTypeMappings[typeof(JavaStatementInfo)] = "JavaStatement";

        var javaStatementAccessors = new Dictionary<string, Func<object, object?>>(bindings.Accessors["Statement"])
        {
            ["IsSynchronized"] = o => (object)((JavaStatementInfo)o).IsSynchronized,
            ["IsTryWithResources"] = o => (object)((JavaStatementInfo)o).IsTryWithResources,
            ["IsEnhancedFor"] = o => (object)((JavaStatementInfo)o).IsEnhancedFor,
            ["IsThrow"] = o => (object)((JavaStatementInfo)o).IsThrow,
            ["IsAssert"] = o => (object)((JavaStatementInfo)o).IsAssert,
        };
        bindings.Accessors["JavaStatement"] = javaStatementAccessors;

        return bindings;
    }
}
