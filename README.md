# MultiHostPz

MultiHostPz is a Windows desktop application for coordinating Project Zomboid multiplayer saves between hosts.

## Current MVP boundary

The first work unit provides a .NET 8 WPF application that:

- Resolves the default local Project Zomboid profile root at `%USERPROFILE%\Zomboid`.
- Displays the expected multiplayer save directory at `%USERPROFILE%\Zomboid\Saves\Multiplayer`.
- Reports whether the local profile root currently exists.
- Clearly reports that synchronization has not happened yet.

The application now has bounded Dropbox snapshot upload/download. It does not lock hosts or resolve conflicts.

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

## Dropbox App Folder setup

1. In the Dropbox App Console, create a scoped-access app with **App folder** access.
2. Enable only `account_info.read`, `files.metadata.read`, `files.content.read`, and `files.content.write`.
3. Register the exact redirect URI `http://127.0.0.1:53682/oauth/callback/`.
4. Set the non-secret app key in the `MULTIHOSTPZ_DROPBOX_APP_KEY` environment variable, then restart MultiHostPz. Do not configure or distribute a client secret.

The desktop authorization flow uses authorization-code PKCE and requests offline access. Refresh and access tokens are encrypted for the current Windows user with DPAPI under `%LOCALAPPDATA%\MultiHostPz`.

Dropbox stores paired ZIP and JSON files under the app-relative `/snapshots` folder. Upload writes the ZIP first and manifest last. Download stages and validates the selected latest valid pair before atomically publishing it into the local snapshots directory; it never restores automatically. Use the existing explicit local restore action afterward.

Host locking, conflict prevention, automatic restore, and multi-writer coordination are not implemented yet.

## Languages and settings

The interface supports English and neutral Spanish. Use the visible language selector to update all current labels and statuses immediately; subsequent confirmation dialogs use the selected language without restarting the application. On first use, Spanish is selected for a Spanish system UI culture; all other cultures use English.

An explicit language choice is saved atomically in `%LOCALAPPDATA%\MultiHostPz\settings.json` and takes precedence over the system UI culture. Missing, malformed, unreadable, or unsupported settings fall back to the system UI culture.

## Build and test

```text
dotnet restore MultiHostPz.sln
dotnet build MultiHostPz.sln --configuration Release --no-restore
dotnet test MultiHostPz.sln --configuration Release --no-build --no-restore
```

## Windows release

Version `0.1.0` is published for 64-bit Windows as a self-contained application. Self-contained means the .NET 8 runtime is included; users do not need to install .NET separately.

Publish the repeatable release profile from the repository root:

```text
dotnet publish src/MultiHostPz.App/MultiHostPz.App.csproj --configuration Release -p:PublishProfile=win-x64
```

The complete publish payload is written to `artifacts/MultiHostPz-0.1.0-win-x64/publish/`. Run `MultiHostPz.exe` from that directory, or extract `artifacts/MultiHostPz-0.1.0-win-x64.zip` and run it there. Keep every extracted file together so the Spanish resources remain available.

Local snapshot and restore work without Dropbox configuration. Dropbox features still require `MULTIHOSTPZ_DROPBOX_APP_KEY` to be set in the launching user's environment; the key is not embedded in the release. Version `0.1.0` does not provide host locking, conflict prevention, or multi-writer coordination.

The release is unsigned and has no installer or MSIX package. Windows SmartScreen may therefore warn before the executable runs.
