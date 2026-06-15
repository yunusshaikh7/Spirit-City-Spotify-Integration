# AI Contributor Guide

This file is for automated contributors and future maintainers working on Spirit Sync. It explains what the project is actually trying to do, how the pieces fit together, and which assumptions are easy to get wrong.

## Project Goal

Spirit Sync is a Spotify integration for **Spirit City: Lofi Sessions** on Steam for Windows.

The goal is not just to run a standalone browser Spotify controller. The integration must appear inside Spirit City's own **Music -> Web Music Player -> External** flow, and native bottom-bar integration should use Spirit City's **Music -> Custom** audio path when possible. In the current Steam build, external web players do not appear in the game's bottom native song bar; they play through the external web panel. Spirit City's own external YouTube entries behave the same way, so treat External as the reliable browser-control baseline and Custom audio as the native-player proxy path.

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
POST /api/player/volume
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
- generates a silent multi-track Custom audio proxy under `<Spirit City>\SpiritSync\CustomAudio\Spotify\<artist>` (one real WAV plus hard links named `Spotify 01`..`Spotify NN`, so disk stays ~1x one WAV); the extra tracks let the native next/previous buttons map to Spotify (see "Native next/previous");
- seeds Spirit City's Custom import save (`ImportFolderAddress`) to the newest generated proxy folder when it is safe to do so: the save is empty, already points inside the Spirit Sync-owned proxy area, is a leftover Spirit Sync folder (path contains "SpiritSync"), or points at a folder that no longer exists on disk. It backs up the save first and never overwrites a real user folder that still exists, so users with their own Custom music are left alone (they import the Spotify folder manually);
- starts the runtime patcher;
- watches CEF debug targets as a fallback and redirects external YouTube pages back to the configured in-game URL.

If Spirit Sync fails, the launcher should fall back to starting the game without the integration.

### Runtime Menu Patcher

`tools/SpiritCityRuntimePatch` scans the running Spirit City process and replaces the first known external music entry in memory:

- visible label becomes `Spirit Sync`;
- the external URL becomes `http://127.0.0.1:8012/spirit-sync` by default.

In `watch` mode it also monitors:

```text
%LOCALAPPDATA%\SpiritCity\Saved\SaveGames\SCLS_MusicPlayer.sav
```

When a normal built-in native music list is playing, it asks the local bridge to pause Spotify. If the active native playlist is the configured Spirit Sync Custom-audio proxy folder, the patcher does the inverse: native play resumes Spotify and native pause pauses Spotify. Play/pause both directions are verified working. Native next/previous/shuffle/repeat are intentionally not remapped: the save does not record them (see "Verified Native Save Behavior"), so they cannot be observed.

Play state is read from `SCLS_MusicPlayer.sav` by detecting the presence of the `IsPlaying` property name, because the game serializes that property only while playing and omits it when paused. Do not "simplify" this to read a boolean value byte expecting 0/1 when paused — the property is absent, not false, when paused.

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
10. Open **Music -> Custom**, import the generated `SpiritSync\CustomAudio\Spotify\<artist>` folder if needed, and verify the native bottom bar shows the generated Spotify song title and folder/artist line.
11. Verify the native Custom proxy play/pause maps to Spotify play/pause, and that starting a normal built-in Spirit City list pauses Spotify.
12. Run `SpiritSyncUninstaller.exe` and verify the original `SpiritCity.exe` is restored.
13. Reinstall afterward if the local machine should remain patched for continued testing.

Before release, scan committed files and the zip for:

- predecessor project names;
- local assistant/tool names;
- secrets, tokens, and private app credentials;
- accidental build output committed to git.

## Current Limitations

These were verified in-game against the Steam build labeled **2.4.1** (June 2026). See "Verified Native Save Behavior" below for the evidence.

