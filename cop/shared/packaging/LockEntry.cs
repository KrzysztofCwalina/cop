// Stores the signed checksum associated with one tamper-protected file.

using YamlDotNet.Serialization;

namespace Cop.Core;

/// <summary>Represents a single locked file entry in .cop-lock.</summary>
public class LockEntry
{
    /// <summary>The checksum of the locked file's content.</summary>
    [YamlMember(Alias = "checksum")]
    public string Checksum { get; set; } = "";

    /// <summary>The signature authenticating the recorded file checksum.</summary>
    [YamlMember(Alias = "signature")]
    public string Signature { get; set; } = "";
}
