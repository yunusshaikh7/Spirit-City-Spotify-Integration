# AI Contributor Guide

This file is for automated contributors and future maintainers working on Spirit Sync. It explains what the project is actually trying to do, how the pieces fit together, and which assumptions are easy to get wrong.

## Project Goal

Spirit Sync is a Spotify integration for **Spirit City: Lofi Sessions** on Steam for Windows.

The goal is not just to run a standalone browser Spotify controller. The integration must appear inside Spirit City's own **Music -> Web Music Player -> External** flow. In the current Steam build, external web players do not appear in the game's bottom native song bar; they play through the external web panel. Spirit City's own external YouTube entries behave the same way, so treat that as the baseline until the native audio/player internals are better understood.

Use this product name in public files and releases:

```text
Spirit Sync - Spotify Integration for Spirit City: Lofi Sessions
```

Do not reintroduce names from abandoned predecessor projects. Public docs, release notes, commit messages, executable names, routes, folders, and UI text should use Spirit Sync.

## Architecture

Spirit Sync has four main layers:

1. Local Spotify bridge
2. In-game web player page
3. Replacement game launcher
4. Runtime menu patcher

### Local Spotify Bridge

The TypeScript server lives in `src/` and builds to `dist/`.

- `src/server.ts` defines the HTTP routes and local API.
- `src/spotify.ts` wraps Spotify Web API calls.
- `src/tokenStore.ts` stores refresh/access tokens on disk.
- `src/config.ts` loads `.env` and `spirit-sync.env`.
- `src/pkce.ts` handles Spotify Authorization Code with PKCE.

The bridge listens on `127.0.0.1:8012` by default. Keep the redirect URI on `127.0.0.1`; Spotify and the config loader intentionally avoid `localhost`.

Important routes:

```text
GET  /
GET  /spirit-sync
GET  /ingame
GET  /spotify
GET  /spotify-ui
GET  /login
GET  /auth/callback
GET  /api/config
GET  /api/status
GET  /api/now-playing
GET  /api/token
GET  /api/player/devices
POST /api/player/transfer
POST /api/player/play
POST /api/player/pause
POST /api/player/next
POST /api/player/previous
POST /api/player/shuffle
POST /api/player/repeat
```

`/api/token` exists for the browser/Web Playback SDK pages. Do not expose the bridge outside the local machine.

### Browser Pages

The static UI lives in `public/`.

- `public/index.html` is the normal browser page and can use Spotify's Web Playback SDK.
- `public/ingame.html` is served at `/spirit-sync` and is shaped for Spirit City's embedded CEF browser.
- `public/player.js` contains the shared UI/control logic.

Inside the game, playback defaults to `remote`: control Spotify Desktop or another existing Spotify Connect device through the local bridge and Spotify Web API. `SPOTIFY_PLAYBACK_MODE` / `SpotifyPlaybackMode` can still be set to `device` for the experimental Web Playback SDK device or `auto` to try that device before falling back to remote control. Do not rely on Spirit City's embedded CEF reliably becoming a Spotify playback device.

### Replacement Launcher

`tools/SpiritSyncLauncher` publishes as `SpiritCity.exe` for install into the game root.

The installer keeps the real small root launcher as:

```text
<Spirit City>\SpiritCityBackup.exe
```

Then it copies the Spirit Sync launcher to:

```text
<Spirit City>\SpiritCity.exe
```

Steam still launches `SpiritCity.exe`, but that executable now:

- starts the local Node bridge from `<Spirit City>\SpiritSync`;
- passes `SPIRIT_CITY_INSTALL_DIR`, `SPIRIT_SYNC_ENV_PATH`, and `TOKEN_STORE_PATH`;
- waits for the bridge to answer;
- opens Spotify login when needed;
- starts the original game via the backup launcher or shipping executable;
- starts the runtime patcher;
- watches CEF debug targets as a fallback and redirects external YouTube pages back to the configured in-game URL.

If Spirit Sync fails, the launcher should fall back to starting the game without the integration.

### Runtime Menu Patcher

`tools/SpiritCityRuntimePatch` scans the running Spirit City process and replaces the first known external music entry in memory:

- visible label becomes `Spirit Sync`;
- the external URL becomes `http://127.0.0.1:8012/spirit-sync` by default.

This is a runtime patch because the current game build stores the Web Music Player list in cooked Unreal data, not in an easy plain JSON file. Keep the patcher conservative and fail-safe. It should only run against the Spirit City process and should tolerate no matches.

The patcher is specific to the currently observed Steam build. If the game updates and the entry stops appearing, inspect the new external music strings and update the replacement table.

### Installer And Uninstaller

Release users should get root-level executables:

```text
SpiritSyncInstaller.exe
SpiritSyncUninstaller.exe
```

Those are thin .NET wrappers around:

```text
scripts/install-spirit-sync-launcher.ps1
scripts/uninstall-spirit-sync-launcher.ps1
```

The installer copies the built bridge, runtime patcher, launcher, package files, and dependencies into:

