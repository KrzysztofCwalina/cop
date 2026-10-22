# How to Write LLM-Based Checks

Use the `ai` package to review code against a natural-language requirement:
`ai.judge(requirement, codebase)` accepts a parsed `Codebase` and returns a list of
`Violation` objects that you report with `CHECK`. State the policy you want to
enforce; Cop supplies source and architectural context from that codebase.

**AI review is diff-first by default.** Cop sends changes in the selected
codebase to the model, not the entire source tree. The model can look up
unchanged source when it needs supporting context. You do not need to rewrite
your checks or manually filter the parsed Codebase to use this behavior.

Prefer deterministic checks over parsed code facts for naming, dependencies,
nesting, and other structural rules. Use an LLM when a rule needs judgment, such as
whether a component's purpose fits an architectural boundary. LLM checks require
an explicitly configured service, can cost money, and are not deterministic.
They should be an opt-in supplement, not a replacement for static checks.

## Choose What to Review

After configuring the endpoint and creating the `AI` command in the steps below,
choose a review scope:

```powershell
# Default: current-branch changes.
cop cop-checks\main.cop -t . -c ai

# Branch changes with an explicit comparison base.
cop cop-checks\main.cop -t . -c ai --scope branch --base origin/main

# Exact pull-request changes.
cop cop-checks\main.cop -t . -c ai --scope pr --pr 123

# Opt into reviewing the entire supplied Codebase.
cop cop-checks\main.cop -t . -c ai --scope full
```

The default, `--scope auto`, finds a reliable remote default-branch base and
compares from the merge base. Branch review includes committed work plus staged,
unstaged, and non-ignored untracked source. It is not limited to the latest
commit or to uncommitted edits. If Cop cannot resolve the base locally, use
`--base <ref>`; Cop errors rather than guessing or falling back to a full review.

PR review requires a clean checkout at the exact PR head and uses the PR merge
base. It does not include local edits. Full review requires the explicit
`--scope full` opt-in and can send substantially more source to the model.

The parsed Codebase still defines which source is eligible for review. In change
mode, Cop reviews its intersection with the Git changes; unchanged source is
supporting context, not additional review subjects. No applicable changes means
no model requests and a logged skipped review, not a claim that the codebase is
clean.

