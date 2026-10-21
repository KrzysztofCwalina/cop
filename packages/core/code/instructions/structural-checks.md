# Authoring Code Checks

Code-structure rules MUST use parsed code facts, not source-text approximations.
Run `cop help code` for the common model and `cop help <language>` for language-only facts.

| Need | Model surface |
|---|---|
| Declarations, visibility, inheritance | `Types`, `Methods`, `Modifiers`, `BaseTypes`, `Interfaces` |
| Invocations | `Calls`, `Kind`, `TypeName`, `MemberName` |
| Nesting | `Parent`, `Children`, `Ancestors`, `Method` |
| Signatures | `Parameters`, `ReturnType`, `TypeReference.Name` and `GenericArguments` |
| Imports and dependencies | `File.Usings`, `Project.References`, `Project.Packages` |
| Language-only syntax | `:asCSharp`, `:asPython`, etc., then documented extra properties |

Comparing parsed names is appropriate. Inferring syntax from `Source`, `Text`, `Condition`,
`Expression`, string-valued `Arguments`, or other raw text is not. This applies to regex,
equality, substring tests, splitting, implicit text conversion, and helpers alike.

If the model lacks the required fact, explain the capability gap. **Before writing any
text-based check, ASK THE USER whether it is okay**, explaining the proposed check's
limitations. Without approval, leave that check unimplemented and report why.

Verify with `cop verify cop-checks/ --strict-structure`. Only after explicit user approval
use `--allow-text-check <approved-name>` for that named check; never authorize an exception
yourself. Source excerpts belong directly in diagnostic messages, not selection logic.
Execute the verified program against known violations and nonviolations.
