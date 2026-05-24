# Folder Change Tracker

A localhost-only Blazor Server app that tracks file system changes inside a folder over time.

**Not intended for networked or multi-user deployment.**

## What it does

Enter a folder path and click Analyze:

- **First run** — snapshots the folder and lists all files and subfolders at version 1.
- **Subsequent runs** — compares against the last snapshot and shows what was added, changed (by content, not timestamp), or removed.

State is persisted to JSON files in the `data/` directory and survives restarts. Only the most recent snapshot per path is kept.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)

## How to run

```bash
cd FolderChangeTracker
dotnet run
```

Open the URL printed to the console (e.g. `https://localhost:xxxx`).

## Known limitations

- Max **100 files** per folder; files over **50 MB** are skipped.
- No history — only the last snapshot is stored. If a tracked folder disappears and you trigger analysis, its snapshot is silently deleted.
- Change detection uses **SHA-256** file hashing, not timestamps or metadata.
