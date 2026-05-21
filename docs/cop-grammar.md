# Cop Language Formal Grammar

This document defines the formal grammar of the Cop language in Extended Backus–Naur Form (EBNF).
It is derived from the recursive-descent parser implementation (`cop/shared/parser/CopParser.cs`)
and the tokenizer (`cop/shared/Tokenizer.cs`). It serves as the authoritative specification of the
language's syntax.

## Notation Conventions

| Notation | Meaning |
|----------|---------|
| `'text'` | Terminal (literal token) |
| `UPPER`  | Token kind (from lexer) |
| `lower`  | Non-terminal (grammar rule) |
| `[ x ]`  | Optional (zero or one) |
| `{ x }`  | Repetition (zero or more) |
| `x \| y` | Alternative |
| `( x )`  | Grouping |

---

## 1. Lexical Grammar

### 1.1 Tokens

```ebnf
IDENTIFIER      = ident_start { ident_continue } ;
INT_LITERAL     = digit { digit } ;
NUMBER_LITERAL  = digit { digit } '.' digit { digit } ;
STRING_LITERAL  = single_quote_string | triple_quote_string ;
DOC_COMMENT     = '##' { char } newline ;

ident_start     = unicode_letter | '_' ;
ident_continue  = unicode_letter | digit | '_' | '-' ;
unicode_letter  = (* any character where char.IsLetter() returns true *) ;
digit           = '0'..'9' ;
```

#### Single-quoted strings

```ebnf
single_quote_string = "'" { string_char | escape } "'" ;
string_char         = (* any character except "'" *) ;
escape              = '\' escape_char ;
escape_char         = 'n'              (* newline *)
                    | 't'              (* tab *)
                    | '\'              (* backslash *)
                    | "'"              (* single quote *)
                    | '@'              (* literal @ *)
                    ;
```

Unrecognized escape sequences (e.g., `\b`, `\s`) are preserved verbatim as backslash + character.
Strings may contain `{expression}` interpolation markers — see §9.

#### Triple-quoted strings

```ebnf
triple_quote_string = "'''" newline { any_char } "'''" ;
```

Triple-quoted strings strip leading indentation based on the indentation of the closing `'''`.
The opening `'''` must be followed by a newline. Content between the delimiters is returned
with common leading whitespace removed.

### 1.2 Keywords

Reserved keywords (tokenized as distinct keyword tokens, but may also appear as identifiers
in expression position — see §11 Design Notes):
```
true  false  nic
import  export
type  enum  flags
function  predicate
let  foreach
feed  RUN  async  test
intrinsic  provider
```

### 1.3 Operators and Punctuation

Single-character:
```
+  -  *  /  %  !  .  ,  :  =  ?  &  |  <  >
(  )  [  ]  {  }
```

Multi-character:
```
==  !=  <=  >=  &&  ||  =>  ::
```

### 1.4 Comments

```ebnf
line_comment       = '#' { char } newline ;
doc_comment        = '##' { char } newline ;
multiline_comment  = '#' newline { line } '#' newline ;
```

- `#` begins a line comment (consumed by the tokenizer, not emitted). Can appear anywhere on a line.
- `##` begins a doc comment (emitted as `DocComment` token, attaches to next declaration).
- A bare `#` alone on a line (no other non-whitespace) opens/closes a multi-line comment block.

### 1.5 Whitespace

Whitespace (spaces, tabs, newlines) separates tokens. Newlines are significant only for:
- Terminating line comments
- Separating indentation-delimited property blocks in type declarations
- Separating mapping body field assignments

---

## 2. Module Structure

```ebnf
module      = { declaration } EOF ;

declaration = [ doc_comment ] [ 'export' ] decl_body
            | 'import' IDENTIFIER
            | 'feed' rest_of_line           (* runtime concern, skipped *)
            | 'RUN' rest_of_line            (* runtime concern, skipped *)
            ;

decl_body   = type_decl
            | enum_decl
            | flags_decl
            | function_decl
            | predicate_decl
            | let_decl
            | foreach_decl
            | test_decl
            ;
```

---

## 3. Declarations

### 3.1 Type Declaration

