// Makes predicate inversion explicit at provider-filter compilation call sites.

namespace Cop.Lang.Interpreter;

/// <summary>Whether a compiled predicate matches its expression or its complement.</summary>
public enum PredicatePolarity
{
    /// <summary>Match values for which the source predicate is true.</summary>
    Positive,

    /// <summary>Match values for which the source predicate is false.</summary>
    Negative
}
