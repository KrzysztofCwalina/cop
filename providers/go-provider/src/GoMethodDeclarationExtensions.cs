// Converts shared method declarations to Go-specific declarations.

using Cop.Providers;

namespace Cop.Providers.SourceModel;

/// <summary>Converts shared method declarations to Go-specific declarations.</summary>
public static class GoMethodDeclarationExtensions
{
    /// <summary>Wraps a shared method with Go receiver and signature facts.</summary>
    public static GoMethodDeclaration AsGo(
        this MethodDeclaration source,
        bool isPointerReceiver = false,
        bool hasNamedReturns = false,
        bool isVariadic = false,
        bool isGeneric = false)
        => new(source, isPointerReceiver, hasNamedReturns, isVariadic, isGeneric);
}
