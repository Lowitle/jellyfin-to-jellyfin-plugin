# Installing Jellyfin to Jellyfin from scratch

This walkthrough assumes two Jellyfin servers:

- **Source** — holds the media files and the user account the plugin logs in as.
- **Destination** — runs the plugin, owns the libraries your users browse, and proxies playback.

The plugin never copies media: it writes `.strm` files that point at the destination server, and the
destination streams the bytes from the source on demand. Both servers must be able to talk to each
other over HTTP(S), and the destination needs a writable folder.

Current version: **0.3.1.1** (requires Jellyfin **10.11.x**).

---

## 1. On the source server: create the mirror account

1. Dashboard → **Users** → **+ Add user**. Name it something recognisable, e.g. `j2j-mirror`.
2. Leave **"Allow this user to manage the server"** (administrator) **off**. Mirroring only reads the
   catalog, so a normal user is enough — and it is the whole point of this design.
3. In the same dialog grant access **only to the libraries you want mirrored**. A library the user
   cannot see is a library the plugin cannot mirror.
4. Under the user's **Profile** tab:
   - **Allow remote connections to this server** → on.
   - **Two-factor authentication** → must be **off** (the plugin logs in with user + password only).
   - Password must not be set to expire.
5. Note the URL the destination will use, e.g. `http://192.168.1.20:8096` — no trailing slash. If the
   destination is not on the same LAN, this is where the decision from
   [Network options](#8-network-options) applies.

> Do **not** use an API key. Jellyfin API keys are server-wide and always authenticate with
> administrator rights. Versions up to 0.2.0.0 accepted one; 0.3.0.0 removed that path completely.

## 2. On the destination server: install the plugin

**From the plugin repository (recommended)**

1. Dashboard → **Plugins** → **Repositories** → **+**.
2. Name: `Jellyfin to Jellyfin` · URL: `https://lowitle.github.io/jellyfin-to-jellyfin-plugin/manifest.json`
3. **Save**, go to **Catalog**, find *Jellyfin to Jellyfin*, **Install**.
4. Restart Jellyfin when prompted.

**Manually**

1. Download `jellyfin-to-jellyfin-plugin_0.3.1.1.zip` from the
   [releases page](https://github.com/Lowitle/jellyfin-to-jellyfin-plugin/releases).
2. Create `<jellyfin-config>/plugins/JellyfinToJellyfin/` and extract `JellyfinToJellyfin.dll` into it
   (Docker: `/config/plugins/JellyfinToJellyfin/`).
3. Restart Jellyfin.

Check it worked: Dashboard → Plugins → **Jellyfin to Jellyfin** shows version `0.3.1.1` and opens the
configuration page.

## 3. Prepare the output folder

Pick a folder **as seen by the Jellyfin process** (inside the container, if you use Docker), for
example `/media/jellyfin-to-jellyfin`.

```bash
mkdir -p /media/jellyfin-to-jellyfin
chown <jellyfin-uid>:<jellyfin-gid> /media/jellyfin-to-jellyfin   # the user Jellyfin runs as
```

- It must be writable by Jellyfin, otherwise the sync fails with a permission error.
- It should be a volume that survives container restarts (mount it from the host).
- Do not point it inside an existing library folder; mirror libraries get their own subfolders.

## 4. Configure the plugin

Dashboard → **Plugins** → **Jellyfin to Jellyfin**.

**General section**

| Field | What to put |
| --- | --- |
| Base output folder | The folder from step 3, e.g. `/media/jellyfin-to-jellyfin`. |
| Local server URL used inside .strm files | Leave empty to auto-detect. Fill it in only if auto-detection picks an address your players cannot reach, e.g. `http://192.168.1.10:8096`. |
| Scheduled sync interval (hours) | `12` is a good default, `0` disables the schedule. |
| Run a sync when the server starts | Optional; handy after host reboots. |

**Remote instances** → **Add instance**

| Field | What to put |
| --- | --- |
| Name | Free text, e.g. `Home NAS`. |
| Instance id | Leave empty to generate one. It appears in folder and URL names, so a short value such as `nas` is nicer if you set it by hand. |
| Remote base URL | `http://192.168.1.20:8096` |
| Remote user name | The account from step 1. |
| Remote password | Its password. |
| Output folder (optional) | Empty = use the global base folder. |
| Local server URL for .strm (optional) | Empty = use the global value. |
| Download artwork | Posters/backdrops/logos. Slower sync, nicer libraries. |
| Remove stale .strm/.nfo files | Deletes files whose source item disappeared. Off by default; safe to enable once you trust the setup. |
| Enabled | On. |

Then:

1. **Test connection** — it must report the source server's name/version and the user it logged in
   as. A `401` here means a wrong password, 2FA enabled on that account, remote connections disabled,
   or a disabled/revoked account.
2. **Load remote libraries** — the list shows exactly what that user can see.

**Libraries to mirror** fills itself with one row per remote library as soon as that list loads.
Every new row starts **Off**: tick the ones you want mirrored locally, tweak the optional columns if
needed, and press **Save**.

| Column | Meaning |
| --- | --- |
| On | Mirror this library. Unticked rows are ignored by the sync. |
| Remote library | Source library. Only what the configured user can see is listed. |
| Folder name | Folder created under the instance base path. Empty (`Auto: <library>`) = the remote library name. |
| Merge with local library | `Create new library` (default) creates a local library pointing at the folder on the first sync; picking an existing library instead adds the folder to that library. The text box under the select names the library to create (empty = the remote library name). |
| Type | Collection type of the library that gets created. `Auto: <type>` = the remote library's own type. |

Loading the remote libraries again keeps what you already configured — rows are matched by library
id first and by name second — and only appends rows for libraries that are new, also Off.

Finally click **Save**.

## 5. First sync

- **Sync now** starts a background sync; **Refresh status** shows the counters
  (`strmWritten`, `nfoWritten`, `imagesWritten`, `duplicatesSkipped`, `staleRemoved`) plus any
  errors and warnings.
- Or use Dashboard → **Scheduled Tasks** → **Jellyfin to Jellyfin mirror sync** → **Run**.

Check the result on disk — you should see `{base}/{instanceId}/{Folder}/…` with `.strm` and `.nfo`
files inside.

If a mapping created a local library, it now appears under Dashboard → Libraries but may still look
empty until it scans: run **Dashboard → Scheduled Tasks → Scan Media Library** (or the library's own
*Scan library files*). Creating the library by hand and pointing it at the mirror folder, with the
right content type, works just as well.

## 6. Validate playback

1. Play a mirrored movie or episode from a normal user account on the destination.
2. Seek a bit — seeking works because the plugin forwards `Range` and cache validators to the source.
3. Transparency check: `GET /JellyfinToJellyfin/stream/{instanceId}/{itemId}?k=video` on the
   destination must answer `206 Partial Content`. The generated `.strm` files contain only that URL,
   never a credential.

## 7. Troubleshooting

| Symptom | Cause / fix |
| --- | --- |
| `No remote user configured` in the sync report | Username or password is empty in the instance: fill both and **Save**. |
| Test connection fails with `401` | Wrong password, 2FA enabled, remote connections disabled for that user, or the account is disabled. |
| Test connection fails with a connection error | Destination cannot reach the source URL: firewall, tunnel down, wrong port or scheme. |
| `Load remote libraries` lists less than expected | The user has no access to those libraries on the source; grant it in the source's user settings. |
| Items missing from the mirror | Only playable items are mirrored: dead database entries (file gone, `404` when streamed) are skipped, as are `BoxSet`, `Playlist`, `Book` and `Photo` items. |
| Sync stops with a permission error | The output folder is not writable by the Jellyfin user (step 3). |
| Playback starts slowly or buffers | The destination proxies the original file; check the link between both servers. The plugin never transcodes. |
| Playback works for you but not for remote users | The URL written into the `.strm` files (auto-detected or `Local server URL`) must be reachable from where they play. |
| Test connection fails but `curl` works in your shell | The plugin runs inside the Jellyfin process, which can sit in a different network namespace (Docker sidecar, container). Test from inside that container: `docker exec <jellyfin> curl -sS <BaseUrl>/System/Info/Public`. |

## 8. Network options

Three ways to connect destination → source, from safest to riskiest:

| Option | Source exposed to internet | Effort | Notes |
| --- | --- | --- | --- |
| Same LAN | no | trivial | `BaseUrl` = private IP, nothing else to do. |
| Private tunnel (WireGuard, Tailscale, NAS VPN) | no | low | Destination joins the tunnel; `BaseUrl` = tunnel IP or MagicDNS name. Best balance for two servers you own. |
| Public endpoint (reverse proxy + HTTPS) | **yes** | medium | Works, but the source's login page is now on the internet: HTTPS only, no public admin accounts, 2FA on everything except the mirror user, rate limits/fail2ban. |

The plugin only makes outbound calls from destination to source, so the source never needs to reach
the destination — which is what makes the tunnel option so convenient.

### Example: Tailscale (or any WireGuard tunnel)

The plugin has no notion of a network option: it just needs a URL it can reach. Swapping
`https://source.example.com` or `192.168.1.10:8096` for a tailnet address is the whole change.

1. Bring the tunnel up on both servers. With Docker on the destination, the usual shape is a
   `tailscale/tailscale` sidecar and Jellyfin joining its namespace (`network_mode: "service:tailscale"`),
   publishing Jellyfin's port on the sidecar (`ports: [ "8096:8096" ]`) so LAN clients keep working.
2. Set the instance `BaseUrl` to the address the destination sees on the tailnet:
   `http://100.101.102.103:8096`, or the MagicDNS name `http://source-host:8096`. Write the scheme
   and the port — nothing is inferred.
3. Leave `Local server URL` pointing at the destination. The `.strm` files must send the players to
   the destination, never to the tunnel. With the sidecar setup the destination only sees the tunnel
   interface, so pin it explicitly to its LAN URL instead of trusting the auto-detection.
4. Delete the port-forward on the source's router. From then on only the tailnet reaches the source,
   and the mirror user's password never crosses the internet unprotected.
5. Verify from inside the Jellyfin container rather than from a laptop shell:
   `docker exec <jellyfin> curl -sS <BaseUrl>/System/Info/Public`.

Plain HTTP is fine inside the tunnel: WireGuard already encrypts the hop, and calling `:8096`
directly skips the reverse proxy, which would otherwise present a certificate naming the public
host and not the tailnet address. If you want HTTPS anyway, `tailscale serve https / http://127.0.0.1:8096`
on the source gives a valid `https://<host>.<tailnet>.ts.net` name. A self-hosted WireGuard, a NAS
VPN or ZeroTier works the same way with their own addresses.

Running Jellyfin in an LXC container on Proxmox? The cheapest path is Tailscale on the node, not in
the container: install it on the Proxmox host, then let the container's traffic to the tailnet be
forwarded and masqueraded (`net.ipv4.ip_forward=1` plus
`iptables -t nat -A POSTROUTING -o tailscale0 -j MASQUERADE`). The container needs no changes:
`BaseUrl` becomes the source's tailnet address, and if the source advertises its own LAN subnet as a
subnet route, its plain LAN address works too. Installing Tailscale *inside* the LXC also works but
needs the TUN device: `modprobe tun` on the node (and `tun` in `/etc/modules`), then in
`/etc/pve/lxc/<id>.conf` the device allow rule (`lxc.cgroup2.devices.allow: c 10:200 rwm`), the bind
mount (`lxc.mount.entry: /dev/net/tun dev/net/tun none bind,create=file`) and `net_admin`/`net_raw`
removed from `lxc.cap.drop`. Tailscale's userspace mode is not a substitute here: it only offers a
SOCKS/HTTP proxy, and the plugin dials the source directly.
