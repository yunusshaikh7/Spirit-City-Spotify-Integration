# Spirit Sync - Spotify Integration for Spirit City: Lofi Sessions

> **Work in progress.** Spirit Sync is still being built. Early releases may work for basic testing, but features, install flow, and game integration are incomplete and can change between versions. Expect rough edges and breaking updates.

Spirit Sync is a Spotify integration mod for **Spirit City: Lofi Sessions**.

The target is the actual Spirit City music player: a Spotify option under the game's **Music -> Web Music Player** flow, plus a native Custom-audio proxy for the bottom song bar. The current game build is Unreal Engine with the WebBrowserWidget/CEF runtime, so this project is shaped around a local web bridge and a small runtime patcher.

## Current Status

This is an early, in-development build for the Steam Windows version of Spirit City. It can install a replacement launcher, start a local Spotify bridge, and add a `Spirit Sync` entry to **Music -> Web Music Player -> External** at runtime, but the integration is not finished yet.

Known limitations while development continues:

- The tile still uses one of the game's existing thumbnail images.
- Spotify playback is available in the in-game External web panel, which shows live track info. A native Custom-audio proxy also drives Spotify from Spirit City's bottom song bar: **play/pause, next, previous, shuffle, repeat, and volume** all control Spotify.
- The native proxy playlist is a set of silent "Spotify NN" tracks (one shared audio file via hard links). The bar shows the slot name ("Spotify 03"), not the live Spotify title, because the game caches the bar's rendered text and the title can't be updated live. For the live, always-current track view, use the in-game External -> Spirit Sync page.
- Native next/previous map to Spotify skip by detecting which proxy track the game is on. This is best-effort and works best with the bar's **loop** turned on (so skipping wraps around the slots). As with Spotify itself, pressing previous mid-track restarts the current track before stepping back.
- The in-game page defaults to PC/existing-device remote control; separate-device mode is experimental.
- Starting a built-in Spirit City music-list track pauses Spotify. Starting the Spirit Sync Custom-audio proxy track resumes Spotify.
- The installer and uninstaller are unsigned Windows EXEs included in the release zip.

## Requirements

- Windows.
- Steam version of **Spirit City: Lofi Sessions**.
- Spotify Premium.
- Node.js 20 or newer.
- A Spotify developer app with this redirect URI:

```text
http://127.0.0.1:8012/auth/callback
```

## Install

Download the latest `SpiritSync-*-windows.zip` from Releases and extract it.

Run:

```text
SpiritSyncInstaller.exe
```

The installer uses the default Steam install folder:

```text
C:\Program Files (x86)\Steam\steamapps\common\Spirit City Lofi Sessions
```

For a custom game folder, run the installer from PowerShell:

```powershell
.\SpiritSyncInstaller.exe -GameRoot "D:\SteamLibrary\steamapps\common\Spirit City Lofi Sessions"
```

On first setup, create `.env` in the extracted Spirit Sync folder:

```powershell
Copy-Item .env.example .env
notepad .env
```

Put your Spotify client ID in `.env`:

```env
PORT=8012
SPOTIFY_CLIENT_ID=your_spotify_client_id_here
SPOTIFY_REDIRECT_URI=http://127.0.0.1:8012/auth/callback
SPOTIFY_DEVICE_NAME=Spirit Sync
SPOTIFY_PLAYBACK_MODE=remote
```

Launch Spirit City through Steam. The first launch may open Spotify login at:

```text
http://127.0.0.1:8012/login
```

After login, open **Music -> Web Music Player -> External** in game and choose `Spirit Sync`.

Use the in-page device selector to choose `Spirit Sync`, Spotify Desktop, or another available Spotify Connect device.

For native bottom-bar text and play/pause, Spirit Sync uses Spirit City's **Music -> Custom** path. Spirit City reads the import folder on every launch, so Spirit Sync generates a silent proxy WAV from Spotify's current title and artist and points the Custom import at it:

