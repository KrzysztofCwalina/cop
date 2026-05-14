---
name: cop
version: 1.0.0
title: Cop Language Analysis
description: Structural analysis of .cop source files — types, predicates, functions, imports
authors: cop-team
tags: cop, language, analysis
dependencies: [code]
provider: clr
providerEntry: Cop.Providers.CopProvider
---

# Cop Language Analysis

Provides structural analysis of `.cop` source files through the standard code analysis collections.

## Collections

All standard code collections are available, filtered to `.cop` files:

- **Types** — `type` and `flags` definitions
- **Statements** — predicates, functions, let bindings, imports, commands
- **Lines** — raw text lines
- **Files** — .cop source files with imports and structure

## Usage

```cop
import cop

# List all exported type definitions in .cop files
foreach cop.Types:isPublic => '{item.Name} ({item.File.Path})'

# Find predicates
foreach cop.Statements:declaration => '{item.MemberName}'
```
