# Azure SDK for .NET — Check Coverage Gap Analysis

This report maps every significant automated check enforced in `azure-sdk-for-net` to coverage
in the Cop packages (`packages/dotnet/csharp*`) and Roslyn analyzers (`analyzers/`).

**Legend:**  ✅ Covered  |  ⚠️ Partial  |  ❌ Gap  |  🔵 Cop-only (no Azure SDK equivalent)

---

## 1. Azure.ClientSdk.Analyzers (NuGet — from azure-sdk-tools)

These are the primary SDK design-guideline analyzers, shipped as the `Azure.ClientSdk.Analyzers` NuGet package.

### Client API Design Rules (AZC0002–AZC0021)

| AZC | Title | Cop Coverage | Cop Check |
|-----|-------|:---:|-----------|
| AZC0002 | Service methods need CancellationToken / RequestContext | ✅ | `csharp-library-client:async-needs-cancellation-token`, `csharp-library-client-azure:service-method-needs-cancellation` |
| AZC0003 | Service methods must be virtual | ✅ | `csharp-library-client:client-methods-virtual` |
| AZC0004 | Provide both async and sync variants | ✅ | `csharp-library-client:async-needs-sync-counterpart` |
| AZC0005 | Protected parameterless constructor for mocking | ✅ | `csharp-library-client-azure:client-needs-mocking-ctor` |
| AZC0006 | Constructor overload with ClientOptions | ✅ | `csharp-library-client:client-needs-options-ctor`, `csharp-library-client-azure:client-needs-options-ctor` |
| AZC0007 | Minimal constructor without ClientOptions | ✅ | `csharp-library-client-azure:client-needs-simple-ctor` |
| AZC0008 | ClientOptions needs nested ServiceVersion enum | ✅ | `csharp-library-client-azure:options-needs-service-version-enum` |
| AZC0009 | ClientOptions ctor first param is ServiceVersion | ✅ | `csharp-library-client-azure:options-first-param-service-version` |
| AZC0010 | ServiceVersion defaults to latest version | ✅ | `csharp-library-client-azure:service-version-default-value` |
| AZC0011 | Avoid InternalsVisibleTo to non-test assemblies | ✅ | `csharp-library-client-azure:internals-visible-to` |
| AZC0013 | TaskCompletionSource needs RunContinuationsAsynchronously | ✅ | `csharp:bare-task-completion-sources` |
| AZC0014 | Avoid banned types in public API | ✅ | `csharp-library-client-azure:no-banned-internal-types` |
| AZC0015 | Unexpected client method return type | ✅ | `csharp-library-client-azure:no-raw-http-return-types` |
| AZC0016 | Invalid ServiceVersion member naming | ✅ | `csharp-library-client-azure:service-version-naming` |
| AZC0017 | Convenience methods must not take RequestContent | ✅ | `csharp-library-client-azure:no-request-content-in-convenience` |
| AZC0018 | Protocol method signature validation | ✅ | `csharp-library-client-azure:protocol-method-return-type` |
| AZC0019 | Avoid ambiguous overloads | ✅ | `csharp-library-client-azure:no-ambiguous-overloads` |
| AZC0020 | Avoid Azure.Core internal shared-source types in public API | ✅ | `csharp-library-client-azure:no-pipeline-types-in-api` |
| AZC0021 | ClientSettings ctor params should not be combined with others | ❌ | — |

### Model Naming Rules (AZC0030–AZC0036)

| AZC | Title | Cop Coverage | Cop Check |
|-----|-------|:---:|-----------|
| AZC0030 | Improper model suffix — 'Collection' | ✅ | `csharp-library-client-azure:no-collection-suffix` |
| AZC0031 | Improper model suffix — 'Request' | ✅ | `csharp-library-client-azure:no-request-suffix` |
| AZC0032 | Improper model suffix — 'Parameter(s)' | ✅ | `csharp-library-client-azure:no-parameter-suffix` |
| AZC0033 | Improper model suffix — 'Option(s)' | ✅ | `csharp-library-client-azure:no-option-suffix` |
| AZC0034 | Duplicate type names across SDK and .NET | ❌ | — |
| AZC0035 | Output model type needs model factory method | ❌ | — |
| AZC0036 | Improper model suffix — 'Resource' | ✅ | `csharp-library-client-azure:no-resource-suffix` |

