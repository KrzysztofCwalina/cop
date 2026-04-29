## arch-layering
Architecture layering enforcement. &nbsp; `import arch-layering`

**Source:** [`packages/arch-layering/src/`](../../packages/arch-layering/src/)

---

### Overview

The `arch-layering` package lets architects formally define architectural layers and enforce dependency direction between projects. It works across all supported languages (C#, Python, JavaScript/TypeScript) by operating on the `Projects` collection.

### Key Concepts

- **Layer**: A named group of projects (e.g., "Presentation", "Business", "Data")
- **Allowed dependency**: A rule that says "projects in layer A may reference projects in layer B"
- **Violation**: A project references another project in a disallowed layer
- **Uncategorized**: A project that isn't assigned to any defined layer

### Usage Pattern

```cop
import arch-layering

# 1. Define layers as lists of project names
let presentation-projects = ['MyApp.Web', 'MyApp.Api']
let business-projects     = ['MyApp.Services', 'MyApp.Domain']
let data-projects         = ['MyApp.Data', 'MyApp.EF']
let infra-projects        = ['MyApp.Logging', 'MyApp.Config']

# 2. Combine all known projects (for uncategorized detection)
let all-known-projects = presentation-projects + business-projects + data-projects + infra-projects

# 3. Define violation predicates (disallowed references)
# Presentation must not reference Data directly (must go through Business)
predicate presentationReferencesData(Project) =>
    Project.Name:in(presentation-projects)
    && Project.References:containsAny(data-projects)

# Data must not reference Presentation
predicate dataReferencesPresentation(Project) =>
    Project.Name:in(data-projects)
    && Project.References:containsAny(presentation-projects)

# Business must not reference Presentation
predicate businessReferencesPresentation(Project) =>
    Project.Name:in(business-projects)
    && Project.References:containsAny(presentation-projects)

# 4. Produce violations
export let layer-violations = Code.Projects:presentationReferencesData
    :toError('Presentation project {item.Name} must not reference Data layer directly')
    + Code.Projects:dataReferencesPresentation
    :toError('Data project {item.Name} must not reference Presentation layer')
    + Code.Projects:businessReferencesPresentation
    :toError('Business project {item.Name} must not reference Presentation layer')

# 5. Detect uncategorized projects (new assemblies not in any layer)
export let uncategorized = Code.Projects:notInLayer
    :toWarning('Project {item.Name} is not assigned to any architectural layer')

# 6. Run all checks
CHECK arch-layering => layer-violations + uncategorized
```

### Exported Types

#### LayerViolation

| Property | Type | Description |
|---|---|---|
| `Name` | `string` | Violating project name |
| `Path` | `string` | Path to project manifest |
| `SourceLayer` | `string` | Layer the project belongs to |
| `Target` | `string` | Referenced project name |
| `TargetLayer` | `string` | Layer of the referenced project |
| `Source` | `string` | Source path for diagnostics |

#### UncategorizedProject

| Property | Type | Description |
|---|---|---|
| `Name` | `string` | Project name |
| `Path` | `string` | Path to project manifest |
| `Source` | `string` | Source path for diagnostics |

### Exported Predicates

| Predicate | Applies To | Description |
|---|---|---|
| `notInLayer` | `Project` | True if project name is not in `all-known-projects` |

### Exported Functions

| Function | Input | Output | Description |
|---|---|---|---|
| `toError` | `Project` | `Violation` | Creates error-level violation |
| `toWarning` | `Project` | `Violation` | Creates warning-level violation |
| `toInfo` | `Project` | `Violation` | Creates info-level violation |

### Cross-Language Examples

#### C# Solution

```cop
import arch-layering

let api-projects = ['Contoso.Api', 'Contoso.Controllers']
let domain-projects = ['Contoso.Domain', 'Contoso.Contracts']
let infra-projects = ['Contoso.Data', 'Contoso.Messaging']
let all-known-projects = api-projects + domain-projects + infra-projects

# Domain must not reference API or Infrastructure
predicate domainReferencesApi(Project) =>
    Project.Name:in(domain-projects)
    && Project.References:containsAny(api-projects)

predicate domainReferencesInfra(Project) =>
    Project.Name:in(domain-projects)
    && Project.References:containsAny(infra-projects)

export let violations = Code.Projects:domainReferencesApi
    :toError('Domain project {item.Name} must not reference API layer')
    + Code.Projects:domainReferencesInfra
    :toError('Domain project {item.Name} must not reference Infrastructure layer')

export let uncategorized = Code.Projects:notInLayer
    :toWarning('Project {item.Name} is not assigned to any layer')

CHECK arch-layering => violations + uncategorized
```

#### Python Monorepo

```cop
import arch-layering

let api-projects = ['myapp-api', 'myapp-routes']
let core-projects = ['myapp-core', 'myapp-domain']
let data-projects = ['myapp-db', 'myapp-models']
let all-known-projects = api-projects + core-projects + data-projects

predicate dataReferencesApi(Project) =>
    Project.Name:in(data-projects)
    && Project.References:containsAny(api-projects)

export let violations = Code.Projects:dataReferencesApi
    :toError('Data project {item.Name} must not reference API layer')

export let uncategorized = Code.Projects:notInLayer
    :toWarning('Project {item.Name} is not assigned to any layer')

CHECK arch-layering => violations + uncategorized
```

#### JavaScript/TypeScript Monorepo

```cop
import arch-layering

let ui-projects = ['@myapp/web', '@myapp/components']
let logic-projects = ['@myapp/services', '@myapp/store']
let api-projects = ['@myapp/api-client', '@myapp/graphql']
let all-known-projects = ui-projects + logic-projects + api-projects

predicate uiReferencesApi(Project) =>
    Project.Name:in(ui-projects)
    && Project.References:containsAny(api-projects)

export let violations = Code.Projects:uiReferencesApi
    :toError('UI package {item.Name} must not reference API layer directly')

export let uncategorized = Code.Projects:notInLayer
    :toWarning('Package {item.Name} is not assigned to any layer')

CHECK arch-layering => violations + uncategorized
```

### Dependencies

- `code` — provides the `Project` type and `Code.Projects` collection
- `code-analysis` — provides the `Violation` type and `CHECK` command
