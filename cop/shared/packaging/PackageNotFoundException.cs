// Signals that the requested package could not be found in its source repository.

namespace Cop.Core;

/// <summary>Thrown when a package is not found in the GitHub repository.</summary>
public class PackageNotFoundException : Exception
{
    /// <summary>Reports that a package could not be found without additional context.</summary>
    public PackageNotFoundException() { }

    /// <summary>Reports a missing package with a descriptive message.</summary>
    public PackageNotFoundException(string message) : base(message) { }

    /// <summary>
    /// Reports a missing package while retaining the underlying failure.
    /// </summary>
    public PackageNotFoundException(string message, Exception innerException) : base(message, innerException) { }
}