```text
<Spirit City>\SpiritSync
```

It creates or preserves:

```text
<Spirit City>\spirit-sync.env
```

The uninstaller restores `SpiritCity.exe` from `SpiritCityBackup.exe`. By default the EXE wrapper also removes the copied `SpiritSync` folder and `spirit-sync.env`; `--keep-files` restores the launcher while keeping those files.

## Config And Credentials

Never commit local credentials or token stores.

Ignored local files include:

```text
.env
.spotify-tokens.json
artifacts/
dist/
node_modules/
bin/
obj/
.spirit-city-snapshots/
```

Release users can configure Spotify from `.env` in the extracted release folder before running the installer. The installer can seed `<Spirit City>\spirit-sync.env` from that file.

Supported config names:

```env
PORT=8012
SPOTIFY_CLIENT_ID=...
SPOTIFY_REDIRECT_URI=http://127.0.0.1:8012/auth/callback
SPOTIFY_CLIENT_SECRET=
SPOTIFY_USER=...
SPOTIFY_DEVICE_NAME=Spirit Sync
SPOTIFY_PLAYBACK_MODE=remote
SPIRIT_SYNC_EXTERNAL_URL=http://127.0.0.1:8012/spirit-sync
```

Compatibility aliases are also accepted:

```env
ClientID=...
ClientSecret=
SpotifyUser=...
SpotifyDeviceName=Spirit Sync
SpotifyPlaybackMode=remote
SpiritSyncExternalUrl=http://127.0.0.1:8012/spirit-sync
```

Client secret is accepted for compatibility, but PKCE means it is not required.

## Build And Package

For source development:

```powershell
npm install
npm run build
dotnet build tools\SpiritSyncLauncher\SpiritSyncLauncher.csproj -c Release
dotnet build tools\SpiritCityRuntimePatch\SpiritCityRuntimePatch.csproj -c Release
dotnet build tools\SpiritSyncInstaller\SpiritSyncInstaller.csproj -c Release
dotnet build tools\SpiritSyncUninstaller\SpiritSyncUninstaller.csproj -c Release
```

For a release package:

```powershell
.\scripts\build-release-package.ps1 -Version 0.1.0
```

Expected output:

```text
artifacts\SpiritSync-0.1.0-windows.zip
```

The zip must include:

```text
SpiritSyncInstaller.exe
SpiritSyncUninstaller.exe
bin\SpiritCity.exe
bin\SpiritCityRuntimePatch.exe
README.md
spirit-sync.env.example
dist\
public\
scripts\
node_modules\
```

Do not commit `artifacts/`; attach the zip to a GitHub release.

## Test Checklist

Use this checklist before publishing a functional release:

1. Run `npm run build`.
2. Build the launcher, patcher, installer, and uninstaller .NET projects.
3. Run `.\scripts\build-release-package.ps1 -Version <version>`.
4. Confirm the zip has both install and uninstall EXEs at the root.
5. Confirm the zip does not include `.env` or `.spotify-tokens.json`.
6. Run `SpiritSyncInstaller.exe` from the extracted release folder.
7. Verify the game root contains `SpiritSync`, `spirit-sync.env`, replacement `SpiritCity.exe`, and `SpiritCityBackup.exe`.
8. Start the installed bridge or launch the game, then verify `http://127.0.0.1:8012/spirit-sync` returns the in-game page.
9. Open **Music -> Web Music Player -> External** in game and verify the `Spirit Sync` entry appears.
10. Run `SpiritSyncUninstaller.exe` and verify the original `SpiritCity.exe` is restored.
11. Reinstall afterward if the local machine should remain patched for continued testing.

Before release, scan committed files and the zip for:

- predecessor project names;
- local assistant/tool names;
- secrets, tokens, and private app credentials;
- accidental build output committed to git.

## Current Limitations

- The external tile currently reuses an existing game thumbnail.
- The in-game page defaults to Web API remote control of an existing Spotify device. Acting as its own Spotify Connect device is experimental and depends on Spotify's Web Playback SDK working inside Spirit City's embedded CEF browser.
- The native bottom song bar is not integrated yet.
- Runtime menu patching depends on strings observed in the current Steam build.

## Useful Investigation Tools

`scripts/snapshot-spirit-city-state.ps1` snapshots local Spirit City data before and after manual in-game changes. Use it when investigating whether a real custom Web Music Player entry can be seeded through saves or user data instead of runtime memory patching.

`tools/SpiritCityAssetProbe` is for exploring cooked Unreal assets. It is auxiliary and not part of the normal release flow.

## Safety Rules

- Treat the game install directory as user data.
- Preserve `SpiritCityBackup.exe` whenever it exists.
- Do not delete or replace files outside the resolved Spirit City game root.
- Keep installer and uninstaller operations reversible.
- Keep the local bridge bound to loopback.
- Make game-launch failure paths start Spirit City normally whenever possible.
- Keep public-facing text brand-clean and focused on Spirit Sync.
