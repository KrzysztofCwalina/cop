---
name: code-layering
version: 1.0.0
title: Architecture Layering Enforcement
description: Formal architecture layer definitions and dependency direction checks
authors: cop-team
tags: architecture, layering, dependencies, enforcement
dependencies:
  - code
  - code-analysis
---

# Architecture Layering Enforcement

Defines types and predicates for declaring architectural layers and enforcing dependency direction between projects.

This package is language-agnostic and works with project/dependency data from C#, JavaScript/TypeScript, Python, Java, Go, and Rust.

## Usage

```cop
import code-layering

# Define layers as project name lists
let presentation-projects = ['MyApp.Web', 'MyApp.Api']
let business-projects     = ['MyApp.Services', 'MyApp.Domain']
let data-projects         = ['MyApp.Data', 'MyApp.EF']
let all-known-projects = presentation-projects + business-projects + data-projects

# Define disallowed references
predicate presentationReferencesData(Project) =>
    Project.Name:in(presentation-projects)
    && Project.References:containsAny(data-projects)

predicate dataReferencesPresentation(Project) =>
    Project.Name:in(data-projects)
    && Project.References:containsAny(presentation-projects)

# Produce violations
export let violations = Code.Projects:presentationReferencesData
    :toError('Presentation project {item.Name} must not reference Data layer directly')
    + Code.Projects:dataReferencesPresentation
    :toError('Data project {item.Name} must not reference Presentation layer')

# Detect uncategorized projects
export let uncategorized = Code.Projects:notInLayer
    :toWarning('Project {item.Name} is not assigned to any architectural layer')

CHECK code-layering => violations + uncategorized
```
