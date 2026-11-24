---
name: code-quality-checks
version: 1.0.0
title: Code Quality Checks (Compatibility)
description: Compatibility forwarding API for ai-slop-checks
authors: cop-team
tags: checks, quality, compatibility
dependencies: ['github.com/KrzysztofCwalina/cop/code: 1.2.0', 'github.com/KrzysztofCwalina/cop/ai-slop-checks: 1.0.0']
---

# Code Quality Checks (Compatibility)

The canonical package and namespace is now **ai-slop-checks**. Browse its
[rule catalog, coverage and research](../ai-slop-checks/ai-slop-checks.md) or
select **ai-slop-checks** in the
[Package Reference](https://krzysztofcwalina.github.io/cop/reference.html).
Run `cop help ai-slop-checks` for the canonical API.

Existing `import code-quality-checks` programs continue to work. All 70 existing
exported functions forward to the canonical implementation, including individual
rules, scopes, `securityChecks`, `operationalSafetyChecks`,
`scopedCodeQualityChecks` and `codeQualityChecks`. Check identifiers, messages,
scoping and `cop-ignore` suppressions are unchanged.

For new programs, use `import ai-slop-checks` and `aiSlopChecks(cb)`. The six
standard language suites already use that package; callers do not need to import
both names.
