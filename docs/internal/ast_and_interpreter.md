# AST & Interpreter: Academic Refactoring Plan

## Problem Statement

The Cop parser and interpreter were built incrementally via "vibe coding" driven by scenarios rather than proper compiler/parser design. The result is:

- **Parser** (1286 lines) mixes parsing with semantic analysis (collection decomposition, rule-id derivation, collection-vs-value guessing)
- **Interpreter** (2450 lines) is an orchestration layer entangled with providers, packages, sinks, streaming, and command routing
- **Evaluator** (2024 lines) is a tree-walker that hardcodes domain-specific dispatch (provider namespaces, collection methods, DataObject access, I/O operations)
- **Type Registry** (1030 lines) conflates language types, provider schemas, collection registries, enum/flags symbols, and runtime bridges
- **No proper AST** — `ScriptFile` is a flat bag of declaration lists, not a tree
- **No proper symbol table** — symbols are resolved by scanning multiple dictionaries ad hoc
- **No phase separation** — parsing, binding, type-checking, and evaluation are all interleaved

**Goal:** Refactor so that someone reading just the parser/interpreter code sees a clean, general-purpose functional language implementation — no domain concepts (checks, packages, providers, sinks, collections) visible in the core.

---

## Current Architecture (As-Is)

```
┌─────────────────────────────────────────────────────────┐
│  ScriptParser.cs (1286 lines)                           │
│  - Lexing via Tokenizer.cs                              │
│  - Produces ScriptFile (flat declaration bags)          │
│  - Mixes parsing with semantic guessing                 │
│  - Domain: collections, commands, sinks, foreach, run   │
└───────────────────────────┬─────────────────────────────┘
                            │
┌───────────────────────────▼─────────────────────────────┐
│  ScriptInterpreter.cs (2450 lines)                      │
│  - Command orchestration                                │
│  - Symbol table building (from flat lists)              │
│  - Provider/sink/streaming resolution                   │
│  - Creates PredicateEvaluator for expression eval       │
└───────────────────────────┬─────────────────────────────┘
                            │
┌───────────────────────────▼─────────────────────────────┐
│  PredicateEvaluator.cs (2024 lines)                     │
│  - Tree-walking expression evaluator                    │
│  - Built-in dispatch (print, save, object, fail, etc.)  │
│  - Collection intrinsics (any, all, where, select...)   │
│  - Provider namespace dispatch                          │
│  - Package-qualified function lookup                    │
│  - DataObject field access, schema validation           │
└─────────────────────────────────────────────────────────┘
```

### Key Files and Line Counts

| File | Lines | Role |
|------|-------|------|
| ScriptParser.cs | 1286 | Recursive-descent parser |
| ScriptInterpreter.cs | 2450 | Command orchestration + symbol table building |
| PredicateEvaluator.cs | 2024 | Expression tree-walker |
| TypeRegistry.cs | 1030 | Type registry + provider bridge |
| Tokenizer.cs | 370 | Lexer |
| FilterCompiler.cs | 252 | Filter → Func<object,bool> compiler |
| FilterEvaluator.cs | 163 | Filter expression evaluator |
| Expression.cs | 46 | AST node records |
| FilterExpression.cs | ~160 | Filter-specific AST nodes |

---

## Target Architecture (To-Be)

