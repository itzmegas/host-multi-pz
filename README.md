# MultiHostPz

MultiHostPz is a Windows desktop application for coordinating Project Zomboid multiplayer saves between hosts.

## Current MVP boundary

The first work unit provides a .NET 8 WPF application that:

- Resolves the default local Project Zomboid profile root at `%USERPROFILE%\Zomboid`.
- Displays the expected multiplayer save directory at `%USERPROFILE%\Zomboid\Saves\Multiplayer`.
- Reports whether the local profile root currently exists.
- Clearly reports that synchronization has not happened yet.

The application now has bounded Google Drive and Dropbox snapshot upload/download. Google Drive is the default provider; Dropbox remains selectable and functional. It does not lock hosts or resolve conflicts.

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

## Google Drive setup (default)

1. In the [Google Cloud Console](https://console.cloud.google.com/), create or select a project.
2. Open **APIs & Services > Library**, find **Google Drive API**, and enable it.
3. Configure the OAuth consent screen and add the `https://www.googleapis.com/auth/drive` scope. Keep the app in testing while developing and add each Google account that will connect as a test user when Google requires it.
4. Open **APIs & Services > Credentials**, create an OAuth client ID with application type **Desktop app**, and download its JSON file.
5. Set `MULTIHOSTPZ_GOOGLE_CLIENT_SECRETS_PATH` to the full local path of that downloaded JSON file, then restart MultiHostPz.

Do not rename or edit the downloaded credential fields. MultiHostPz reads only `installed.client_id` and `installed.client_secret`. A desktop client secret identifies the installed-app configuration but cannot be kept confidential in a desktop application. The normal repository release contains no Google credentials.

### Release with credentials embedded

To build a private EXE that already contains the Desktop OAuth configuration, keep the downloaded JSON outside Git and publish with its path:

```powershell
$env:MULTIHOSTPZ_GOOGLE_CLIENT_SECRETS_PATH = 'C:\private\client_secret.json'
dotnet publish src/MultiHostPz.App/MultiHostPz.App.csproj --configuration Release -p:PublishProfile=win-x64
```

The publish build embeds the JSON as an assembly resource. The resulting single-file EXE does not need the JSON file or the environment variable at runtime. Alternatively, place the file at `env\google-client-secrets.json` before publishing. The embedded client configuration can be extracted from a desktop EXE, so distribute that build only to the intended users and never commit the JSON.

Google authorization opens the system browser and uses authorization-code PKCE, a random-state check, a dynamic `127.0.0.1` loopback redirect, offline access, and encrypted refresh-token storage. Desktop OAuth clients support this dynamic loopback flow; no web-app redirect URI registration is needed. Tokens are encrypted for the current Windows user with DPAPI in `%LOCALAPPDATA%\MultiHostPz\google-drive.tokens`. Disconnect attempts to revoke the Google grant and always removes local Google token state.

To synchronize hosts using different Google accounts, share the top-level `MultiHostPz` folder in Google Drive with each other account. The app first finds the managed folder in the current account and then searches accessible shared folders before creating a new one. Each host must disconnect and connect again after upgrading to this version so Google grants the new `drive` scope. The `drive` scope is restricted; keep the OAuth app in testing for private use and register every test account.

MultiHostPz requests the restricted `https://www.googleapis.com/auth/drive` scope so a shared `MultiHostPz/snapshots` folder can be used by multiple Google accounts. It only queries and manages its own folders and files, identified by names and private `appProperties`; it does not display or synchronize unrelated Drive content. Do not manually duplicate or rename managed folders/files: ambiguous names fail safely instead of selecting an arbitrary file.

## Dropbox setup

1. In the Dropbox App Console, create a scoped-access app with **App folder** access.
2. Enable only `account_info.read`, `files.metadata.read`, `files.content.read`, and `files.content.write`.
3. Register the exact redirect URI `http://127.0.0.1:53682/oauth/callback/`.
4. Set the non-secret app key in the `MULTIHOSTPZ_DROPBOX_APP_KEY` environment variable, then restart MultiHostPz. Do not configure or distribute a client secret.

The Dropbox desktop authorization flow uses authorization-code PKCE and requests offline access. Refresh and access tokens are encrypted for the current Windows user with DPAPI in `%LOCALAPPDATA%\MultiHostPz\dropbox.tokens`, isolated from Google token state.

Dropbox stores paired ZIP and JSON files under the app-relative `/snapshots` folder. Upload writes the ZIP first and manifest last. Download stages and validates the selected latest valid pair before atomically publishing it into the local snapshots directory; it never restores automatically. Use the existing explicit local restore action afterward.

Both providers publish the ZIP first and the JSON manifest last. Google Drive uses bounded resumable chunks; both providers stream archive downloads into staging. A download is validated and published only into the local snapshots directory and never restores automatically. Host locking, conflict prevention, automatic restore, and multi-writer coordination are not implemented yet.

## Languages and settings

The interface supports English and neutral Spanish. Use the visible language selector to update all current labels and statuses immediately; subsequent confirmation dialogs use the selected language without restarting the application. On first use, Spanish is selected for a Spanish system UI culture; all other cultures use English.

Explicit language and cloud-provider choices are saved atomically in `%LOCALAPPDATA%\MultiHostPz\settings.json`. The language choice takes precedence over the system UI culture. A missing or invalid provider choice defaults to Google Drive; Dropbox remains available in the provider selector.

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

The complete publish payload is written to `artifacts/MultiHostPz-0.1.0-win-x64/publish/`. Run `MultiHostPz.exe` from that directory, or extract `artifacts/MultiHostPz-0.1.0-win-x64.zip` and run it there. The single-file executable contains the localized resources.

Local snapshot and restore work without cloud configuration. Google Drive actions require either the embedded build configuration or `MULTIHOSTPZ_GOOGLE_CLIENT_SECRETS_PATH`; Dropbox actions require `MULTIHOSTPZ_DROPBOX_APP_KEY`. Version `0.1.0` does not provide host locking, conflict prevention, or multi-writer coordination.

The release is unsigned and has no installer or MSIX package. Windows SmartScreen may therefore warn before the executable runs.
