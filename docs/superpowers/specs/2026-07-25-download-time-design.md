# Download Time — Design Spec (2026-07-25)

Jellyfin plugin that detects missing media: episode gaps in owned shows, newly
aired episodes not yet downloaded, and unowned movies in owned film franchises.
Read-only by default; optionally writes native virtual "missing episode"
placeholders.

- **Name:** Download Time
- **Assembly:** `Jellyfin.Plugin.DownloadTime`
- **GUID:** `4d557ba6-d562-4209-9a04-b782775dc2ff`
- **Targets:** Jellyfin 10.10.7 / 10.11 / 12.0 (three ABIs via `-p:JellyfinVersion=`, same as Ronin/Filler Skip)
- **Repo:** `mhollier117/jellyfin-plugin-downloadtime`, distributed through `mhollier117/jellyfin-repo` manifest

## 1. Core principle (the Ronin lesson)

Never trust or infer from local season/episode numbers. Every local episode
carries per-episode provider IDs stamped at scan time (`Tvdb`, `AniDB`, `Imdb`
— verified present library-wide on VMHOLLIER). Detection joins local and
remote catalogs on those IDs whenever the remote source exposes them; local
numbering (including Ronin merge/split renumbering) can never corrupt results.

Routing follows **the source each item is identified with** (folder tag /
dominant ProviderId): `tvdbid` → TVDB lane, `anidb` → AniDB lane, `tmdbid` →
TMDB lane. Precedence when multiple IDs are present: items in an anime
library → AniDB; otherwise the source named in the folder tag; otherwise
Tvdb > Tmdb > Imdb.

## 2. Detection lanes

### 2.1 TVDB lane (most of D:\TV)
- Fetch `https://www.thetvdb.com/series/{idOrSlug}/allseasons/official` —
  public page listing all episodes (season/episode numbers + air dates) in one
  request per series. Scrape with HtmlAgilityPack, browser User-Agent,
  throttled (Ronin precedent: `ResolveEpisodeNumber.cs` scrapes the same site).
- Match owned↔remote **by TVDB episode ID first** — the all-seasons page's
  per-episode links (`/series/{slug}/episodes/{episodeId}`) carry the same
  episode IDs stamped on local items as `ProviderIds["Tvdb"]` (verified
  2026-07-25 against the live page for American Gods) — with `(season,
  episode)` tuple matching as fallback for local items lacking the ID.
- Numeric-ID → slug resolution via `thetvdb.com/dereferrer/series/{id}`
  (301 redirect to the slug URL; verified live).
- **Fallback:** if scraping fails (markup change, HTTP error), automatically
  use TVmaze `lookup/shows?thetvdb={id}` → `/shows/{id}/episodes` (free,
  keyless, independent database cross-referenced by TVDB ID). Report marks the
  series as served-by-fallback.

### 2.2 AniDB lane (D:\Anime)
- Fetch AniDB HTTP API (`http://api.anidb.net:9001/httpapi?request=anime&aid={id}...`)
  with a registered client name — returns the entry's full episode list
  (AniDB episode IDs, epno, type, air dates) in one request.
- Match owned↔remote **by AniDB episode ID** — immune to Ronin
  merge/split/renumbering.
- Only `type=1` (regular) episodes count; specials follow the IncludeSpecials
  setting.
- Hard throttle (min 2 s between requests) + aggressive caching (see 2.4).
  AniDB bans aggressive clients; pacing is non-negotiable.

### 2.3 TMDB lane (tmdbid-identified shows + all movies)
- Requires a free user-supplied TMDB API key; without it the lane is skipped
  and the report says so per item.
- Shows: `/tv/{id}` + `/tv/{id}/season/{n}` → episode lists with air dates;
  match by `(season, episode)`.
- Movies: `/movie/{id}` → `belongs_to_collection`; `/collection/{id}` →
  member list with release dates. Missing = released member (theatrical date
  + MovieReleaseBufferDays elapsed) whose TMDB ID is not on any owned movie.

### 2.4 Caching & scheduling
- Remote catalogs cached as JSON under the plugin data directory.
- TTL: continuing/airing series 1 day; ended/completed series 7 days; movie
  collections 7 days. Cache bypassed by the manual "Scan now (full refresh)".
- Scan runs as a Jellyfin scheduled task, default daily, manual anytime.
- Per-item fetch failures are isolated: logged, recorded in the report,
  never abort the scan.

## 3. Classification (DiffEngine — pure, no I/O)

For each series: `owned` = non-virtual local episodes; `remote` = lane catalog.

- **Missing** = remote episode with a known air date where
  `airedAt + GraceHours < now` and no owned match. `GraceHours` default 24,
  adjustable, **0 disables the grace window entirely**.
- **Air-time rule:** sources with an exact timestamp (TVmaze `airstamp`) use
  it verbatim. Date-only sources (TVDB page, AniDB, TMDB) define
  `airedAt = airdate 23:59 UTC` — an episode has "aired" once its air *date*
  has fully elapsed in UTC. One rule, applied everywhere, unit-tested at the
  boundary.
