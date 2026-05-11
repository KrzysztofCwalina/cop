---
name: python-snippets-checks
version: 1.0.0
title: Python Snippet Validation
description: Validates that Python code snippets in # [START/END] blocks match markdown documentation references
authors: cop-team
tags: python, snippets, documentation, markdown
language: Python
dependencies:
  - github.com/cop/cop/python-checks
---

# Python Snippet Validation

Checks that `# [START snippet_name]` / `# [END snippet_name]` blocks in Python source files have matching `` ```python snippet_name `` fences in markdown README files, and that the content stays in sync.
