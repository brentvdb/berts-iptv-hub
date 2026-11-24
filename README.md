# Bert's IPTV Hub

One IPTV (Xtream) account, shared with every TV in the house.

- **`hub/`** is a Windows service on the server PC. It logs into the provider once and relays that single
  connection to every TV, without transcoding. It also keeps the channel list and EPG up to date in the
  background and works out now/next for every channel.
- **`tv/`** is the Android TV app (Bert's IPTV Hub). It gives you channel browsing, shared favourites,
  now/next, and a player where every TV stays in sync.

## How it behaves

- Open the app on any TV while something is playing, and it goes straight into that stream.
- Pick a channel on any TV, or use Up/Down in the player, and every TV switches to it.
- Press **Back** in the player on any TV, and every TV leaves the player and the provider connection closes.
- Favourites (hold **OK** or press **☰ Menu** on a channel) are shared by all TVs.
- **4K/UHD:** the app has VLC's engine built in. In **Auto** mode (the default), 4K/UHD channels, and any channel
  the box's built-in decoder can't handle, play through the VLC engine inside the app and stay in sync with the
  other TVs. The VLC app isn't needed.
- **TV guide:** for channels your provider has no guide for (e.g. Sky Sports, Premier Sports), the hub merges free
  public guides from epgshare01 (UK and South Africa by default). Check or fix the matches on the hub's
  **Guide matching** page at `http://<pc-ip>:8090/epg`.
- The provider only ever sees one device.

## Downloads

Every push builds both parts. Open **Actions**, pick the latest run, and download from **Artifacts**:

| Workflow | Download | Install on |
|---|---|---|
| Hub installer (Windows) | `BertsIptvHub-Setup-<version>.exe` | the server PC |
| TV app APK (Android) | `BertsIptvHub-TV-<version>.apk` | each Android TV / Google TV box |

To make a proper release, run both workflows manually with **Publish a GitHub release** ticked. Both files
then appear together under **Releases → v<version>**.

### Installing the APK on a TV box

The easiest way is the **Downloader** app (by AFTVnews) on the TV, or `adb install BertsIptvHub-TV-<version>.apk`
from a PC. Every build is signed with the same key (`tv/signing`), so a new APK installs over the old one and
keeps your settings.

On first launch the app searches your network for the hub. Pick it, then enter the TV password you chose in
the hub installer.

## Versions

The `VERSION` file in the repo root (for example `1.1.0`) sets the version for everything:

- installer and APK file names, and build artifact names (`…-build12`)
- the hub exe's file version, the hub status page and `/api/version`
- the app's version name (shown in the app footer and Settings). The APK's version code is the CI run number.

## Repo layout

```
VERSION                     one version for hub + app
hub/src                     .NET 8 hub service
hub/installer               Inno Setup installer, neon artwork (make_art.py also draws the TV icon/banner)
hub/tests                   end-to-end tests against a fake one-connection provider
tv/                         Android TV app (Kotlin, Media3)
.github/workflows           hub-installer.yml, tv-apk.yml
```

See [hub/README.md](hub/README.md) for hub settings and the API the app uses.
