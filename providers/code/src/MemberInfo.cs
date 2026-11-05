// Identifies a source member and its declaring type.

namespace Cop.Providers.SourceModel;

public record MemberInfo(string Name, string DeclaringType, int Line);
