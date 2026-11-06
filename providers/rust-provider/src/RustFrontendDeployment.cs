// Locates and safely deploys the pinned Rust frontend and standard-library bundle.

using System.IO.Compression;
using System.Security.Cryptography;

namespace Cop.Providers.SourceParsers;

internal sealed record RustFrontendDeployment(string Executable, Func<string>? PrepareStandardLibrary = null);