- **Owned-match rule:** a local episode owns remote number N if
  `IndexNumber ≤ N ≤ (IndexNumberEnd ?? IndexNumber)` — multi-episode files
  (S01E01-E02) count for every number they span. ID-based lanes match on ID
  sets and ignore numbering entirely.
- **Gap** = missing episode that aired on/before the newest owned episode's
  air date (old logic missed it).
- **New** = missing episode that aired after the newest owned episode
  (aired, not yet downloaded).
- Unaired or undated remote episodes are never missing.
- Season 0 / specials excluded unless `IncludeSpecials`.
- Muted items (`ExcludedItemIds`) are skipped and shown in a collapsed
  "muted" report section.
- Movies: single **Missing** state (no gap/new split) with release date shown.

## 4. Display surfaces

### 4.1 Dashboard report page (`configPage.html`)
Settings + report: per-item rows (title, gap count, new count, expandable
episode list with air dates and lane/fallback used), totals, last-scan time,
fetch-error list, Scan Now / Full Refresh buttons. Backed by the REST API.

### 4.2 Poster badges (web injection)
`web/badges.js` + CSS registered with the File Transformation plugin at
startup (Ronin `TransformationPatch` pattern; degrade to no-op with a log line
when File Transformation is absent). Amber count badge on series cards; on a
series detail page, a "N missing — X gaps, Y new" line. Data from
`GET /DownloadTime/Report` (user-session auth). Toggleable per surface.
Movie/collection badges deferred to v1.1.

### 4.3 Native virtual episodes (opt-in, default OFF)
- Creates `Episode { IsVirtualItem = true }` rows for missing episodes,
  modeled on Jellyfin 10.4's retired `MissingEpisodeProvider` (reference copy
  in repo docs): deterministic item ID, `season.AddChild`, metadata refresh;
  virtual season creation only when the season doesn't exist locally.
- Every placeholder gets `ProviderIds["DownloadTime"] = <lane episode key>` —
  we only ever delete items bearing our marker.
- Numbering follows the **local scheme**, inferred by anchoring on owned
  episodes' providerId→(S,E) mapping (handles Ronin-merged libraries).
  If the scheme can't be inferred confidently (no anchor neighbors, unnumbered
  local items — cf. `HasInvalidContent` guard in the reference), skip creation
  for that series and note it in the report.
- Lifecycle: Jellyfin 12.0's own `SeriesMetadataService.RemoveObsoleteEpisodes`
  keeps well-numbered virtual episodes and auto-deletes them when a physical
  twin appears (verified in source, `SeriesMetadataService.cs:147-184`).
  Our scan reconciles the rest: delete our placeholders that vanished from the
  remote catalog, moved numbering, or when the feature/lane is disabled.
- `ResetTask` (manual-only scheduled task): delete every item carrying our
  marker. Escape hatch for uninstall.
- Users must enable Jellyfin's per-user "Display missing episodes" setting to
  see placeholders; the config page states this.
- Ships disabled until the live E2E (section 7) passes.

## 5. REST API

- `GET /DownloadTime/Report` — last scan result (JSON): items, missing lists,
  classifications, errors, timestamps. Auth: any logged-in user (badges need
  it); settings mutation admin-only as usual.
- `POST /DownloadTime/Scan?fullRefresh=` — trigger scan (admin).

## 6. Configuration

| Setting | Default |
|---|---|
| EnableTvLane / EnableAnimeLane / EnableMovieLane | true / true / true |
| TmdbApiKey | "" (tmdb items skipped when blank) |
| GraceHours (0 = off) | 24 |
| MovieReleaseBufferDays | 90 |
| IncludeSpecials | false |
| CreateVirtualEpisodes | false |
| ShowPosterBadges / ShowDetailBadges | true / true |
| ExcludedItemIds (mute list) | [] |
| RequestDelayMs (scrapers + AniDB) | 2000 |
| AniDbClientName | registered client id |

## 7. Testing (true TDD, EVERY component, per standing mandate)

**Process rules (non-negotiable):**
- Every component follows red→green: its test file is written and observed
  failing before its implementation exists. No code without a failing test
  demanding it.
- Each component's test file opens with an edge-case inventory (comment
  block) written during analysis, before the first test — the checklist the
  suite must cover.
- All time-dependent logic takes an injected clock (`Func<DateTimeOffset>`)
  — no direct `DateTime.Now/UtcNow` outside composition roots — so boundary
  tests are deterministic.
- FREEZE RULE applies to every suite: once an implementation result has been
  observed against a test, that test and its pass criteria are frozen; fixes
  change code, never tests.
- Test project: xunit, `tests/Jellyfin.Plugin.DownloadTime.Tests`, runs
  against the 12.0 ABI build; fixtures committed under `tests/fixtures/`.

**7.1 DiffEngine suite (pure logic) — edge-case catalog:**
- Gaps: single mid-season; multiple scattered; entire middle season absent
  (own S1+S3, S2 missing); missing season premiere (S2E1); missing series
  premiere (S1E1) with later episodes owned.
