// Distinguishes collection predicates from predicates bound to individual elements.

namespace Cop.Lang.Interpreter;

/// <summary>The implicit bindings available while resolving a query predicate.</summary>
internal enum QueryPredicateContext
{
    /// <summary>Resolve the expression without introducing an implicit item binding.</summary>
    Collection,

    /// <summary>Keep item references bound to the current collection element.</summary>
    Element
}
