# MultiHostPz

MultiHostPz is a Windows desktop application for coordinating Project Zomboid multiplayer saves between hosts.

## Current MVP boundary

The first work unit provides a .NET 8 WPF application that:

- Resolves the default local Project Zomboid profile root at `%USERPROFILE%\Zomboid`.
- Displays the expected multiplayer save directory at `%USERPROFILE%\Zomboid\Saves\Multiplayer`.
- Reports whether the local profile root currently exists.
- Clearly reports that synchronization has not happened yet.

The application does not upload, download, or synchronize any Project Zomboid files. It has no cloud integration.

## Local snapshot preparation

The second work unit adds a `Create local snapshot` action for the resolved multiplayer saves directory. It is deliberately local-only:

- Snapshots are written by default to `%LOCALAPPDATA%\MultiHostPz\snapshots`.
- Each snapshot is a ZIP plus a JSON sidecar manifest containing its identity and file statistics.
- ZIP and manifest files are built as temporary files and atomically moved into place. Failed operations clean up temporary output and do not intentionally publish a partial snapshot.
- The source directory is never used as a staging area and is never modified.
- Snapshot creation is refused when the source is missing, the destination is inside the source, or a known Project Zomboid process is running.

## Local snapshot restore

The `Restore latest local snapshot` action restores the newest valid local snapshot after explicit confirmation. Restore is local-only and refuses to run while a known Project Zomboid process is active.

- The archive and manifest are validated before the current target is changed, including format, identity, file-count, byte-count, and ZIP path safety checks.
- The archive is extracted into a temporary directory adjacent to the target. The current target is then preserved as a timestamped sibling backup that is never deleted.
- The extracted directory replaces the target. If replacement fails, the original target is rolled back from the retained backup.
- Missing, malformed, mismatched, unsupported, or unsafe snapshot inputs leave the current target untouched.

Cloud storage, synchronization, and conflict handling remain out of scope.

## Languages and settings

The interface supports English and neutral Spanish. Use the visible language selector to update all current labels, statuses, and dialogs immediately without restarting the application. On first use, Spanish is selected for a Spanish system UI culture; all other cultures use English.

An explicit language choice is saved atomically in `%LOCALAPPDATA%\MultiHostPz\settings.json`. Missing, malformed, or unreadable settings fall back to the system UI culture.

## Build and test

```text
dotnet restore MultiHostPz.sln
dotnet build MultiHostPz.sln --no-restore
dotnet test MultiHostPz.sln --no-build --no-restore
```
