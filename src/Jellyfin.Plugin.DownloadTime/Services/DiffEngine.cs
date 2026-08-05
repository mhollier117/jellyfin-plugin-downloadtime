using Jellyfin.Plugin.DownloadTime.Model;

namespace Jellyfin.Plugin.DownloadTime.Services;

/// <summary>
/// Pure missing-episode detection. ID-first matching when the catalog carries
/// an IdProviderKey; (season,episode) tuple matching otherwise / as fallback
/// for local items lacking the ID. See spec §3.
/// </summary>
public static class DiffEngine
{
    public static SeriesDiff Diff(IReadOnlyList<OwnedEpisode> owned, RemoteCatalog remote, DiffOptions opts)
    {
        var notes = new List<string>();

        // --- owned partitions -------------------------------------------------
        var idKey = remote.IdProviderKey;
        var ownedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tupleOwned = new List<OwnedEpisode>();  // participate in tuple matching
        var unnumbered = 0;
        var seasonless = remote.Episodes.Count > 0 && remote.Episodes.All(e => e.Season is null);

        // Id reliability: two FILES can never be the same episode, so the same
        // per-episode id on 2+ locals proves the metadata provider misidentified
        // them (live Bleach shape 2026-08-02: split-era AniDB stamping survived a
        // Ronin merge). Untrustworthy ids demote the series to numbering
        // fallback for id-bearing locals too — never report owned eps missing.
        var unreliableIds = false;
        if (idKey is not null)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var o in owned)
            {
                if (o.ProviderIds.TryGetValue(idKey, out var id) && !string.IsNullOrEmpty(id) && !seen.Add(id))
                {
                    unreliableIds = true;
                    break;
                }
            }
        }
        if (unreliableIds)
        {
            notes.Add("Local episode ids are duplicated across files (metadata misidentification) - ids are ignored and numbering decides for this series.");
        }

        // Stale upstream ids (audit D2b, 2026-08-03): a file whose id the
        // catalog no longer knows still occupies its (S,E)/absolute slot.
        // Only when catalog-VERIFIED locals form the majority — a wrong-tag
        // library with one lucky match must not numbering-claim everything.
        var remoteIds = new HashSet<string>(
            remote.Episodes.Where(e => e.SourceEpisodeId is not null).Select(e => e.SourceEpisodeId!),
            StringComparer.OrdinalIgnoreCase);
        var knownIdLocals = 0;
        var staleIdLocals = 0;
        if (idKey is not null)
        {
            foreach (var o in owned)
            {
                if (o.ProviderIds.TryGetValue(idKey, out var id) && !string.IsNullOrEmpty(id))
                {
                    if (remoteIds.Contains(id)) { knownIdLocals++; } else { staleIdLocals++; }
                }
            }
        }
        var staleInclusion = knownIdLocals > staleIdLocals;

        foreach (var o in owned)
        {
            var hasId = idKey is not null && o.ProviderIds.TryGetValue(idKey, out var id) && !string.IsNullOrEmpty(id);
            if (hasId)
            {
                ownedIds.Add(o.ProviderIds[idKey!]);
                // ID-bearing locals match by ID only (spec: tuple fallback is
                // for id-less locals) — except when numbering must speak too:
                //  - the series' ids are provably unreliable (duplicates);
                //  - a multi-episode file: one id can never vouch for the
                //    whole Covers span (audit D2a);
                //  - this file's id is stale (unknown to the catalog) while
                //    verified locals are the majority (audit D2b).
                var multiEpisode = o.NumberEnd.HasValue && o.Number.HasValue && o.NumberEnd.Value > o.Number.Value;
                var stale = staleInclusion && !remoteIds.Contains(o.ProviderIds[idKey!]);
                if ((unreliableIds || multiEpisode || stale)
                    && o.Number.HasValue && (o.Season.HasValue || seasonless))
                {
                    tupleOwned.Add(o);
                }
                continue;
            }
            if (o.Number.HasValue && (o.Season.HasValue || seasonless))
            {
                tupleOwned.Add(o);
            }
            else
            {
                unnumbered++;
            }
        }
        if (unnumbered > 0)
        {
            notes.Add($"{unnumbered} local episode(s) unnumbered and unidentifiable - excluded from matching.");
        }

        // --- M7 fail-safe (synthesized/anime catalogs only, analysis 2026-07-26):
        // owned episodes exist but NONE are matchable -> never report the whole
        // show missing; note it instead. Tuple-lane behavior is pinned elsewhere.
        if (remote.SynthesizedSeasons && owned.Count > 0 && ownedIds.Count == 0 && tupleOwned.Count == 0)
        {
            notes.Add("Local episodes are unidentifiable (no AniDB ids, no usable numbering) - skipping missing detection for this series (fail-safe).");
            return new SeriesDiff(Array.Empty<MissingEpisode>(), notes);
        }

        // --- relevant remote set ----------------------------------------------
        bool IsSpecialEp(RemoteEpisode e) => e.IsSpecial || (e.Season == 0 && !remote.SynthesizedSeasons);
        var considered = remote.Episodes.Where(e => opts.IncludeSpecials || !IsSpecialEp(e)).ToList();

        // --- matching -----------------------------------------------------------
        // Synthesized unions (AniDB entry chains) let id-less locals match by
        // EITHER the (entry ordinal, epno) tuple (split layouts) OR absolute-
        // number coverage (merged layouts). Conservative OR: false-missing is
        // worse than false-owned.
        // Synthesized specials carry Season = entry ordinal, but locals keep
        // specials in season 0 — treat local S0 as a season wildcard for them
        // (v1.2 season-less catalogs matched these; regression guard).
        // The bare-number wildcard is only proof while UNAMBIGUOUS: with
        // several chain entries each carrying a special #N, content (airdate)
        // must decide instead (audit D3, 2026-08-03).
        var specialEpnoCounts = remote.SynthesizedSeasons
            ? remote.Episodes.Where(e => e.IsSpecial && e.Number.HasValue)
                .GroupBy(e => e.Number!.Value).ToDictionary(g => g.Key, g => g.Count())
            : new Dictionary<int, int>();
        bool UniqueSpecialEpno(RemoteEpisode e)
            => e.Number.HasValue && specialEpnoCounts.TryGetValue(e.Number.Value, out var c) && c == 1;
        // Same-day sibling specials (a movie plus its bonus short) make bare
        // date equality ambiguous — a date only proves ownership when it is
        // unique among the union's specials (audit S-7).
        var specialDateCounts = remote.SynthesizedSeasons
            ? remote.Episodes.Where(e => e.IsSpecial && e.AiredAt.HasValue)
                .GroupBy(e => e.AiredAt!.Value.Date).ToDictionary(g => g.Key, g => g.Count())
            : new Dictionary<DateTime, int>();
        bool UniqueSpecialDate(RemoteEpisode e)
            => e.AiredAt.HasValue && specialDateCounts.TryGetValue(e.AiredAt.Value.Date, out var c) && c == 1;
        // The bare-number wildcard is VETOED when the local's own content
        // contradicts the special: a usable title and/or date that BOTH fail
        // to match means the file is something else that merely shares the
        // number (audit S-8: HxH movies at S0E1/E2 vs radio shorts #1/#2).
        // Locals without any usable signal keep the wildcard (conservative).
        bool WildcardVetoed(RemoteEpisode e, OwnedEpisode o)
        {
            var localTitle = TitleKey(o.Title);
            if (localTitle is null && !o.AiredAt.HasValue)
            {
                return false;
            }
            var titleMatches = localTitle is not null
                && (TitleKey(e.Title) == localTitle || TitleKey(e.EntryName) == localTitle);
            var dateMatches = e.AiredAt.HasValue && o.AiredAt.HasValue
                && e.AiredAt.Value.Date == o.AiredAt.Value.Date;
            return !titleMatches && !dateMatches;
        }

        // Synthesized specials carry an entry ORDINAL, not a season: regular
        // numbering in the same ordinal must never own them (audit S-3) —
        // only the unique-epno S0 wildcard (ids and content match elsewhere).
        bool SeasonAgrees(RemoteEpisode e, OwnedEpisode o)
            => remote.SynthesizedSeasons && e.IsSpecial
                ? o.Season == 0 && UniqueSpecialEpno(e) && !WildcardVetoed(e, o)
                : e.Season is null || !o.Season.HasValue || o.Season == e.Season;
        // Movie/special entry content matching: an owned S0 item with the same
        // air DATE — or the same normalized title (episode OR chain-entry
        // title; movie entries are titled by the film, their sole "episode"
        // is just "Complete Movie") — is the same content regardless of
        // numbering or ids (audit D3ii).
        // Shared with RuntimeEnricher; strips leading AFM bracket markers.
        static string? TitleKey(string? t) => TitleKeys.Normalize(t);
        bool SpecialContentPair(RemoteEpisode e, OwnedEpisode o)
        {
            if (!remote.SynthesizedSeasons || !e.IsSpecial || o.Season != 0)
            {
                return false;
            }
            if (UniqueSpecialDate(e) && o.AiredAt.HasValue && e.AiredAt!.Value.Date == o.AiredAt.Value.Date)
            {
                return true;
            }
            var local = TitleKey(o.Title);
            if (local is null)
            {
                return false;
            }
            if (TitleKey(e.Title) == local)
            {
                return true;
            }
            // An entry-name match claims "I own this entry's FILM". Only the
            // entry's "Complete Movie" part qualifies — same-entry bonus
            // shorts share the entry name but are distinct content
            // (audit S-7: Boruto movie vs its same-day short).
            return TitleKey(e.EntryName) == local && TitleKey(e.Title) == "completemovie";
        }
        bool SpecialContentMatch(RemoteEpisode e) => owned.Any(o => SpecialContentPair(e, o));
        bool TupleMatch(RemoteEpisode e) => e.Number.HasValue && tupleOwned.Any(o =>
            SeasonAgrees(e, o) && o.Covers(e.Number.Value));
        bool AbsMatch(RemoteEpisode e) => remote.SynthesizedSeasons && e.AbsoluteNumber.HasValue
            && tupleOwned.Any(o => o.Covers(e.AbsoluteNumber.Value));
        // Season-ful tuple catalogs (TVDB shape) against a merged local layout:
        // aired SxxEyy has no local (S,E) twin — presence lives on the
        // cumulative absolute axis instead (live Bleach shape 2026-08-02).
        var cumulativeAbs = !remote.SynthesizedSeasons
                            && remote.Episodes.Any(e => e.Season is > 1)
                            && Layout.MergedSeasonOne(owned)
            ? Layout.AbsoluteIndex(remote)
            : null;
        bool CumulativeAbsMatch(RemoteEpisode e) => cumulativeAbs is not null
            && e.Season.HasValue && e.Number.HasValue && !IsSpecialEp(e)
            && cumulativeAbs.TryGetValue((e.Season.Value, e.Number.Value), out var abs)
            && tupleOwned.Any(o => o.Covers(abs));
        bool IdMatch(RemoteEpisode e) => e.SourceEpisodeId is not null && ownedIds.Contains(e.SourceEpisodeId);
        var fallbackMatched = 0;
        bool IsOwned(RemoteEpisode e)
        {
            // Unreliable (duplicated) ids prove nothing in either direction:
            // a corrupt eid on an owned file must not vouch for an episode
            // that is genuinely absent (audit D4, 2026-08-03).
            if (!unreliableIds && IdMatch(e))
            {
                return true;
            }
            if (TupleMatch(e) || AbsMatch(e) || CumulativeAbsMatch(e) || SpecialContentMatch(e))
            {
                fallbackMatched++;
                return true;
            }
            return false;
        }

        // --- aired rule ---------------------------------------------------------
        bool AiredLongEnough(RemoteEpisode e)
            => e.AiredAt.HasValue && e.AiredAt.Value.AddHours(opts.GraceHours) < opts.Now;

        // --- classification -----------------------------------------------------
        var newestOwnedAir = owned.Where(o => o.AiredAt.HasValue).Select(o => o.AiredAt!.Value)
            .DefaultIfEmpty(DateTimeOffset.MinValue).Max();
        var hasOwnedAir = owned.Any(o => o.AiredAt.HasValue);

        var missing = new List<MissingEpisode>();
        foreach (var e in considered)
        {
            if (IsOwned(e) || !AiredLongEnough(e))
            {
                continue;
            }
            var kind = !hasOwnedAir || e.AiredAt!.Value <= newestOwnedAir ? MissingKind.Gap : MissingKind.New;
            missing.Add(new MissingEpisode(e, kind));
        }

        // --- misidentification fail-safe (synthesized/anime only): owned
        // episodes exist but not ONE matched anything — e.g. a wrong anidbid
        // folder tag puts foreign episode ids on every local. Reporting the
        // whole franchise missing would be pure noise; suppress and note it
        // (extends M7 to "ids present but none match").
        if (remote.SynthesizedSeasons && owned.Count > 0 && missing.Count > 0 && fallbackMatched == 0
            && !remote.Episodes.Any(e => e.SourceEpisodeId is not null && ownedIds.Contains(e.SourceEpisodeId)))
        {
            notes.Add("None of the local episodes match this AniDB entry chain (wrong anidbid tag?) - skipping missing detection for this series (fail-safe).");
            return new SeriesDiff(Array.Empty<MissingEpisode>(), notes);
        }

        if (remote.SynthesizedSeasons && fallbackMatched > 0)
        {
            notes.Add($"{fallbackMatched} episode(s) matched via numbering fallback (AniDB episode ids missing or unreliable on those local files).");
        }

        // --- undated specials note (audit S-6) ----------------------------------
        if (opts.IncludeSpecials)
        {
            var undatedSpecials = remote.Episodes.Count(e => IsSpecialEp(e) && !e.AiredAt.HasValue);
            if (undatedSpecials > 0)
            {
                notes.Add($"{undatedSpecials} undated special(s) not auditable (no air date from the source).");
            }
        }

        // --- "library exceeds source" note --------------------------------------
        var strayIds = ownedIds.Count(id => !remote.Episodes.Any(e =>
            e.SourceEpisodeId is not null && string.Equals(e.SourceEpisodeId, id, StringComparison.OrdinalIgnoreCase)));
        var strayTuples = tupleOwned.Count(o => !remote.Episodes.Any(e =>
            (e.Number.HasValue && SeasonAgrees(e, o) && o.Covers(e.Number.Value))
            || (remote.SynthesizedSeasons && e.AbsoluteNumber.HasValue && o.Covers(e.AbsoluteNumber.Value))
            || (cumulativeAbs is not null && e.Season.HasValue && e.Number.HasValue
                && cumulativeAbs.TryGetValue((e.Season.Value, e.Number.Value), out var abs) && o.Covers(abs))
            || SpecialContentPair(e, o)));
        var stray = strayIds + strayTuples;
        if (stray > 0)
        {
            notes.Add($"{stray} local episode(s) unknown to the source.");
        }

        return new SeriesDiff(missing, notes);
    }
}
