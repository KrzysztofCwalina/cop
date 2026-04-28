namespace Cop.Lang;

/// <summary>
/// A RUN statement: RUN commandName(arg1, arg2, ...)
/// Arguments are expressions (typically collection names or inline collection expressions).
/// </summary>
public record RunInvocation(
    string CommandName,
    List<Expression> Arguments,
    int Line);
