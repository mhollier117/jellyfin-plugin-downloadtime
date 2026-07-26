# Download Time

A Jellyfin plugin that tells you what's **missing** from your library:

- **Gaps** — episodes you skipped or missed inside seasons you already have
- **New** — episodes that aired but you haven't downloaded yet
- **Missing movies** — released entries in film franchises you collect (via TMDB collections)

Everything is matched by the **provider IDs your library items are already
identified with** (TheTVDB / AniDB / TMDB) — never by title guessing — so it
works with renamed files and season-merged anime layouts.

## Features

- **Missing Media dashboard page** — its own entry in the admin dashboard
  sidebar: stat tiles that double as filters, search and sorting, poster cards
  with per-season episode breakdowns, one-click mute per show, movie
  collection cards, and scan buttons with live progress.
- **Poster badges** — an amber count badge on series posters and a summary
  line on series pages (requires the free
  [File Transformation](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation)
  plugin; badges quietly disable themselves without it).
- **Native missing-episode placeholders** *(optional, off by default)* —
  greyed-out entries inside each season using Jellyfin's built-in
  missing-episode rendering. Placeholders are marker-tagged, auto-removed when
  the real file arrives, and a **Reset** scheduled task deletes every one the
  plugin ever created. Viewers must enable **"Display missing episodes"** in
  their Jellyfin display settings to see them.
- **Fail-safe by design** — a source outage or page-format change can never
  make everything look missing; per-show errors are isolated and reported.
- Grace period ("don't nag until it's been out N hours"), specials toggle,
  per-show mute list, movie release buffer, request throttling, disk-cached
  catalogs (daily for airing shows, weekly for ended ones).

## How each item is checked

The plugin routes every show/movie to **the source it is identified with**
(folder tags like `[tvdbid-123]` / provider IDs from your metadata plugins):

| Identified with | Data source | Key needed? |
|---|---|---|
| TheTVDB (most TV) | TheTVDB public episode listings, TVmaze fallback | **No** — works out of the box |
| AniDB (anime) | AniDB HTTP API, matched by **episode ID** (immune to merged/split seasons) | Your own **AniDB client registration** (free, see below) |
| TMDB (some shows + all movie franchises) | TMDB API | Free **TMDB API key** |

## Requirements

- Jellyfin **10.10.7**, **10.11**, or **12.0** (a build for each is in every release)
- Optional, per feature:
  - **TMDB API key** (free) — for tmdb-identified shows and movie franchises
  - **AniDB client registration** (free) — for anime
  - **File Transformation plugin** — for poster badges only

## Installation

1. Dashboard → Plugins → Repositories → add:
   `https://raw.githubusercontent.com/mhollier117/jellyfin-repo/master/manifest.json`
2. Catalog → **Download Time** → Install, then restart Jellyfin.
3. Open **Dashboard → Plugins → Download Time** for settings, and the
   **Missing Media** sidebar entry for the report.

## Setting up the API credentials

### TMDB (movies + tmdb-identified shows)

1. Create a free account at [themoviedb.org](https://www.themoviedb.org).
2. Avatar → **Settings → API** → request a key (Developer) — you want the
   short hex **"API Key" (v3 auth)**, *not* the long Read Access Token.
3. Paste it into **Download Time settings → TMDB API key** and Save.

### AniDB (anime) — register your own client

AniDB doesn't use API keys; it uses **registered client names**, and every
registration is tied to the AniDB account that created it. **Register your
own — don't borrow someone else's string** (their account would carry
responsibility for your traffic, and a shared string getting banned kills
everyone using it). The anime lane stays politely disabled until you set this.

1. Create a free account at [anidb.net](https://anidb.net).
2. Go to [anidb.net/software/add](https://anidb.net/software/add) and add a
   "software" entry (name it anything, e.g. *My Jellyfin Tools*).
3. On your new software's page, click **add client**: choose the **HTTP API**,
   pick a unique lowercase **client string** (e.g. `yournamejf`), version `1`.
4. Enter that client string and version in **Download Time settings →
   AniDB client name / version** and Save.

> The profile-page "UDP API Key" is unrelated — the plugin never uses it.

## Settings reference

| Setting | Default | Meaning |
|---|---|---|
| Scan TV / anime / movie lanes | on | enable detection per library type |
| TMDB API key | *(blank)* | tmdb items are skipped (with a note) until set |
| AniDB client name / version | *(blank)* / 1 | anime lane inert until you register your own client |
| Grace period (hours) | 24 | how long after airing before an episode counts as missing; `0` = immediately |
| Movie release buffer (days) | 90 | a franchise movie counts as missing this long after its theatrical date |
| Include specials | off | count Season 0 / specials |
| Create placeholders | off | write native missing-episode entries (see Features) |
| Poster badges / series summary | on | web UI injection (needs File Transformation) |
| Muted item ids | *(empty)* | shows/movies to stop reporting (use the mute button on the Missing Media page) |
| Request throttle (ms) | 2000 | pacing for scraped/rate-limited sources |

Scanning runs as the scheduled task **"Scan for missing media"** (daily at
06:00 by default), or on demand from the Missing Media page.

## Privacy / network behavior

The plugin talks only to: `thetvdb.com` (public pages), `api.tvmaze.com`,
`api.anidb.net` (your client name), `api.themoviedb.org` (your key). One
request per show per scan, cached to disk, hard-throttled. Nothing about your
library leaves your server beyond the provider IDs being looked up.

## Building from source

```bash
dotnet test                                                  # unit suite
dotnet build src/Jellyfin.Plugin.DownloadTime -c Release \
  -p:JellyfinVersion=12.0     # or 10.11 / 10.10.7
```

## License

MIT