```
┌─────────────────────────────────────────────────────────────────────┐
│  LAYER 1: LEXER (Tokenizer)                          [parser/]      │
│  - Pure tokenization, no semantic keywords beyond general-purpose   │
│  - Tokens: ident, number, string, operators, punctuation, keywords  │
│  - Keywords: let, fn, type, enum, if, match, import, export         │
└───────────────────────────┬─────────────────────────────────────────┘
                            │ Token stream
┌───────────────────────────▼─────────────────────────────────────────┐
│  LAYER 2: PARSER → AST                              [parser/]       │
│  - Pure recursive-descent parser                                    │
│  - Produces a proper AST tree (not flat declaration bags)           │
│  - No semantic analysis, no collection decomposition                │
│  - General-purpose: declarations, expressions, statements           │
│  - Knows NOTHING about providers, sinks, data objects               │
└───────────────────────────┬─────────────────────────────────────────┘
                            │ Untyped AST  [ast/]
┌───────────────────────────▼─────────────────────────────────────────┐
│  LAYER 3: BINDER / SEMANTIC ANALYSIS                [interpreter/]  │
│  - Name resolution (scoped symbol table)                            │
│  - Type inference and checking                                      │
│  - Import resolution                                                │
│  - Produces a Bound Tree / Semantic Model                           │
│  - Still general-purpose (works with abstract "external modules")   │
└───────────────────────────┬─────────────────────────────────────────┘
                            │ Bound/Typed IR
┌───────────────────────────▼─────────────────────────────────────────┐
│  LAYER 4: EVALUATOR (Tree-walking Interpreter)      [interpreter/]  │
│  - Pure expression evaluator with clean call dispatch               │
│  - Environment-based (lexical scopes, closures)                     │
│  - Extensible via "foreign function interface" (not hardcoded)      │
│  - Collection operations as library functions, not built-in magic   │
│  - No direct knowledge of DataObject, providers, sinks              │
└───────────────────────────┬─────────────────────────────────────────┘
                            │ FFI boundary
┌───────────────────────────▼─────────────────────────────────────────┐
│  LAYER 5: RUNTIME BRIDGE (Domain adapter — outside core language)    │
│  - Provider integration                                             │
│  - Sink/streaming orchestration                                     │
│  - Package loading and command routing                              │
│  - DataObject ↔ language value marshaling                           │
│  - Collection source resolution                                     │
│  - This is where ALL domain-specific code lives                     │
└─────────────────────────────────────────────────────────────────────┘
```

### Folder Structure

```
cop/shared/
├── parser/                 # Lexing + parsing (syntax only, no semantics)
│   ├── Tokenizer.cs        # Lexer — token stream production
│   ├── Parser.cs           # Recursive-descent parser → AST
│   └── Token.cs            # Token types and keyword definitions
│
├── ast/                    # AST node definitions (pure data, no behavior)
│   ├── AstNodes.cs         # All declaration, statement, expression nodes
│   ├── AstVisitor.cs       # Visitor pattern base class
│   └── AstPrinter.cs       # Debug pretty-printer
│
├── interpreter/            # Binding, type-checking, evaluation (execution engine)
│   ├── Binder.cs           # Name resolution, scope building → BoundTree
│   ├── SymbolTable.cs      # Scoped symbol table with parent pointers
│   ├── TypeChecker.cs      # Type inference and checking
│   ├── BoundNodes.cs       # Bound tree nodes (AST + resolved type info)
│   ├── Evaluator.cs        # Tree-walking interpreter over BoundTree
│   ├── Environment.cs      # Lexical scope chain (Define/Lookup/Extend)
│   ├── Value.cs            # Language value types (CopString, CopNumber, etc.)
│   └── ForeignFunction.cs  # FFI interface for runtime-provided functions
│
└── (existing files)        # Kept during migration, removed in Phase 6
```

**Separation of concerns:**
- `parser/` — knows only syntax. Input: source text. Output: untyped AST nodes from `ast/`.
- `ast/` — pure data definitions. No logic, no imports from parser or interpreter. Shared by all layers.
- `interpreter/` — knows semantics. Input: AST. Output: evaluated results. Extensible via FFI only.

---

## Proposed AST Node Hierarchy

Replace the current flat `Expression` + domain-specific declaration records with a proper AST:

