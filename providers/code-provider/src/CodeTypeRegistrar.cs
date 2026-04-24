using Cop.Core;
using Cop.Lang;
using Cop.Providers.SourceModel;

namespace Cop.Providers;

/// <summary>
/// Compatibility shim that delegates to <see cref="CodeProvider"/>.
/// Existing code (Engine, tests) can continue calling Register()
/// while the actual logic lives in the provider abstraction.
/// </summary>
public static class CodeTypeRegistrar
{
    private static readonly CodeProvider _provider = new();

    /// <summary>
    /// Registers all CLR type mappings, property accessors, and collection extractors
    /// for the code analysis source model. Delegates to <see cref="CodeProvider"/>.
    /// </summary>
    public static void Register(TypeRegistry registry)
    {
        var schema = ProviderSchema.FromJson(_provider.GetSchema());
        registry.RegisterProviderSchema(schema);

        var bindings = _provider.GetRuntimeBindings()!;

        foreach (var (clrType, copTypeName) in bindings.ClrTypeMappings)
            registry.RegisterClrType(clrType, copTypeName);
        foreach (var (typeName, accessors) in bindings.Accessors)
            registry.RegisterAccessors(typeName, accessors);

        if (bindings.TextConverters != null)
        {
            foreach (var (typeName, converter) in bindings.TextConverters)
            {
                var desc = registry.GetType(typeName);
                if (desc != null)
                    desc.TextConverter = converter;
            }
        }

        if (bindings.CollectionExtractors != null)
        {
            foreach (var (collName, extractor) in bindings.CollectionExtractors)
                registry.RegisterCollectionExtractor(collName, doc => extractor(doc.As<SourceFile>()));
        }

        if (bindings.MethodEvaluators != null)
        {
            foreach (var ((typeName, methodName), evaluator) in bindings.MethodEvaluators)
                registry.RegisterMethodEvaluator(typeName, methodName, evaluator);
        }
    }
}
