# How to Write LLM-Based Checks

Use the `ai` package to review code against a natural-language requirement:
`ai.judge(requirement, codebase)` accepts a parsed `Codebase` and returns a list of
`Violation` objects that you report with `CHECK`. State the policy you want to
enforce; Cop supplies source and architectural context from that codebase.

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

The provider builds a single JSON context containing the following evidence,
when supplied by the language parsers:

| Evidence | Context sent to the model |
|---|---|
| Source | Full parsed source lines, with file paths and line numbers |
| Files | Language, namespace, and imports |
| Declarations | Types, inheritance, interfaces, methods, and parameter types |
| Calls | Calling locations and referenced type/member names |
| Projects | Project paths, references, packages, and target frameworks |

The model sees implementation code, not just type names or a list of filenames.
Cop does not reread arbitrary paths from disk or follow cyclic model
relationships. The context is limited to what the selected parsers return:
unparsed files, excluded files, and external dependency source are not included.

Scope at the parser level to limit disclosure and request size. The provider
does not silently truncate source. An empty codebase, or a file without source
lines, produces an error violation instead of reporting a clean review.
Paths are those returned by the parsers, typically relative to each parsed root.
When combining scopes, use distinguishable file paths; conflicting source for
the same path and line is rejected rather than merged into misleading context.

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

### Migrating Older Checks

The second argument is now a `Codebase`, not a source string or an individual
collection. Replace calls using `cb.Types`, `cb.Files`, or `cb.Lines` with `cb`.
For literal source examples, put the source in a real file and parse its
directory. Use `parse('path')` rather than filtering a type inventory to scope a
review. The signature declares `Codebase`, and the provider rejects old argument
shapes before making a model request. Static verification does not currently
catch all incompatible intrinsic arguments
([#61](https://github.com/KrzysztofCwalina/cop/issues/61)).

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
