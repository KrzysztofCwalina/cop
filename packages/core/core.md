---
name: core
version: 1.0.0
title: Core Runtime Types
description: Fundamental types for provider access — Data, and language samples
authors: cop-team
tags: core, runtime, types
---

# Core

Defines fundamental runtime types used by all provider packages.

## Types

- **Data** — Dynamic provider accessor returned by `data(providerName)`. Properties correspond to the provider's declared collections.

## Usage

```cop
import core

let db = data('my-provider')
let items = db.MyCollection
```

