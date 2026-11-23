---
name: python-checks
version: 1.0.2
title: Python Checks
description: Python correctness, style, idiom, quality, and documentation checks for coding agents
authors: cop-team
tags: python, coding-standards, style, idioms, quality, documentation
language: Python
dependencies: ['github.com/KrzysztofCwalina/cop/python: 2.0.0', 'github.com/KrzysztofCwalina/cop/code: 1.2.0', 'github.com/KrzysztofCwalina/cop/code-quality-checks: 1.0.0']
provider: clr
providerEntry: Cop.Providers.PythonProvider
---

# Python Checks

Comprehensive Python checks covering correctness, PEP 8 style, Pythonic idioms, code quality, and documentation.

The standard suite includes `codeQualityChecks(cb)` from `code-quality-checks`.
See its coverage table for error-masking, literal-assertion, unfinished-code
and other review checks, including source-only parser limitations.