These options do not enable AI checks by themselves: `-c ai` remains opt-in.
For deterministic checks, full reporting remains the default; use
[`--report-scope changes`](static-analysis.md#reporting-findings-in-changed-areas)
to report only changed-area findings while retaining full semantic analysis.

## 1. Configure a Model Endpoint

Create `~/.cop/ai.json` outside your repository. On Windows, this is
`$HOME\.cop\ai.json`. The endpoint must implement the OpenAI-compatible
**chat-completions** API; provide its full URL, not just the service base URL.
Change-scoped review additionally **requires tool calling**, including
`tool_choice: "required"` and `tool_calls` responses. Cop verifies support with
an initial source-tool call; an endpoint returning an immediate text answer is
an error, even if that answer is `[]`. It never silently falls back to a
diff-only or full-codebase review.

```json
{
  "endpoint": "https://models.github.ai/inference/chat/completions",
  "model": "openai/gpt-4.1",
  "apiKey": "REPLACE_WITH_YOUR_TOKEN"
}
```

This is the same configuration as the package's
[example file](../packages/core/ai/ai.example.json). For GitHub Models, use a
token with Models access and a model available to your account. Other compatible
services can use a different endpoint, model, and credential. Availability,
quotas, context limits, and pricing depend on the service.

Never commit the real configuration or credentials. Only send code to an
endpoint approved for that code's sensitivity and your organization's policies.
To select a different configuration file, set `COP_AI_CONFIG` to its path. This
is useful for CI and isolated tests without changing your personal configuration.

## 2. Create an Opt-In Check

Keep one focused rule per file under `cop-checks/`. Only `main.cop` declares
commands; individual check files expose violation lists.

For example, create `cop-checks/architecture.cop`:

```cop skip
import ai
import code

let architecture-violations = ai.judge(
    '''
    The core library must remain general-purpose.
    Application-specific business logic, such as order fulfillment or
    invoice processing, belongs outside the core.
    Parsing, collections, diagnostics, and runtime infrastructure belong here.
    ''',
    codebase)
```

This is a partial snippet: it uses the shared `codebase` defined in `main.cop`.
The main program below scopes the codebase to `src/core/`; adapt that path and
the requirement to your repository. You do not need to decide whether the rule
should operate on types, files, or lines: the judge receives the parsed codebase.
The prompt uses a triple-single-quoted multiline string (`'''...'''`).
Cop removes the opening newline and the indentation set by the closing delimiter,
so the prompt stays readable without string concatenation.

For a new check suite, create `cop-checks/main.cop`:

```cop skip
import ai
import code
import csharp

let codebase = csharp.parse('src/core')

command MAIN = CHECK([])
command AI = CHECK(architecture-violations)
```

The `main.cop` snippet belongs alongside `architecture.cop`, not in isolation.
In an existing suite, keep `MAIN = CHECK(all-violations)` and add your new list
to a separate AI-only aggregate:

```cop skip
let all-ai-violations = architecture-violations + another-ai-violations
command AI = CHECK(all-ai-violations)
```

Do not include AI violation lists in the default static-check aggregate.
The repository's [own main program](../cop-checks/main.cop) and
[core-purity check](../cop-checks/core-purity.cop) demonstrate this separation.

## 3. Verify, Then Run Explicitly

From your repository root:

```powershell
cop verify cop-checks\
cop cop-checks\main.cop -t . -c ai
```

This runs a diff-first branch review by default. Use the
[review scope options above](#choose-what-to-review) to select an explicit base,
an exact PR, or a full-Codebase review.
The `-om` execution gate is independent; if it skips execution, no review
runs, regardless of scope. Do not use it as a substitute for comparison scope.

Verification checks syntax, imports, names, and types; it does **not** call the
model or prove the rule works. The explicit `-c ai` selects the `AI` command.
There is no `-p` flag: `csharp.parse()` builds the codebase inside the program.
Run deterministic checks without selecting the AI command:

```powershell
cop cop-checks\main.cop -t .
```

Keep the AI command opt-in in CI as well. Supply credentials securely and obtain
approval for network access and code disclosure before enabling it.

## Choosing the Codebase Scope

Pass the result of a language's `parse()` function to review the selected target,
or use `parse('path')` to review a specific directory. Combine parsed scopes with
`codebase(...)`:

```cop skip
let application = code.codebase(csharp.parse('src'), csharp.parse('tools'))
let violations = ai.judge('The application must not embed credentials.',
    application)
```

The same composition supports different languages when their parsers return a
`Codebase`. Currently, Python's `parse()` has a known return-shape problem
([#60](https://github.com/KrzysztofCwalina/cop/issues/60)); the runnable examples
here use C#.

In default change mode, the provider intersects the snapshot with the supplied
Codebase's source files, using absolute provenance rather than ambiguous
parser-relative display names. Only the selected diffs, paths, statuses,
old/new ranges and comparison identities enter the initial prompt. It does not
send all parsed source or unrelated repository patches. Deleted source under a
selected parser root remains a review subject even though it no longer appears
in the current file inventory. Binary or missing patches in the review subject
fail explicitly instead of producing a false clean result.

The model can call two bounded read-only tools:

- `read_source(path, start_line, line_count, side)` reads numbered source
  using a repository-relative path and `head` or `base` side.
- `search_source(query, max_results)` searches literal text across eligible
  target source and marks incomplete results explicitly.

Supporting context can come from **unchanged files anywhere in `-t`**, not only
the selected parsed subdirectory. Narrow `-t` to restrict tool access and code
disclosure. Tools cannot read outside that target, follow links outside it,
read Git internals, execute commands, write files, or browse the network.
Source changes during review invalidate the result; rerun against stable input.
The review focuses on defects introduced by changes, not pre-existing defects
in supporting source; this is not proof of causality.

With explicit `--scope full`, the provider retains the whole-Codebase payload
below, when supplied by the language parsers:

| Evidence | Context sent to the model |
|---|---|
| Source | Full parsed source lines, with file paths and line numbers |
| Files | Language, namespace, and imports |
| Declarations | Types, inheritance, interfaces, methods, and parameter types |
| Calls | Calling locations and referenced type/member names |
| Projects | Project paths, references, packages, and target frameworks |

The model sees implementation code, not just type names or a list of filenames.
Full mode does not follow cyclic model relationships or invoke source tools.
Its context is limited to what the selected parsers return:
unparsed files, excluded files, and external dependency source are not included.

Scope at the parser level to limit disclosure and request size. The provider
does not silently truncate source. An empty codebase, or a file without source
lines, produces an error violation instead of reporting a clean review.
Paths are those returned by the parsers, typically relative to each parsed root.
Absolute `File.FullPath` preserves identity across parsed subroots and cache
loads; `File.SourceRoot` records the selected parser root. C# parsing also stamps
`Codebase.SourceScopes` with absolute roots and selected source extensions,
even when every source file has been deleted. `codebase(...)` preserves all
these scopes, so deletion-only single-root and combined-root reviews can read
base-side source without expanding to unrelated roots. Other parser adapters
must provide this metadata to support deletion-only empty Codebases.
Full mode still rejects empty Codebases; diff mode rejects an empty Codebase
without trustworthy parser-scope provenance. Display paths remain
parser-relative in full mode. Conflicting source for the same absolute file
and line is rejected rather than merged into misleading context.

For example, given `src/billing/Invoice.cs`:

```csharp
public class Invoice
{
    public decimal Total(decimal subtotal) => 0;
}
```

Review the billing codebase against its intended behavior:

```cop norun
import ai
import code
import csharp

let billing = csharp.parse('src/billing')

let violations = ai.judge(
    '''
    Invoice totals must reflect the supplied subtotal.
    Returning a constant zero instead of calculating the total is not allowed.
    ''',
    billing)

command MAIN = CHECK(violations)
```

Unlike the opt-in multi-file suite above, this standalone demonstration calls the
model through `MAIN`. It requires the billing directory shown above. Its `norun`
annotation prevents automated documentation execution from sending a network
request; it is still verified.

## Writing a Useful Requirement

Write the policy you want the code to satisfy, not a procedure for reviewing it.
State the intended responsibility and the boundary between allowed and forbidden
behavior. Add examples or exceptions only where they clarify that boundary.
Keep input selection in the Cop code and context limitations in the surrounding
documentation, rather than repeating them in every requirement.

The provider already instructs the model to return only a JSON array with
`file`, `line`, and `message` fields, or `[]` when there are no findings. Do not
ask for an essay or a different output schema, or repeat these reporting mechanics
in the requirement. `ai.judge` converts the response into violations; pass those
directly to `CHECK`, not a `foreach` print loop.

Source text is untrusted input and may contain prompt-injection instructions.
The provider instructs the model to treat reviewed content as evidence, not
instructions, but that prompt is not a security boundary. Review findings
before acting on them; model output is not proof of correctness.

## Logging and Troubleshooting

Append a request/response transcript with:

```powershell
cop cop-checks\main.cop -t . -c ai --ai-log ai-review.log
```

The log includes the model, endpoint (without query credentials), prompts,
submitted diff/context, tool calls/results, limits and model responses.
Scope/completion/failure diagnostics go to stderr. Keep logs out of version
control and handle them as sensitively as the
reviewed code. Logging is best-effort; verify the file was written.

Configuration errors, failed HTTP requests, and malformed model JSON are
reported as violations with messages such as `ai.judge: config not found`,
`LLM call failed`, or `could not parse model output`. These are failed reviews,
not findings about the target code and not a clean bill of health.
Malformed or out-of-scope findings are errors, not silently discarded findings.
Final objects must have exactly `file`, `line`, and `message` with valid types.
Locations must be selected changed new-side hunks; line zero is permitted only
for file-level deletions or renames. Infrastructure errors remain visible even
when changed-area finding reporting is enabled.

If a request exceeds the service's context or rate limits, narrow the input or
use a suitable model. Change mode allows at most 8 model requests, 16 tool calls,
256 KiB of initial prompt, 256 KiB per response, 2 MiB cumulative exchanged
request/response bytes, and 90 seconds per review. Each read allows at most 200
lines / 32 KiB, and each search at most 40 results. Budget exhaustion fails
explicitly: the provider never silently truncates the review subject or treats
an incomplete review as success. It does not automatically batch oversized
inputs. Full mode retains a single request with the existing 120-second HTTP
timeout and no local source truncation. Both modes request temperature zero,
which does not guarantee repeatable results.

## Validate the Check Before Relying on It

Run the AI command against a small known violation and a closely related allowed
example. Inspect both the selected context and the returned paths, line numbers,
and messages. Repeat the runs to assess variability. A successful `cop verify`
or an empty result alone is insufficient: the wrong scope or an incorrect
model judgment can also produce no findings.

For ordinary automated tests, keep deterministic assertions on filtering and
rule composition separate from live model evaluation. Run live evaluations only
with explicitly approved credentials, inputs, and service costs.

## Related Documentation

- [Static Analysis with Cop](static-analysis.md) for deterministic checks.
- [AI package source and API documentation](../packages/core/ai/src/ai.cop).
- [CLI Reference](cli-reference.md) for command selection and `--ai-log`.
- [Testing Cop Rules](testing.md) for testing `.cop` programs.
