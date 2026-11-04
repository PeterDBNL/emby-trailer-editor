# Trailer Editor for Emby

An Emby Server plugin that lets you edit the **trailer links** stored in the metadata of
movies and TV shows, for example to swap a wrong or dead YouTube trailer for the right one.

Emby's built-in metadata editor doesn't let you change these links. This plugin adds a
**Trailer Editor** page to the server dashboard where you can:

- search for a movie or TV show by name
- add, change or remove its trailer links
- paste full YouTube links (`youtube.com/watch?v=…`, `youtu.be/…`, `/embed/…`, `/shorts/…`)
  or just the 11-character video id; they are all saved as a standard
  `https://www.youtube.com/watch?v=ID` link that Emby apps can play
- see right away whether each YouTube link will actually **play in Emby** (see below)
- **pin** your links so a metadata refresh (e.g. "Replace all metadata") can't overwrite them
- **scan your whole library** for titles without trailers and trailer links that are broken
  or won't play

Requires Emby Server **4.8 or newer**.

## Install

1. Get `TrailerEditor.dll`:
   - from the **Actions** tab of this repo (latest "Build" run → *Artifacts* → `TrailerEditor`), or
   - from a GitHub release, or
   - build it yourself (see below).
2. Copy `TrailerEditor.dll` into the Emby `plugins` folder:
   - Linux: `/var/lib/emby/plugins`
   - Windows: `%AppData%\Emby-Server\programdata\plugins`
   - Docker: `/config/plugins` inside the container
   - Synology/other NAS: the `plugins` folder inside Emby's data folder
3. Restart Emby Server.

## Use

1. Open the Emby dashboard (as an administrator).
2. Click **Trailer Editor** in the side menu (also under *Plugins → Trailer Editor*).
3. Search for a title, click it, edit the links and click **Save**.

Each link shows a YouTube thumbnail preview and an **Open** button so you can check it.
Leave every link box empty and save to remove all trailers from the item.

### Checking that a trailer will play

Emby plays YouTube trailers through YouTube's embedded player, and YouTube refuses to play
some videos there. Emby then shows *"No compatible streams are currently available"*.
Every YouTube link is checked automatically as soon as it is shown or pasted, in two steps:

1. **Server check.** Your Emby server asks YouTube about the video. This catches embedding
   turned off by the uploader, age-restricted, private and deleted videos.
2. **Playback test.** The page plays the video for a moment, muted, in a tiny invisible
   YouTube embedded player: the same kind of player Emby uses. This also catches videos
   where a **copyright owner blocks playback in other apps**, which is common for fan edits
   and re-uploads of studio trailers and is invisible to the server check.

The result shows under the link:

- ✓ **It plays in an embedded player, so it should play in Emby.**
- ✗ **Won't play**, with the reason
- ? **Couldn't check**, e.g. YouTube didn't answer or it isn't a YouTube link

Links are tested one at a time and results are remembered for an hour. The **Test** button
runs the same playback test in a visible player, so you can watch the trailer yourself.
Saving a link marked ✗ asks for confirmation first, but is still allowed.

### Pinning

Emby only stores one list of trailer links per item and has no "lock" for it, so a full
metadata refresh can replace your edits with whatever the metadata provider returns.
When **Pin these links** is ticked on save (the default), the plugin remembers your links
and puts them back automatically if a refresh changes them.

Pinned items are listed on the **Trailer Library** page (see below). **Unpin** in the editor
keeps the current links but stops protecting them.

### Trailer Library page

Open it from the side menu, or with the *Library scan & pinned items* button on the
Trailer Editor page. It has:

- **Library scan**: goes through every movie and TV show and reports
  - *Won't play*: YouTube links that are deleted, private, have embedding turned off, or
    (optionally) are age-restricted
  - *No trailers*: titles without trailer links (optionally ignoring titles that have local
    trailer files)
  - *Invalid link* and *Duplicate link*

  Press **Scan now**, or let it run on its weekly schedule (change it under
  *Scheduled Tasks → Trailer Editor → Scan trailer links*). Progress is shown on the page and
  in Scheduled Tasks. Click the summary buttons to filter the results, and click a title to
  open it in the editor. Saving a title in the editor removes its results until the next scan.
