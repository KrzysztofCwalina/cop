// Identifies a source member and its declaring type.

namespace Cop.Providers.SourceModel;

/// <summary>
/// The shared identity and source facts of a declared member.
/// </summary>
public record MemberInfo(string Name, string DeclaringType, int Line);
