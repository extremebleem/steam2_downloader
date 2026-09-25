# Steam2 Downloader

[![Latest release](https://img.shields.io/github/v/release/extremebleem/steam2_downloader?label=release&color=4c8b2b)](https://github.com/extremebleem/steam2_downloader/releases/latest)
[![Total downloads](https://img.shields.io/github/downloads/extremebleem/steam2_downloader/total?label=downloads&color=4c8b2b)](https://github.com/extremebleem/steam2_downloader/releases)
[![Stars](https://img.shields.io/github/stars/extremebleem/steam2_downloader?label=stars&color=4c8b2b)](https://github.com/extremebleem/steam2_downloader/stargazers)
[![Build status](https://github.com/extremebleem/steam2_downloader/actions/workflows/release.yml/badge.svg)](https://github.com/extremebleem/steam2_downloader/actions/workflows/release.yml)
![Windows and Linux, x64 and arm64](https://img.shields.io/badge/platform-windows%20%7C%20linux%20%7C%20arm64-555)
![11,452 lines by Claude Code](https://img.shields.io/badge/lines%20by%20Claude%20Code-11%2C452-d97757)
![530 lines from pull requests](https://img.shields.io/badge/lines%20from%20PRs-530-4c8b2b)
![0 lines by the maintainer](https://img.shields.io/badge/lines%20by%20the%20maintainer-0-555)

A desktop browser and downloader for the terarelease Steam2 content
dump: 10 876 depots, 116 339 files, 13.3 TB (12.1 TiB). It shows what the archive holds, resolves
which files a given depot version actually needs, downloads them, verifies them and unpacks them.

Steam2 was Valve's content system before Steam3 and CDN manifests. Its depots are stored as delta
chains of `.dat` payloads with `.blob` metadata beside them, so no single file is a complete
version — extracting version *N* needs every version below it. This tool exists because working
that out by hand across 58 441 blobs is not practical.

A self-contained folder for Windows and Linux, on x64 and arm64 — the executable with its runtime
beside it. Nothing to install. It starts a local server and opens your browser.

![Steam2 Downloader browsing depot 841 (Portal 2): the depot list, the delta chain planner with its download size estimate, and the version history expanded on v37 to show the four changed files.](assets/img1.png)

Every line here was written by [Claude Code](https://claude.com/claude-code) or arrived in a pull
request. The maintainer wrote none of it by hand: 11 452 of the 11 982 source lines came out of
Claude Code sessions — the archive format work, the extractor, the chain planner and the interface —
and the other 530 came from contributors, listed under [Credits](#credits). Counted over `.cs`,
`.js`, `.css`, `.html`, `.yml` and `.md`, excluding the depot key table, the catalog snapshot and
other data files.

## Install and run

These links always resolve to the newest build. No .NET install and no dependencies — the runtime
ships alongside the executable. Release notes and older builds are on the
[releases page](https://github.com/extremebleem/steam2_downloader/releases/latest).

Unzip the whole folder and keep it together; the executable will not run on its own. Updating means
unpacking over the old folder, which leaves `steam2info/` — your settings, downloads and extracted
files — where it is. Unpack somewhere else and the app starts fresh and will not find them.

**Windows** — [`steam2browser-win-x64.zip`](https://github.com/extremebleem/steam2_downloader/releases/latest/download/steam2browser-win-x64.zip).
Unzip and run `steam2browser.exe`.

```
steam2browser.exe                 # opens http://steam2downloader.localhost:5099
steam2browser.exe --port=6000     # different port
steam2browser.exe --port=80       # drops the port: http://steam2downloader.localhost
steam2browser.exe --no-browser    # do not launch a browser
```

**Linux** — [`steam2browser-linux-x64.zip`](https://github.com/extremebleem/steam2_downloader/releases/latest/download/steam2browser-linux-x64.zip),
or [`steam2browser-linux-arm64.zip`](https://github.com/extremebleem/steam2_downloader/releases/latest/download/steam2browser-linux-arm64.zip)
on arm64.
On arm64 machines (Raspberry Pi, arm64 VPS) take
[`steam2browser-linux-arm64.zip`](https://github.com/extremebleem/steam2_downloader/releases/latest/download/steam2browser-linux-arm64.zip)
instead — the x64 build will not start there.
Unzip, mark it executable once, then run it. The browser is opened through `xdg-open`, so on a
machine with no desktop session use `--no-browser` and open the address yourself.

```
chmod +x steam2browser
./steam2browser                   # opens http://steam2downloader.localhost:5099
./steam2browser --port=6000       # different port
./steam2browser --no-browser      # do not launch a browser
```

The address is a name rather than a number. Anything under `.localhost` is reserved and resolved to
loopback by the browser itself, so this needs no DNS, no hosts file entry and no administrator, and
changes nothing on the machine. `http://127.0.0.1:5099` keeps working and is printed alongside it.

Port 5099 is the default and nothing is taken that was not asked for. `--port=80` drops the port
from the address entirely, which Windows generally allows without elevation; it is worth knowing
that the app then holds the machine's HTTP port for as long as it runs, and that an ordinary user
on Linux is not permitted to bind it at all.

Everything it writes stays in `steam2info/` next to the executable: the name cache, downloads
(`archive/blobs`, `archive/dats`) and extracted files (`extracted/`).

Everything the app knows about the archive is built into it: the catalog of all 116 339 files with
their dates and sizes, a product name for 10 870 of the 10 876 depots, and what reading the
manifests inside 4 879 of them turned up. The first run is ready in well under a second and needs
no network for any of it. None of this can be refreshed and none of it needs to be — it was derived
from mirrors that have closed, and the archive stopped changing when they did.

## Features

### Browse depots

Every depot with its versions, dates, sizes and sha256 hashes. Search by depot id or product name;
quote the term for an exact match — `440` also finds 4400 and 14400, `"440"` finds only 440. Each
depot links to its [SteamDB](https://steamdb.info/) page. Dates render in your own locale.

### Resolve a delta chain

Where Valve reset a depot, the same version number exists twice and the chain forks. The planner
follows the parent CRC links recorded inside each blob and picks the right `.dat` by the exact size
the blob records, instead of downloading both branches. Reset depots are split into branches so a
fork does not read as one jumbled history.

### Skip the dats a version never reads

A chain is not the same thing as the bytes a version needs. Every file in a depot records which
version's `.dat` holds its payload, and a later version that rewrites a file takes that payload
over completely — so a `.dat` whose every file was overwritten again before your target version
contributes nothing to it and does not need downloading.

The planner works this out from the blobs, which are small, and drops those dats before the
download starts. On depot 241 at v56 that is 55 of 57 dats. The figure is shown before you commit
to anything, and a checkbox next to the version selector turns the whole thing off for archiving
the depot in full.

### Version history and diffs

Per version: which files were added, changed and removed, with the size delta for each, expandable
like a diff view. Comparison is by path, not by file id — Steam2 assigns a new file id when a file
is rewritten, so matching on ids reports every changed file as both new and removed.

### Search inside depots

A global file search over the manifests of every blob already on disk, grouped by depot. It answers
"which depot ships `client.dll`" without downloading a single `.dat`. Results say when the index is
behind the blobs on disk and offer to rebuild it.

### Download

From the swarm, and only from the swarm. There were three HTTP mirrors at `de`, `ro` and `us`
`.steam2.download`; the site has closed and the domain no longer resolves, so the torrent is what
the archive is now. Everything the app used to fetch over HTTP — the chain planner reading blobs,
the version history, the depot namer — asks the torrent instead, and reads from disk whatever is
already there.

Only the files a version actually needs are asked for: the piece picker is handed that selection
and never requests the other 13 TB. Downloads are resumable and verified against the sha256 that
forms the fourth part of every file name, the same check an HTTP download used to get.

A download the swarm cannot satisfy says so plainly — nobody sharing those files is online — rather
than failing file by file. That makes seeding matter: see below.

Free space on the download drive is checked before a download starts, and shown as a bar in
Settings. A chain that does not fit leaves the download button disabled with the reason on hover,
and the same check runs again inside the download itself, so a pack whose disk fills up on its
fourth depot stops with a clear message rather than a write error.

### Share what you have

The archive is 13 TB and the swarm is the only place it still exists. The mirrors that used to
carry it have closed, so every file now comes from somebody who chose to keep sharing it, and a
depot nobody seeds is a depot nobody can download. Sharing is therefore on by default: everything
already downloaded is offered back, and files finishing now join it without a restart. Uploads only
— nothing extra is ever fetched in order to share it.

A first run does not join the swarm until the notice explaining this has been answered. Nothing
reaches the network on behalf of the torrent engine before then.

Files are hard-linked into the engine's own directory rather than copied, so sharing a downloaded
depot costs no additional disk space, and that directory sits inside the download directory so the
links can never be asked to cross a volume.

Upload and download speed caps are in Settings, unlimited by default, and apply without a restart.
Sharing can be switched off on its own and the whole engine with it — though with the mirrors gone,
switching the engine off leaves no source at all, and the app says so rather than failing each
download in turn.

### Extract

Built in. The blob container, manifest, file id tables, AES-128-CFB and zlib chunk handling are all
implemented in process. Output was verified byte-for-byte against the original `extract.exe` on two
depots, one of them with a chain spanning 146 versions.

### Depot packs

A depot is not a game. Counter-Strike: Source is a client depot, a content depot and ten
localization depots, each at its own version — and that mapping is recorded nowhere in the archive,
because it lived on Steam's side and was never dumped. The blobs describe only what is inside one
depot.

So it is written by hand. [`apps/`](apps/) holds one JSON file per Steam appid listing the depots
and versions each build is made of; the app lists them as packs and queues every depot of a build
in one click, each as its own download with its own chain.

Contributions go through a pull request, and a check validates them against the real archive —
a build naming a depot or version that does not exist fails before it can be merged.
[`apps/README.md`](apps/README.md) has the format.

## Other tools for the same archive

This one downloads and extracts. If that is not what you are after, these are worth knowing about,
and two of them answer questions this app deliberately does not.

**[steambrowser.net](https://www.steambrowser.net)** — a web index of every file in the leak. It
opens the VPKs and reads what is inside them, so you can look through the contents of a depot in a
browser without downloading anything at all.

**[steam2-db.pages.dev](https://steam2-db.pages.dev/)** — a second web index of the same kind, and
a useful cross-check when one of them is missing something.

**[valves-2pacalypse](https://archive.org/details/valves-2pacalypse)** on archive.org — an archive
of everything notable to come out of the Steam2 depot leaks, beyond the depots themselves.

**[dr3murr/steam2-winfsp](https://github.com/dr3murr/steam2-winfsp)** — mounts `.blob` and `.dat`
archives as an ordinary filesystem through WinFsp on Windows or FUSE3 on Linux, decoding chunks on
demand. Nothing is extracted: it resolves depot ancestry, pairs the DATs, composes the overlay and
launches the build straight from the mounted tree. Run a game without unpacking it first. Its depot
label table is also where this app gets most of its product names — see [Credits](#credits).

## Things worth knowing about the archive

**A missing decryption key usually does not matter.** 4 758 depots appear in the key table, but that
table only covers depots that are actually encrypted. Every file records a filemode: `1` is plain
zlib and needs no key, only `2` and `3` involve AES. In a sample of 40 depots absent from the key
table, 38 were checkable and every one was unencrypted. So a key is requested only when a file being
extracted really needs one. The original `extract.exe` refuses these depots outright, before it ever
looks at the filemodes.

**223 `(depot, version)` pairs have a blob but no dat**, and 62 depots have gaps in their chain.
Those are flagged `incomplete`, because extraction fails partway through. 303 depots were reset at
some point.

## Build from source

Needs the .NET 10 SDK.

```
cd Steam2Browser
dotnet run
```

Release build:

```
dotnet publish Steam2Browser/Steam2Browser.csproj -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o out/win-x64

dotnet publish Steam2Browser/Steam2Browser.csproj -c Release -r linux-x64 --self-contained true \
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o out/linux-x64
```

Either target builds from either host, which is how the release workflow produces both from one
runner.

## Credits

The archive, the original C++ extractor and the depot key table come from the terarelease dump.
Please mirror and seed it.

Linux support was contributed by [SkyKingPX](https://github.com/SkyKingPX) in
[#6](https://github.com/extremebleem/steam2_downloader/pull/6).

The linux-arm64 build was contributed by [MatveyKostis](https://github.com/MatveyKostis), written by
[Hermes Agent](https://github.com/NousResearch/hermes-agent) — so a release now exists for a
Raspberry Pi or an arm64 VPS, which is the sort of small always-on box a 13 TB archive gets pulled
on. The SDK cross-compiles it on the existing Windows runner, so there is no second job and no ARM
hardware in CI; the artifact was checked on an actual aarch64 machine, where it serves the interface
and loads the catalog.

The piece picker that made sharing practical was contributed by
[Chopper1337](https://github.com/Chopper1337) in
[#8](https://github.com/extremebleem/steam2_downloader/pull/8). Selecting files one at a time
through MonoTorrent's own API costs about 4 ms each, which over 116 346 files is eight and a half
minutes before sharing can begin; the picker holds the selection itself instead. Bundling the
torrent into the release came from the same contributor in
[#7](https://github.com/extremebleem/steam2_downloader/pull/7), as did the resizable activity
footer in [#15](https://github.com/extremebleem/steam2_downloader/pull/15) and remembering the
depot list's sort, filter and mode between runs in
[#16](https://github.com/extremebleem/steam2_downloader/pull/16).

Depot names come from [dr3murr/steam2-winfsp](https://github.com/dr3murr/steam2-winfsp), whose
[`data/depot_labels.tsv`](https://github.com/dr3murr/steam2-winfsp/blob/main/data/depot_labels.tsv)
puts a real product name on 10 870 of the 10 876 depots here. That is painstaking work and it is
what makes the archive searchable at all — a manifest only ever yields folder names like `cstrike`
or `platform`. Depots it marks `Unknown / No Depot` fall through to this app's own naming passes,
which read the manifest inside each blob and ask the Steam store about each depot id.

That table is now shipped inside this app rather than fetched when needed. It used to be pulled
from that repository on demand, and the naming passes that fill its gaps needed blobs from the
mirrors; with the mirrors closed, a first run would otherwise show a list of numbers. The copy
here is unmodified.
