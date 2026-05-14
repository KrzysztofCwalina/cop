---
name: sample
version: 1.0.0
title: Sample Provider
description: A minimal sample provider demonstrating the Cop plugin pattern
authors: cop-team
tags: sample, template
provider: clr
providerEntry: SampleProvider.SampleProvider
---

# Sample Provider

A minimal provider that reads `.widget` files and exposes them as a `Widgets` collection.

## Usage

```cop
import sample

foreach Widgets   # Widgets is exported by the sample package via: export let Widgets = data('sample').Widgets
    '{Widget.Name} ({Widget.Category}) size={Widget.Size}'
```

## Widget File Format

Create `.widget` files with key=value pairs:

```
name=MyWidget
category=ui
size=42
```
