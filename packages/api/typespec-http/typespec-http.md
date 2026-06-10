---
name: typespec-http
version: 1.0.0
title: TypeSpec HTTP Protocol Analysis
description: HTTP protocol graph derived from TypeSpec API specifications
authors: cop-team
tags: typespec, http, api, analysis
provider: clr
providerEntry: TypeSpecProvider.TypeSpecHttpProvider
---

# TypeSpec HTTP Protocol Analysis

Transforms TypeSpec API specifications into an HTTP protocol graph with resolved verbs, paths, and parameters.
Import with `import typespec-http` in check files.

Provides types for: HttpOperation, HttpService, HttpParameter, HttpResponse, HttpHeader.
Provides collections for: Operations, Services.
