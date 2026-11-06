---
name: java-checks
version: 1.0.2
title: Java Checks
description: Java coding conventions and correctness checks for coding agents
authors: cop-team
tags: java, coding-standards, correctness
language: Java
dependencies: ['github.com/KrzysztofCwalina/cop/java: 2.0.0', 'github.com/KrzysztofCwalina/cop/code: 1.2.0']
---

# Java Checks

Java coding conventions and correctness checks for coding agents, covering console
output, exception handling, and process-control anti-patterns.

These checks hardcode the Java provider via `codebase(java.parse())`, so they run with
just a target directory — no `-p` flag required:

```bash
cop java-checks -t .
```
