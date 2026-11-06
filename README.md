# Jellyfin to Jellyfin

[![Build](https://github.com/Lowitle/jellyfin-to-jellyfin-plugin/actions/workflows/build.yml/badge.svg)](https://github.com/Lowitle/jellyfin-to-jellyfin-plugin/actions/workflows/build.yml)

A Jellyfin plugin that mirrors the libraries of one or more **remote Jellyfin servers** into your
local server as a `.strm` tree, and proxies the original files so they play here without ever
downloading a full copy.

It is meant for the common "two servers, one library" case: a source server that holds the files
(friends/family NAS, seedbox, second home) and a destination server that only needs to browse and
play them with its own users, libraries and metadata.

## How it works

```
                        +-------------------------------+
                        |  Destination Jellyfin server  |
 /Items queries  <----  |  JellyfinToJellyfin plugin    |
 (remote user login)   |  1. writes {mount}/{Library}/ |
                        |     Movie (Year)/Movie.strm   |
                        |     .../tvshow.nfo, poster.jpg|
 Player  ------------>  |  2. GET /JellyfinToJellyfin/  |
 (anonymous)            |     stream/{instance}/{item}  |
                        +---------------+---------------+
                                        |
                                streams Range/bytes
                                        |
                        +---------------v---------------+
                        |     Source Jellyfin server    |
                        +-------------------------------+
```

1. The plugin logs in on the remote server as a regular user and reads the library listing (`/Items`)
   with that session token. The password never leaves the server and the token is kept in memory.
2. It writes one `.strm` per playable item, plus `.nfo` metadata and (optionally) artwork, into a
   folder that a local library points at.
3. Each `.strm` contains only a URL of the destination server
   (`http://<your-server>/JellyfinToJellyfin/stream/<instance>/<itemId>?k=video`).
4. When a user plays that file, the plugin proxies the request to the source server, forwarding
   `Range`, `ETag` and cache validators, so seeking and direct play work as usual.

## Requirements

- Jellyfin **10.11.x** (built against `10.11.6`, `net9.0`).
- The destination server must be able to reach the source server over HTTP(S).
- A path that the Jellyfin process can write to (local disk, SMB/NFS mount, etc.).
- A user account on every source server with access to the libraries to mirror. A **non-admin** account
  is enough and strongly recommended: the plugin only reads the catalog. Two-factor authentication must
  be off for that account and its password must not expire.

## Installation

New to the plugin? The step-by-step walkthrough for a fresh setup — source user, plugin install,
output folder, mappings, first sync and troubleshooting — is in **[docs/INSTALL.md](docs/INSTALL.md)**.
Short version:

### From the plugin repository

1. Dashboard → Plugins → Repositories → **+**
2. Add `https://lowitle.github.io/jellyfin-to-jellyfin-plugin/manifest.json`
3. Catalog → *Jellyfin to Jellyfin* → Install, then restart Jellyfin.

### Manual

1. Download `jellyfin-to-jellyfin-plugin_<version>.zip` from the
   [releases page](https://github.com/Lowitle/jellyfin-to-jellyfin-plugin/releases).
2. Create `<jellyfin-config>/plugins/JellyfinToJellyfin/` and extract
   `JellyfinToJellyfin.dll` into it.
3. Restart Jellyfin.

### From source

```powershell
dotnet build .\JellyfinToJellyfin\JellyfinToJellyfin.csproj -c Release
copy .\JellyfinToJellyfin\bin\Release\net9.0\JellyfinToJellyfin.dll "$env:LOCALAPPDATA\jellyfin\plugins\JellyfinToJellyfin\"
```

## Configuration

The UI lives in Dashboard → Plugins → **Jellyfin to Jellyfin**.

| Setting | Default | Description |
| --- | --- | --- |
| Default base path | `/media/jellyfin-to-jellyfin` | Root folder for the mirror. Each instance writes to `{root}/JellyfinToJellyfin/{instanceId}/` unless the instance overrides it. |
| Server URL override | *(empty)* | Base URL written into `.strm` files. Empty means auto-detect from the server's network interfaces. |
| Sync on startup | `false` | Run one mirror sync when the server starts. |
| Schedule interval (hours) | `12` | Interval of the scheduled task. `0` disables it. |

Per **source instance**:

| Setting | Description |
| --- | --- |
| `Id` | Short identifier used in folders and stream URLs. Generated when you leave it empty. |
| `Name` | Display name. |
| `BaseUrl` | Source server URL, e.g. `http://192.168.1.20:8096`. |
| `Username` | Source user name. Use a dedicated **non-admin** account that only sees the libraries you mirror. |
| `Password` | Password for `Username`. Stored in the plugin configuration XML, like any other plugin secret. |
| `BasePath` | Optional output root for this instance. |
| `LocalServerUrl` | Optional per-instance override of the URL written into `.strm` files. |
| `DownloadImages` | Download posters/backdrops/logos/banners from the source. |
| `RemoveStaleFiles` | After a successful sync, delete `.strm`/`.nfo` files (and artwork, when artwork download is on) that no longer map to a source item. Default `false`. |
| `Enabled` | Include this instance in scheduled and global syncs. |

Per **library mapping** (in the UI, press **Load remote libraries** and the table fills itself with one
row per remote library; new rows start unticked):

| Setting | Description |
| --- | --- |
| `RemoteLibraryId` / `RemoteLibraryName` | Source library to mirror. |
| `FolderName` | Folder created under the instance base path. Defaults to the remote library name. |
| `CreateNewLibrary` | Create a local Jellyfin library pointing at the folder on first sync. |
| `TargetLibraryName` | Name of the local library (created or matched by name). |
| `TargetCollectionType` | Collection type used when creating the library. Defaults to the remote one. |
| `TargetLibraryId` | Attach the folder to an existing local library instead of matching by name. |
| `Enabled` | Include this mapping in the sync. |


## Generated layout

```
/media/jellyfin-to-jellyfin/JellyfinToJellyfin/homelab/
  Movies/
    Blade Runner 2049 (2017)/
      Blade Runner 2049 (2017).strm
      Blade Runner 2049 (2017).nfo
      poster.jpg  fanart.jpg  logo.png  banner.jpg
  TV/
    Bichos - La verdadera aventura (2024)/
      tvshow.nfo  poster.jpg  fanart.jpg
      Season 01/
        season.nfo
        Bichos - La verdadera aventura S01E01 - La gran ciudad.strm
        Bichos - La verdadera aventura S01E01 - La gran ciudad.nfo
  Music/
    ABBA/
      ABBA (1975)/
        album.nfo
        01 - Mamma Mia.strm
        02 - Hey, Hey Helen.strm
```

Behaviour worth knowing:

- Dates (`<premiered>`, `<aired>`) are normalised to `yyyy-MM-dd` in local time.
- Album folders get the release year appended so same-titled releases do not collide; when two
  source folders still sanitise to the same name, the second one gets a short item-id suffix
  (e.g. `documentals [957b29ef]`) instead of overwriting the first.
- Files whose path is already claimed by another item (true duplicates in the source library) are
  counted as `duplicatesSkipped` in the sync report rather than silently overwritten.
- Discovery uses the remote **user** account, so Jellyfin only returns items that account can see and
  actually play. Database entries whose file is gone (they answer `404` when streamed) are left out,
  which keeps the mirror free of dead `.strm` files — on a real library the mirror is therefore
  smaller than an admin view of the same library, and every generated `.strm` plays.
- With `RemoveStaleFiles` enabled, a sync deletes mirror files that no longer belong to any source item
  and then prunes the folders left empty. The pass is deliberately conservative: it only runs when the
  sync produced mirror paths and no item failed, it only touches generated `.strm`/`.nfo` files (plus
  artwork when artwork download is on) inside the mapped folder, and anything else you keep there
  (extra `.srt`, `.mkv`, `.txt`, hand-made artwork) is never touched. Deletions are reported as
  `staleRemoved`.

## Scheduled task

*Dashboard → Scheduled Tasks → **Jellyfin to Jellyfin mirror sync*** (`JellyfinToJellyfinMirrorSync`).
It honours the startup flag and the interval configured above, and can be triggered manually.


## HTTP API

All `/JellyfinToJellyfin/api/*` endpoints require an admin (elevated) session.

| Method | Route | Purpose |
| --- | --- | --- |
| `GET` | `/JellyfinToJellyfin/api/config` | Current configuration. |
| `POST` | `/JellyfinToJellyfin/api/config` | Replace and persist the configuration. |
| `POST` | `/JellyfinToJellyfin/api/test` | Body `{ BaseUrl, Username, Password }`: check a remote server, returns its name/version and the user used. |
| `POST` | `/JellyfinToJellyfin/api/libraries` | Same body: list the remote libraries. |
| `GET` | `/JellyfinToJellyfin/api/local-libraries` | List local libraries (for mapping targets). |
| `POST` | `/JellyfinToJellyfin/api/sync?instanceId=` | Start a background sync (all enabled instances when omitted). |
| `GET` | `/JellyfinToJellyfin/api/status` | Running state and last report. |

`GET /JellyfinToJellyfin/stream/{instanceId}/{itemId}?k=video|audio` is the anonymous playback
proxy that the generated `.strm` files point at.

The last report returned by `/api/status` exposes `strmWritten`, `nfoWritten`, `imagesWritten`,
`duplicatesSkipped` and `staleRemoved` counters plus the `errors`/`warnings`/`notes` lists.

## Security notes

- `.strm` files contain **no** credential: only a URL of your own server, so sharing the media mount
  does not leak secrets.
- The stream endpoint is anonymous by design (players do not send a token for `.strm` contents);
  keep the destination server on a trusted network or behind a reverse proxy with its own access
  control.
- **The plugin only logs in as a normal user; it never uses an API key.** Jellyfin API keys are
  server-wide, created in the admin dashboard, and always authenticate with administrator rights on
  the source server — far more access than mirroring needs. A regular account can only read the
  libraries you grant it. The plugin logs in through `POST /Users/AuthenticateByName`, keeps the
  session token in memory, shares it between syncs and the stream proxy and re-authenticates on
  `401`. Library discovery uses `GET /UserViews`, because `/Library/VirtualFolders` requires
  elevation.
- The account must be allowed to connect remotely, must **not** have two-factor authentication
  enabled and its password must not expire, otherwise the scheduled sync starts failing with a login
  error.
- The password is stored in the plugin configuration XML under the Jellyfin config folder, with the
  same protection as any other plugin secret. It is sent to the plugin API in the request body —
  never in a URL — so it does not leak into access logs or reverse proxy logs.
- The plugin only needs server-to-server reachability. A private tunnel (WireGuard, Tailscale, the
  NAS VPN server) plus an internal `BaseUrl` is safer than exposing the source server to the
  internet. If it must stay public: HTTPS only, keep administrator accounts off the public endpoint
  (or behind 2FA), and revoke the credential immediately if it leaks.


## Development

```
JellyfinToJellyfin/         plugin (net9.0, Jellyfin.Controller/Model 10.11.6)
tests/JellyfinToJellyfin.Tests/  xUnit tests for PathSanitizer, NfoBuilder, MirrorGenerator, SyncReport and remote auth
tools/ConfigCheck/          offline + live smoke tests (DTO round-trip, NFO, live /Items, mirror)
tools/Inspect/              dumps the Jellyfin model shapes the DTOs must match
tools/page-lint.js          static check of the embedded configuration page
tools/update-manifest.js    adds a version entry (with checksum) to manifest.json
```

```powershell
dotnet build .\JellyfinToJellyfin.sln -c Release
dotnet test .\JellyfinToJellyfin.sln -c Release
dotnet run --project .\tools\ConfigCheck\ConfigCheck.csproj            # offline checks
node .\tools\page-lint.js                                              # config page lint
```

Live end-to-end check against a real server:

```powershell
$env:J2J_REMOTE_URL    = 'http://192.168.1.20:8096'
$env:J2J_REMOTE_USER   = '<user>'
$env:J2J_REMOTE_PW     = '<password>'
$env:J2J_LIBRARY_INDEX = '0'                   # optional, 0-based library index
$env:J2J_MIRROR_DIR    = "$env:TEMP\j2j-mirror" # optional, writes a real mirror
$env:J2J_DUMP_DIR      = "$env:TEMP\j2j-dump"   # optional, dumps the /Items JSON
dotnet run --project .\tools\ConfigCheck\ConfigCheck.csproj -- --live
```

## Releasing

```powershell
# 1. Bump the version in Directory.Build.props and commit it, e.g. 0.3.1.0.
# 2. Tag that commit with the same version and push the tag.
git tag -a v0.3.1.0 -m 'Jellyfin to Jellyfin 0.3.1.0'
git push origin v0.3.1.0
```

Pushing a `v*` tag runs `.github/workflows/release.yml`, which builds the plugin, zips
`JellyfinToJellyfin.dll`, publishes the GitHub release, and updates `manifest.json` with the new
version, target ABI and MD5 checksum. GitHub Pages then serves the updated manifest used by the
plugin repository URL above.

## Limitations

- Only direct play of the original file is supported; the plugin does not transcode.
- `BoxSet`, `Playlist`, `Book`, `Photo` and `PhotoAlbum` items are skipped (no original-file stream).
- Mirrored metadata is written fresh each sync; manual edits to generated `.nfo` files are
  overwritten. Files left behind by removed items are only cleaned up when `RemoveStaleFiles` is
  enabled for that instance (it is off by default).
- Library/mapping changes take effect on the next sync.

## License

GPL-3.0 — see [LICENSE](LICENSE).

