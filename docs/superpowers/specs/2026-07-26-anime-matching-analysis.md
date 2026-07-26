# Anime Matching Deep Analysis (2026-07-26)

Requirement: Ronin-grade anime matching that is correct for BOTH single-season
(merged/absolute) and split-season local layouts. This audits the shipped
AniDB path (AniDbFetcher → ScanService routing → DiffEngine → Placer →
VirtualEpisodePlanner) against eight failure modes and specifies the fix.

## Shipped pipeline (v1.2.0.0 baseline)

- `ScanService` routes an anime series to ONE `FetchByAnimeIdAsync(seriesAniDbId)`
  call — exactly one AniDB entry is ever fetched per series.
- The resulting catalog is season-less: every `RemoteEpisode.Season == null`,
  `Number = epno` within that single entry; `IdProviderKey = "AniDB"`.
- `DiffEngine`: locals WITH an AniDB episode id match by id only; locals
  WITHOUT match by tuple, and because the catalog is season-less the tuple
  rule collapses to "any owned whose Number-range covers the epno,
  season ignored".
- `Placer`: season-less missing episodes use anchor math on `Number` (epno);
  season-ful episodes short-circuit to the remote (Season, Number).

## Failure-mode matrix

### M1 — merged local, single entry, ids present  ✅ works (regression baseline)
Detection by episode-id set difference; placement anchors (epno → local S1,
absolute number) interpolate/extrapolate correctly. This is the designed case
and must not regress.

### M2 — split local, single entry, ids present  ✅ detect / ⚠ placement (conservative)
Detection: id matching is layout-independent — correct.
Placement: anchors carry (epno → local S/E). Within one local season the
interpolation works; when the two nearest anchors straddle local seasons the
Placer intentionally returns null (skip placeholder, detection still
reported). Root cause: no cross-season extrapolation rule. Verdict: correct
detection, conservative placement; boundary-episode placeholders are skipped.
Kept conservative (false placement is worse than no placeholder); the union
design below does not change this single-entry property.

### M3 — merged local, multi-entry franchise, ids present  ❌ sequel cours undetectable
AniDB models most franchises as one entry per season/cour, linked by
`<relatedanime type="Sequel">`. We fetch only the identified entry, so:
- Local files from later cours carry AniDB episode ids belonging to sequel
  entries → they land in the "N local episode(s) unknown to the source" note
  (no false-missing, good) — but
- Missing episodes in ANY later cour can never be detected, because those
  episodes are simply absent from the remote catalog.
Root cause: single-entry fetch. This is the core deviation from the
requirement.

### M4 — split local, multi-entry  ❌ same as M3
The series-level anidbid identifies entry 1 only. Later local seasons are
invisible to detection exactly as in M3. Root cause identical.

### M5 — id-less locals (e.g. NFO-sourced), merged layout  ✅ entry 1 / ❌ beyond
Season-less tuple fallback compares epno to the local absolute number —
correct for entry 1 (epno == absolute there). Beyond entry 1 nothing exists
to compare (M3). Under the union fix, entry-2 epno 1 must match local
absolute 13 — requires an absolute-number concept in the catalog.

### M6 — id-less locals, split layout  ❌ wrong both directions
With season ignored, ANY local season's episode numbers cover low epnos
(false-owned), while epnos above the per-season count (e.g. epno 13 of a
24-episode entry vs locals S1E1..12 + S2E1..12) match nothing → FALSE
MISSING for episodes the user owns. Root cause: comparing entry-epno against
local numbers without any layout awareness.

### M7 — mis-parsed/unnumbered locals  ⚠ can explode
Unnumbered id-less locals are excluded from matching (with a note). A series
whose locals are ALL unidentifiable (no ids, no usable numbers) currently
reports every aired episode missing. Must fail safe instead: if a series has
owned episodes but ZERO of them are matchable, suppress missing output and
note it. Scoped to the anime/union path only — the equivalent tuple-lane
behavior is pinned by a frozen test (`OwnedWithoutSeason_…`) and TV file
names virtually always parse.

### M8 — placement across M1–M4  ❌ for multi-entry
Placement must land in the LOCAL scheme in both layouts. With a union
catalog, entry ordinals are NOT local season numbers (a local "Season 2"
folder may be entry 3 after a movie entry, etc.), so the season-ful
short-circuit that is correct for TVDB/TMDB would be WRONG for synthesized
seasons. Placement must instead anchor on a cross-entry monotonic axis
(absolute number over the union), which works for both merged (anchors map
absolute → S1/absolute) and split (anchors map absolute → local S/E,
same-season interpolation) layouts.

## Design (implemented in this change)

1. **Entry chain.** `AniDbFetcher.ParseEntry` additionally extracts
   `<relatedanime><anime type="Sequel">` ids (prequels and other relation
   types ignored). A new `IAniDbSource.FetchEntryAsync` returns
   `(Catalog, SequelIds, Error)`; it has a default interface implementation
   that adapts `FetchByAnimeIdAsync` with no sequels so existing test fakes
   remain valid. `ScanService` walks the chain breadth-first from the
   identified entry: depth/size cap 16 entries, visited-set cycle guard,
   each entry fetched through the existing 2s pacer and cached individually
   (`anidb-entry-{aid}`, existing ended/continuing TTLs).
2. **Union catalog.** `AniDbChain.BuildUnion(entries)` produces one catalog:
   `Season` = entry ordinal (1-based, discovery order), `Number` = epno
   within the entry, and a new `RemoteEpisode.AbsoluteNumber` = cumulative
   position across the chain counting regular episodes only (specials get
   null). New `RemoteCatalog.SynthesizedSeasons = true` marks that ordinals
   are NOT trustworthy local season numbers. Both new members are optional
   record parameters — every existing construction and cached JSON document
   remains valid.
3. **DiffEngine.** Id matching unchanged (now runs against the union). The
   id-less fallback for synthesized catalogs matches if EITHER the
   (ordinal, epno) tuple matches the local (season, number) OR the local
   number-range covers `AbsoluteNumber` — a deliberate conservative OR:
   false-owned merely delays a report; false-missing spams it. A note is
   emitted whenever the fallback path matched anything. M7 fail-safe: a
   synthesized catalog with owned episodes but zero matchable locals
   reports no missing plus an explanatory note.
4. **Placer.** Synthesized catalogs NEVER take the season-ful short-circuit;
   anchors are keyed on `AbsoluteNumber` (episodes without one — specials —
   are unplaceable). All other catalogs behave exactly as before.
5. **ScanService/report.** Lane label becomes `AniDB (N entries)` when the
   chain has more than one entry. Per-sequel fetch failures degrade to a
   partial union with a per-series note naming the failed entry; a failed
   ROOT entry still errors the series. A partial union is never presented as
   complete.

## Residual known gaps (documented honestly)

- **Local episode ids pointing at entries OUTSIDE the sequel chain** (e.g.
  side stories/movies filed inside the show folder, or AniDB restructuring):
  the HTTP API has no episode-id → anime-id lookup, so we cannot resolve an
  arbitrary orphan episode id to its entry without a full dump. These locals
  keep landing in the "unknown to the source" note; their cours remain
  undetected unless reachable via Sequel relations. Tractable only with the
  nightly AniDB dumps — deferred, noted here per the analysis mandate.
- **Single-entry shows split locally with id-less episodes** (M6 variant
  where no entry boundary exists): neither tuple nor absolute matching can
  recover the local layout; false-missing remains possible. Pathological
  (requires hand-renumbered NFO-only files); the fallback note makes it
  visible.
- **M2 placement across local-season boundaries** stays conservative (skip)
  by design.