- New: single tail episode; multi-episode tail; tail spanning a season
  boundary (S2 finale + S3E1 both new).
- Boundaries: `airedAt + grace == now` exactly (not yet missing);
  one second past (missing); GraceHours=0 (flag immediately after air-time
  rule elapses); date-only vs timestamped air data.
- Ownership: multi-episode file spans (E01-E02 covers both); duplicate local
  copies (two qualities = one owned); local episode with IndexNumber but no
  ParentIndexNumber (excluded from tuple matching, flagged in report).
- Remote data quality: unaired tail excluded; undated episodes never missing;
  empty remote catalog with owned episodes → **fetch treated as failed, zero
  missing reported** (fail-safe: a source outage must never scream
  "everything is missing"); remote list shorter than owned (we own more than
  the source knows) → zero missing, note in report.
- Specials: S0 excluded by default; included when configured; AniDB
  type!=1 mapped to specials semantics.
- Anime/ID lane: Ronin-merged absolute numbering (ID diff finds exact gaps);
  split-season layout (same result); some local episodes lacking AniDB IDs →
  fall back to epno-within-entry matching for those only; local-scheme anchor
  inference for placement (merged and split cases); no usable anchors →
  placement skipped, detection still reported.
- Classification: gap-vs-new split keyed on newest owned air date; series
  with zero owned episodes (all aired = gaps); fully complete series (zero
  output); muted item skipped.
- Movies: unreleased collection member (never missing); released within
  buffer (not yet missing); past buffer (missing); member owned as different
  edition (TMDB ID match = owned); movie not in any collection (no output);
  one owned movie whose collection has every other member missing (all
  flagged); two owned movies sharing one collection (collection processed
  once, not twice).

**7.2 Lane client suites (fixture-pinned parsers):**
- TVDB scraper: golden allseasons HTML fixture (normal show); show with
  specials season; missing air dates on some rows; **mutated/redesigned
  markup fixture → parser must return ParseFailure (not empty list), which
  the orchestrator turns into TVmaze fallback (asserted)**; numeric-ID URL
  slug redirect handling.
- TVmaze client: lookup 404 (show absent) → recorded as fetch failure;
  episodes with null airdate/airstamp; airstamp preferred over date.
- AniDB client: golden anime XML; error/ban response XML → fetch failure +
  scan continues; type filtering; **pacing test with fake clock proving ≥
  RequestDelayMs between consecutive requests**; cache honored within TTL
  (no second request), expired after.
- TMDB client: 401 invalid key → lane skipped with per-item report reason;
  429 with Retry-After honored; collection fetch golden fixture.
- Cache store: TTL expiry via injected clock; corrupt/truncated cache file →
  refetch, never crash; full-refresh bypass.

**7.3 Orchestrator/scan suite:** per-series failure isolation (one throwing
lane doesn't abort the scan); routing picks the identified source (tvdbid vs
tmdbid vs anidb precedence when multiple IDs present: anime library → AniDB,
else folder-tag source, else Tvdb>Tmdb>Imdb); lane toggles respected; scan
lock (second trigger while running is rejected politely).

**7.4 VirtualEpisodeWriter suite (against mocked ILibraryManager):**
idempotency (re-scan creates no duplicates — deterministic IDs); marker
stamped on every creation; reconciliation deletes only marker-bearing items
(foreign virtual items untouched — asserted); refuses creation on unnumbered
local content (`HasInvalidContent` guard); creates virtual season only when
absent; feature-off scan removes all ours.

**7.5 API suite:** report endpoint before first scan (well-formed empty
state); report after seeded scan; scan trigger auth (admin only); report
readable by non-admin user (badges).

**7.6 Live E2E on VMHOLLIER** (rig alongside `C:\JF-Dev\jf-e2e`, frozen once
first fix observed):
- Detection E2E: seeded expectations for 3 known series (one per lane) —
  temporarily rename one owned episode file away → scan → exactly that
  episode appears as Gap → restore file → scan → zero missing. Red is the
  pre-implementation run proving the harness detects the planted gap.
- Virtual placeholder E2E: create placeholders for a test series → visible
  via API/web with DisplayMissingEpisodes on → drop the real file back →
  library scan → server removes twin (12.0 `RemoveObsoleteEpisodes` path) →
  ResetTask → zero marker-bearing items in DB. `CreateVirtualEpisodes` stays
  default-off until green.
- Badge smoke: report endpoint serves data to a live web tab; badge renders
  (scripted CDP check, jf-e2e pattern).

## 8. Release

Standard flow: build 10.10.7/10.11/12.0 → zip (DLL + logo + meta.json) → gh
release → manifest.json sourceUrl+md5 → ~5 min raw-CDN cache → repo install.
Authored on GitHub as mhollier117, no AI attribution (standing instruction).

## 9. Out of scope (v1)

- Push/webhook notifications (declined for v1).
- Movie/collection poster badges (v1.1 candidate).
- AniDB sequel-relation chasing ("season 2 exists and you own none of it")
  — v1.1 candidate; v1 detects only within identified entries.
- Any file downloading/automation — this plugin only detects and displays.