```csharp
// === AST Root ===
abstract record AstNode;

// === Module-level ===
record ModuleNode(List<Declaration> Declarations) : AstNode;

// === Declarations ===
abstract record Declaration : AstNode;
record ImportDecl(string ModuleName, List<string>? Symbols) : Declaration;
record TypeDecl(string Name, string? BaseType, List<PropertyDecl> Properties, bool IsExported) : Declaration;
record EnumDecl(string Name, List<string> Members, bool IsExported) : Declaration;
record FlagsDecl(string Name, List<string> Members, bool IsExported) : Declaration;
record FunctionDecl(string Name, List<Parameter> Params, TypeRef? ReturnType, 
                    FunctionBody Body, bool IsExported) : Declaration;
record LetDecl(string Name, TypeRef? TypeAnnotation, Expression Value, bool IsExported) : Declaration;
record CommandDecl(string Name, List<Statement> Body, bool IsExported) : Declaration;

// === Statements (inside commands/blocks) ===
abstract record Statement : AstNode;
record LetStatement(string Name, Expression Value) : Statement;
record ForEachStatement(string Variable, Expression Collection, List<Statement> Body) : Statement;
record ExpressionStatement(Expression Expr) : Statement;
record PipelineStatement(Expression Source, List<PipelineStage> Stages) : Statement;

// === Expressions (reuse existing + extend) ===
abstract record Expression : AstNode;
record IdentifierExpr(string Name) : Expression;
record LiteralExpr(object Value) : Expression;
record BinaryExpr(Expression Left, BinaryOp Op, Expression Right) : Expression;
record UnaryExpr(UnaryOp Op, Expression Operand) : Expression;
record CallExpr(Expression Callee, List<Expression> Args) : Expression;
record MemberExpr(Expression Object, string Member) : Expression;
record IndexExpr(Expression Object, Expression Index) : Expression;
record LambdaExpr(List<Parameter> Params, Expression Body) : Expression;
record ConditionalExpr(Expression Cond, Expression Then, Expression Else) : Expression;
record MatchExpr(Expression Discriminant, List<MatchArm> Arms) : Expression;
record ListExpr(List<Expression> Elements) : Expression;
record ObjectExpr(string? TypeHint, List<(string Key, Expression Value)> Fields) : Expression;
record FilterExpr(Expression Collection, Expression Predicate) : Expression;

// === Supporting ===
record Parameter(string Name, TypeRef? Type);
record TypeRef(string Name, bool IsCollection = false);
record MatchArm(Pattern Pat, Expression Body);
abstract record Pattern;
record LiteralPattern(object Value) : Pattern;
record WildcardPattern() : Pattern;
record IdentifierPattern(string Name) : Pattern;

// === Function bodies ===
abstract record FunctionBody;
record ExpressionBody(Expression Expr) : FunctionBody;
record MappingBody(List<(string Field, Expression Value)> Mappings) : FunctionBody;
record IntrinsicBody() : FunctionBody;  // marker: implementation provided by runtime
```

### Key Differences from Current Design

| Current | Target | Rationale |
|---------|--------|-----------|
| `ScriptFile` (flat bags of lists) | `ModuleNode` (tree of declarations) | Proper AST structure |
| `PredicateDefinition` separate from `FunctionDefinition` | Unified `FunctionDecl` (predicates are functions returning bool) | Predicates are just typed functions |
| `CollectionDeclaration` + `LetDeclaration` dual-purpose | `LetDecl` only (collection = `let x : [T] = ...`) | `let` is the only binding form |
| `CommandBlock` with embedded template/filters/sinks | `CommandDecl` with `Statement` body | Commands are just named statement blocks |
| `CollectionUnionExpr`, `PathScopedExpr`, `NicExpr` | Remove — use `BinaryExpr(+)`, `CallExpr`, `LiteralExpr(null)` | No domain-specific nodes |
| `FilterExpression` separate hierarchy | `Expression` with predicate functions | Filters are just function application |

---

## Refactoring Phases

### Phase 1: Define Clean AST (Non-breaking)

**Goal:** Create the target AST node hierarchy alongside existing code.

1. Create `cop/shared/ast/` directory with:
   - `AstNodes.cs` — all node types above
   - `AstVisitor.cs` — visitor pattern base class
   - `AstPrinter.cs` — debug printer for verification
2. Keep existing `Expression.cs`, `ScriptFile.cs`, etc. unchanged
3. No behavior changes — purely additive

### Phase 2: New Parser (Parallel implementation)

**Goal:** Implement a clean recursive-descent parser that produces the new AST.

1. Create `cop/shared/parser/Parser.cs` — new parser targeting `ModuleNode`
   - Pure recursive descent with Pratt parsing for expressions
   - Zero domain knowledge — just syntax
   - Uses existing `Tokenizer` (moved into `parser/`)
2. Create `cop/shared/parser/Tokenizer.cs` (refactored from current):
   - Remove domain-specific keywords (`collection`, `feed`, `run`, `test`, `async`, `intrinsic`, `provider`)
   - Replace with general-purpose keywords (`fn`, `let`, `type`, `enum`, `flags`, `import`, `export`, `if`, `match`, `command`, `foreach`)
   - Keep as backward-compatible as possible (old keywords become identifiers)
