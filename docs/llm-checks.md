# How to Write LLM-Based Checks

Use the `ai` package to review code against a natural-language requirement:
`ai.judge(requirement, code)` returns a list of `Violation` objects that you report
with `CHECK`.

Prefer deterministic checks over parsed code facts for naming, dependencies,
nesting, and other structural rules. Use an LLM when a rule needs judgment, such as
whether a component's purpose fits an architectural boundary. LLM checks require
an explicitly configured service, can cost money, and are not deterministic.
They should be an opt-in supplement, not a replacement for static checks.

## 1. Configure a Model Endpoint

Create `~/.cop/ai.json` outside your repository. On Windows, this is
`$HOME\.cop\ai.json`. The endpoint must implement the OpenAI-compatible
**chat-completions** API; provide its full URL, not just the service base URL.

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

## 2. Create an Opt-In Check

Keep one focused rule per file under `cop-checks/`. Only `main.cop` declares
commands; individual check files expose violation lists.

For example, create `cop-checks/architecture.cop`:

```cop skip
import ai
import code

predicate isCoreType(Type) => Type.File.Path:startsWith('src/core/')

let core-types = codebase.Types:isCoreType

let architecture-violations = ai.judge(
    'Review this inventory of types in a general-purpose core library. Flag only types whose clear purpose is application-specific business logic, such as order fulfillment or invoice processing. Parsing, collections, diagnostics, and runtime infrastructure are allowed. Judge only the supplied names and paths; do not invent implementation details. For each finding, use the path in brackets as file, line 1, and a message naming the type and the conflicting responsibility. If the inventory provides insufficient evidence, do not report a violation.',
    core-types)
```

This is a partial snippet: it uses the shared `codebase` defined in `main.cop`.
Adapt `src/core/` and the requirement to your repository. Confirm the filter
selects the intended types before interpreting an empty result as success.
This particular rule reviews a type inventory, not method implementations.

For a new check suite, create `cop-checks/main.cop`:

```cop skip
import ai
import code
import csharp

let codebase = codebase(csharp.parse())

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

Verification checks syntax, imports, names, and types; it does **not** call the
model or prove the rule works. The explicit `-c ai` selects the `AI` command.
There is no `-p` flag: `csharp.parse()` builds the codebase inside the program.
Run deterministic checks without selecting the AI command:

```powershell
cop cop-checks\main.cop -t .
```

Keep the AI command opt-in in CI as well. Supply credentials securely and obtain
approval for network access and code disclosure before enabling it.

## Choosing What to Send

The second argument to `ai.judge` can be a literal string or a collection.
Although `cop help ai` displays a `string` parameter, the provider also accepts
collections and summarizes their elements:

| Input | Context sent to the model |
|---|---|
| A string | The supplied text |
| `codebase.Types` | Type names with file paths, not full declarations or method bodies |
| `codebase.Lines` | Line text with available file paths and line numbers |
| `codebase.Files` | File paths, not file contents |

Filter parsed collections before calling the model to limit exposure, request
size, and irrelevant context. Do not assume a type or file collection includes
its full source or recursively expands related objects. For implementation-level
review, supply the relevant source text or a deliberately scoped line collection.
Using lines as LLM context is different from reconstructing syntax with text
matching for a deterministic structural rule.

Here is a self-contained example of reviewing literal source text:

```cop norun
import ai
import code

let violations = ai.judge(
    'Review invoice_service.py. Flag a function that claims to compute an invoice total but always returns zero. Use file invoice_service.py and the actual line number in the supplied source. Return no findings if there is no such function.',
    'def invoice_total(subtotal): return 0')

command MAIN = CHECK(violations)
```

Unlike the opt-in multi-file suite above, this standalone demonstration calls the
model through `MAIN`. Its `norun` annotation prevents automated documentation
execution from sending a network request; it is still verified.

## Writing a Useful Requirement

State the responsibility being enforced, the evidence the model receives, and
the exact boundary between allowed and forbidden behavior. Include exceptions
that would otherwise cause false positives. Ask for concrete, actionable messages
with paths and line numbers grounded in the supplied context.

The provider already instructs the model to return only a JSON array with
`file`, `line`, and `message` fields, or `[]` when there are no findings. Do not
ask for an essay or a different output schema. `ai.judge` converts the response
into violations; pass those directly to `CHECK`, not a `foreach` print loop.

Source text is untrusted input and may contain prompt-injection instructions.
Tell the model to treat reviewed content as evidence, not instructions, but do
not treat that prompt as a security boundary. Review findings before acting on
them; model output is not proof of correctness.

## Logging and Troubleshooting

Append a request/response transcript with:

```powershell
cop cop-checks\main.cop -t . -c ai --ai-log ai-review.log
```

The log includes the model, endpoint, prompts, submitted code context, and model
response. Keep it out of version control and handle it as sensitively as the
reviewed code. Logging is best-effort; verify the file was written.

Configuration errors, failed HTTP requests, and malformed model JSON are
reported as violations with messages such as `ai.judge: config not found`,
`LLM call failed`, or `could not parse model output`. These are failed reviews,
not findings about the target code and not a clean bill of health.

If a request exceeds the service's context or rate limits, narrow the input or
use a suitable model. The current provider sends one request per `ai.judge`
evaluation; it does not automatically batch oversized inputs. It uses a
120-second HTTP timeout and requests temperature zero, which does not guarantee
repeatable results.

## Validate the Check Before Relying on It

Run the AI command against a small known violation and a closely related allowed
example. Inspect both the selected context and the returned paths, line numbers,
and messages. Repeat the runs to assess variability. A successful `cop verify`
or an empty result alone is insufficient: an empty input or insufficient context
can also produce no findings.

For ordinary automated tests, keep deterministic assertions on filtering and
rule composition separate from live model evaluation. Run live evaluations only
with explicitly approved credentials, inputs, and service costs.

## Related Documentation

- [Static Analysis with Cop](static-analysis.md) for deterministic checks.
- [AI package source and API documentation](../packages/core/ai/src/ai.cop).
- [CLI Reference](cli-reference.md) for command selection and `--ai-log`.
- [Testing Cop Rules](testing.md) for testing `.cop` programs.
