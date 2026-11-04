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
- **pin** your links so a metadata refresh (e.g. "Replace all metadata") can't overwrite them

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

### Pinning

Emby only stores one list of trailer links per item and has no "lock" for it, so a full
metadata refresh can replace your edits with whatever the metadata provider returns.
When **Pin these links** is ticked on save (the default), the plugin remembers your links
and puts them back automatically if a refresh changes them.

- Pinned items are listed on the Trailer Editor page.
- **Unpin** keeps the current links but stops protecting them.
- The *Restore pinned trailer links after metadata refreshes* setting turns the
  protection off for all items without forgetting your pins.

## HTTP API

All endpoints need an administrator's API key or access token.

| Method | Path | What it does |
| --- | --- | --- |
| GET | `/emby/TrailerEditor/Search?SearchTerm=name&Limit=30` | Find movies / TV shows |
| GET | `/emby/TrailerEditor/Items/{Id}` | Get an item's trailer links |
| POST | `/emby/TrailerEditor/Items/{Id}?Urls=…&Pin=true` | Replace the links. `Urls` is one link per line (URL-encoded); leave it empty to remove all trailers |
| DELETE | `/emby/TrailerEditor/Items/{Id}/Pin` | Unpin an item |
| GET | `/emby/TrailerEditor/Pinned` | List pinned items |

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