### Async / Sync Pattern Rules (AZC0100–AZC0112)

| AZC | Title | Cop Coverage | Cop Check |
|-----|-------|:---:|-----------|
| AZC0100 | ConfigureAwait(false) required on all awaits | ✅ | `csharp-library:awaits-using-default` |
| AZC0101 | Do not use ConfigureAwait(true) | ✅ | `csharp:configure-await-true-calls` |
| AZC0102 | Do not use GetAwaiter().GetResult() | ✅ | `csharp:sync-over-async-calls` |
| AZC0103 | Do not wait synchronously in async scope | ❌ | — |
| AZC0104 | Use EnsureCompleted() directly on async return | ❌ | — |
| AZC0105 | Do not add 'async' bool param to public methods | ✅ | `csharp-library:public-async-bool-params` |
| AZC0106 | Non-public async method needs 'async' bool param | ✅ | `csharp-library:async-missing-bool-param` |
| AZC0107 | Do not call public async method in sync scope | ❌ | — |
| AZC0108 | Incorrect 'async' parameter value in call | ❌ | — |
| AZC0109 | Misuse of 'async' parameter (only allowed in ?: or if) | ❌ | — |
| AZC0110 | Do not use await in possibly-synchronous scope | ❌ | — |
| AZC0111 | Do not use EnsureCompleted in possibly-async scope | ❌ | — |
| AZC0112 | Misuse of internal type via [InternalsVisibleTo] | ❌ | — |

### AOT Compatibility (AZC0150)

| AZC | Title | Cop Coverage | Cop Check |
|-----|-------|:---:|-----------|
| AZC0150 | Use ModelReaderWriter overload with ModelReaderWriterContext | ✅ | `csharp-library-client-azure:model-reader-writer-context` |

---

## 2. Azure.SdkAnalyzers (in-repo — azure-sdk-for-net)

These are .NET-specific analyzers maintained in `sdk/tools/Azure.SdkAnalyzers/`.

| AZC | Title | Cop Coverage | Cop Check |
|-----|-------|:---:|-----------|
| AZC0012 | Avoid single-word type names | ❌ | — |
| AZC0020 | Propagate CancellationToken to RequestContext | ❌ | — |
| AZC0101 | Do not use ConfigureAwait(true) | ✅ | `csharp:configure-await-true-calls` |

> Note: AZC0012 and AZC0020 in `Azure.SdkAnalyzers` are different rules from the same IDs in
> `Azure.ClientSdk.Analyzers`. The in-repo versions are .NET-specific refinements.

---

## 3. Our Roslyn Analyzers (analyzers/)

These analyzers overlap with some Cop checks and provide compile-time enforcement.

| Diagnostic | Title | AZC Equivalent | Also in Cop? |
|-----------|-------|----------------|:---:|
| CLIENT001 | Client class should accept options parameter | AZC0006 | ✅ `csharp-library-client:client-needs-options-ctor` |
| CLIENT002 | Async method should accept CancellationToken | AZC0002 | ✅ `csharp-library-client:async-needs-cancellation-token` |
| CLIENT003 | Client class should be sealed or abstract | — (no AZC) | ✅ `csharp-library-client:client-sealed-or-abstract` |
| AZURE001 | Azure client must accept TokenCredential | — (no AZC) | ✅ `csharp-library-client-azure:client-needs-token-credential` |
| AZURE002 | Options must inherit ClientOptions/ClientPipelineOptions | — (no AZC) | ✅ `csharp-library-client-azure:options-inherits-base` |

---

## 4. StyleCop Rules

`azure-sdk-for-net` enables StyleCop.Analyzers for all client libraries.
Cop covers naming, file organization, and whitespace hygiene rules.
Formatting, ordering, and documentation-comment rules are covered by agent instructions only.