```ebnf
type_decl       = 'type' type_spec [ '=' type_body ] ;

type_spec       = type_name ':' IDENTIFIER         (* Base:Subtype — declares subtype relationship *)
                | type_name                        (* standalone type, no base *)
                ;

type_name       = IDENTIFIER
                | '[' IDENTIFIER ']'            (* generic collection type *)
                ;

type_body       = '{' { property_decl } '}'     (* brace-enclosed *)
                | IDENTIFIER                    (* type alias *)
                ;

property_decl   = [ doc_comment ] IDENTIFIER ':' type_ref [ '?' ] [ ',' ] ;
```

The `Base:Name` syntax reads as "Base narrowed to Name" — consistent with predicate guards.
Base type is optional; `type Foo = {}` and `type object:Foo = {}` both declare `Foo`.

When `=` is absent, properties follow on subsequent indented lines:

```ebnf
type_decl_indented = 'type' type_spec
                     { property_decl }          (* indentation-delimited *)
                   ;
```

### 3.2 Enum Declaration

```ebnf
enum_decl   = 'enum' IDENTIFIER [ ':' type_ref ] '=' enum_member { '|' enum_member } ;
enum_member = IDENTIFIER | STRING_LITERAL ;
```

### 3.3 Flags Declaration

```ebnf
flags_decl  = 'flags' IDENTIFIER '=' IDENTIFIER { '|' IDENTIFIER } ;
```

### 3.4 Function Declaration

```ebnf
function_decl = 'function' IDENTIFIER param_list [ ( ':' | '=>' ) type_ref ]
                [ ':' '(' expression ')' ] function_body ;

function_body = '=' 'intrinsic'                 (* runtime-provided *)
              | '=' expression                  (* expression body *)
              | '=' '{' { statement } '}'       (* block body — UPPERCASE names only *)
              | mapping_body                    (* field-mapping body *)
              ;

mapping_body  = { IDENTIFIER '=' expression } ; (* indentation-delimited *)
```

The return type separator accepts both `:` and `=>` (the canonical form is `:`).
A guard clause is expressed as `: (expr)` — a colon followed by a parenthesized expression. The
parentheses disambiguate from the return type annotation (type_ref never starts with `(`).

**Uppercase convention:** Functions with ALL-UPPERCASE names (e.g., `MAIN`,
`RUN-CHECKS`) may have statement-block bodies (`= { ... }`). For lowercase names, `{ }` after `=`
is parsed as an object literal expression.

### 3.5 Predicate Declaration

Predicates are functions that return bool, with optional narrowing type:

```ebnf
predicate_decl = 'predicate' IDENTIFIER param_list [ ':' type_ref ]
                 [ ':' '(' expression ')' ] predicate_body ;

predicate_body = ( '=>' | '=' ) expression      (* explicit body separator *)
               | expression                     (* constraint body, no separator *)
               ;
```

The optional `: TypeRef` after parameters is a **narrowing type** (e.g., `predicate isCall(Statement) : Call`).

### 3.6 Let Declaration

```ebnf
let_decl = 'let' IDENTIFIER [ ':' type_ref ] '=' expression ;
```

### 3.7 Foreach Declaration

```ebnf
foreach_decl = 'foreach' expression { '=>' expression } ;
```

Top-level `foreach` declares an anonymous uppercase function with a foreach statement body.

### 3.8 Test Declaration

```ebnf
test_decl = 'test' IDENTIFIER '=' '{' { statement } '}' | statement ;
```

`test foo = body` produces an uppercase function `TEST-FOO` with block body.

### 3.9 Import Declaration

```ebnf
import_decl = 'import' IDENTIFIER ;
```

---

## 4. Statements

```ebnf
statement = let_statement
          | foreach_statement
          | pipeline_statement
          | expression_statement
          ;

let_statement       = 'let' IDENTIFIER [ ':' type_ref ] '=' expression ;

foreach_statement   = 'foreach' expression { '=>' expression } ;

pipeline_statement  = expression '=>' expression { '=>' expression } ;

expression_statement = expression ;
```

---

## 5. Expressions

### 5.1 Precedence Table (lowest to highest)