3. Create `cop/shared/parser/Token.cs` — token type definitions
4. Write comprehensive parser tests against the new parser
5. Verify: parse all existing `.cop` files in `packages/` and compare output

### Phase 3: Proper Symbol Table & Binder

**Goal:** Replace ad-hoc symbol resolution with scoped symbol tables.

1. Create `cop/shared/interpreter/SymbolTable.cs`:
   - Scoped symbol table with parent pointers
   - Symbol kinds: Variable, Function, Type, Module, Intrinsic
   - No domain-specific symbol kinds
2. Create `cop/shared/interpreter/Binder.cs`:
   - Walks AST, builds scopes, resolves names
   - Produces `BoundTree` (AST with resolved symbols and inferred types)
   - Reports unresolved references as diagnostics
3. Create `cop/shared/interpreter/TypeChecker.cs`:
   - Type inference for let bindings
   - Type checking for function calls
   - Structural type compatibility (not just string comparison)
4. Create `cop/shared/interpreter/BoundNodes.cs`:
   - Bound tree nodes (like AST but with resolved type info)

### Phase 4: Clean Evaluator

**Goal:** Replace `PredicateEvaluator` with a clean tree-walking interpreter.

1. Create `cop/shared/interpreter/Evaluator.cs`:
   - Walks `BoundTree`
   - Lexical `Environment` (not the current `EvaluationContext`)
   - Clean call convention: all calls go through one dispatch path
   - No hardcoded built-ins — intrinsics registered via FFI table
   - Collection methods as registered library functions (not a switch statement)
2. Create `cop/shared/interpreter/Environment.cs`:
   - Proper lexical scope chain
   - `Define(name, value)`, `Lookup(name) → value`, `Extend() → child scope`
3. Create `cop/shared/interpreter/Value.cs`:
   - Language value types: `CopString`, `CopNumber`, `CopBool`, `CopList`, `CopObject`, `CopFunction`, `CopNull`
   - No `DataObject` references in core evaluator
4. Create `cop/shared/interpreter/ForeignFunction.cs`:
   - FFI interface for runtime-provided functions
   - `delegate object? ForeignCall(List<object?> args, Environment env)`
   - All intrinsics (print, read, save, provider, etc.) registered through this

### Phase 5: Runtime Bridge (Domain adapter)

**Goal:** Move all domain-specific code out of the core into an adapter layer.

1. Create `cop/runtime/LanguageBridge.cs`:
   - Registers provider functions into evaluator's FFI table
   - Marshals `DataObject` ↔ `CopObject` at the boundary
   - Handles sink/streaming orchestration
   - Manages package loading and command dispatch
2. Refactor `ScriptInterpreter.cs` → becomes thin orchestrator:
   - Parse → Bind → Evaluate with registered runtime
   - Command routing stays here but calls into clean evaluator
   - Provider/sink setup stays here but registers via FFI
3. `TypeRegistry.cs` splits into:
   - Core type definitions (in `interpreter/`) — just type environment
   - `ProviderSchemaRegistry` (in runtime layer) — provider-specific schema management

### Phase 6: Migration & Cleanup

**Goal:** Switch existing code paths to use new infrastructure, remove old code.

1. Route `ScriptInterpreter.Run()` through new parser + binder + evaluator
2. Verify all tests pass (Lang.Tests + Cop.Tests)
3. Verify all sample packages run correctly
4. Remove old `ScriptParser.cs`, `PredicateEvaluator.cs`, `FilterCompiler.cs`, `FilterEvaluator.cs`
5. Remove old `Expression.cs`, `ScriptFile.cs`, domain-specific declaration records
6. Final cleanup: the three subfolders (`parser/`, `ast/`, `interpreter/`) become the canonical structure

---

## Specific Domain Concepts to Extract

These concepts currently pollute the core parser/evaluator and must move to the runtime bridge:

| Domain Concept | Current Location | Target Location |
|----------------|-----------------|-----------------|
| `DataObject` field access | PredicateEvaluator:806-925 | Runtime bridge (marshaling) |
| Provider namespace dispatch | PredicateEvaluator:261-272, 932-943 | FFI registration |
| Collection intrinsics (any/all/where/select...) | PredicateEvaluator:1064-1460 | Standard library (registered functions) |
| Sink/streaming | ScriptInterpreter:501-609, CommandBlock.SinkTarget | Runtime bridge |
| Package-qualified lookups | PredicateEvaluator:274-305 | Module/import resolution in binder |
| `print`/`save`/`debug`/`assert`/`fail` | PredicateEvaluator:143-201 | FFI-registered intrinsics |
| Provider schema merging | TypeRegistry:741-755, 946-1048 | ProviderSchemaRegistry |
| Collection decomposition during parse | ScriptParser:973-1080 | Binder or runtime bridge |
| Rule-id derivation | ScriptParser:958-971 | Runtime bridge |
| `foreach` streaming semantics | ScriptParser:835-873 | Binder lowers to iterator pattern |
| `RunInvocation` | ScriptParser:940-956, ScriptFile | Runtime bridge |
| `FeedPaths` | ScriptFile | Runtime bridge |

---

## Design Principles for the New Core

1. **No domain-specific keywords in the grammar** — `collection`, `feed`, `run`, `sink`, `provider` are identifiers, not keywords
2. **Predicates are functions** — a predicate is `fn name(x: T) -> bool = body`; the "narrowing" behavior is a binder concern
3. **Collections are typed values** — `let files : [File] = expr`; no special `collection` declaration needed
4. **Commands are statement blocks** — `command MAIN = { ... }`; execution semantics in runtime, not parser
5. **Single call dispatch** — all function calls (user-defined, intrinsic, foreign) go through one path
6. **Explicit FFI boundary** — the evaluator doesn't know about `DataObject`, providers, or sinks; it calls registered foreign functions
7. **Proper lexical scoping** — environment chain, not a global dictionary of type-keyed slots
8. **Type annotations are optional** — the language supports gradual typing; the binder infers where possible
9. **No filter-specific AST** — `FilterExpression` nodes become regular `Expression` nodes with predicate application
10. **Template strings are expressions** — `'{Name} is {Status}'` parses as interpolated string expression, not a separate mini-language

---

## Risk Mitigation

- **Parallel implementation**: New code lives in `cop/shared/Ast/` alongside existing code until ready
- **Incremental switchover**: Can route specific features through new path while others use old path
- **Test coverage**: All existing `Lang.Tests` must pass against new implementation before old code is removed
- **Package compatibility**: Parse all 30+ packages in `packages/` directory with new parser; diff results
- **Performance**: Tree-walking is already the model; no performance regression expected from cleaner dispatch

---

## Success Criteria

When complete, the `cop/shared/Ast/` (or `cop/language/`) directory should:

1. ✅ Be readable as a **general-purpose functional language** implementation
2. ✅ Have **zero imports** from provider/runtime/domain namespaces
3. ✅ Use standard compiler terminology: AST, Binder, Environment, Value, Module
4. ✅ Have **one unified call path** for all function invocations
5. ✅ Use **proper lexical scoping** with environment chains
6. ✅ Support **extensibility only via FFI** (no hardcoded switches for specific functions)
7. ✅ Pass all existing tests with domain behavior provided by the runtime bridge
8. ✅ Be under ~3000 lines total for parser + binder + evaluator (currently ~5800 lines combined)
9. ✅ Have a **formal grammar document** (`docs/cop-grammar.md`) in BNF/EBNF notation

---

## Deliverable: Formal Grammar Document

As a final deliverable, produce `docs/cop-grammar.md` — a formal specification of the Cop language grammar in EBNF notation. This document should:

- Define the **complete grammar** as implemented by the new parser
- Use standard EBNF notation (terminals in quotes, `|` for alternatives, `[ ]` for optional, `{ }` for repetition)
- Be organized into sections: Lexical Grammar, Module Structure, Declarations, Statements, Expressions
- Include operator precedence table
- Be standalone — someone should be able to implement a parser from this document alone
- Stay synchronized with the parser implementation (update when grammar evolves)

**When to produce:** After Phase 2 (new parser) is stable and before Phase 6 (final cleanup). The grammar is derived mechanically from the parser's structure.