| Category | Examples | Cop Coverage | Cop Check |
|----------|---------|:---:|-----------|
| Spacing (SA1000–SA1028) | Keyword spacing, trailing whitespace, tabs | ⚠️ | `csharp-style:no-tabs` (SA1027), `csharp-style:no-trailing-whitespace` (SA1028), `csharp-style:comment-spacing` (SA1005) |
| Readability (SA1100–SA1139) | Parenthesis placement, comma placement | ❌ | |
| Ordering (SA1200–SA1217) | Access modifier order, property accessor order | ❌ | |
| Naming (SA1300–SA1314) | Interface names begin with I | ✅ | `csharp-style:interface-prefix` (SA1302), `csharp-style:type-name-casing` (SA1300), `csharp-style:method-name-casing` (SA1300) |
| Maintainability (SA1400–SA1413) | Access modifier declared, file has single type | ⚠️ | `csharp-style:single-type-per-file` (SA1402) |
| Layout (SA1500–SA1520) | Blank lines around braces, end-of-file newlines | ❌ | |
| Documentation (SA1600–SA1651) | File headers, copyright text, XML doc rules | ❌ | |

> StyleCop covers ~30 enabled rules across these categories. The `csharp-style` package
> machine-checks 7 rules (naming, file organization, whitespace). Remaining rules are
> formatting, ordering, and documentation concerns covered by agent instructions.

---

## 5. .NET Analyzers (CA Rules)

Enabled via `Microsoft.CodeAnalysis.NetAnalyzers` for shipping client libraries.
200+ rules; most are active by default. Key disabled rules:

| CA Rule | Title | Disabled? | Cop Coverage |
|---------|-------|:---------:|:---:|
| CA1031 | Don't catch general exceptions | Yes (NoWarn) | ⚠️ `csharp:base-exception-catches` (similar) |
| CA1062 | Validate public method arguments | Yes (NoWarn) | ❌ |
| CA2007 | Don't directly await Task (use ConfigureAwait) | Yes (NoWarn) | ✅ `csharp-library:awaits-using-default` |
| CA1812 | Avoid uninstantiated internal classes | Yes (NoWarn) | ❌ |
| CA1716 | Identifiers should not match reserved keywords | Yes (NoWarn) | ❌ |
| CA2000 | Dispose IDisposable objects | Yes (NoWarn) | ❌ |
| All other CA rules | General .NET code quality | Active | ❌ |

> The CA rules cover broad .NET code quality (security, performance, reliability, design).
> Cop focuses on API design guidelines rather than general code quality.

---

## 6. Banned API Analyzers

| Banned Symbol | Reason | Cop Coverage |
|--------------|--------|:---:|
| `System.Uri.ToString()` | Prefer `Uri.AbsoluteUri` for consistent output | ❌ |

---

## 7. Build Script Validations

These are PowerShell scripts and MSBuild targets that run during CI.

| Check | Tool / Script | Cop Coverage |
|-------|---------------|:---:|
| Code formatting | `dotnet format` via CodeChecks.ps1 | ❌ |
| API listing export & diff | Export-API.ps1 + GenAPI | ❌ |
| API compatibility / breaking changes | Microsoft.DotNet.ApiCompat | ❌ |
| Public API spell checking | spell-check-public-api.ps1 (cspell) | ❌ |
| Code snippet validation | Update-Snippets.ps1 (snippet-generator) | ❌ |
| CHANGELOG.md validation | Verify-ChangeLog.ps1 | ❌ |
| README install instructions | CodeChecks.ps1 (no Install-Package) | ❌ |
| Central Package Management compliance | Validate-CpmCompliance.ps1 | ❌ |
| Bicep template validation | Validate-Bicep.ps1 | ❌ |
| Target framework validation | MSBuild ValidateTargetFrameworks | ❌ |
| Configuration schema validation | MSBuild ConfigurationSchema | ❌ |

---

## 8. .editorconfig Rules