| Precedence | Operators | Associativity | Description |
|------------|-----------|---------------|-------------|
| 0          | `?`       | Right         | Ternary conditional / pattern match |
| 1          | `\|\|`    | Left          | Logical OR |
| 2          | `&&`      | Left          | Logical AND |
| 3          | `== !=`   | Left          | Equality |
| 4          | `< > <= >=` | Left        | Comparison |
| 5          | `\|`      | Left          | Bitwise OR |
| 6          | `&`       | Left          | Bitwise AND |
| 7          | `+ -`     | Left          | Additive |
| 8          | `* / %`   | Left          | Multiplicative |
| 9          | `! -`     | Right (prefix)| Unary |
| 10         | `. () [] :`| Left (postfix)| Member access, call, index, filter |
| 11         | (atoms)   | —             | Literals, identifiers, grouping |

### 5.2 Expression Grammar

```ebnf
expression      = ternary_expr ;

ternary_expr    = or_expr [ '?' ternary_tail ] ;

ternary_tail    = or_expr '=>' expression { '|' expression '=>' expression }  (* match *)
                | or_expr ':' expression                                       (* ternary else *)
                | or_expr                                                      (* implicit else = nic *)
                ;

or_expr         = and_expr { '||' and_expr } ;
and_expr        = equality_expr { '&&' equality_expr } ;
equality_expr   = comparison_expr { ( '==' | '!=' ) comparison_expr } ;
comparison_expr = bitwise_or_expr { ( '<' | '>' | '<=' | '>=' ) bitwise_or_expr } ;
bitwise_or_expr = bitwise_and_expr { '|' bitwise_and_expr } ;
bitwise_and_expr = additive_expr { '&' additive_expr } ;
additive_expr   = mult_expr { ( '+' | '-' ) mult_expr } ;
mult_expr       = unary_expr { ( '*' | '/' | '%' ) unary_expr } ;

unary_expr      = ( '!' | '-' ) unary_expr
                | postfix_expr
                ;

postfix_expr    = primary_expr { postfix_op } ;

postfix_op      = '.' IDENTIFIER [ '(' arg_list ')' ]      (* member access / method call *)
                | ':' [ '!' ] filter_predicate              (* filter application *)
                | '(' arg_list ')'                          (* direct function call *)
                | '[' expression ']'                        (* indexing *)
                ;
```

Note: The `?` ternary operator is handled at the `ternary_expr` level (lowest precedence),
NOT as a postfix operator. The then-branch of a ternary uses `or_expr` (not full `expression`)
to prevent the `:` else-separator from being consumed as a filter colon.

### 5.3 Filter Syntax

```ebnf
filter_predicate = IDENTIFIER { '.' IDENTIFIER } [ '(' arg_list ')' ] ;
```

The filter colon `:` is distinguished from ternary else `:` and type annotation `:` by two rules:
1. **Lookahead**: the colon must be followed by an identifier or `!` (negation).
2. **Left-hand side**: the expression before `:` must be "filterable" — i.e., NOT a literal
   (numbers, strings, booleans are never filterable). This prevents `cond ? 1 : x` from
   parsing `1:x` as a filter.

### 5.4 Ternary and Match

After the `?` token, the parser reads the first branch using `or_expr`, then disambiguates:

```ebnf
ternary_tail = or_expr '=>' expression { '|' expression '=>' expression }  (* match *)
             | or_expr ':' expression                                       (* ternary else *)
             | or_expr                                                      (* implicit else *)
             ;
```

- If `=>` follows: it's a **pattern match** expression with one or more arms.
- If `:` follows: it's a **ternary conditional** (`condition ? then : else`).
- If neither: it's a **conditional with implicit else** (else branch is `nic`).

### 5.5 Primary Expressions

```ebnf
primary_expr    = STRING_LITERAL                            (* string / interpolated *)
                | INT_LITERAL
                | NUMBER_LITERAL
                | 'true' | 'false' | 'nic'
                | IDENTIFIER
                | '(' expression ')'                        (* grouping *)
                | '(' params ')' '=>' expression            (* lambda *)
                | list_literal
                | object_literal
                ;

list_literal    = '[' [ expression { [ ',' ] expression } ] ']' ;

object_literal  = '{' [ field_init { ( ',' | ) field_init } ] '}' ;

field_init      = IDENTIFIER ( ':' | '=' ) expression ;
```

### 5.6 Lambda Expressions

```ebnf
lambda_expr     = '(' [ param { ',' param } ] ')' '=>' expression ;
```

