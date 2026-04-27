## filesystem

File and folder analysis. &nbsp; `import filesystem`

**Source:** [`packages/filesystem/src/filesystem.cop`](../../packages/filesystem/src/filesystem.cop)

---

### Collections

`Disk` is the top-level `Filesystem` object containing:

| Collection | Type | Description |
|---|---|---|
| `Disk.Folders` | `[Folder]` | All folders in the scanned tree |
| `Disk.Files` | `[DiskFile]` | All files in the scanned tree |

---

### Types

#### Folder

| Property | Type | Description |
|---|---|---|
| `Path` | string | Full path |
| `Name` | string | Folder name (last segment) |
| `Empty` | bool | True if no files inside |
| `FileCount` | int | Files directly in folder |
| `SubfolderCount` | int | Immediate subfolders |
| `Depth` | int | Nesting depth from scan root |
| `MinutesSinceModified` | int | Minutes since last modified |

#### DiskFile

| Property | Type | Description |
|---|---|---|
| `Path` | string | Full path |
| `Name` | string | File name with extension |
| `Extension` | string | Extension (e.g. `.cs`, `.md`) |
| `Size` | int | Size in bytes |
| `Folder` | string | Parent folder path |
| `Depth` | int | Nesting depth from scan root |
| `MinutesSinceModified` | int | Minutes since last modified |
| `Checksum` | string? | Content hash (for lock checking) |
| `Locked` | bool | Under lock control |
| `LockStatus` | string | `locked`, `modified`, `deleted`, or `unlocked` |

#### Filesystem

| Property | Type | Description |
|---|---|---|
| `Folders` | `[Folder]` | Collection of all folders |
| `Files` | `[DiskFile]` | Collection of all files |

---

### Predicates

| Predicate | Applies To | Condition |
|---|---|---|
| `empty` | any | Built-in: collection/string has no items, or `Empty` property is true |
| `recentlyModified` | Folder, DiskFile | `MinutesSinceModified < 10` |
| `stale` | Folder, DiskFile | `MinutesSinceModified > 1440` |
| `locked` | DiskFile | `Locked == true` |
| `lockViolation` | DiskFile | `LockStatus == 'modified'` or `'deleted'` |

---

### Checks

| Check | Severity | Condition |
|---|---|---|
| `empty-folders` | error | `Disk.Folders:empty` — flags empty folders |
| `lock-violations` | error | `Disk.Files:lockViolation` — flags unauthorized changes |

---

### Examples

```ruby
import filesystem

predicate isLarge(DiskFile) => DiskFile.Size > 1048576
let large-files = Disk.Files:isLarge:toWarning('Large file ({item.Size} bytes): {item.Path}')

predicate isDeeplyNested(Folder) => Folder.Depth > 5
let deep-folders = Disk.Folders:isDeeplyNested:toWarning('Deep folder: {item.Path}')
```