```text
<Spirit City>\SpiritSync\CustomAudio\Spotify\<artist>
```

Spirit City uses the audio file name as the song title and the folder name as the second line. In most cases the proxy playlist appears under **Music -> Custom** automatically on the next launch with **no manual import**: the launcher seeds the Custom import folder when it is empty, already points inside `SpiritSync\CustomAudio\Spotify`, is a leftover Spirit Sync folder, or points at a folder that no longer exists. If you already use the Custom tab for your own real folder, Spirit Sync leaves it alone, and you can import the Spotify folder above manually. Open **Music -> Custom**, pick the generated artist playlist, and press play; pausing it pauses Spotify.

## Uninstall

Run this from the extracted release folder:

```text
SpiritSyncUninstaller.exe
```

It restores the original `SpiritCity.exe` from `SpiritCityBackup.exe` and removes the copied `SpiritSync` folder plus `spirit-sync.env`.

To restore the launcher but keep the copied files/config:

```powershell
.\SpiritSyncUninstaller.exe --keep-files
```

## Source Install

If you are running from source instead of the release zip, install Node.js 20+ and the .NET 8 SDK, then use:

```powershell
npm install
npm run build
.\scripts\install-spirit-sync-launcher.ps1
```

Source uninstall:

```powershell
.\scripts\uninstall-spirit-sync-launcher.ps1
```

Add `-RemoveSpiritSyncFiles` to also remove the copied `SpiritSync` folder and `spirit-sync.env`.

## Maintainer Notes

Implementation notes for automated contributors live in [AGENTS.md](AGENTS.md).

## What Exists

- Local Spotify OAuth login using Authorization Code with PKCE.
- Local Spotify Web Playback SDK device for the normal browser page; the in-game page can try it only in experimental `device` or `auto` mode.
- Browser player at `http://127.0.0.1:8012`.
- In-game oriented page at `http://127.0.0.1:8012/spirit-sync`.
- Playback modes:
  - `device`: only play through the `Spirit Sync` Web Playback SDK device.
  - `remote`: intentionally control Spotify Desktop or another existing Spotify device.
  - `auto`: try `Spirit Sync` first, then fall back to existing-device remote control.
- In-page Spotify Connect device selector with saved selection.
- Experimental Spotify web app handoff at `http://127.0.0.1:8012/spotify`.
- Playback API for devices, play, pause, next, previous, shuffle, repeat, transfer, status, and now-playing metadata.
- Replacement launcher project that can mirror the old mod's `SpiritCity.exe` / `SpiritCityBackup.exe` install layout.
- Runtime patcher that renames the first Web Music Player entry to `Spirit Sync` and points it at the local in-game Spotify page.
- Runtime monitor that pauses Spotify when a normal Spirit City native music-list track is playing.
- Native Custom-audio proxy support: Spirit Sync generates a multi-track silent proxy playlist (hard-linked, so ~1x one WAV on disk) and maps the native bar's play/pause, next, previous, shuffle, repeat/loop, and volume to Spotify. Next/previous are detected from process memory by which proxy track the game is playing. The proxy playlist appears under **Music -> Custom** for any artist name, including non-Latin scripts (e.g. Japanese or accented names), and all Spotify control stops automatically when Spirit City closes.
- Legacy `spirit-sync.env` compatibility for the old README's `ClientID`, `ClientSecret`, and `SpotifyUser` names.

## Spotify App Setup

In the Spotify Developer Dashboard, set the redirect URI to:

```text
http://127.0.0.1:8012/auth/callback
```

The bridge requests these scopes:

```text
streaming user-read-email user-read-private user-read-playback-state user-modify-playback-state user-read-currently-playing
```

This rebuild uses PKCE auth, so a client secret is not required even though `ClientSecret` is accepted in `spirit-sync.env` for compatibility.

## Run The Bridge

```powershell
npm install
Copy-Item .env.example .env
notepad .env
npm run dev
```

Put your Spotify client ID in `.env`, then open:

```text
http://127.0.0.1:8012/login
```