Lambda detection uses backtracking: if `(ident [: type], ...) =>` is found, it's a lambda;
otherwise, the parenthesized content is a grouped expression.

---

## 6. Type References

```ebnf
type_ref        = '[' IDENTIFIER ']'            (* collection type *)
                | '{' balanced_braces '}'       (* anonymous record type — treated as 'object' *)
                | IDENTIFIER                    (* named type *)
                ;
```

---

## 7. Parameters

```ebnf
param_list      = '(' [ param { ',' param } ] ')' ;

param           = IDENTIFIER ':' type_ref                (* named + typed: name : Type *)
                | IDENTIFIER ':' constraint_chain        (* type + constraints: Type:pred:pred *)
                | IDENTIFIER                             (* named untyped or uppercase type name *)
                ;

constraint_chain = IDENTIFIER [ '(' arg_list ')' ] { ':' IDENTIFIER [ '(' arg_list ')' ] } ;
```

Parameter disambiguation:
- If `Name:` is followed by an identifier and that identifier is followed by `(`, `:`, `)`, or `,`:
  the parameter is `Type:constraint` (uppercase first letter → type name, lowercased for param name).
- Otherwise: `name : Type` (standard named parameter).
- Bare uppercase identifier: treated as type name with auto-generated param name.

---

## 8. Argument Lists

```ebnf
arg_list        = [ expression { ',' expression } ] ;
```

---

## 9. String Interpolation

String literals use single quotes and support interpolation:

```ebnf
interpolated_string = "'" { text_segment | interpolation_segment } "'" ;

text_segment        = (* characters not containing '{' or "'" *) ;
interpolation_segment = '{' dotted_path [ '@' style_name ] '}' ;
dotted_path         = IDENTIFIER { '.' IDENTIFIER } ;
style_name          = IDENTIFIER [ '-' IDENTIFIER ] ;
```

The `@style` syntax applies formatting/color hints (e.g., `{name@bold}`, `{msg@red-dim}`).
The interpolation expression is limited to dotted paths (e.g., `{item.Name}`) — arbitrary
expressions or function calls are not supported inside `{}`.

---

## 10. Doc Comments

Doc comments use `##` prefix and attach to the following declaration:

```ebnf
doc_comment_block = { '##' text newline } ;
```

Multiple consecutive `##` lines form a single doc comment block.

---

## 11. Design Notes

### Domain-agnostic grammar
The grammar defines a **general-purpose functional language**. All constructs are generic
programming concepts: functions, predicates, types, enums, let-bindings, and expressions.
There are no domain-specific keywords or constructs.

### Uppercase function convention
Functions with side effects use ALL-UPPERCASE names. The parser enforces this by allowing
statement-block bodies (`= { ... }`) only for uppercase function names. For lowercase function
names, `{ }` after `=` is always parsed as an object literal expression body. Examples:
```cop
function MAIN() = {
    foreach items => print(item.Name)
}

function RUN-CHECKS() = {
    let errors = validate()
    print(errors)
}

function PRINT-HELLO() = print('hello')    # expression body also valid for uppercase
function makeObj() = { name: 'foo' }       # lowercase: { } is object literal
```

### Keywords as identifiers
All reserved keywords are also accepted as identifiers in expression position. This allows
domain-specific code to use names like `type`, `test`, `function` etc. as field names or
variables without conflict. The parser's `IsIdentifierLike()` function treats all keyword
tokens as valid identifier tokens.

### Single namespace
All declarations (types, functions, variables, enums) share a single namespace within a module.
Enum members are injected into module scope alongside the enum type itself.

### No semicolons
Statements and declarations are separated by newlines. No semicolons are required or supported.

### Expression-oriented
Functions and predicates have expression bodies (not statement blocks), unless using the
ALL-UPPERCASE naming convention which enables block bodies for side-effectful operations.

### Hyphens in identifiers
Identifiers may contain hyphens (`-`), which is unusual for programming languages. This enables
natural naming like `my-predicate`, `code-checks`, or `csharp-library-client`. The tokenizer
does not distinguish between `-` as subtraction and `-` within an identifier — lexically, if
a `-` is adjacent to letters/digits (no space), it becomes part of the identifier.
