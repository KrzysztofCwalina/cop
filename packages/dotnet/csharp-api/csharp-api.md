---
name: csharp-api
version: 1.0.0
title: C# API Surface Analysis
description: API surface tracking, export, and diff for .NET libraries
authors: cop-team
tags: csharp, api, surface, diff, breaking-changes
language: C#
dependencies:
  - github.com/cop/cop/csharp
---

# C# API Surface Analysis

Predicates and checks for tracking public API surface, detecting breaking changes,
and validating API listings against baseline files.

## Checks

- `api-removed` — Detect API entries in baseline file that no longer exist (breaking change)
- `api-added` — Detect new API entries not in baseline (informational)

## Usage

Place an `api-baseline.txt` file in your project root with one API signature per line.
Run cop with the `csharp-api` package to compare current code against the baseline.
