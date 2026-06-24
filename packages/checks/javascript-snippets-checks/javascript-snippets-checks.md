---
name: javascript-snippets-checks
version: 1.0.0
title: JavaScript Snippet Validation
description: Validates that JavaScript/TypeScript code snippets in // [START/END] blocks match markdown documentation references
authors: cop-team
tags: javascript, typescript, snippets, documentation, markdown
language: JavaScript
dependencies:
  - github.com/cop/cop/javascript-checks
---

# JavaScript Snippet Validation

Checks that `// [START snippet_name]` / `// [END snippet_name]` blocks in JavaScript/TypeScript source files have matching `` ```javascript snippet_name `` fences in markdown README files, and that the content stays in sync.