- The external tile currently reuses an existing game thumbnail.
- The in-game page defaults to Web API remote control of an existing Spotify device. Acting as its own Spotify Connect device is experimental and depends on Spotify's Web Playback SDK working inside Spirit City's embedded CEF browser.
- The native bottom song bar is integrated through Spirit City's Custom audio system, not by replacing the game's internal Spotify-free music data. The bar shows the proxy slot name (`Spotify 01`..`NN`), intentionally generic; the live, always-current Spotify track/artist is shown on the in-game External -> Spirit Sync page.
- **Live native title is a confirmed hard limit — do NOT re-attempt memory patching of the bar title.** Investigated exhaustively in 2.4.1 with three escalating experiments: (1) patching the track's source FStrings updates the Music *list* view but not the bottom bar; (2) patching the bar's `.wav` path string (both writable copies) does not change the bar on a track change; (3) patching *every* copy including read-only (via VirtualProtect) still does not change it. Decisive proof: a memory-wide search for the displayed string (e.g. `Spotify 03`) returns ZERO matches even while the bar shows it — the only matching bytes are the `.wav` file path. So the bar text is rasterized **Slate glyphs**, and its title is **baked at folder-import time** (on launch, from the filename) into a form that is not a reachable string; after import the bar only re-renders on a game-driven track change and reuses the cached title (it does not re-read the path). The only lever on the bar title is the filename at scan time (= launcher naming), which cannot track Spotify live. Spotify auto-advance also produces no native track change to ride. Naming the proxy after the launch track or the queue was considered and rejected: it shows a real song only until the first skip/auto-advance, then displays the WRONG song. Generic `Spotify NN` + the live External page is the chosen, honest design.
- **The whole native bottom bar drives Spotify** (verified in 2.4.1): play/pause, volume, shuffle, repeat/loop, next, and previous. Volume/shuffle/repeat come from `SCLS_MusicPlayer.sav` (`currentVolume`, `isShuffle`, `isLoop`); next/previous come from a process-memory scan (see "Native next/previous" below). Save-driven syncs are only-on-change with a per-session baseline, so entering the proxy never clobbers Spotify's existing state.
- Native built-in list playback pauses Spotify through the runtime patcher's save-file monitor (verified working). On a fresh launch the game may auto-start a built-in list, which keeps Spotify paused until you play the Spotify Custom proxy track instead.
- Runtime menu patching depends on strings observed in the current Steam build. The External entry's YouTube URL and "Peaceful Piano" label were both still present and patchable in 2.4.1.

## Verified Native Save Behavior

Confirmed by toggling native music in-game and diffing `%LOCALAPPDATA%\SpiritCity\Saved\SaveGames`:

- `SCLS_MusicPlayer.sav` serializes the `IsPlaying` BoolProperty **only while playing** and omits the whole property when paused (playing save ~1903 bytes, paused ~1863 bytes). So the presence of the `IsPlaying` property name is itself the play signal; the patcher matches that name. The class name `SAVE_Sessions_MusicPlayer_C` does not contain "IsPlaying", so there is no false match.
- The same save also carries `currentVolume` (DoubleProperty 0-1, updated when the volume slider moves), `isShuffle`, and `isLoop` (both BoolProperty, serialized **only while on**, same presence pattern as `IsPlaying`). The proxy controller forwards these to Spotify (`/api/player/volume`, `/api/player/shuffle`, `/api/player/repeat`; loop on -> repeat `context`). The save does NOT record the current track index — next/previous are not here (see below).
- Custom-imported playlists get `currentPlaylistID` in `[200, 300)` (e.g. "Liked songs" = 200, the first imported folder = 201). The Spotify-proxy detector keys on this range.
- `SCLS_CustomMusic.sav` stores only `ImportFolderAddress`. The track list is NOT stored — the game **auto-scans that folder on launch** and builds the custom playlist (folder name = playlist/album, file name = track title). This is why seeding the import folder makes the proxy playlist appear without a manual import.
- **`ImportFolderAddress` must follow UE's FString encoding or the proxy silently fails to appear.** An ASCII-only path serializes as UTF-8 with a **positive** length; a path containing ANY non-ASCII character (e.g. a Japanese or accented artist folder) MUST serialize as **UTF-16LE with a NEGATIVE** length (magnitude = code units including the null terminator). Writing non-ASCII as positive-length UTF-8 makes the game read the bytes as Latin-1, look for a folder that does not exist, and show only the built-in "Liked songs" slot (id 200). The launcher's writer (`TryReplaceTaggedString`) and both readers (launcher + patcher `TryFindTaggedString`/`ReadImportFolderAddress`) handle both encodings; correctness is byte-round-trip verified. ASCII artists happened to work before this fix because UTF-8 == ASCII.
- On a fresh launch the save's `currentPlaylistID` can briefly reflect the previous session until the game rewrites it, so the patcher's first reads after launch may lag the visible UI.

