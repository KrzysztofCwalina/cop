// Source-selected declaration and instantiated signature.

namespace Cop.Lang.Interpreter;

using Cop.Lang.Ast;

/// <summary>Source-selected declaration and instantiated signature. Null types remain unknown.</summary>
public sealed record StaticCallInfo(FunctionDecl? Declaration, IReadOnlyList<TypeInfo?> ParameterTypes,
    TypeInfo? ResultType, IReadOnlyList<string> Diagnostics);
