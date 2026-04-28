---
name: csharp-snippets
version: 1.0.0
title: C# Snippet Validation
description: Validates that C# code snippets in #region blocks match markdown documentation references
authors: cop-team
tags: csharp, snippets, documentation, markdown
language: C#
dependencies:
  - github.com/cop/cop/csharp
---

# C# Snippet Validation

Checks that `#region Snippet:X` blocks in C# source files have matching `` ```csharp Snippet:X `` fences in markdown README files, and that the content stays in sync.
