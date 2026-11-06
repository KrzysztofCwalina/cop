// Reports checksum verification for one locked project file.

namespace Cop.Core;

/// <summary>Result of verifying a single locked file.</summary>
public record LockVerification(string RelativePath, string Status, string? ExpectedChecksum, string? ActualChecksum);