- **Scan results**, **Pinned items** and **Settings**: collapsible sections. The lists load
  only when opened, are searchable by title and are paged 50 at a time, so they stay fast
  with tens of thousands of titles.

**Large libraries.** The scan pauses between YouTube requests (1 second by default, about
3,600 links an hour) so YouTube doesn't block your server. A link's result is reused for 30
days, so after the first full scan, weekly scans only check new and older links. If YouTube
starts refusing requests, the scan slows down, and after 25 failures in a row it stops checking
links for that run and says so. All of this can be changed under *Settings*.

**Copyright blocks (optional, needs an open browser).** Some videos, often fan edits and
re-uploads of studio trailers, are blocked by a copyright owner from playing in other apps.
Only a browser can detect this. Tick *Test playback in this browser to catch copyright blocks*
under Settings, and the Trailer Library page plays each scanned link for a moment, muted, in a
hidden YouTube player. **This only runs while the Trailer Library page stays open and visible
in a browser window.** If you close it, switch tabs or the computer sleeps, testing pauses and
continues where it left off the next time you open the page. Blocked links appear in the scan
results as *Won't play*. Links that already failed the server check, or were tested within the
re-check period, are skipped.

**Fewer tests needed.** Playback results are shared:
- every link tested on the Trailer Editor page (automatically or with Test) counts, and
- with *Learn from trailers played in Emby* on (the default), a YouTube trailer that Emby
  reports as playing is marked as working. Failed trailers never reach the server (they fail in
  the browser), so this only finds working links. Each reported trailer play is written to the
  Emby log as `Trailer Editor: trailer playback reported`.

## HTTP API

All endpoints need an administrator's API key or access token.

| Method | Path | What it does |
| --- | --- | --- |
| GET | `/emby/TrailerEditor/Search?SearchTerm=name&Limit=30` | Find movies / TV shows |
| GET | `/emby/TrailerEditor/Items/{Id}` | Get an item's trailer links |
| POST | `/emby/TrailerEditor/Items/{Id}?Urls=…&Pin=true` | Replace the links. `Urls` is the links separated by spaces (URL-encoded). To remove all trailers send `Clear=true` instead of `Urls` |
| GET | `/emby/TrailerEditor/Check?Url=…` | Check whether a link will play in Emby |
| DELETE | `/emby/TrailerEditor/Items/{Id}/Pin` | Unpin an item |
| GET | `/emby/TrailerEditor/Pinned?SearchTerm=&StartIndex=0&Limit=50` | List pinned items, one page at a time |
| GET | `/emby/TrailerEditor/Scan` | Scan status and summary |
| POST | `/emby/TrailerEditor/Scan` | Start a library scan |
| POST | `/emby/TrailerEditor/Scan/Cancel` | Stop a running scan |
| GET | `/emby/TrailerEditor/Playback/Queue?Limit=10` | Next scanned YouTube links that need a browser playback test |
| POST | `/emby/TrailerEditor/Playback/Result?VideoId=…&Status=ok\|error\|unknown&Message=…` | Record a browser playback test result |
| GET | `/emby/TrailerEditor/Scan/Issues?Kind=&SearchTerm=&StartIndex=0&Limit=50` | Scan results, one page at a time. `Kind`: `Unplayable`, `NoTrailers`, `InvalidLink` or `DuplicateLink` |

`{Id}` is the item's id as shown in Emby web URLs (`…/item?id=12345`); item Guids also work.

Example:

```sh
curl -X POST -G "http://localhost:8096/emby/TrailerEditor/Items/12345" \
  --data-urlencode "api_key=YOUR_KEY" \
  --data-urlencode "Urls=https://youtu.be/dQw4w9WgXcQ" \
  --data-urlencode "Pin=true"
```

## Build

Needs the .NET SDK 6 or newer.

```sh
dotnet test
dotnet build TrailerEditor/TrailerEditor.csproj -c Release
# output: TrailerEditor/bin/Release/netstandard2.0/TrailerEditor.dll
```

## Notes

- Only remote (online) trailer links are edited. Local trailer files, such as
  `Movie-trailer.mkv` or a `trailers` folder next to the movie, are not affected.
- TV show trailers are set on the series, not on seasons or episodes.
- If you change an item's trailers some other way while it is pinned, the next metadata
  refresh puts the pinned links back. Unpin it first if you don't want that.
