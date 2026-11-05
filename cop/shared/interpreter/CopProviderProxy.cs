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
    public string ProviderName { get; }
    public CopObject? Data { get; }
    private readonly Environment _env;

    public CopProviderProxy(string providerName, Environment env, CopObject? data = null)
    {
        ProviderName = providerName;
        _env = env;
        Data = data;
    }

    public bool HasField(string name)
    {
        // Only check qualified name (provider.Collection) — no bare-name fallback
        return _env.TryLookup($"{ProviderName}.{name}", out _);
    }

    public CopValue GetField(string name)
    {
        // Only use qualified name (e.g., "csharp.Types") — no bare-name fallback
        if (_env.TryLookup($"{ProviderName}.{name}", out CopValue? qualified))
        {
            // Force thunks eagerly
            if (qualified is CopThunk thunk)
            {
                return thunk.Force();
            }

            return qualified;
        }
        return CopNull.Instance;
    }

    public override string Display() => $"<provider {ProviderName}>";
    public override string ToString() => Display();
}
