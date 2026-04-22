---
name: filesystem
version: 1.0.0
title: Filesystem Types
description: Type definitions for filesystem structural and temporal analysis
authors: cop-team
tags: types, filesystem, analysis
---

# Filesystem Types

Defines the type system for filesystem structural and temporal analysis. Import with `import filesystem` in `.cop` files.

Provides types for: Folder, DiskFile.
Provides collections for: Folders, DiskFiles.

## Time-based properties

Both `Folder` and `DiskFile` include a `MinutesSinceModified` property (int) representing how many minutes have elapsed since the item was last modified. Convenience predicates:

- `recentlyModified` — modified within the last 10 minutes
- `stale` — not modified in over 24 hours (1440 minutes)
