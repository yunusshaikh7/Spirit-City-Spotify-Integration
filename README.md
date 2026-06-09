# Spirit Sync - Spotify Integration for Spirit City: Lofi Sessions

Spirit Sync is a Spotify integration mod for **Spirit City: Lofi Sessions**.

The target is the actual Spirit City music player: a Spotify option under the game's **Music -> Web Music Player** flow. The current game build is Unreal Engine with the WebBrowserWidget/CEF runtime, so this project is shaped around a local web bridge that the in-game browser can load.

## Current Status

This is a functional first release for the Steam Windows version of Spirit City. It installs a replacement launcher, starts a local Spotify bridge, and adds a `Spirit Sync` entry to **Music -> Web Music Player -> External** at runtime.

Still rough:

- The tile still uses one of the game's existing thumbnail images.
- Spotify playback appears in the in-game External web panel, not the bottom native song bar. Spirit City's own YouTube External players behave the same way.
- The installer and uninstaller are unsigned Windows EXEs included in the release zip.

## Requirements

- Windows.
- Steam version of **Spirit City: Lofi Sessions**.
- Spotify Premium.
- Spotify desktop app open on the PC.
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
```

Launch Spirit City through Steam. The first launch may open Spotify login at:

```text
http://127.0.0.1:8012/login
```

After login, open **Music -> Web Music Player -> External** in game and choose `Spirit Sync`.

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
- Local Spotify Web Playback SDK device for the normal browser page.
- Browser player at `http://127.0.0.1:8012`.
- In-game oriented page at `http://127.0.0.1:8012/spirit-sync` that controls the open Spotify desktop app through the Web API instead of relying on CEF to become a Spotify speaker.
- Playback API for devices, play, pause, next, previous, shuffle, repeat, transfer, status, and now-playing metadata.
- Replacement launcher project that can mirror the old mod's `SpiritCity.exe` / `SpiritCityBackup.exe` install layout.
- Runtime patcher that renames the first Web Music Player entry to `Spirit Sync` and points it at the local in-game Spotify page.
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
```

Do not expose this server to your network. `/api/token` returns a short-lived Spotify access token for the local Web Playback SDK page.

Setup redirects are also available:

```text
GET /spirit-sync-setup
GET /setup
```
