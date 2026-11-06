// A provider proxy returned by `object('providerName')`.

using Cop.Core;
using Cop.Lang.Ast;

namespace Cop.Lang.Interpreter;

/// <summary>
/// A provider proxy returned by `object('providerName')`.
/// Member access resolves to provider collections registered in the environment.
/// For example: object('code').Types → looks up "Types" or "code.Types" in the environment.
/// </summary>
public sealed class CopProviderProxy : CopValue
{
    /// <summary>
    /// The registered provider namespace used for deferred member queries.
    /// </summary>
    public string ProviderName { get; }
    /// <summary>
    /// The provider's eager object members, when supplied alongside deferred queries.
    /// </summary>
    public CopObject? Data { get; }
    private readonly Environment _env;

    public CopProviderProxy(string providerName, Environment env, CopObject? data = null)
    {
        ProviderName = providerName;
        _env = env;
        Data = data;
    }

    /// <summary>
    /// Tests whether the provider-qualified member is registered in the environment.
    /// </summary>
    public bool HasField(string name)
    {
        // Only check qualified name (provider.Collection) — no bare-name fallback
        return _env.TryLookup($"{ProviderName}.{name}", out _);
    }

    /// <summary>
    /// Resolves a provider-qualified member, forcing a deferred binding when necessary.
    /// </summary>
    public async ValueTask<CopValue> GetFieldAsync(string name,
        CancellationToken cancellationToken = default)
    {
        // Only use qualified name (e.g., "csharp.Types") — no bare-name fallback
        if (_env.TryLookup($"{ProviderName}.{name}", out CopValue? qualified))
        {
            // Force thunks eagerly
            if (qualified is CopThunk thunk)
            {
                return await thunk.ForceAsync(cancellationToken).ConfigureAwait(false);
            }

            return qualified;
        }
        return CopNull.Instance;
    }

    /// <inheritdoc/>
    public override string Display() => $"<provider {ProviderName}>";
    /// <inheritdoc/>
    public override string ToString() => Display();
}
