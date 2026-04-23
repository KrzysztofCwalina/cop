---
name: typespec
version: 1.0.0
title: TypeSpec Analysis
description: Types and collections for TypeSpec API specification analysis
authors: cop-team
tags: typespec, api, analysis
provider: clr
providerEntry: TypeSpecProvider.TypeSpecRawProvider
---

# TypeSpec Analysis

Provides raw TypeSpec type graph for API specification analysis. Import with `import typespec` in check files.

Provides types for: TspModel, TspOperation, TspInterface, TspEnum, TspUnion, TspScalar, TspProperty, TspDecorator, TspNamespace.
Provides collections for: Models, Operations, Interfaces, Enums, Unions, Scalars, Namespaces.
