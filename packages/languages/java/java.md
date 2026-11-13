# Java query helpers

`java.parse()` uses the JDK compiler-tree API when available and retains a built-in
structural fallback when a JDK is absent. Neither backend executes target code.

## Reusable declaration and environment queries

- `isInterface(Type:isJava)` includes ordinary and annotation interfaces.
- `isRecord`, `isGenericType` and `isGenericMethod` expose the corresponding Java
  declarations. Existing helpers also cover enums, sealed/non-sealed/final types,
  synchronized/native/default/strictfp methods, synchronized blocks, enhanced
  loops, throws and assertions.
- `isEnvironmentVariableAccess(Statement:isJava)` matches parsed calls to
  `System.getenv` and `java.lang.System.getenv`. Both the named overload and
  zero-argument environment enumeration qualify.
- `isEnvironmentVariableRead` selects the one-argument named read. It does not
  select enumeration or `System.getProperty`/`getProperties`/`setProperty`.
  These are syntactic name queries, not symbol resolution: a same-named receiver
  can match. Static imports, other receiver names and aliases are not inferred.
  The helpers are Java-constrained when packages are imported together.

## Explicit-path proofs

Narrow statements with `:asJava` before using the following helpers. A false result
means **not proved**, not necessarily unsafe. None models implicit exceptions
from calls, constructors, monitors or resource closing.

- `alwaysRethrowsOriginalException` accepts a catch body consisting of an original
  `throw caught`, optionally parenthesized or in nested blocks, or an `if/else`
  whose every branch has that form. Conditions must have no calls, assignments
  or increments. Reassignment, replacement throws, one-sided branches, loops,
  nested functions, general statement sequences and try/finally are not proofs.
  The JDK backend rejects enclosing finally clauses up to the callable boundary.
  The smaller fallback supports braced branches and rejects all such proofs in
  a file containing a finally clause.
- `isUsingResource` marks only the invocation or constructor directly initializing
  a try-with-resources variable, including parenthesized initializers. Calls
  nested in its arguments or body are not resource initializers. Java 9
  existing-variable resources have no initializer call to mark.
- `transfersResourceOwnership` marks object creation directly returned or assigned
  to a compiler-bound field (including field initializers). It is an escape-site
  proof, **not** a proof of eventual closing. Factory method calls, locals later
  returned, ordinary arguments, array storage and arbitrary destination setters
  are not supported. Finally regions are conservatively rejected. Direct returns
  work in the fallback; field storage requires JDK attribution.
- `isCancellationHandler` uses attributed catch types to identify
  `java.lang.InterruptedException` and its subclasses. Broad `Exception` catches,
  multi-catches and cancellation exceptions from futures are not treated as
  interruption-only handlers.
- `isCallerCancellationPreserved` requires an interruption handler and a supported
  explicit path proof. Each path must either throw the original caught exception
  or call the **bound** `java.lang.Thread.currentThread().interrupt()`. Restoration
  alone, or restoration immediately followed by a void return, is accepted.
  Braced blocks and side-effect-free if/else paths are supported. Other threads,
  lookalike methods, replacement throws, clearing the interrupt flag, intervening
  calls, reassignment, lambdas and finally cleanup are not accepted.
  Rethrowing `InterruptedException` propagates interruption to the caller; it does
  not claim that the current thread's flag was set.

Interruption and bound field-ownership facts demand JDK attribution over locally
available source and dependencies. Missing context produces compiler diagnostics.
Without that backend, interruption helpers return false and requesting semantic
helpers emits an explicit capability warning; no interruption proof is fabricated
from a type or method spelling. Structural resource/return/rethrow proofs still
work. Java currently reparses on engine runs instead of persistently caching
process-backed semantic work. Materialized Java query flags nevertheless survive
source-model cache serialization.

## C# concepts without Java equivalents

Java has no C# async-void, nullable directives, readonly/ref/record structs,
file-local/partial declarations, extension methods, unsafe/fixed/checked blocks,
iterator `yield`, `goto`, async enumeration, catch filters or primary constructors
in the supported language model. Java records and native/synchronized declarations
have their own helpers rather than misleading C# aliases. C# caller-token lineage,
linked tokens and canceled completion-source proofs do not translate to Java
interruption; Java future cancellation and arbitrary implicit exception effects
are intentionally not inferred.
