# Filesystem Package Reference

The `filesystem` package provides types, predicates, and pre-built checks for analyzing files and folders on disk.

## Import

```ruby
import filesystem
```

## Lists

The package exports a `Disk` binding containing two collections:

```ruby
import filesystem

# Use Disk.Files and Disk.Folders
foreach Disk.Files => PRINT('{item.Path}')
```

`Disk` is a `Filesystem` object containing two collections:

| Collection | Type       | Description                        |
|------------|------------|------------------------------------|
| `Disk.Folders`  | `[Folder]` | All folders in the scanned tree   |
| `Disk.Files`    | `[DiskFile]` | All files in the scanned tree   |

## Type Reference

### Folder

| Property              | Type   | Description                                  |
|-----------------------|--------|----------------------------------------------|
| `Path`                | string | Full path to the folder                      |
| `Name`                | string | Folder name (last segment of the path)       |
| `Empty`             | bool   | `true` if the folder contains no files       |
| `FileCount`           | int    | Number of files directly in the folder       |
| `SubfolderCount`      | int    | Number of immediate subfolders               |
| `Depth`               | int    | Nesting depth relative to the scan root      |
| `MinutesSinceModified`| int    | Minutes since the folder was last modified   |

Usage — filter folders by properties:

```ruby
import filesystem

predicate deepFolder(Folder) => Folder.Depth > 5
predicate manyFiles(Folder) => Folder.FileCount > 100
foreach Disk.Folders:manyFiles => PRINT('{item.Path} has {item.FileCount} files')
```

### DiskFile

| Property              | Type     | Description                                      |
|-----------------------|----------|--------------------------------------------------|
| `Path`                | string   | Full path to the file                            |
| `Name`                | string   | File name including extension                    |
| `Extension`           | string   | File extension (e.g. `.cs`, `.md`)               |
| `Size`                | int      | File size in bytes                               |
| `Folder`              | string   | Path of the parent folder                        |
| `Depth`               | int      | Nesting depth relative to the scan root          |
| `MinutesSinceModified`| int      | Minutes since the file was last modified         |
| `Checksum`            | string?  | Content hash (nullable); used for lock checking  |
| `Locked`            | bool     | `true` if the file is under lock control         |
| `LockStatus`          | string   | Current lock state (see Lock Integration below)  |

Usage — filter files by properties:

```ruby
import filesystem

predicate isLargeFile(DiskFile) => DiskFile.Size > 1048576
predicate isMarkdown(DiskFile) => DiskFile.Extension == '.md'
foreach Disk.Files:isLargeFile => PRINT('{item.Name} ({item.Size} bytes)')
```

### Filesystem

| Property  | Type         | Description                |
|-----------|--------------|----------------------------|
| `Folders` | `[Folder]`   | Collection of all folders  |
| `Files`   | `[DiskFile]` | Collection of all files    |

## Exported Predicates

| Predicate            | Applies To | Condition                              |
|----------------------|------------|----------------------------------------|
| `empty`            | Folder     | `Folder.Empty == true`               |
| `recentlyModified` | Folder     | `Folder.MinutesSinceModified < 10`     |
| `recentlyModified` | DiskFile   | `DiskFile.MinutesSinceModified < 10`   |
| `stale`            | Folder     | `Folder.MinutesSinceModified > 1440`   |
| `stale`            | DiskFile   | `DiskFile.MinutesSinceModified > 1440` |
| `locked`           | DiskFile   | `DiskFile.Locked == true`            |
| `lockViolation`    | DiskFile   | `DiskFile.LockStatus == 'modified'` or `DiskFile.LockStatus == 'deleted'` |

`recentlyModified` is overloaded for both `Folder` and `DiskFile`. The same is true for `stale`.

Usage — apply exported predicates as filters:

```ruby
import filesystem

# Filter using the built-in predicates
let RecentFiles = Disk.Files:recentlyModified
let StaleFolders = Disk.Folders:stale
let LockIssues = Disk.Files:lockViolation

foreach RecentFiles => PRINT('{Recently modified:@yellow} {item.Path}')
foreach StaleFolders => PRINT('{Stale folder:@yellow} {item.Path}')
```

## Pre-built Checks

The package ships two ready-to-use violation collections:

### empty-folders

Flags every empty folder as an error.

```ruby
export let empty-folders = Disk.Folders:empty
    :toError('Empty folder: {item.Path}')
```

### lock-violations

Flags every file whose lock status indicates an unauthorized change.

```ruby
export let lock-violations = Disk.Files:lockViolation
    :toError('Lock violation: {item.Path} ({item.LockStatus})')
```

## Lock Integration

The `DiskFile` type includes two lock-related properties:

- **`Locked`** — `true` when the file is under lock control.
- **`LockStatus`** — a string indicating the current state of the lock.
- **`Checksum`** — a nullable content hash used to detect modifications to locked files.

### LockStatus Values

| Value      | Meaning                                                  |
|------------|----------------------------------------------------------|
| `locked`   | File is locked and unmodified                            |
| `modified` | File is locked but its content has changed (violation)   |
| `deleted`  | File is locked but has been removed (violation)          |
| `unlocked` | File is not under lock control                           |

The `lockViolation` predicate matches files with a `LockStatus` of `'modified'` or `'deleted'`.

Usage — check lock status:

```ruby
import filesystem

let ModifiedLocked = Disk.Files:locked:recentlyModified
foreach Disk.Files:lockViolation => PRINT('{warning:@yellow} Locked file changed: {item.Path} (status: {item.LockStatus})')
```

## Examples

### Flag large files

```ruby
import filesystem

predicate isLarge(DiskFile) => DiskFile.Size > 1048576

let large-files = Disk.Files:isLarge
    :toWarning('Large file ({item.Size} bytes): {item.Path}')
```

### Find deeply nested folders

```ruby
import filesystem

predicate isDeeplyNested(Folder) => Folder.Depth > 5

let deep-folders = Disk.Folders:isDeeplyNested
    :toWarning('Deeply nested folder: {item.Path} (depth {item.Depth})')
```

### Detect stale locked files

```ruby
import filesystem

predicate isStaleLocked(DiskFile) =>
    DiskFile.Locked == true &&
    DiskFile.MinutesSinceModified > 1440

let stale-locked = Disk.Files:isStaleLocked
    :toWarning('Locked file is stale: {item.Path}')
```

### Check for specific file extensions

```ruby
import filesystem

predicate isTempFile(DiskFile) =>
    DiskFile.Extension == '.tmp' ||
    DiskFile.Extension == '.bak'

let temp-files = Disk.Files:isTempFile
    :toWarning('Temporary file found: {item.Path}')
```