After login, use this URL for the Spirit City Web Music Player:

```text
http://127.0.0.1:8012/spirit-sync
```

## Legacy Config

The old README created this file in the Spirit City install folder:

```env
PORT=8012
ClientID=""
ClientSecret=""
SpotifyUser=""
```

This bridge will read `spirit-sync.env` from the current working directory, or from `SPIRIT_CITY_INSTALL_DIR` if the launcher script is used.

Optional device name:

```env
SpotifyDeviceName="Spirit Sync"
```

Optional playback mode:

```env
SpotifyPlaybackMode="remote"
```

Use `SpotifyPlaybackMode="remote"` to make Spirit Sync just remote-control Spotify on the PC. Use `SpotifyPlaybackMode="auto"` to try the separate `Spirit Sync` device first and fall back to remote control.

Optional in-game URL:

```env
SpiritSyncExternalUrl="http://127.0.0.1:8012/spirit-sync"
```

Set this to `http://127.0.0.1:8012/spotify` to test Spotify's own web UI inside Spirit City's external browser.

## Launcher Script

This script starts the bridge and then launches Spirit City without replacing the game's root `SpiritCity.exe`:

```powershell
.\scripts\launch-spirit-sync.ps1
```

## EXE Replacement Installer Details

The installer replaces the small root `SpiritCity.exe`, renames the original to `SpiritCityBackup.exe`, and adds a `SpiritSync` folder plus `spirit-sync.env`. It is reversible:

```powershell
.\scripts\install-spirit-sync-launcher.ps1
```

What it does:

- Builds the bridge.
- Publishes `tools\SpiritSyncLauncher` as a Windows launcher.
- Publishes `tools\SpiritCityRuntimePatch`.
- Copies the bridge to `<Spirit City>\SpiritSync`.
- Creates `<Spirit City>\spirit-sync.env` if it does not exist, seeded from local `.env` when possible.
- Copies the local Spotify token store when available, so you do not have to log in again.
- Renames `<Spirit City>\SpiritCity.exe` to `SpiritCityBackup.exe`.
- Copies the Spirit Sync launcher to `<Spirit City>\SpiritCity.exe`.

The replacement launcher starts the local bridge, opens Spotify login when needed, starts the real game, enables CEF debugging, and runs the runtime patcher. In the current Steam build this turns the first **Music -> Web Music Player -> External** tile into `Spirit Sync` and sends it to:

```text
http://127.0.0.1:8012/spirit-sync
```

The launcher also watches the embedded browser as a fallback. If any YouTube external-player page still opens, it redirects that in-game browser target back to Spirit Sync.

The default game path is:

```text
C:\Program Files (x86)\Steam\steamapps\common\Spirit City Lofi Sessions
```

## In-Game Entry Notes

The current Steam build stores the Web Music Player list in cooked Unreal data, not in a plain JSON file. Spirit Sync handles it at runtime by patching the loaded menu data after Spirit City starts.

Known related assets from the manifest:

```text
SAVE_Sessions_CustomMusic
CustomMusicDataToDisk
DATA_All_Music
ENUM_MusicPlayerModes
UI_Single_ExternalMusic
```

The supplied old `en.json` is launcher localization, not this in-game list. Use the snapshot script while testing future menu or save changes:

```powershell
.\scripts\snapshot-spirit-city-state.ps1 -Label before
```

Then create or edit a custom Web Music Player entry in Spirit City, close or save the game, and run:

```powershell
.\scripts\snapshot-spirit-city-state.ps1 -Label after -CompareTo .\.spirit-city-snapshots\before.json
```

Once we know the exact save file, cooked asset, or runtime table hook, the installer can seed a real "Spirit Sync" entry pointing at `/spirit-sync`.

## Local API

```text
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

Do not expose this server to your network. `/api/token` returns a short-lived Spotify access token for the local Web Playback SDK page.

Setup redirects are also available:

```text
GET /spirit-sync-setup
GET /setup
```