| Category | Examples | Cop Coverage |
|----------|---------|:---:|
| Naming conventions | `_` prefix for private fields, `s_` for statics, PascalCase constants | ❌ |
| Code style preferences | Braces always, pattern matching, modifier order | ❌ |
| Using directive placement | Outside namespace | ❌ |
| Indentation & line breaks | Allman-style braces, indent block contents | ❌ |

---

## 9. Cop-Only Checks (not in azure-sdk-for-net)

These checks exist in Cop packages but have **no equivalent** enforcement in `azure-sdk-for-net`:

| Cop Check | Package | Description |
|-----------|---------|-------------|
| `var-declarations` | csharp | Disallow implicit typing with var |
| `dynamic-declarations` | csharp | Disallow dynamic typing |
| `thread-sleep-calls` | csharp | Use Task.Delay instead of Thread.Sleep |
| `console-calls` | csharp | Avoid Console output in library code |
| `client-sealed-or-abstract` | csharp-library-client | Client types must be sealed or abstract |
| `client-needs-token-credential` | csharp-library-client-azure | Azure clients must accept TokenCredential |
| `options-inherits-base` | csharp-library-client-azure | Options must inherit ClientOptions base |

> `client-needs-token-credential` and `options-inherits-base` are enforced by our Roslyn
> analyzers (AZURE001, AZURE002) but not by any azure-sdk-for-net tool.

---

## 10. Summary

### AZC Rule Coverage

| Category | Total | Covered | Gaps |
|----------|:-----:|:-------:|:----:|
| Client API Design (AZC0002–AZC0021) | 18 | 17 | 1 |
| Model Naming (AZC0030–AZC0036) | 6 | 5 | 1 |
| Async/Sync Patterns (AZC0100–AZC0112) | 12 | 5 | 7 |
| AOT (AZC0150) | 1 | 1 | 0 |
| In-repo SdkAnalyzers (unique rules) | 2 | 0 | 2 |
| **Totals** | **39** | **28** | **11** |

### Gap List (AZC rules not covered by Cop)

| AZC | Title | Notes |
|-----|-------|-------|
| AZC0021 | ClientSettings ctor param isolation | New System.ClientModel rule |
| AZC0034 | Duplicate type names across SDK/.NET | Cross-assembly semantic check |
| AZC0035 | Output model needs model factory method | Testability/mocking pattern |
| AZC0103 | Don't wait synchronously in async scope | Advanced sync-over-async detection |
| AZC0104 | Use EnsureCompleted() directly on async return | Azure-specific sync pattern |
| AZC0107 | Don't call public async method in sync scope | Call-graph analysis |
| AZC0108 | Incorrect 'async' parameter value | Data-flow analysis |
| AZC0109 | Misuse of 'async' parameter | Control-flow analysis |
| AZC0110 | Don't await in possibly-synchronous scope | Scope-sensitive analysis |
| AZC0111 | Don't use EnsureCompleted in possibly-async scope | Scope-sensitive analysis |
| AZC0112 | Internal type misuse via InternalsVisibleTo | Cross-assembly analysis |

### Non-AZC Check Categories (not covered)

| Category | Approx. Rules | Notes |
|----------|:--------:|-------|
| StyleCop | ~30 | Formatting, documentation — orthogonal to Cop's goals |
| .NET Analyzers (CA) | ~200 | Broad .NET code quality — most are enabled by default |
| Banned API | 1 | Uri.ToString() banned |
| Build scripts | 11 | CI-specific validations (API compat, formatting, spelling, etc.) |
| .editorconfig | ~20 | IDE-level naming and style conventions |

### Overall

- **39 AZC rules** total → **28 covered (72%)**, **11 gaps (28%)**
- Most gaps are in the **advanced async/sync flow analysis** rules (AZC0103–AZC0112) which require data-flow and scope analysis beyond what Cop's set-based approach currently supports.
- **StyleCop, CA rules, and build scripts** are orthogonal categories focused on formatting, general code quality, and CI workflow — not API design guidelines.
- Cop provides **7 unique checks** with no azure-sdk-for-net equivalent, adding value for `var`/`dynamic` bans, `Thread.Sleep`, `Console` calls, sealed-or-abstract enforcement, and Azure identity requirements.