### Native next/previous (track-index detection)

Next/previous are NOT in any save, so the launcher makes the proxy a **multi-track** playlist of identically-silent tracks (one real WAV plus hard links, named `Spotify 01`..`Spotify NN`, created in order so the game lists them in that order). The runtime patcher's `NativeTrackController` then, every 600ms while the proxy is the active playlist, scans the game's memory and **counts how often each slot's name appears**: the game references the CURRENT track's name more than the others, so the most-referenced name is the current track. When that index changes the user pressed native next/previous, and the patcher forwards it to Spotify (`/api/player/next`/`/previous`). This is **coupled to the import-folder encoding fix above**: a non-ASCII proxy that does not import can never be the active playlist, so next/back silently no-op until the playlist appears (this was the actual cause of a "next/back stopped working" report — the proxy had a Japanese artist name and never imported).

Ranking signal: use the **display name** ("Spotify NN") count as primary. Measured live, the playing slot's title out-counts every other slot by a clean, stable margin and the just-played slot drops back to the baseline, whereas the file-name and full-path counts LINGER for the previous track after a skip (the audio subsystem keeps them resident) and can tie. Display+file summed is the fallback when the display margin is not decisive (e.g. the music panel is closed).

Performance: a naive scan of all private-committed memory is ~5GB across ~58k regions and took ~20s per poll, so skips lagged ~10-20s and felt dead. Three things keep it ~1s: a **prefix pre-filter** (one check for the shared `Spotify ` name prefix skips the per-slot counting on the near-all regions that lack it), a **16MB region-size cap** (the slot strings live in <=3MB heap pools; the multi-GB asset/render buffers never hold them), and a single **reused buffer** (no per-region allocation). The `trackscan` diagnostic mode (`SpiritCityRuntimePatch trackscan --process=<name> --spotify-proxy-folder=<path>`) prints per-slot display/file/path counts plus read volume against the live game — use it to re-tune if a game update moves the strings into larger regions.

Multi-step: several quick native presses land between scans, so the index can jump multiple slots at once; the patcher sends one Spotify skip per slot moved (the shorter way around the ring) instead of collapsing the jump into a single skip, so no presses are dropped. Verified in 2.4.1: native next ~1s latency, previous ~0.3s, three rapid nexts advanced Spotify multiple tracks.

Why this design: it depends only on the generic UE behavior that the current item is referenced most, not on build-specific memory offsets, so it survives game updates better than offset hooks. Caveats: names must be SHORT (the bar truncates long titles, which would break the count), the bar shows the slot name not the live song, it needs at least two tracks, loop-on makes skipping wrap, and shuffle makes native next pick a random slot (the patcher maps any change under shuffle to a single Spotify next). Disable with `--no-native-track-skip`.

### Patcher lifecycle (must stop with the game)

The runtime patcher runs in `watch` mode and drives the user's Spotify from the **last-written save** — play/pause each poll plus a 4s enforcement that re-pauses (built-in track active) or re-resumes (proxy active) so an external change can't desync it while the game is up. Because it acts on the save, not on a live game signal, it MUST stop when the game stops; otherwise an orphaned patcher keeps pausing/resuming Spotify every 4s for the full `--duration-sec` (12h), which the user experiences as **Spotify starting and pausing on its own**. The launcher kills the patcher on a clean game exit, but that path is skipped if the launcher itself is force-killed (e.g. Steam's Stop button) or the game is relaunched. Two defenses cover this: (1) every controller checks the game's `process.HasExited` at the top of each poll and self-terminates within ~1.5s of exit, independent of the launcher; (2) the launcher calls `StopOrphanedRuntimePatchers()` to kill any leftover `SpiritCityRuntimePatch` before starting a new one. Verified by force-killing the launcher and then the game: the orphaned patcher exited ~2s later. The 4s enforcement itself is correct **while the game runs** — do not remove it; only the missing liveness check was the bug.

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
