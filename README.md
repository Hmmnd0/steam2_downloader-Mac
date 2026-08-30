# Steam2 Archive Browser

A browser for the [terarelease](https://de.steam2.download/) Steam2 content dump — 10 876 depots,
116 339 files, about 12.1 TiB. It shows what the archive holds, works out exactly which files a
given depot version needs, downloads them, and unpacks them.

Single executable. It starts a local server and opens your browser.

## Run

Download `steam2browser-win-x64.zip` from the
[latest release](https://github.com/extremebleem/steam2_downloader/releases), unzip, run
`steam2browser.exe`. Nothing else to install.

```
steam2browser.exe                 # opens http://127.0.0.1:5099
steam2browser.exe --port=6000     # different port
steam2browser.exe --no-browser    # do not launch a browser
```

Everything it writes lives in `steam2info/` next to the executable — the name cache, downloads
(`archive/blobs`, `archive/dats`) and extracted files (`extracted/`).

The release carries a snapshot of the whole catalog inside the executable, so the first run needs
no network at all. Fetching that index instead would mean 13 MB of `*_dates.txt` plus two ~20 MB
directory listings for the sizes — about 54 MB before anything appears. Compacted and gzipped the
same data is roughly 5 MB, most of it the 116 339 sha256 hashes, which cannot compress. Settings
has **Re-download index** when you want a fresher one.

## What it does

**Browse.** Every depot with its versions, dates, sizes and sha256. Search by depot id or by name;
quote the term for an exact match — `440` finds 4400 and 14400 too, `"440"` finds only 440.

**Plan a chain.** Depot data is stored as deltas, so extracting version *N* needs every version
below it. Where Valve reset a depot the same version number exists twice and the chain forks; the
planner follows the parent links recorded inside each blob and picks the right `.dat` by the exact
size the blob records, instead of downloading both branches.

**Download.** Parallel, resumable via HTTP range, verified against the sha256 that forms the fourth
part of every file name. Three mirrors (`de`, `ro`, `us`) serve byte-identical files; the app races
them on startup, picks the fastest and falls back to the others when one fails.

**Extract.** Built in — the blob container, manifest, file id tables, AES-128-CFB and zlib chunk
handling are all implemented in process. Output was verified byte-for-byte against the original
`extract.exe` on two depots, including one whose chain spans 146 versions.

**Name depots.** Two passes run together. One reads the manifest embedded in each depot's blob,
which needs no key and gives the real top-level directory names. The other asks the Steam store
about each depot id and overrides the name when it answers — expect few hits, because Steam2 depot
ids are not Steam3 app ids. Progress is appended to `steam2info/names.jsonl` after every record, so
it resumes where it stopped.

## Two things worth knowing

**A missing decryption key usually does not matter.** Only 4 629 depots appear in the key table, but
that table only covers the depots that are actually encrypted. Every file records a filemode: `1` is
plain zlib and needs no key, only `2` and `3` involve AES. In a sample of 40 depots absent from the
key table, 38 were checkable and every one of them was unencrypted. So the app asks for a key only
when a file being extracted really needs one, and marks a depot `no key` only when it is both
encrypted and unknown to the table. The original `extract.exe` refuses these depots outright,
before it ever looks at the filemodes.

**223 `(depot, version)` pairs have a blob but no dat**, and 62 depots have gaps in their chain.
Those are flagged as `incomplete`, because extraction will fail partway through.

## Build

Needs the .NET 10 SDK.

```
cd Steam2Browser
dotnet run
```

Release build:

```
dotnet publish Steam2Browser/Steam2Browser.csproj -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o out
```

## Credits

The archive, the original C++ extractor and the depot key table come from the terarelease dump.
Please mirror and seed it.

The depot names come from [dr3murr/steam2-winfsp](https://github.com/dr3murr/steam2-winfsp), whose
[`data/depot_labels.tsv`](https://github.com/dr3murr/steam2-winfsp/blob/main/data/depot_labels.tsv)
puts a real product name on 9 877 of the 10 876 depots here. That is painstaking work and it is what
makes the archive searchable — a manifest only ever yields folder names like `cstrike` or
`platform`. Depots it marks `Unknown / No Depot` fall through to this app's own naming passes.
