---
name: prefer-async
version: 1.0.0
title: Prefer Async Over Sync
description: Flags calls to sync methods when an async variant exists on any type in the codebase
authors: cop-team
tags: async, performance, best-practices
dependencies:
  - code
  - code-analysis
---

# Prefer Async Over Sync

Detects calls to synchronous methods (e.g., `Read`, `Write`, `Send`) when an async variant (`ReadAsync`, `WriteAsync`, `SendAsync`) exists on any type in the codebase.

## Usage

```cop
import prefer-async

CHECK prefer-async => Code.Statements:callsSyncWhenAsyncExists
    :toWarning('Use {item.MemberName}Async instead of {item.MemberName}')
```

## How It Works

The predicate uses **cross-collection flattening** (`Types.MethodNames`) to check all declared methods across all types. If a method named `FooAsync` exists anywhere and a call to `Foo` is found, it flags it.

This works across C#, Python, and JavaScript — any language where async variants follow the naming convention of appending `Async` to the method name.
