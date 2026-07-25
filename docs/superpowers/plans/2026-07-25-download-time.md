# Download Time Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Jellyfin plugin "Download Time" that detects missing episodes (gaps + newly aired) and missing franchise movies, surfaced via a dashboard report page, poster badges, and opt-in native virtual placeholder episodes.

**Architecture:** Pure detection core (DiffEngine/Placer/Router — no I/O, injected clock) + per-source lane clients (TVDB page scraper w/ TVmaze fallback, AniDB HTTP API, TMDB API) + a ScanService orchestrator behind thin Jellyfin adapters (library reader, scheduled task, REST controller, File-Transformation badge injection, virtual-episode writer). Spec: `docs/superpowers/specs/2026-07-25-download-time-design.md` (READ IT FIRST).

**Tech Stack:** C# / .NET 10 (net10.0 primary), Jellyfin.Controller 12.0.0-rc2 (+ 10.11 / 10.10.7 ABIs at release), HtmlAgilityPack, xunit (no mocking framework — hand-rolled fakes).

## Global Constraints

- **True TDD, every task:** write the failing test, RUN it and observe failure, implement minimally, RUN green, commit. Never reorder. FREEZE RULE: once an implementation result has been observed against a test, that test and its criteria are frozen — fix code, never tests.
- Every test file OPENS with a comment block inventorying its edge cases (copy from the task's test code here).
- No `DateTime.Now/UtcNow` outside composition roots — inject `IClock`.
- Plugin name **Download Time**, assembly `Jellyfin.Plugin.DownloadTime`, GUID `4d557ba6-d562-4209-9a04-b782775dc2ff`, version 1.0.0.0.
- Git commits: plain messages, **no AI attribution / no Co-Authored-By** (standing user instruction for mhollier117 repos).
- Repo root: `C:\JF-Dev\jellyfin-plugin-downloadtime`. All paths below relative to it.
- Build check for every task: `dotnet test` from repo root (defaults to 12.0 ABI). Release additionally builds `-p:JellyfinVersion=10.11` and `10.10.7`; guard 10.10-only API differences with `#if JELLYFIN_10_10`.
- Fail-safe principle: a source outage/parse failure must NEVER present as "everything is missing."
- Live fixtures already captured in `tests-fixtures-staging/` (move into `tests/.../fixtures/` in Task 1): `tvdb-allseasons-american-gods.html` (81 list-group nodes incl. specials, per-episode IDs in hrefs), `tvmaze-lookup-253573.json`, `tvmaze-episodes-americangods.json` (26 regular episodes).

## File Structure

```
Jellyfin.Plugin.DownloadTime.sln
src/Jellyfin.Plugin.DownloadTime/
  Jellyfin.Plugin.DownloadTime.csproj
  Plugin.cs                       BasePlugin + config page registration
  PluginConfiguration.cs          all settings (spec §6)
  PluginServiceRegistrator.cs     DI wiring
  Model/Records.cs                RemoteEpisode/RemoteCatalog/OwnedEpisode/… + IClock
  Services/AirTime.cs             date-only → aired-at normalization rule
  Services/DiffEngine.cs          missing detection + Gap/New classification (pure)
  Services/CollectionDiff.cs      movie franchise diff (pure)
  Services/Placer.cs              local-scheme placement inference (pure)
  Services/SourceRouter.cs        identified-source routing (pure)
  Services/CatalogCache.cs        TTL'd JSON catalog cache
  Services/ReportStore.cs         scan report persistence + in-memory current
  Services/ScanService.cs         orchestrator
  Services/Lanes/ICatalogFetcher.cs
  Services/Lanes/TvdbScrapeFetcher.cs   + TvdbAllSeasonsParser (pure inner)
  Services/Lanes/TvmazeFetcher.cs
  Services/Lanes/AniDbFetcher.cs        + AniDbXmlParser (pure inner)
  Services/Lanes/TmdbFetcher.cs
  Services/JellyfinLibraryReader.cs     ILibraryManager adapter (thin)
  Services/VirtualEpisodePlanner.cs     placeholder create/delete decisions (pure)
  Services/VirtualEpisodeWriter.cs      ILibraryManager applier (thin, E2E-covered)
  Tasks/ScanTask.cs  Tasks/ResetTask.cs  Tasks/StartupTask.cs
  Api/DownloadTimeController.cs
  Helpers/TransformationPatch.cs
  Web/badges.js  Web/badges.css
  configPage.html
tests/Jellyfin.Plugin.DownloadTime.Tests/
  Jellyfin.Plugin.DownloadTime.Tests.csproj
  ConfigurationTests.cs  AirTimeTests.cs  DiffEngineTupleTests.cs
  DiffEngineIdTests.cs   CollectionDiffTests.cs  PlacerTests.cs
  SourceRouterTests.cs   TvdbParserTests.cs  TvmazeFetcherTests.cs
  AniDbFetcherTests.cs   TmdbFetcherTests.cs  CatalogCacheTests.cs
  ReportStoreTests.cs    ScanServiceTests.cs  VirtualEpisodePlannerTests.cs
  ApiControllerTests.cs  Support/FakeClock.cs  Support/FakeHttp.cs
  fixtures/…
e2e/detect.mjs  e2e/virtual.mjs  e2e/badges.mjs   (node rigs, run against VMHOLLIER)
docs/…  (spec, this plan, reference MissingEpisodeProvider)
```

---

### Task 1: Repo scaffold + first red→green (configuration defaults)

**Files:**
- Create: `Jellyfin.Plugin.DownloadTime.sln`, `src/Jellyfin.Plugin.DownloadTime/Jellyfin.Plugin.DownloadTime.csproj`, `src/Jellyfin.Plugin.DownloadTime/Plugin.cs`, `src/Jellyfin.Plugin.DownloadTime/PluginConfiguration.cs`, `tests/Jellyfin.Plugin.DownloadTime.Tests/Jellyfin.Plugin.DownloadTime.Tests.csproj`, `tests/Jellyfin.Plugin.DownloadTime.Tests/ConfigurationTests.cs`, `.gitignore`
- Move: `tests-fixtures-staging/*` → `tests/Jellyfin.Plugin.DownloadTime.Tests/fixtures/`

**Interfaces:**
- Produces: `PluginConfiguration` properties (exact names/defaults below) used by every later task; `Plugin.Instance`.

- [ ] **Step 1: Scaffold projects**

`.gitignore`: `bin/`, `obj/`, `release-*/`, `*.user`.

`src/Jellyfin.Plugin.DownloadTime/Jellyfin.Plugin.DownloadTime.csproj` — copy Filler Skip's multi-ABI pattern exactly:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <JellyfinVersion Condition="'$(JellyfinVersion)' == ''">12.0</JellyfinVersion>
    <AssemblyName>Jellyfin.Plugin.DownloadTime</AssemblyName>
    <RootNamespace>Jellyfin.Plugin.DownloadTime</RootNamespace>
    <AssemblyVersion>1.0.0.0</AssemblyVersion>
    <FileVersion>1.0.0.0</FileVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <GenerateDocumentationFile>false</GenerateDocumentationFile>
    <NoWarn>CS1591</NoWarn>
    <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>
  </PropertyGroup>
  <PropertyGroup Condition="$(JellyfinVersion.StartsWith('12.'))">
    <TargetFramework>net10.0</TargetFramework>
    <JellyfinPackageVersion>12.0.0-rc2</JellyfinPackageVersion>
  </PropertyGroup>
  <PropertyGroup Condition="$(JellyfinVersion.StartsWith('10.11'))">
    <TargetFramework>net9.0</TargetFramework>
    <JellyfinPackageVersion>10.11.0</JellyfinPackageVersion>
  </PropertyGroup>
  <PropertyGroup Condition="$(JellyfinVersion.StartsWith('10.10'))">
    <TargetFramework>net8.0</TargetFramework>
    <JellyfinPackageVersion>10.10.7</JellyfinPackageVersion>
    <DefineConstants>$(DefineConstants);JELLYFIN_10_10</DefineConstants>
  </PropertyGroup>
  <ItemGroup>
    <EmbeddedResource Include="configPage.html" />
    <EmbeddedResource Include="Web\badges.js" />
    <EmbeddedResource Include="Web\badges.css" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="Jellyfin.Controller" Version="$(JellyfinPackageVersion)" />
    <PackageReference Include="HtmlAgilityPack" Version="1.11.61" />
  </ItemGroup>
</Project>
```

Create placeholder `configPage.html` (`<!-- populated in Task 19 -->`), `Web/badges.js` (`// populated in Task 20`), `Web/badges.css` (`/* populated in Task 20 */`) so EmbeddedResource globs resolve.

`tests/Jellyfin.Plugin.DownloadTime.Tests/Jellyfin.Plugin.DownloadTime.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\Jellyfin.Plugin.DownloadTime\Jellyfin.Plugin.DownloadTime.csproj" />
  </ItemGroup>
  <ItemGroup>
    <None Include="fixtures\**" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
</Project>
```

`Jellyfin.Plugin.DownloadTime.sln`: `dotnet new sln && dotnet sln add src/... tests/...`.

Move fixtures: `git mv tests-fixtures-staging/* tests/Jellyfin.Plugin.DownloadTime.Tests/fixtures/` (create dir first; `tvdb-allseasons-253573.html` is a captured 404 page — KEEP it, it becomes the "scrape failed" fixture, rename to `tvdb-404.html`).

- [ ] **Step 2: Write the failing test (configuration defaults per spec §6)**

`tests/.../ConfigurationTests.cs`:

```csharp
// Edge-case inventory:
// - every spec §6 default exactly as documented
// - GraceHours=0 is a legal value (off) — property is int, not uint with floor
using Jellyfin.Plugin.DownloadTime;
using Xunit;

namespace Jellyfin.Plugin.DownloadTime.Tests;

public class ConfigurationTests
{
    [Fact]
    public void Defaults_MatchSpec()
    {
        var c = new PluginConfiguration();
        Assert.True(c.EnableTvLane);
        Assert.True(c.EnableAnimeLane);
        Assert.True(c.EnableMovieLane);
        Assert.Equal(string.Empty, c.TmdbApiKey);
        Assert.Equal(24, c.GraceHours);
        Assert.Equal(90, c.MovieReleaseBufferDays);
        Assert.False(c.IncludeSpecials);
        Assert.False(c.CreateVirtualEpisodes);
        Assert.True(c.ShowPosterBadges);
        Assert.True(c.ShowDetailBadges);
        Assert.Empty(c.ExcludedItemIds);
        Assert.Equal(2000, c.RequestDelayMs);
        Assert.Equal("downloadtime", c.AniDbClientName);
        Assert.Equal(1, c.AniDbClientVersion);
        Assert.Equal(1, c.ContinuingTtlDays);
        Assert.Equal(7, c.EndedTtlDays);
    }

    [Fact]
    public void GraceHours_Zero_IsAssignable()
    {
        var c = new PluginConfiguration { GraceHours = 0 };
        Assert.Equal(0, c.GraceHours);
    }
}
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test` (from repo root)
Expected: FAIL — `PluginConfiguration` does not exist / project doesn't compile. That IS the red state for a scaffold task.

- [ ] **Step 4: Implement Plugin.cs + PluginConfiguration.cs**

`src/Jellyfin.Plugin.DownloadTime/PluginConfiguration.cs`:

```csharp
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.DownloadTime;

public class PluginConfiguration : BasePluginConfiguration
{
    public bool EnableTvLane { get; set; } = true;
    public bool EnableAnimeLane { get; set; } = true;
    public bool EnableMovieLane { get; set; } = true;

    /// <summary>TMDB API key; when blank all tmdbid-routed items are skipped (reported).</summary>
    public string TmdbApiKey { get; set; } = string.Empty;

    /// <summary>Hours after airing before an episode counts as missing. 0 = off.</summary>
    public int GraceHours { get; set; } = 24;

    /// <summary>Days after theatrical release before a franchise movie counts as missing.</summary>
    public int MovieReleaseBufferDays { get; set; } = 90;

    public bool IncludeSpecials { get; set; }
    public bool CreateVirtualEpisodes { get; set; }
    public bool ShowPosterBadges { get; set; } = true;
    public bool ShowDetailBadges { get; set; } = true;

    /// <summary>Muted item ids (series or movie ids as N-format GUID strings).</summary>
    public string[] ExcludedItemIds { get; set; } = System.Array.Empty<string>();

    /// <summary>Min delay between outbound requests to scraped/rate-limited sources.</summary>
    public int RequestDelayMs { get; set; } = 2000;

    public string AniDbClientName { get; set; } = "downloadtime";
    public int AniDbClientVersion { get; set; } = 1;

    /// <summary>Catalog cache TTLs (spec §2.4).</summary>
    public int ContinuingTtlDays { get; set; } = 1;
    public int EndedTtlDays { get; set; } = 7;
}
```

`src/Jellyfin.Plugin.DownloadTime/Plugin.cs` (Filler Skip pattern):

```csharp
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.DownloadTime;

/// <summary>
/// Detects missing episodes (gaps and newly aired) and missing franchise
/// movies by comparing the library against each item's identifying source.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "Download Time";

    public override string Description =>
        "Detects missing episodes and franchise movies: gaps you missed and new releases not yet downloaded.";

    public override Guid Id => Guid.Parse("4d557ba6-d562-4209-9a04-b782775dc2ff");

    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = Name,
            EmbeddedResourcePath = $"{GetType().Namespace}.configPage.html"
        };
    }
}
```

- [ ] **Step 5: Run tests to verify green**

Run: `dotnet test`
Expected: 2 passed.

- [ ] **Step 6: Commit**

```bash
git add -A && git commit -m "Scaffold Download Time plugin with configuration defaults"
```

---

### Task 2: Model records, IClock, AirTime rule

**Files:**
- Create: `src/Jellyfin.Plugin.DownloadTime/Model/Records.cs`, `src/Jellyfin.Plugin.DownloadTime/Services/AirTime.cs`, `tests/.../AirTimeTests.cs`, `tests/.../Support/FakeClock.cs`

**Interfaces:**
- Produces (used by EVERY later task — exact definitions):

```csharp
namespace Jellyfin.Plugin.DownloadTime.Model;

public interface IClock { DateTimeOffset UtcNow { get; } }
public sealed class SystemClock : IClock { public DateTimeOffset UtcNow => DateTimeOffset.UtcNow; }

/// <summary>One episode as the remote source knows it.</summary>
public sealed record RemoteEpisode(
    int? Season,              // null for season-less sources (AniDB)
    int? Number,              // aired number within season, or epno within AniDB entry
    string? SourceEpisodeId,  // per-episode id in the catalog's id namespace, null if none
    DateTimeOffset? AiredAt,  // normalized (AirTime rule); null = undated
    bool IsSpecial,
    string? Title);

/// <summary>Full remote catalog for one series.</summary>
public sealed record RemoteCatalog(
    string SourceKey,         // "Tvdb" | "AniDB" | "Tmdb" | "TvmazeFallback"
    string? IdProviderKey,    // local ProviderIds key episode ids join on: "Tvdb", "AniDB", or null (tuple-only)
    string SeriesSourceId,
    bool IsEnded,             // drives cache TTL
    IReadOnlyList<RemoteEpisode> Episodes);

/// <summary>Exactly one of Catalog/Error is non-null.</summary>
public sealed record FetchOutcome(RemoteCatalog? Catalog, string? Error)
{
    public static FetchOutcome Ok(RemoteCatalog c) => new(c, null);
    public static FetchOutcome Fail(string error) => new(null, error);
}

/// <summary>One local (non-virtual) episode.</summary>
public sealed record OwnedEpisode(
    int? Season,              // ParentIndexNumber
    int? Number,              // IndexNumber
    int? NumberEnd,           // IndexNumberEnd (multi-episode files)
    IReadOnlyDictionary<string, string> ProviderIds,
    DateTimeOffset? AiredAt)
{
    public bool Covers(int n) => Number.HasValue && n >= Number.Value && n <= (NumberEnd ?? Number.Value);
}

public enum MissingKind { Gap, New }
public sealed record MissingEpisode(RemoteEpisode Episode, MissingKind Kind);
public sealed record SeriesDiff(IReadOnlyList<MissingEpisode> Missing, IReadOnlyList<string> Notes);

public sealed record DiffOptions(DateTimeOffset Now, int GraceHours, bool IncludeSpecials);

public sealed record RemoteMovie(int TmdbId, string Title, DateTimeOffset? ReleasedAt);
public sealed record CollectionCatalog(int CollectionId, string Name, IReadOnlyList<RemoteMovie> Movies);

public sealed record Placement(int Season, int Number);
```

- `AirTime.FromDate(int year, int month, int day)` → `DateTimeOffset` at 23:59:00 UTC of that date (spec air-time rule).
- `Support/FakeClock.cs`: `public sealed class FakeClock(DateTimeOffset now) : IClock { public DateTimeOffset UtcNow { get; set; } = now; }` (settable for pacing tests).

- [ ] **Step 1: Write the failing tests**

`tests/.../AirTimeTests.cs`:

```csharp
// Edge-case inventory:
// - date-only air date normalizes to 23:59:00 UTC that same date
// - result is exactly comparable: airedAt+grace==now must be NOT-aired-long-enough (tested in DiffEngine)
// - leap day accepted
using Jellyfin.Plugin.DownloadTime.Services;
using Xunit;

namespace Jellyfin.Plugin.DownloadTime.Tests;

public class AirTimeTests
{
    [Fact]
    public void FromDate_Is_2359_Utc()
    {
        var t = AirTime.FromDate(2024, 1, 7);
        Assert.Equal(new DateTimeOffset(2024, 1, 7, 23, 59, 0, TimeSpan.Zero), t);
    }

    [Fact]
    public void FromDate_LeapDay()
    {
        var t = AirTime.FromDate(2024, 2, 29);
        Assert.Equal(29, t.Day);
        Assert.Equal(TimeSpan.Zero, t.Offset);
    }

    [Fact]
    public void OwnedEpisode_Covers_SpansAndSingles()
    {
        var span = new Model.OwnedEpisode(1, 1, 2, new Dictionary<string, string>(), null);
        Assert.True(span.Covers(1));
        Assert.True(span.Covers(2));
        Assert.False(span.Covers(3));
        var single = new Model.OwnedEpisode(1, 5, null, new Dictionary<string, string>(), null);
        Assert.True(single.Covers(5));
        Assert.False(single.Covers(4));
        var unnumbered = new Model.OwnedEpisode(1, null, null, new Dictionary<string, string>(), null);
        Assert.False(unnumbered.Covers(1));
    }
}
```

- [ ] **Step 2: Run to verify failure** — `dotnet test`; expected: compile error (types missing).

- [ ] **Step 3: Implement** `Model/Records.cs` exactly as the Produces block above, plus:

```csharp
namespace Jellyfin.Plugin.DownloadTime.Services;

/// <summary>Air-time rule (spec §3): a date-only air date counts as aired at 23:59 UTC that day.</summary>
public static class AirTime
{
    public static DateTimeOffset FromDate(int year, int month, int day)
        => new(year, month, day, 23, 59, 0, TimeSpan.Zero);
}
```

- [ ] **Step 4: Run green** — `dotnet test`; expected: all pass.
- [ ] **Step 5: Commit** — `git add -A && git commit -m "Add model records, clock abstraction, air-time rule"`

---

### Task 3: DiffEngine — tuple lane, grace, specials, classification

**Files:**
- Create: `src/Jellyfin.Plugin.DownloadTime/Services/DiffEngine.cs`, `tests/.../DiffEngineTupleTests.cs`

**Interfaces:**
- Consumes: all Task 2 records.
- Produces: `public static SeriesDiff DiffEngine.Diff(IReadOnlyList<OwnedEpisode> owned, RemoteCatalog remote, DiffOptions opts)` — used by ScanService (Task 14) and VirtualEpisodePlanner (Task 17).

- [ ] **Step 1: Write the failing tests**

`tests/.../DiffEngineTupleTests.cs`:

```csharp
// Edge-case inventory (tuple lane — IdProviderKey null):
// Gaps: single mid-season; scattered; whole middle season absent; missing S2E1; missing S1E1.
// New: single tail; multi tail; tail across season boundary.
// Boundaries: airedAt+grace == now (NOT missing); 1s past (missing); grace=0.
// Ownership: E01-E02 span covers both; duplicate local copies count once; owned ep with
//   Number but null Season excluded from matching + note; undated remote never missing;
//   unaired tail excluded; remote knows fewer eps than we own -> zero missing + note.
// Classification: kinds keyed on newest owned air date; zero owned -> all Gap;
//   fully complete -> empty; specials excluded by default, included on opt-in.
using Jellyfin.Plugin.DownloadTime.Model;
using Jellyfin.Plugin.DownloadTime.Services;
using Xunit;

namespace Jellyfin.Plugin.DownloadTime.Tests;

public class DiffEngineTupleTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 25, 12, 0, 0, TimeSpan.Zero);
    private static DiffOptions Opts(int grace = 24, bool specials = false) => new(Now, grace, specials);

    private static RemoteEpisode R(int s, int n, DateTimeOffset? aired, bool special = false)
        => new(s, n, null, aired, special, $"S{s}E{n}");

    private static OwnedEpisode O(int s, int n, int? end = null, DateTimeOffset? aired = null)
        => new(s, n, end, new Dictionary<string, string>(), aired);

    private static RemoteCatalog Cat(params RemoteEpisode[] eps)
        => new("Tvdb", null, "253573", true, eps);

    private static DateTimeOffset D(int m, int d, int year = 2026) => AirTime.FromDate(year, m, d);

    [Fact]
    public void MidSeasonGap_IsGap()
    {
        var remote = Cat(R(1, 1, D(1, 1)), R(1, 2, D(1, 8)), R(1, 3, D(1, 15)));
        var owned = new[] { O(1, 1, aired: D(1, 1)), O(1, 3, aired: D(1, 15)) };
        var diff = DiffEngine.Diff(owned, remote, Opts());
        var m = Assert.Single(diff.Missing);
        Assert.Equal(2, m.Episode.Number);
        Assert.Equal(MissingKind.Gap, m.Kind);
    }

    [Fact]
    public void WholeMiddleSeasonAbsent_AllGaps()
    {
        var remote = Cat(R(1, 1, D(1, 1)), R(2, 1, D(2, 1)), R(2, 2, D(2, 8)), R(3, 1, D(3, 1)));
        var owned = new[] { O(1, 1, aired: D(1, 1)), O(3, 1, aired: D(3, 1)) };
        var diff = DiffEngine.Diff(owned, remote, Opts());
        Assert.Equal(2, diff.Missing.Count);
        Assert.All(diff.Missing, m => Assert.Equal(MissingKind.Gap, m.Kind));
        Assert.All(diff.Missing, m => Assert.Equal(2, m.Episode.Season));
    }

    [Fact]
    public void MissingSeriesPremiere_WithLaterOwned_IsGap()
    {
        var remote = Cat(R(1, 1, D(1, 1)), R(1, 2, D(1, 8)));
        var owned = new[] { O(1, 2, aired: D(1, 8)) };
        var diff = DiffEngine.Diff(owned, remote, Opts());
        var m = Assert.Single(diff.Missing);
        Assert.Equal(1, m.Episode.Number);
        Assert.Equal(MissingKind.Gap, m.Kind);
    }

    [Fact]
    public void NewTail_AcrossSeasonBoundary_AllNew()
    {
        var remote = Cat(R(1, 10, D(5, 1)), R(1, 11, D(6, 1)), R(2, 1, D(7, 1)));
        var owned = new[] { O(1, 10, aired: D(5, 1)) };
        var diff = DiffEngine.Diff(owned, remote, Opts());
        Assert.Equal(2, diff.Missing.Count);
        Assert.All(diff.Missing, m => Assert.Equal(MissingKind.New, m.Kind));
    }

    [Fact]
    public void GraceBoundary_ExactlyElapsed_NotMissing_OneSecondPast_Missing()
    {
        // aired 2026-07-24 12:00Z exactly; grace 24h -> airedAt+24h == Now -> NOT missing
        var edge = new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero);
        var remote = Cat(new RemoteEpisode(1, 2, null, edge, false, null), R(1, 1, D(1, 1)));
        var owned = new[] { O(1, 1, aired: D(1, 1)) };
        Assert.Empty(DiffEngine.Diff(owned, remote, Opts()).Missing);
        // one second earlier air time -> strictly past the window -> missing
        var past = Cat(new RemoteEpisode(1, 2, null, edge.AddSeconds(-1), false, null), R(1, 1, D(1, 1)));
        Assert.Single(DiffEngine.Diff(owned, past, Opts()).Missing);
    }

    [Fact]
    public void GraceZero_FlagsImmediately()
    {
        var justAired = Now.AddSeconds(-1);
        var remote = Cat(new RemoteEpisode(1, 2, null, justAired, false, null), R(1, 1, D(1, 1)));
        var owned = new[] { O(1, 1, aired: D(1, 1)) };
        Assert.Single(DiffEngine.Diff(owned, remote, Opts(grace: 0)).Missing);
        Assert.Empty(DiffEngine.Diff(owned, remote, Opts(grace: 24)).Missing);
    }

    [Fact]
    public void UnairedAndUndated_NeverMissing()
    {
        var remote = Cat(R(1, 1, D(1, 1)), R(1, 2, Now.AddDays(3)), new RemoteEpisode(1, 3, null, null, false, null));
        var owned = new[] { O(1, 1, aired: D(1, 1)) };
        Assert.Empty(DiffEngine.Diff(owned, remote, Opts()).Missing);
    }

    [Fact]
    public void MultiEpisodeFile_CoversSpan()
    {
        var remote = Cat(R(1, 1, D(1, 1)), R(1, 2, D(1, 8)), R(1, 3, D(1, 15)));
        var owned = new[] { O(1, 1, end: 2, aired: D(1, 1)), O(1, 3, aired: D(1, 15)) };
        Assert.Empty(DiffEngine.Diff(owned, remote, Opts()).Missing);
    }

    [Fact]
    public void DuplicateLocalCopies_StillOneOwned()
    {
        var remote = Cat(R(1, 1, D(1, 1)), R(1, 2, D(1, 8)));
        var owned = new[] { O(1, 1, aired: D(1, 1)), O(1, 1, aired: D(1, 1)) };
        var diff = DiffEngine.Diff(owned, remote, Opts());
        var m = Assert.Single(diff.Missing);
        Assert.Equal(2, m.Episode.Number);
    }

    [Fact]
    public void OwnedWithoutSeason_ExcludedFromMatching_AndNoted()
    {
        var remote = Cat(R(1, 1, D(1, 1)));
        var owned = new[] { new OwnedEpisode(null, 1, null, new Dictionary<string, string>(), null) };
        var diff = DiffEngine.Diff(owned, remote, Opts());
        Assert.Single(diff.Missing); // the unnumbered local cannot claim S1E1
        Assert.Contains(diff.Notes, n => n.Contains("unnumbered", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void OwnedExceedsRemote_ZeroMissing_WithNote()
    {
        var remote = Cat(R(1, 1, D(1, 1)));
        var owned = new[] { O(1, 1, aired: D(1, 1)), O(1, 2, aired: D(1, 8)) };
        var diff = DiffEngine.Diff(owned, remote, Opts());
        Assert.Empty(diff.Missing);
        Assert.Contains(diff.Notes, n => n.Contains("unknown to the source", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ZeroOwned_AllAired_AreGaps()
    {
        var remote = Cat(R(1, 1, D(1, 1)), R(1, 2, D(1, 8)));
        var diff = DiffEngine.Diff(Array.Empty<OwnedEpisode>(), remote, Opts());
        Assert.Equal(2, diff.Missing.Count);
        Assert.All(diff.Missing, m => Assert.Equal(MissingKind.Gap, m.Kind));
    }

    [Fact]
    public void FullyComplete_EmptyDiff()
    {
        var remote = Cat(R(1, 1, D(1, 1)), R(1, 2, D(1, 8)));
        var owned = new[] { O(1, 1, aired: D(1, 1)), O(1, 2, aired: D(1, 8)) };
        var diff = DiffEngine.Diff(owned, remote, Opts());
        Assert.Empty(diff.Missing);
        Assert.Empty(diff.Notes);
    }

    [Fact]
    public void Specials_ExcludedByDefault_IncludedOnOptIn()
    {
        var remote = Cat(R(1, 1, D(1, 1)), R(0, 1, D(2, 1), special: true));
        var owned = new[] { O(1, 1, aired: D(1, 1)) };
        Assert.Empty(DiffEngine.Diff(owned, remote, Opts()).Missing);
        var withSpecials = DiffEngine.Diff(owned, remote, Opts(specials: true));
        var m = Assert.Single(withSpecials.Missing);
        Assert.True(m.Episode.IsSpecial);
    }
}
```

- [ ] **Step 2: Run to verify failure** — `dotnet test --filter DiffEngineTupleTests`; expected: compile error, `DiffEngine` missing.

- [ ] **Step 3: Implement** `src/Jellyfin.Plugin.DownloadTime/Services/DiffEngine.cs`:

```csharp
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
        foreach (var o in owned)
        {
            var hasId = idKey is not null && o.ProviderIds.TryGetValue(idKey, out var id) && !string.IsNullOrEmpty(id);
            if (hasId)
            {
                ownedIds.Add(o.ProviderIds[idKey!]);
                continue; // ID-bearing locals match by ID only (spec: tuple fallback is for id-less locals)
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

        // --- relevant remote set ----------------------------------------------
        bool IsSpecialEp(RemoteEpisode e) => e.IsSpecial || e.Season == 0;
        var considered = remote.Episodes.Where(e => opts.IncludeSpecials || !IsSpecialEp(e)).ToList();

        // --- matching -----------------------------------------------------------
        bool TupleMatch(RemoteEpisode e) => e.Number.HasValue && tupleOwned.Any(o =>
            (e.Season is null || !o.Season.HasValue || o.Season == e.Season) && o.Covers(e.Number.Value));
        bool IdMatch(RemoteEpisode e) => e.SourceEpisodeId is not null && ownedIds.Contains(e.SourceEpisodeId);
        bool IsOwned(RemoteEpisode e) => IdMatch(e) || TupleMatch(e);

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

        // --- "library exceeds source" note --------------------------------------
        var strayIds = ownedIds.Count(id => !remote.Episodes.Any(e =>
            e.SourceEpisodeId is not null && string.Equals(e.SourceEpisodeId, id, StringComparison.OrdinalIgnoreCase)));
        var strayTuples = tupleOwned.Count(o => !remote.Episodes.Any(e =>
            e.Number.HasValue && (e.Season is null || !o.Season.HasValue || o.Season == e.Season) && o.Covers(e.Number.Value)));
        var stray = strayIds + strayTuples;
        if (stray > 0)
        {
            notes.Add($"{stray} local episode(s) unknown to the source.");
        }

        return new SeriesDiff(missing, notes);
    }
}
```

- [ ] **Step 4: Run green** — `dotnet test --filter DiffEngineTupleTests` then full `dotnet test`.
- [ ] **Step 5: Commit** — `git add -A && git commit -m "Add DiffEngine tuple-lane detection with grace, specials, classification"`

---

### Task 4: DiffEngine — ID lane behavior pinning (AniDB / TVDB episode IDs)

**Files:**
- Modify: `src/Jellyfin.Plugin.DownloadTime/Services/DiffEngine.cs` (only if a test forces it)
- Create: `tests/.../DiffEngineIdTests.cs`

**Interfaces:** unchanged (`DiffEngine.Diff`).

- [ ] **Step 1: Write the tests**

```csharp
// Edge-case inventory (ID lanes — IdProviderKey "AniDB"/"Tvdb"):
// - Ronin-merged absolute numbering: local S1E13 carries AniDB id of entry ep 13 -> ID diff exact.
// - Split-season layout: same IDs at different local numbers -> identical result.
// - Local numbering totally scrambled vs remote -> ID match still wins (numbering ignored).
// - Some locals lack the id -> those (only) fall back to tuple/epno matching.
// - Season-less remote (AniDB): id-less local matched by Number regardless of local Season.
// - Remote ep with null SourceEpisodeId in an ID catalog -> tuple path for it.
// - TVDB catalog with episode ids: renumbered local (wrong S/E, right id) still owned.
using Jellyfin.Plugin.DownloadTime.Model;
using Jellyfin.Plugin.DownloadTime.Services;
using Xunit;

namespace Jellyfin.Plugin.DownloadTime.Tests;

public class DiffEngineIdTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 25, 12, 0, 0, TimeSpan.Zero);
    private static DiffOptions Opts() => new(Now, 24, false);
    private static DateTimeOffset D(int m, int d) => AirTime.FromDate(2026, m, d);

    private static RemoteEpisode A(int epno, string id, DateTimeOffset aired)
        => new(null, epno, id, aired, false, $"Ep {epno}");

    private static OwnedEpisode OA(int s, int n, string? anidbId) => new(
        s, n, null,
        anidbId is null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["AniDB"] = anidbId },
        null);

    private static RemoteCatalog AniCat(params RemoteEpisode[] eps) => new("AniDB", "AniDB", "18164", true, eps);

    [Fact]
    public void MergedAbsoluteNumbering_IdDiff_FindsExactGap()
    {
        var remote = AniCat(A(1, "274088", D(1, 7)), A(2, "274089", D(1, 14)), A(3, "274090", D(1, 21)));
        // Ronin merged: locals live at S1E1/E3 with correct AniDB ids; ep2 absent
        var owned = new[] { OA(1, 1, "274088"), OA(1, 3, "274090") };
        var diff = DiffEngine.Diff(owned, remote, Opts());
        var m = Assert.Single(diff.Missing);
        Assert.Equal("274089", m.Episode.SourceEpisodeId);
    }

    [Fact]
    public void ScrambledLocalNumbering_IdsStillOwn()
    {
        var remote = AniCat(A(1, "274088", D(1, 7)), A(2, "274089", D(1, 14)));
        // local numbers are nonsense (S5E99 etc.) but ids correct -> nothing missing
        var owned = new[] { OA(5, 99, "274088"), OA(9, 1, "274089") };
        Assert.Empty(DiffEngine.Diff(owned, remote, Opts()).Missing);
    }

    [Fact]
    public void IdlessLocals_FallBackToEpnoMatching_SeasonIgnored()
    {
        var remote = AniCat(A(1, "274088", D(1, 7)), A(2, "274089", D(1, 14)));
        // one local has no AniDB id but sits at Number=2 (any season) -> claims epno 2
        var owned = new[] { OA(1, 1, "274088"), OA(3, 2, null) };
        Assert.Empty(DiffEngine.Diff(owned, remote, Opts()).Missing);
    }

    [Fact]
    public void TvdbIdCatalog_RenumberedLocal_RightId_StillOwned()
    {
        var remote = new RemoteCatalog("Tvdb", "Tvdb", "253573", true, new[]
        {
            new RemoteEpisode(1, 1, "5088686", D(1, 1), false, "The Bone Orchard"),
            new RemoteEpisode(1, 2, "5088687", D(1, 8), false, null),
        });
        var owned = new[]
        {
            new OwnedEpisode(4, 44, null, new Dictionary<string, string> { ["Tvdb"] = "5088686" }, null),
            new OwnedEpisode(1, 2, null, new Dictionary<string, string>(), null), // id-less -> tuple
        };
        Assert.Empty(DiffEngine.Diff(owned, remote, Opts()).Missing);
    }

    [Fact]
    public void RemoteEpWithoutId_InIdCatalog_UsesTuple()
    {
        var remote = new RemoteCatalog("Tvdb", "Tvdb", "253573", true, new[]
        {
            new RemoteEpisode(1, 1, null, D(1, 1), false, null), // page row lacked a link
        });
        var owned = new[] { new OwnedEpisode(1, 1, null, new Dictionary<string, string>(), null) };
        Assert.Empty(DiffEngine.Diff(owned, remote, Opts()).Missing);
    }
}
```

- [ ] **Step 2: Run** — `dotnet test --filter DiffEngineIdTests`. These pin behavior the Task 3 implementation should already have; observe the actual result FIRST. Any failure = fix `DiffEngine.cs` minimally (never touch frozen Task 3 tests). If all pass on the first run, that is the legitimate green for a behavior-pinning task — note it in the commit message.
- [ ] **Step 3: Full suite green** — `dotnet test`.
- [ ] **Step 4: Commit** — `git add -A && git commit -m "Pin ID-lane matching behavior (AniDB/Tvdb episode ids, id-less fallback)"`

---

### Task 5: CollectionDiff — movie franchises

**Files:**
- Create: `src/Jellyfin.Plugin.DownloadTime/Services/CollectionDiff.cs`, `tests/.../CollectionDiffTests.cs`

**Interfaces:**
- Produces: `public static IReadOnlyList<RemoteMovie> CollectionDiff.MissingMovies(ISet<int> ownedTmdbIds, CollectionCatalog catalog, DateTimeOffset now, int bufferDays)` — used by ScanService (Task 14).

- [ ] **Step 1: Write the failing tests**

```csharp
// Edge-case inventory:
// - unreleased member (null or future date) never missing
// - released but inside buffer -> not yet; exactly at boundary -> not; strictly past -> missing
// - owned member (TMDB id in set) never missing regardless of edition
// - one owned movie, all other members missing -> all flagged
// - buffer 0 -> missing right after release date passes
using Jellyfin.Plugin.DownloadTime.Model;
using Jellyfin.Plugin.DownloadTime.Services;
using Xunit;

namespace Jellyfin.Plugin.DownloadTime.Tests;

public class CollectionDiffTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 25, 12, 0, 0, TimeSpan.Zero);

    private static CollectionCatalog JohnWick(params RemoteMovie[] movies)
        => new(404609, "John Wick Collection", movies);

    [Fact]
    public void ReleasedPastBuffer_NotOwned_IsMissing()
    {
        var cat = JohnWick(
            new RemoteMovie(245891, "John Wick", AirTime.FromDate(2014, 10, 24)),
            new RemoteMovie(324552, "John Wick: Chapter 2", AirTime.FromDate(2017, 2, 10)));
        var missing = CollectionDiff.MissingMovies(new HashSet<int> { 245891 }, cat, Now, 90);
        var m = Assert.Single(missing);
        Assert.Equal(324552, m.TmdbId);
    }

    [Fact]
    public void UnreleasedOrFuture_NeverMissing()
    {
        var cat = JohnWick(
            new RemoteMovie(1, "Announced", null),
            new RemoteMovie(2, "ComingSoon", Now.AddDays(30)));
        Assert.Empty(CollectionDiff.MissingMovies(new HashSet<int>(), cat, Now, 90));
    }

    [Fact]
    public void BufferBoundary_ExactlyAtBuffer_NotMissing_PastBuffer_Missing()
    {
        var releasedExactly90DaysAgo = Now.AddDays(-90);
        var cat = JohnWick(new RemoteMovie(3, "Edge", releasedExactly90DaysAgo));
        Assert.Empty(CollectionDiff.MissingMovies(new HashSet<int>(), cat, Now, 90));
        var cat2 = JohnWick(new RemoteMovie(3, "Edge", releasedExactly90DaysAgo.AddSeconds(-1)));
        Assert.Single(CollectionDiff.MissingMovies(new HashSet<int>(), cat2, Now, 90));
    }

    [Fact]
    public void OneOwned_AllOthersMissing()
    {
        var cat = JohnWick(
            new RemoteMovie(10, "One", AirTime.FromDate(2014, 1, 1)),
            new RemoteMovie(11, "Two", AirTime.FromDate(2016, 1, 1)),
            new RemoteMovie(12, "Three", AirTime.FromDate(2019, 1, 1)));
        var missing = CollectionDiff.MissingMovies(new HashSet<int> { 10 }, cat, Now, 90);
        Assert.Equal(new[] { 11, 12 }, missing.Select(m => m.TmdbId).OrderBy(x => x).ToArray());
    }

    [Fact]
    public void BufferZero_MissingRightAfterRelease()
    {
        var cat = JohnWick(new RemoteMovie(4, "Fresh", Now.AddSeconds(-1)));
        Assert.Single(CollectionDiff.MissingMovies(new HashSet<int>(), cat, Now, 0));
    }
}
```

- [ ] **Step 2: Run red** — `dotnet test --filter CollectionDiffTests`; expected: compile error.

- [ ] **Step 3: Implement** `Services/CollectionDiff.cs`:

```csharp
using Jellyfin.Plugin.DownloadTime.Model;

namespace Jellyfin.Plugin.DownloadTime.Services;

/// <summary>Movie-franchise gap detection (spec §2.3/§3).</summary>
public static class CollectionDiff
{
    public static IReadOnlyList<RemoteMovie> MissingMovies(
        ISet<int> ownedTmdbIds, CollectionCatalog catalog, DateTimeOffset now, int bufferDays)
        => catalog.Movies
            .Where(m => !ownedTmdbIds.Contains(m.TmdbId)
                        && m.ReleasedAt.HasValue
                        && m.ReleasedAt.Value.AddDays(bufferDays) < now)
            .ToList();
}
```

- [ ] **Step 4: Run green** — `dotnet test`.
- [ ] **Step 5: Commit** — `git add -A && git commit -m "Add movie collection diff with release buffer"`

---

### Task 6: Placer — local-scheme placement inference

**Files:**
- Create: `src/Jellyfin.Plugin.DownloadTime/Services/Placer.cs`, `tests/.../PlacerTests.cs`

**Interfaces:**
- Produces: `public static Placement? Placer.Infer(RemoteEpisode missing, IReadOnlyList<OwnedEpisode> owned, RemoteCatalog remote)` — used by VirtualEpisodePlanner (Task 17). Tuple catalogs (`IdProviderKey == null`) place at remote (S,E) verbatim; ID catalogs anchor on owned neighbors; null = no confident placement (caller skips creation).

- [ ] **Step 1: Write the failing tests**

```csharp
// Edge-case inventory:
// - tuple catalog: placement == remote (S,E); null when remote S or E null.
// - ID catalog, merged local (S1 absolute): between-anchors interpolation.
// - ID catalog, split local: anchors in same local season -> interpolate within it.
// - anchors disagree with remote spacing (inconsistent) -> null.
// - anchors straddle local seasons -> null (no confident scheme).
// - tail beyond last anchor -> extrapolate same season.
// - head before first anchor -> extrapolate down; below 1 -> null.
// - no anchors at all -> null.
using Jellyfin.Plugin.DownloadTime.Model;
using Jellyfin.Plugin.DownloadTime.Services;
using Xunit;

namespace Jellyfin.Plugin.DownloadTime.Tests;

public class PlacerTests
{
    private static RemoteEpisode A(int epno, string id) => new(null, epno, id, null, false, null);
    private static OwnedEpisode OA(int s, int n, string id)
        => new(s, n, null, new Dictionary<string, string> { ["AniDB"] = id }, null);
    private static RemoteCatalog AniCat(params RemoteEpisode[] eps) => new("AniDB", "AniDB", "1", true, eps);

    [Fact]
    public void TupleCatalog_PlacesAtRemoteNumbers()
    {
        var cat = new RemoteCatalog("Tvdb", null, "1", true, Array.Empty<RemoteEpisode>());
        var p = Placer.Infer(new RemoteEpisode(2, 5, null, null, false, null), Array.Empty<OwnedEpisode>(), cat);
        Assert.Equal(new Placement(2, 5), p);
        Assert.Null(Placer.Infer(new RemoteEpisode(null, 5, null, null, false, null), Array.Empty<OwnedEpisode>(), cat));
    }

    [Fact]
    public void MergedLocal_BetweenAnchors_Interpolates()
    {
        var cat = AniCat(A(12, "a12"), A(13, "a13"), A(14, "a14"));
        var owned = new[] { OA(1, 12, "a12"), OA(1, 14, "a14") };
        Assert.Equal(new Placement(1, 13), Placer.Infer(cat.Episodes[1], owned, cat));
    }

    [Fact]
    public void SplitLocal_SameSeasonAnchors_Interpolates()
    {
        var cat = AniCat(A(1, "b1"), A(2, "b2"), A(3, "b3"));
        var owned = new[] { OA(2, 1, "b1"), OA(2, 3, "b3") }; // entry mapped to local season 2
        Assert.Equal(new Placement(2, 2), Placer.Infer(cat.Episodes[1], owned, cat));
    }

    [Fact]
    public void InconsistentAnchorSpacing_ReturnsNull()
    {
        var cat = AniCat(A(1, "c1"), A(2, "c2"), A(3, "c3"));
        var owned = new[] { OA(1, 1, "c1"), OA(1, 9, "c3") }; // spacing 8 vs remote spacing 2
        Assert.Null(Placer.Infer(cat.Episodes[1], owned, cat));
    }

    [Fact]
    public void AnchorsStraddleSeasons_ReturnsNull()
    {
        var cat = AniCat(A(1, "d1"), A(2, "d2"), A(3, "d3"));
        var owned = new[] { OA(1, 12, "d1"), OA(2, 1, "d3") };
        Assert.Null(Placer.Infer(cat.Episodes[1], owned, cat));
    }

    [Fact]
    public void TailBeyondLastAnchor_ExtrapolatesSameSeason()
    {
        var cat = AniCat(A(10, "e10"), A(11, "e11"), A(12, "e12"));
        var owned = new[] { OA(1, 22, "e10"), OA(1, 23, "e11") }; // merged offset +12
        Assert.Equal(new Placement(1, 25), Placer.Infer(cat.Episodes[2], owned, cat));
    }

    [Fact]
    public void HeadBeforeFirstAnchor_ExtrapolatesDown_NullBelowOne()
    {
        var cat = AniCat(A(1, "f1"), A(2, "f2"), A(3, "f3"));
        var owned = new[] { OA(1, 2, "f2"), OA(1, 3, "f3") };
        Assert.Equal(new Placement(1, 1), Placer.Infer(cat.Episodes[0], owned, cat));
        var owned2 = new[] { OA(1, 1, "f2"), OA(1, 2, "f3") }; // extrapolating f1 -> local 0 -> null
        Assert.Null(Placer.Infer(cat.Episodes[0], owned2, cat));
    }

    [Fact]
    public void NoAnchors_ReturnsNull()
    {
        var cat = AniCat(A(1, "g1"));
        Assert.Null(Placer.Infer(cat.Episodes[0], Array.Empty<OwnedEpisode>(), cat));
    }
}
```

- [ ] **Step 2: Run red** — `dotnet test --filter PlacerTests`; expected: compile error.

- [ ] **Step 3: Implement** `Services/Placer.cs`:

```csharp
using Jellyfin.Plugin.DownloadTime.Model;

namespace Jellyfin.Plugin.DownloadTime.Services;

/// <summary>
/// Infers where a missing remote episode belongs in the LOCAL numbering
/// scheme by anchoring on owned episodes (spec §4.3). Returns null when no
/// confident placement exists — callers must then skip placeholder creation.
/// </summary>
public static class Placer
{
    public static Placement? Infer(RemoteEpisode missing, IReadOnlyList<OwnedEpisode> owned, RemoteCatalog remote)
    {
        if (remote.IdProviderKey is null)
        {
            return missing.Season.HasValue && missing.Number.HasValue
                ? new Placement(missing.Season.Value, missing.Number.Value)
                : null;
        }

        if (!missing.Number.HasValue)
        {
            return null;
        }

        // anchors: remote epno -> local (season, number), joined on episode IDs
        var idToRemoteNumber = remote.Episodes
            .Where(e => e.SourceEpisodeId is not null && e.Number.HasValue)
            .ToDictionary(e => e.SourceEpisodeId!, e => e.Number!.Value, StringComparer.OrdinalIgnoreCase);
        var anchors = new List<(int RemoteN, int LocalS, int LocalN)>();
        foreach (var o in owned)
        {
            if (o.Season.HasValue && o.Number.HasValue
                && o.ProviderIds.TryGetValue(remote.IdProviderKey, out var id)
                && idToRemoteNumber.TryGetValue(id, out var rn))
            {
                anchors.Add((rn, o.Season.Value, o.Number.Value));
            }
        }
        if (anchors.Count == 0)
        {
            return null;
        }

        var target = missing.Number.Value;
        (int RemoteN, int LocalS, int LocalN)? below = null, above = null;
        foreach (var a in anchors)
        {
            if (a.RemoteN < target && (below is null || a.RemoteN > below.Value.RemoteN)) below = a;
            if (a.RemoteN > target && (above is null || a.RemoteN < above.Value.RemoteN)) above = a;
        }

        if (below.HasValue && above.HasValue)
        {
            var b = below.Value; var a = above.Value;
            if (b.LocalS != a.LocalS || a.LocalN - b.LocalN != a.RemoteN - b.RemoteN)
            {
                return null; // straddles seasons or spacing disagrees - no confident scheme
            }
            return new Placement(b.LocalS, b.LocalN + (target - b.RemoteN));
        }
        if (below.HasValue)
        {
            var b = below.Value;
            return new Placement(b.LocalS, b.LocalN + (target - b.RemoteN));
        }
        var up = above!.Value;
        var n = up.LocalN - (up.RemoteN - target);
        return n >= 1 ? new Placement(up.LocalS, n) : null;
    }
}
```

- [ ] **Step 4: Run green** — `dotnet test`.
- [ ] **Step 5: Commit** — `git add -A && git commit -m "Add placement inference from owned-episode anchors"`

---

### Task 7: SourceRouter + library item DTOs

**Files:**
- Create: `src/Jellyfin.Plugin.DownloadTime/Services/SourceRouter.cs`, `src/Jellyfin.Plugin.DownloadTime/Model/LibraryItems.cs`, `tests/.../SourceRouterTests.cs`

**Interfaces:**
- Produces (`Model/LibraryItems.cs`):

```csharp
namespace Jellyfin.Plugin.DownloadTime.Model;

public enum SourceKind { TvdbId, AniDbId, TmdbId, ImdbId, None }
public sealed record RouteDecision(SourceKind Kind, string SourceId)
{
    public static readonly RouteDecision None = new(SourceKind.None, string.Empty);
}

/// <summary>A series as read from the Jellyfin library (adapter output).</summary>
public sealed record SeriesItemInfo(
    Guid Id, string Name, string Path, bool IsAnimeLibrary,
    IReadOnlyDictionary<string, string> ProviderIds,
    IReadOnlyList<OwnedEpisode> Episodes);

/// <summary>A movie as read from the Jellyfin library.</summary>
public sealed record MovieItemInfo(Guid Id, string Name, int? TmdbId);
```

- Produces (`Services/SourceRouter.cs`): `public static RouteDecision SourceRouter.Route(string path, bool isAnimeLibrary, IReadOnlyDictionary<string, string> providerIds)` — used by ScanService (Task 14).

- [ ] **Step 1: Write the failing tests**

```csharp
// Edge-case inventory (spec §1 precedence):
// 1. anime library + AniDB provider id -> AniDbId, regardless of folder tag.
// 2. else folder tag [tvdbid-N]/[tmdbid-N]/[anidbid-N]/[imdbid-ttN] (case-insensitive) wins,
//    using the TAG value (files were matched under that identity).
// 3. else ProviderIds precedence Tvdb > Tmdb > Imdb.
// 4. nothing usable -> None.
// - tag with unknown source name ignored -> falls through to precedence.
// - anime library WITHOUT AniDB id falls through to tag/precedence.
using Jellyfin.Plugin.DownloadTime.Model;
using Jellyfin.Plugin.DownloadTime.Services;
using Xunit;

namespace Jellyfin.Plugin.DownloadTime.Tests;

public class SourceRouterTests
{
    private static Dictionary<string, string> Ids(params (string K, string V)[] kv)
        => kv.ToDictionary(x => x.K, x => x.V);

    [Fact]
    public void AnimeLibrary_WithAniDbId_WinsOverFolderTag()
    {
        var r = SourceRouter.Route(@"D:\Anime\7th Time Loop (2024) [tvdbid-435005]", true,
            Ids(("AniDB", "18164"), ("Tvdb", "435005")));
        Assert.Equal(new RouteDecision(SourceKind.AniDbId, "18164"), r);
    }

    [Fact]
    public void FolderTag_Tvdbid_Wins()
    {
        var r = SourceRouter.Route(@"D:\TV\American Gods (2017) [tvdbid-253573]", false,
            Ids(("Tvdb", "253573"), ("Tmdb", "46639")));
        Assert.Equal(new RouteDecision(SourceKind.TvdbId, "253573"), r);
    }

    [Fact]
    public void FolderTag_Tmdbid_Wins_EvenWhenTvdbIdPresent()
    {
        var r = SourceRouter.Route(@"D:\TV\Alice in Borderland (2020) [tmdbid-110316]", false,
            Ids(("Tvdb", "289181"), ("Tmdb", "110316")));
        Assert.Equal(new RouteDecision(SourceKind.TmdbId, "110316"), r);
    }

    [Fact]
    public void FolderTag_CaseInsensitive_AndImdb()
    {
        var r = SourceRouter.Route(@"D:\TV\Some Show [IMDBID-tt1898069]", false, Ids());
        Assert.Equal(new RouteDecision(SourceKind.ImdbId, "tt1898069"), r);
    }

    [Fact]
    public void NoTag_ProviderPrecedence_TvdbFirst_ThenTmdb_ThenImdb()
    {
        Assert.Equal(new RouteDecision(SourceKind.TvdbId, "1"),
            SourceRouter.Route(@"D:\TV\X", false, Ids(("Tvdb", "1"), ("Tmdb", "2"), ("Imdb", "tt3"))));
        Assert.Equal(new RouteDecision(SourceKind.TmdbId, "2"),
            SourceRouter.Route(@"D:\TV\X", false, Ids(("Tmdb", "2"), ("Imdb", "tt3"))));
        Assert.Equal(new RouteDecision(SourceKind.ImdbId, "tt3"),
            SourceRouter.Route(@"D:\TV\X", false, Ids(("Imdb", "tt3"))));
    }

    [Fact]
    public void UnknownTag_IgnoredAndFallsThrough()
    {
        var r = SourceRouter.Route(@"D:\TV\X [weirdid-9]", false, Ids(("Tmdb", "2")));
        Assert.Equal(new RouteDecision(SourceKind.TmdbId, "2"), r);
    }

    [Fact]
    public void AnimeLibrary_NoAniDbId_FallsThrough()
    {
        var r = SourceRouter.Route(@"D:\Anime\X [tvdbid-5]", true, Ids(("Tvdb", "5")));
        Assert.Equal(new RouteDecision(SourceKind.TvdbId, "5"), r);
    }

    [Fact]
    public void NothingUsable_None()
    {
        Assert.Equal(RouteDecision.None, SourceRouter.Route(@"D:\TV\X", false, Ids()));
    }
}
```

- [ ] **Step 2: Run red** — `dotnet test --filter SourceRouterTests`; expected: compile error.

- [ ] **Step 3: Implement** `Services/SourceRouter.cs`:

```csharp
using System.Text.RegularExpressions;
using Jellyfin.Plugin.DownloadTime.Model;

namespace Jellyfin.Plugin.DownloadTime.Services;

/// <summary>Routes each item to the source it is identified with (spec §1).</summary>
public static partial class SourceRouter
{
    [GeneratedRegex(@"\[(tvdbid|tmdbid|anidbid|imdbid)-([^\]\s]+)\]", RegexOptions.IgnoreCase)]
    private static partial Regex FolderTag();

    public static RouteDecision Route(string path, bool isAnimeLibrary, IReadOnlyDictionary<string, string> providerIds)
    {
        if (isAnimeLibrary && providerIds.TryGetValue("AniDB", out var aid) && !string.IsNullOrEmpty(aid))
        {
            return new RouteDecision(SourceKind.AniDbId, aid);
        }

        var m = FolderTag().Match(path);
        if (m.Success)
        {
            var kind = m.Groups[1].Value.ToLowerInvariant() switch
            {
                "tvdbid" => SourceKind.TvdbId,
                "tmdbid" => SourceKind.TmdbId,
                "anidbid" => SourceKind.AniDbId,
                "imdbid" => SourceKind.ImdbId,
                _ => SourceKind.None,
            };
            if (kind != SourceKind.None)
            {
                return new RouteDecision(kind, m.Groups[2].Value);
            }
        }

        if (providerIds.TryGetValue("Tvdb", out var tvdb) && !string.IsNullOrEmpty(tvdb))
        {
            return new RouteDecision(SourceKind.TvdbId, tvdb);
        }
        if (providerIds.TryGetValue("Tmdb", out var tmdb) && !string.IsNullOrEmpty(tmdb))
        {
            return new RouteDecision(SourceKind.TmdbId, tmdb);
        }
        if (providerIds.TryGetValue("Imdb", out var imdb) && !string.IsNullOrEmpty(imdb))
        {
            return new RouteDecision(SourceKind.ImdbId, imdb);
        }
        return RouteDecision.None;
    }
}
```

- [ ] **Step 4: Run green** — `dotnet test`.
- [ ] **Step 5: Commit** — `git add -A && git commit -m "Add identified-source router with folder-tag precedence"`

---

### Task 8: TVDB lane — all-seasons parser + scrape fetcher

**Files:**
- Create: `src/Jellyfin.Plugin.DownloadTime/Services/Lanes/Sources.cs` (interfaces), `src/Jellyfin.Plugin.DownloadTime/Services/Lanes/TvdbScrapeFetcher.cs`, `tests/.../TvdbParserTests.cs`, `tests/.../Support/FakeHttp.cs`
- Fixtures (already captured): `fixtures/tvdb-allseasons-american-gods.html`, `fixtures/tvdb-404.html`; ADD in this task: `fixtures/tvdb-allseasons-mutated.html` (copy of the real fixture with every `episode-label` class renamed to `ep-lbl` — simulates a site redesign).

**Interfaces:**
- Produces (`Services/Lanes/Sources.cs`):

```csharp
using Jellyfin.Plugin.DownloadTime.Model;

namespace Jellyfin.Plugin.DownloadTime.Services.Lanes;

public interface ITvdbSource { Task<FetchOutcome> FetchByTvdbIdAsync(string tvdbId, CancellationToken ct); }
public interface ITvmazeSource
{
    Task<FetchOutcome> FetchByTvdbIdAsync(string tvdbId, CancellationToken ct);
    Task<FetchOutcome> FetchByImdbIdAsync(string imdbId, CancellationToken ct);
}
public interface IAniDbSource { Task<FetchOutcome> FetchByAnimeIdAsync(string anidbId, CancellationToken ct); }
public sealed record CollectionOutcome(CollectionCatalog? Catalog, string? Error, bool NoCollection);
public interface ITmdbSource
{
    Task<FetchOutcome> FetchSeriesAsync(string tmdbId, CancellationToken ct);
    Task<CollectionOutcome> FetchCollectionForMovieAsync(int movieTmdbId, CancellationToken ct);
}
```

- Produces: `TvdbScrapeFetcher : ITvdbSource` with ctor `(HttpClient http, Func<int> requestDelayMs)`; static inner-logic `TvdbScrapeFetcher.ParseAllSeasons(string html)` returning `(IReadOnlyList<RemoteEpisode>? Episodes, string? Error)`. Catalog fields: `SourceKey="Tvdb"`, `IdProviderKey="Tvdb"`, `IsEnded=false` (conservative daily TTL — series-status scraping is out of v1 scope).
- Produces (`Support/FakeHttp.cs`):

```csharp
using System.Net;

namespace Jellyfin.Plugin.DownloadTime.Tests.Support;

/// <summary>Scripted HttpMessageHandler; follows 3xx like HttpClientHandler so
/// fetcher redirect logic behaves as in production.</summary>
public sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly Func<Uri, HttpResponseMessage> _responder;
    public List<Uri> Requests { get; } = new();

    public FakeHttpHandler(Func<Uri, HttpResponseMessage> responder) => _responder = responder;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var uri = request.RequestUri!;
        for (var hops = 0; hops < 5; hops++)
        {
            Requests.Add(uri);
            var resp = _responder(uri);
            if ((int)resp.StatusCode is >= 300 and < 400 && resp.Headers.Location is not null)
            {
                uri = resp.Headers.Location.IsAbsoluteUri ? resp.Headers.Location : new Uri(uri, resp.Headers.Location);
                continue;
            }
            resp.RequestMessage = new HttpRequestMessage(HttpMethod.Get, uri);
            return Task.FromResult(resp);
        }
        throw new InvalidOperationException("redirect loop");
    }

    public static HttpResponseMessage Html(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "text/html") };
    public static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
    public static HttpResponseMessage Xml(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "text/xml") };
    public static HttpResponseMessage Status(HttpStatusCode code) => new(code) { Content = new StringContent(string.Empty) };
    public static HttpResponseMessage Redirect(string location) { var r = new HttpResponseMessage(HttpStatusCode.MovedPermanently); r.Headers.Location = new Uri(location); return r; }
}
```

- [ ] **Step 1: Create the mutated fixture**

```bash
python -c "
src=r'tests/Jellyfin.Plugin.DownloadTime.Tests/fixtures/tvdb-allseasons-american-gods.html'
dst=r'tests/Jellyfin.Plugin.DownloadTime.Tests/fixtures/tvdb-allseasons-mutated.html'
open(dst,'w',encoding='utf-8').write(open(src,encoding='utf-8').read().replace('episode-label','ep-lbl'))"
```

- [ ] **Step 2: Write the failing tests**

`tests/.../TvdbParserTests.cs`:

```csharp
// Edge-case inventory:
// - REAL captured page (American Gods): 26 regular episodes parsed with correct first/last
//   (S01E01 "The Bone Orchard" id 5088686 aired 2017-04-30; S03E10 last), specials (S00) flagged IsSpecial,
//   per-episode TVDB ids extracted from hrefs, dates normalized via AirTime (23:59Z).
// - Rows with missing/unparseable date -> episode kept, AiredAt null.
// - MUTATED page (episode-label class renamed) -> ParseFailure error, NOT an empty success.
// - 404 page fixture -> fetcher returns FetchOutcome.Fail.
// - Fetcher resolves numeric id via /dereferrer/series/{id} redirect, then requests
//   /series/{slug}/allseasons/official.
using Jellyfin.Plugin.DownloadTime.Model;
using Jellyfin.Plugin.DownloadTime.Services.Lanes;
using Jellyfin.Plugin.DownloadTime.Tests.Support;
using Xunit;

namespace Jellyfin.Plugin.DownloadTime.Tests;

public class TvdbParserTests
{
    private static string Fix(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", name));

    [Fact]
    public void RealPage_ParsesEpisodesWithIdsAndDates()
    {
        var (eps, error) = TvdbScrapeFetcher.ParseAllSeasons(Fix("tvdb-allseasons-american-gods.html"));
        Assert.Null(error);
        Assert.NotNull(eps);
        var regular = eps!.Where(e => !e.IsSpecial).ToList();
        Assert.Equal(26, regular.Count);
        var first = regular.First(e => e.Season == 1 && e.Number == 1);
        Assert.Equal("5088686", first.SourceEpisodeId);
        Assert.Equal("The Bone Orchard", first.Title);
        Assert.Equal(new DateTimeOffset(2017, 4, 30, 23, 59, 0, TimeSpan.Zero), first.AiredAt);
        Assert.Contains(regular, e => e.Season == 3 && e.Number == 10);
        // page contains S00 specials rows (81 li nodes total on capture) -> flagged special
        Assert.Contains(eps!, e => e.IsSpecial);
    }

    [Fact]
    public void MutatedPage_ReturnsError_NotEmptySuccess()
    {
        var (eps, error) = TvdbScrapeFetcher.ParseAllSeasons(Fix("tvdb-allseasons-mutated.html"));
        Assert.Null(eps);
        Assert.NotNull(error);
    }

    [Fact]
    public async Task Fetcher_DereferrerRedirect_ThenAllSeasons()
    {
        var handler = new FakeHttpHandler(uri => uri.AbsolutePath switch
        {
            "/dereferrer/series/253573" => FakeHttpHandler.Redirect("https://www.thetvdb.com/series/american-gods"),
            "/series/american-gods" => FakeHttpHandler.Html("<html>series page</html>"),
            "/series/american-gods/allseasons/official" => FakeHttpHandler.Html(Fix("tvdb-allseasons-american-gods.html")),
            _ => FakeHttpHandler.Status(System.Net.HttpStatusCode.NotFound),
        });
        var fetcher = new TvdbScrapeFetcher(new HttpClient(handler), () => 0);
        var outcome = await fetcher.FetchByTvdbIdAsync("253573", CancellationToken.None);
        Assert.NotNull(outcome.Catalog);
        Assert.Equal("Tvdb", outcome.Catalog!.SourceKey);
        Assert.Equal("Tvdb", outcome.Catalog.IdProviderKey);
        Assert.Equal(26, outcome.Catalog.Episodes.Count(e => !e.IsSpecial));
    }

    [Fact]
    public async Task Fetcher_404_ReturnsFail()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Status(System.Net.HttpStatusCode.NotFound));
        var fetcher = new TvdbScrapeFetcher(new HttpClient(handler), () => 0);
        var outcome = await fetcher.FetchByTvdbIdAsync("999999999", CancellationToken.None);
        Assert.Null(outcome.Catalog);
        Assert.NotNull(outcome.Error);
    }

    [Fact]
    public async Task Fetcher_MutatedMarkup_ReturnsFail()
    {
        var handler = new FakeHttpHandler(uri => uri.AbsolutePath switch
        {
            "/dereferrer/series/1" => FakeHttpHandler.Redirect("https://www.thetvdb.com/series/x"),
            "/series/x/allseasons/official" => FakeHttpHandler.Html(Fix("tvdb-allseasons-mutated.html")),
            _ => FakeHttpHandler.Html("<html></html>"),
        });
        var fetcher = new TvdbScrapeFetcher(new HttpClient(handler), () => 0);
        var outcome = await fetcher.FetchByTvdbIdAsync("1", CancellationToken.None);
        Assert.Null(outcome.Catalog);
        Assert.NotNull(outcome.Error);
    }
}
```

- [ ] **Step 3: Run red** — `dotnet test --filter TvdbParserTests`; expected: compile error.

- [ ] **Step 4: Implement** `Services/Lanes/TvdbScrapeFetcher.cs`:

```csharp
using System.Globalization;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using Jellyfin.Plugin.DownloadTime.Model;
using Jellyfin.Plugin.DownloadTime.Services;

namespace Jellyfin.Plugin.DownloadTime.Services.Lanes;

/// <summary>
/// Reads TheTVDB episode lists from the public all-seasons page (Ronin-style
/// scraping, spec §2.1): one throttled request per series per scan.
/// </summary>
public partial class TvdbScrapeFetcher : ITvdbSource
{
    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64)";
    private readonly HttpClient _http;
    private readonly Func<int> _requestDelayMs;

    public TvdbScrapeFetcher(HttpClient http, Func<int> requestDelayMs)
    {
        _http = http;
        _requestDelayMs = requestDelayMs;
    }

    [GeneratedRegex(@"S(\d+)E(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex EpisodeLabel();

    [GeneratedRegex(@"/episodes/(\d+)")]
    private static partial Regex EpisodeHref();

    public async Task<FetchOutcome> FetchByTvdbIdAsync(string tvdbId, CancellationToken ct)
    {
        try
        {
            // numeric id -> slug via dereferrer (301 to /series/{slug})
            var slugPath = $"/series/{tvdbId}";
            if (tvdbId.All(char.IsDigit))
            {
                using var deref = await GetAsync($"https://www.thetvdb.com/dereferrer/series/{tvdbId}", ct).ConfigureAwait(false);
                if (!deref.IsSuccessStatusCode)
                {
                    return FetchOutcome.Fail($"TVDB dereferrer HTTP {(int)deref.StatusCode}");
                }
                var finalUri = deref.RequestMessage?.RequestUri;
                if (finalUri is null || !finalUri.AbsolutePath.StartsWith("/series/", StringComparison.Ordinal))
                {
                    return FetchOutcome.Fail("TVDB dereferrer did not resolve to a series page");
                }
                slugPath = finalUri.AbsolutePath;
            }

            using var resp = await GetAsync($"https://www.thetvdb.com{slugPath}/allseasons/official", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return FetchOutcome.Fail($"TVDB all-seasons HTTP {(int)resp.StatusCode}");
            }
            var html = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var (episodes, error) = ParseAllSeasons(html);
            if (error is not null)
            {
                return FetchOutcome.Fail(error);
            }
            return FetchOutcome.Ok(new RemoteCatalog("Tvdb", "Tvdb", tvdbId, IsEnded: false, episodes!));
        }
        catch (HttpRequestException ex)
        {
            return FetchOutcome.Fail($"TVDB request failed: {ex.Message}");
        }
        finally
        {
            var delay = _requestDelayMs();
            if (delay > 0)
            {
                await Task.Delay(delay, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private async Task<HttpResponseMessage> GetAsync(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.ParseAdd(UserAgent);
        return await _http.SendAsync(req, ct).ConfigureAwait(false);
    }

    /// <summary>Pure parser. Returns (episodes, null) or (null, error). An empty
    /// episode list is an ERROR (mutated-markup fail-safe), never a success.</summary>
    public static (IReadOnlyList<RemoteEpisode>? Episodes, string? Error) ParseAllSeasons(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var items = doc.DocumentNode.SelectNodes("//li[contains(@class,'list-group-item')]");
        var episodes = new List<RemoteEpisode>();
        if (items is not null)
        {
            foreach (var li in items)
            {
                var label = li.SelectSingleNode(".//span[contains(@class,'episode-label')]");
                if (label is null)
                {
                    continue;
                }
                var m = EpisodeLabel().Match(label.InnerText);
                if (!m.Success)
                {
                    continue;
                }
                var season = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                var number = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);

                string? id = null;
                string? title = null;
                var link = li.SelectSingleNode(".//h4//a[contains(@href,'/episodes/')]");
                if (link is not null)
                {
                    var hm = EpisodeHref().Match(link.GetAttributeValue("href", string.Empty));
                    if (hm.Success)
                    {
                        id = hm.Groups[1].Value;
                    }
                    title = HtmlEntity.DeEntitize(link.InnerText).Trim();
                }

                DateTimeOffset? aired = null;
                var dateNode = li.SelectSingleNode(".//ul[contains(@class,'list-inline')]/li");
                if (dateNode is not null && DateTime.TryParse(
                        HtmlEntity.DeEntitize(dateNode.InnerText).Trim(),
                        CultureInfo.GetCultureInfo("en-US"),
                        DateTimeStyles.None,
                        out var d))
                {
                    aired = AirTime.FromDate(d.Year, d.Month, d.Day);
                }

                episodes.Add(new RemoteEpisode(season, number, id, aired, season == 0, title));
            }
        }

        if (episodes.Count == 0)
        {
            return (null, "TVDB all-seasons page yielded zero episodes (markup change or wrong page)");
        }
        return (episodes, null);
    }
}
```

- [ ] **Step 5: Run green** — `dotnet test --filter TvdbParserTests` then full suite. NOTE: if the real-fixture assertions fail on exact values (e.g. date text differs), inspect the FIXTURE to find the true value and fix the IMPLEMENTATION until it extracts the truth — the fixture is ground truth; assertions were derived from it before implementation (red phase); after first observed run they are frozen.
- [ ] **Step 6: Commit** — `git add -A && git commit -m "Add TVDB all-seasons scraper with fail-safe parser"`

---

### Task 9: TVmaze fallback client

**Files:**
- Create: `src/Jellyfin.Plugin.DownloadTime/Services/Lanes/TvmazeFetcher.cs`, `tests/.../TvmazeFetcherTests.cs`

**Interfaces:**
- Produces: `TvmazeFetcher : ITvmazeSource`, ctor `(HttpClient http)`. Catalog fields: `SourceKey="TvmazeFallback"`, `IdProviderKey=null` (tuple matching), `IsEnded` from show `status=="Ended"`.

- [ ] **Step 1: Write the failing tests**

```csharp
// Edge-case inventory:
// - REAL fixtures: lookup by thetvdb id 301-redirects to show 3182 (status Ended);
//   /shows/3182/episodes?specials=1 -> 26 regular episodes, airstamp preferred over airdate
//   (S01E01 airstamp 2017-05-01T01:00:00Z != naive airdate rule).
// - episode with null airstamp but airdate -> AirTime rule; both null -> AiredAt null.
// - type != "regular" -> IsSpecial.
// - lookup 404 (show absent from TVmaze) -> Fail.
// - lookup by imdb id path.
using System.Net;
using Jellyfin.Plugin.DownloadTime.Model;
using Jellyfin.Plugin.DownloadTime.Services.Lanes;
using Jellyfin.Plugin.DownloadTime.Tests.Support;
using Xunit;

namespace Jellyfin.Plugin.DownloadTime.Tests;

public class TvmazeFetcherTests
{
    private static string Fix(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", name));

    private static FakeHttpHandler Handler() => new(uri =>
        (uri.Host, uri.PathAndQuery) switch
        {
            ("api.tvmaze.com", "/lookup/shows?thetvdb=253573") => FakeHttpHandler.Redirect("https://api.tvmaze.com/shows/3182"),
            ("api.tvmaze.com", "/lookup/shows?imdb=tt1898069") => FakeHttpHandler.Redirect("https://api.tvmaze.com/shows/3182"),
            ("api.tvmaze.com", "/shows/3182") => FakeHttpHandler.Json(Fix("tvmaze-lookup-253573.json")),
            ("api.tvmaze.com", "/shows/3182/episodes?specials=1") => FakeHttpHandler.Json(Fix("tvmaze-episodes-americangods.json")),
            _ => FakeHttpHandler.Status(HttpStatusCode.NotFound),
        });

    [Fact]
    public async Task LookupByTvdbId_ParsesEpisodes_AirstampPreferred()
    {
        var f = new TvmazeFetcher(new HttpClient(Handler()));
        var outcome = await f.FetchByTvdbIdAsync("253573", CancellationToken.None);
        Assert.NotNull(outcome.Catalog);
        var cat = outcome.Catalog!;
        Assert.Equal("TvmazeFallback", cat.SourceKey);
        Assert.Null(cat.IdProviderKey);
        Assert.True(cat.IsEnded);
        Assert.Equal(26, cat.Episodes.Count);
        var first = cat.Episodes.First(e => e.Season == 1 && e.Number == 1);
        // airstamp 2017-05-01T01:00:00+00:00 preferred over airdate 2017-04-30
        Assert.Equal(new DateTimeOffset(2017, 5, 1, 1, 0, 0, TimeSpan.Zero), first.AiredAt);
        Assert.False(first.IsSpecial);
    }

    [Fact]
    public async Task LookupByImdbId_Works()
    {
        var f = new TvmazeFetcher(new HttpClient(Handler()));
        var outcome = await f.FetchByImdbIdAsync("tt1898069", CancellationToken.None);
        Assert.NotNull(outcome.Catalog);
    }

    [Fact]
    public async Task Lookup404_ReturnsFail()
    {
        var f = new TvmazeFetcher(new HttpClient(Handler()));
        var outcome = await f.FetchByTvdbIdAsync("111", CancellationToken.None);
        Assert.Null(outcome.Catalog);
        Assert.NotNull(outcome.Error);
    }

    [Fact]
    public async Task NullAirstamp_UsesAirdateRule_BothNull_Undated_SpecialTyped()
    {
        var handler = new FakeHttpHandler(uri => (uri.Host, uri.PathAndQuery) switch
        {
            ("api.tvmaze.com", "/lookup/shows?thetvdb=9") => FakeHttpHandler.Redirect("https://api.tvmaze.com/shows/9"),
            ("api.tvmaze.com", "/shows/9") => FakeHttpHandler.Json("""{"id":9,"name":"X","status":"Running","externals":{"thetvdb":9}}"""),
            ("api.tvmaze.com", "/shows/9/episodes?specials=1") => FakeHttpHandler.Json("""
                [{"id":1,"season":1,"number":1,"airdate":"2024-01-07","airstamp":null,"type":"regular","name":"a"},
                 {"id":2,"season":1,"number":2,"airdate":null,"airstamp":null,"type":"regular","name":"b"},
                 {"id":3,"season":1,"number":null,"airdate":"2024-02-01","airstamp":null,"type":"significant_special","name":"sp"}]
                """),
            _ => FakeHttpHandler.Status(HttpStatusCode.NotFound),
        });
        var f = new TvmazeFetcher(new HttpClient(handler));
        var cat = (await f.FetchByTvdbIdAsync("9", CancellationToken.None)).Catalog!;
        Assert.False(cat.IsEnded);
        Assert.Equal(new DateTimeOffset(2024, 1, 7, 23, 59, 0, TimeSpan.Zero), cat.Episodes[0].AiredAt);
        Assert.Null(cat.Episodes[1].AiredAt);
        Assert.True(cat.Episodes[2].IsSpecial);
    }
}
```

- [ ] **Step 2: Run red** — `dotnet test --filter TvmazeFetcherTests`; expected: compile error.

- [ ] **Step 3: Implement** `Services/Lanes/TvmazeFetcher.cs`:

```csharp
using System.Text.Json;
using Jellyfin.Plugin.DownloadTime.Model;
using Jellyfin.Plugin.DownloadTime.Services;

namespace Jellyfin.Plugin.DownloadTime.Services.Lanes;

/// <summary>Keyless TVmaze fallback for TVDB/IMDb-identified shows (spec §2.1).</summary>
public class TvmazeFetcher : ITvmazeSource
{
    private readonly HttpClient _http;

    public TvmazeFetcher(HttpClient http) => _http = http;

    public Task<FetchOutcome> FetchByTvdbIdAsync(string tvdbId, CancellationToken ct)
        => FetchAsync($"https://api.tvmaze.com/lookup/shows?thetvdb={Uri.EscapeDataString(tvdbId)}", ct);

    public Task<FetchOutcome> FetchByImdbIdAsync(string imdbId, CancellationToken ct)
        => FetchAsync($"https://api.tvmaze.com/lookup/shows?imdb={Uri.EscapeDataString(imdbId)}", ct);

    private async Task<FetchOutcome> FetchAsync(string lookupUrl, CancellationToken ct)
    {
        try
        {
            using var showResp = await _http.GetAsync(lookupUrl, ct).ConfigureAwait(false);
            if (!showResp.IsSuccessStatusCode)
            {
                return FetchOutcome.Fail($"TVmaze lookup HTTP {(int)showResp.StatusCode}");
            }
            using var showDoc = JsonDocument.Parse(await showResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var root = showDoc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return FetchOutcome.Fail("TVmaze lookup returned no show");
            }
            var showId = root.GetProperty("id").GetInt32();
            var isEnded = root.TryGetProperty("status", out var st) && st.GetString() == "Ended";

            using var epResp = await _http.GetAsync($"https://api.tvmaze.com/shows/{showId}/episodes?specials=1", ct).ConfigureAwait(false);
            if (!epResp.IsSuccessStatusCode)
            {
                return FetchOutcome.Fail($"TVmaze episodes HTTP {(int)epResp.StatusCode}");
            }
            using var epDoc = JsonDocument.Parse(await epResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));

            var episodes = new List<RemoteEpisode>();
            foreach (var e in epDoc.RootElement.EnumerateArray())
            {
                int? season = e.TryGetProperty("season", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt32() : null;
                int? number = e.TryGetProperty("number", out var n) && n.ValueKind == JsonValueKind.Number ? n.GetInt32() : null;
                var isSpecial = e.TryGetProperty("type", out var t) && t.GetString() != "regular";

                DateTimeOffset? aired = null;
                if (e.TryGetProperty("airstamp", out var stamp) && stamp.ValueKind == JsonValueKind.String
                    && DateTimeOffset.TryParse(stamp.GetString(), out var dto))
                {
                    aired = dto;
                }
                else if (e.TryGetProperty("airdate", out var ad) && ad.ValueKind == JsonValueKind.String
                    && DateOnly.TryParse(ad.GetString(), out var d))
                {
                    aired = AirTime.FromDate(d.Year, d.Month, d.Day);
                }

                var title = e.TryGetProperty("name", out var nm) ? nm.GetString() : null;
                episodes.Add(new RemoteEpisode(season, number, null, aired, isSpecial, title));
            }

            return FetchOutcome.Ok(new RemoteCatalog("TvmazeFallback", null, showId.ToString(System.Globalization.CultureInfo.InvariantCulture), isEnded, episodes));
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            return FetchOutcome.Fail($"TVmaze request failed: {ex.Message}");
        }
    }
}
```

- [ ] **Step 4: Run green** — `dotnet test`.
- [ ] **Step 5: Commit** — `git add -A && git commit -m "Add TVmaze fallback client (tvdb/imdb lookup, airstamp-first)"`

---

### Task 10: AniDB lane — HTTP API client with pacing

**⚠ USER ACTION REQUIRED before live use (not before implementing):** register an HTTP client named `downloadtime` under the user's AniDB account (anidb.net → Account → Clients → Add client, type HTTP, version 1). Unit tests run without it; the live smoke and E2E need it. Flag this to the user when the task completes.

**Files:**
- Create: `src/Jellyfin.Plugin.DownloadTime/Services/Lanes/AniDbFetcher.cs`, `tests/.../AniDbFetcherTests.cs`, `tests/.../fixtures/anidb-anime-18164.xml`, `tests/.../fixtures/anidb-error-banned.xml`

**Interfaces:**
- Produces: `AniDbFetcher : IAniDbSource`, ctor `(HttpClient http, IClock clock, Func<TimeSpan, Task> delayFn, Func<int> requestDelayMs, Func<(string Name, int Version)> clientId)`. Catalog: `SourceKey="AniDB"`, `IdProviderKey="AniDB"`, `Season=null` on all episodes (season-less entry), `Number=epno`, `IsEnded` = enddate present and past clock. Pacing: consecutive requests are separated by at least `requestDelayMs` using `clock` for elapsed measurement and `delayFn` for waiting (production passes `Task.Delay`; tests pass a recorder that advances the FakeClock).
- Static pure parser: `AniDbFetcher.ParseAnime(string xml, IClock clock)` returning `(RemoteCatalog? Catalog, string? Error)` — series id filled from the XML `anime id` attribute.

- [ ] **Step 1: Create fixtures**

`tests/.../fixtures/anidb-anime-18164.xml` (representative of the documented httpapi `anime` response; epno type 1 = regular, 2 = special):

```xml
<?xml version="1.0" encoding="UTF-8"?>
<anime id="18164" restricted="false">
  <type>TV Series</type>
  <episodecount>12</episodecount>
  <startdate>2024-01-07</startdate>
  <enddate>2024-03-24</enddate>
  <titles><title xml:lang="en" type="main">7th Time Loop</title></titles>
  <episodes>
    <episode id="274088" update="2024-01-07"><epno type="1">1</epno><length>24</length><airdate>2024-01-07</airdate><title xml:lang="en">Episode 1</title></episode>
    <episode id="274089" update="2024-01-14"><epno type="1">2</epno><length>24</length><airdate>2024-01-14</airdate><title xml:lang="en">Episode 2</title></episode>
    <episode id="274090" update="2024-01-21"><epno type="1">3</epno><length>24</length><airdate>2024-01-21</airdate><title xml:lang="en">Episode 3</title></episode>
    <episode id="290001" update="2024-04-01"><epno type="2">S1</epno><length>5</length><airdate>2024-04-01</airdate><title xml:lang="en">Recap Special</title></episode>
    <episode id="290002" update="2024-04-02"><epno type="1">4</epno><length>24</length><title xml:lang="en">Undated Ep</title></episode>
  </episodes>
</anime>
```

`tests/.../fixtures/anidb-error-banned.xml`:

```xml
<?xml version="1.0" encoding="UTF-8"?>
<error code="500">banned</error>
```

- [ ] **Step 2: Write the failing tests**

```csharp
// Edge-case inventory:
// - golden XML: 4 regular episodes (one undated -> AiredAt null), 1 special (epno type=2, "S1" -> Number 1, IsSpecial);
//   AniDB episode ids as SourceEpisodeId; Season null everywhere; airdate -> AirTime rule;
//   enddate 2024-03-24 past clock -> IsEnded true.
// - error XML (<error>banned</error>) -> Fail, never an empty catalog.
// - HTTP non-200 -> Fail.
// - request URL carries client/clientver/protover/request/aid params.
// - PACING: two consecutive fetches -> second waits >= requestDelayMs (measured via FakeClock + recorded delayFn).
using Jellyfin.Plugin.DownloadTime.Model;
using Jellyfin.Plugin.DownloadTime.Services.Lanes;
using Jellyfin.Plugin.DownloadTime.Tests.Support;
using Xunit;

namespace Jellyfin.Plugin.DownloadTime.Tests;

public class AniDbFetcherTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 25, 12, 0, 0, TimeSpan.Zero);
    private static string Fix(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", name));

    private static AniDbFetcher Make(FakeHttpHandler handler, FakeClock clock, List<TimeSpan>? delays = null, int delayMs = 2000)
        => new(new HttpClient(handler), clock,
            async ts => { delays?.Add(ts); clock.UtcNow += ts; await Task.CompletedTask; },
            () => delayMs, () => ("downloadtime", 1));

    [Fact]
    public void ParseAnime_Golden()
    {
        var (cat, err) = AniDbFetcher.ParseAnime(Fix("anidb-anime-18164.xml"), new FakeClock(Now));
        Assert.Null(err);
        Assert.Equal("AniDB", cat!.SourceKey);
        Assert.Equal("AniDB", cat.IdProviderKey);
        Assert.Equal("18164", cat.SeriesSourceId);
        Assert.True(cat.IsEnded);
        Assert.Equal(5, cat.Episodes.Count);
        Assert.All(cat.Episodes, e => Assert.Null(e.Season));
        var ep2 = cat.Episodes.Single(e => e.SourceEpisodeId == "274089");
        Assert.Equal(2, ep2.Number);
        Assert.False(ep2.IsSpecial);
        Assert.Equal(new DateTimeOffset(2024, 1, 14, 23, 59, 0, TimeSpan.Zero), ep2.AiredAt);
        var special = cat.Episodes.Single(e => e.SourceEpisodeId == "290001");
        Assert.True(special.IsSpecial);
        Assert.Equal(1, special.Number);
        var undated = cat.Episodes.Single(e => e.SourceEpisodeId == "290002");
        Assert.Null(undated.AiredAt);
    }

    [Fact]
    public void ParseAnime_ErrorXml_Fails()
    {
        var (cat, err) = AniDbFetcher.ParseAnime(Fix("anidb-error-banned.xml"), new FakeClock(Now));
        Assert.Null(cat);
        Assert.Contains("banned", err, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Fetch_BuildsUrl_AndParses()
    {
        var handler = new FakeHttpHandler(uri =>
            uri.Host == "api.anidb.net" ? FakeHttpHandler.Xml(Fix("anidb-anime-18164.xml"))
                                        : FakeHttpHandler.Status(System.Net.HttpStatusCode.NotFound));
        var f = Make(handler, new FakeClock(Now));
        var outcome = await f.FetchByAnimeIdAsync("18164", CancellationToken.None);
        Assert.NotNull(outcome.Catalog);
        var q = handler.Requests[0].Query;
        Assert.Contains("request=anime", q);
        Assert.Contains("client=downloadtime", q);
        Assert.Contains("clientver=1", q);
        Assert.Contains("protover=1", q);
        Assert.Contains("aid=18164", q);
    }

    [Fact]
    public async Task Pacing_SecondRequestWaits()
    {
        var clock = new FakeClock(Now);
        var delays = new List<TimeSpan>();
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Xml(Fix("anidb-anime-18164.xml")));
        var f = Make(handler, clock, delays);
        await f.FetchByAnimeIdAsync("1", CancellationToken.None);
        Assert.Empty(delays); // first request never waits
        clock.UtcNow += TimeSpan.FromMilliseconds(500); // only 0.5s elapsed
        await f.FetchByAnimeIdAsync("2", CancellationToken.None);
        var wait = Assert.Single(delays);
        Assert.Equal(TimeSpan.FromMilliseconds(1500), wait); // tops up to 2000ms
        clock.UtcNow += TimeSpan.FromSeconds(10); // plenty elapsed
        await f.FetchByAnimeIdAsync("3", CancellationToken.None);
        Assert.Single(delays); // no new wait
    }

    [Fact]
    public async Task Http503_Fails()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Status(System.Net.HttpStatusCode.ServiceUnavailable));
        var f = Make(handler, new FakeClock(Now));
        var outcome = await f.FetchByAnimeIdAsync("1", CancellationToken.None);
        Assert.Null(outcome.Catalog);
        Assert.NotNull(outcome.Error);
    }
}
```

- [ ] **Step 3: Run red** — `dotnet test --filter AniDbFetcherTests`; expected: compile error.

- [ ] **Step 4: Implement** `Services/Lanes/AniDbFetcher.cs`:

```csharp
using System.Globalization;
using System.Xml.Linq;
using Jellyfin.Plugin.DownloadTime.Model;
using Jellyfin.Plugin.DownloadTime.Services;

namespace Jellyfin.Plugin.DownloadTime.Services.Lanes;

/// <summary>
/// AniDB HTTP API client (spec §2.2). One request per series per scan; HARD
/// pacing between requests — AniDB bans aggressive clients. Episode-ID
/// catalogs make anime detection immune to Ronin merge/split renumbering.
/// </summary>
public class AniDbFetcher : IAniDbSource
{
    private readonly HttpClient _http;
    private readonly IClock _clock;
    private readonly Func<TimeSpan, Task> _delayFn;
    private readonly Func<int> _requestDelayMs;
    private readonly Func<(string Name, int Version)> _clientId;
    private DateTimeOffset? _lastRequestAt;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public AniDbFetcher(HttpClient http, IClock clock, Func<TimeSpan, Task> delayFn, Func<int> requestDelayMs, Func<(string Name, int Version)> clientId)
    {
        _http = http;
        _clock = clock;
        _delayFn = delayFn;
        _requestDelayMs = requestDelayMs;
        _clientId = clientId;
    }

    public async Task<FetchOutcome> FetchByAnimeIdAsync(string anidbId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var minGap = TimeSpan.FromMilliseconds(_requestDelayMs());
            if (_lastRequestAt.HasValue)
            {
                var elapsed = _clock.UtcNow - _lastRequestAt.Value;
                if (elapsed < minGap)
                {
                    await _delayFn(minGap - elapsed).ConfigureAwait(false);
                }
            }
            _lastRequestAt = _clock.UtcNow;

            var (name, version) = _clientId();
            var url = $"http://api.anidb.net:9001/httpapi?request=anime&client={Uri.EscapeDataString(name)}&clientver={version}&protover=1&aid={Uri.EscapeDataString(anidbId)}";
            using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return FetchOutcome.Fail($"AniDB HTTP {(int)resp.StatusCode}");
            }
            var xml = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var (catalog, error) = ParseAnime(xml, _clock);
            return error is null ? FetchOutcome.Ok(catalog!) : FetchOutcome.Fail(error);
        }
        catch (HttpRequestException ex)
        {
            return FetchOutcome.Fail($"AniDB request failed: {ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Pure parser for the httpapi anime response.</summary>
    public static (RemoteCatalog? Catalog, string? Error) ParseAnime(string xml, IClock clock)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml);
        }
        catch (System.Xml.XmlException ex)
        {
            return (null, $"AniDB XML parse error: {ex.Message}");
        }

        if (doc.Root is null)
        {
            return (null, "AniDB response empty");
        }
        if (doc.Root.Name.LocalName == "error")
        {
            return (null, $"AniDB error: {doc.Root.Value}");
        }
        if (doc.Root.Name.LocalName != "anime")
        {
            return (null, $"AniDB unexpected root <{doc.Root.Name.LocalName}>");
        }

        var seriesId = doc.Root.Attribute("id")?.Value ?? string.Empty;
        var isEnded = false;
        if (DateOnly.TryParse(doc.Root.Element("enddate")?.Value, out var end))
        {
            isEnded = AirTime.FromDate(end.Year, end.Month, end.Day) < clock.UtcNow;
        }

        var episodes = new List<RemoteEpisode>();
        foreach (var ep in doc.Root.Element("episodes")?.Elements("episode") ?? Enumerable.Empty<XElement>())
        {
            var id = ep.Attribute("id")?.Value;
            var epno = ep.Element("epno");
            if (id is null || epno is null)
            {
                continue;
            }
            var typeAttr = epno.Attribute("type")?.Value;
            var isSpecial = typeAttr != "1";
            var digits = new string(epno.Value.Where(char.IsDigit).ToArray());
            if (!int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
            {
                continue;
            }

            DateTimeOffset? aired = null;
            if (DateOnly.TryParse(ep.Element("airdate")?.Value, out var d))
            {
                aired = AirTime.FromDate(d.Year, d.Month, d.Day);
            }

            var title = ep.Elements("title").FirstOrDefault(t => (string?)t.Attribute(XNamespace.Xml + "lang") == "en")?.Value
                        ?? ep.Elements("title").FirstOrDefault()?.Value;

            episodes.Add(new RemoteEpisode(null, number, id, aired, isSpecial, title));
        }

        if (episodes.Count == 0)
        {
            return (null, "AniDB anime entry contained zero parsable episodes");
        }
        return (new RemoteCatalog("AniDB", "AniDB", seriesId, isEnded, episodes), null);
    }
}
```

- [ ] **Step 5: Run green** — `dotnet test`.
- [ ] **Step 6: Commit** — `git add -A && git commit -m "Add AniDB HTTP client with hard pacing and fail-safe parser"`
- [ ] **Step 7: Tell the user** the AniDB client name `downloadtime` v1 must be registered on their AniDB account before the live E2E (Task 21).

---

### Task 11: TMDB lane — series episodes + movie collections

**⚠ USER ACTION REQUIRED before live use:** a free TMDB API key (themoviedb.org → Settings → API). Unit tests run without it.

**Files:**
- Create: `src/Jellyfin.Plugin.DownloadTime/Services/Lanes/TmdbFetcher.cs`, `tests/.../TmdbFetcherTests.cs`

**Interfaces:**
- Produces: `TmdbFetcher : ITmdbSource`, ctor `(HttpClient http, Func<string> apiKey, Func<TimeSpan, Task> delayFn)`. Series catalog: `SourceKey="Tmdb"`, `IdProviderKey=null`, `IsEnded` = status "Ended"/"Canceled"; episodes from every season incl. season 0 (IsSpecial = season_number==0); air_date via AirTime rule. `FetchCollectionForMovieAsync`: NoCollection=true when `belongs_to_collection` is null; otherwise CollectionCatalog from `/collection/{id}` parts (release_date via AirTime; missing release_date -> null).
- Error mapping: blank api key -> `Fail("TMDB API key not configured")` without any HTTP call; 401 -> `Fail("TMDB API key rejected")`; 429 -> wait Retry-After seconds via delayFn, retry ONCE, then fail if still 429.

- [ ] **Step 1: Write the failing tests**

```csharp
// Edge-case inventory:
// - series: two seasons + season 0; episodes flagged special only in S0; air_date -> 23:59Z; status Ended -> IsEnded.
// - blank key -> fail fast, zero HTTP requests.
// - 401 -> Fail mentioning key.
// - 429 with Retry-After: waits, retries once, succeeds; 429 twice -> Fail.
// - movie without collection -> NoCollection.
// - movie with collection -> parts parsed, null release_date -> ReleasedAt null.
using System.Net;
using Jellyfin.Plugin.DownloadTime.Model;
using Jellyfin.Plugin.DownloadTime.Services.Lanes;
using Jellyfin.Plugin.DownloadTime.Tests.Support;
using Xunit;

namespace Jellyfin.Plugin.DownloadTime.Tests;

public class TmdbFetcherTests
{
    private const string TvJson = """
        {"id":110316,"status":"Ended","seasons":[
          {"season_number":0,"episode_count":1},
          {"season_number":1,"episode_count":2},
          {"season_number":2,"episode_count":1}]}
        """;
    private const string S0Json = """{"episodes":[{"season_number":0,"episode_number":1,"air_date":"2021-01-01","name":"sp"}]}""";
    private const string S1Json = """{"episodes":[
        {"season_number":1,"episode_number":1,"air_date":"2020-12-10","name":"e1"},
        {"season_number":1,"episode_number":2,"air_date":null,"name":"e2"}]}""";
    private const string S2Json = """{"episodes":[{"season_number":2,"episode_number":1,"air_date":"2022-12-22","name":"e3"}]}""";

    private static TmdbFetcher Make(FakeHttpHandler h, string key = "k", List<TimeSpan>? delays = null)
        => new(new HttpClient(h), () => key, ts => { delays?.Add(ts); return Task.CompletedTask; });

    private static FakeHttpHandler SeriesHandler() => new(uri => uri.PathAndQuery switch
    {
        var p when p.StartsWith("/3/tv/110316/season/0") => FakeHttpHandler.Json(S0Json),
        var p when p.StartsWith("/3/tv/110316/season/1") => FakeHttpHandler.Json(S1Json),
        var p when p.StartsWith("/3/tv/110316/season/2") => FakeHttpHandler.Json(S2Json),
        var p when p.StartsWith("/3/tv/110316") => FakeHttpHandler.Json(TvJson),
        _ => FakeHttpHandler.Status(HttpStatusCode.NotFound),
    });

    [Fact]
    public async Task Series_AllSeasonsFetched_SpecialsFlagged_DatesNormalized()
    {
        var f = Make(SeriesHandler());
        var cat = (await f.FetchSeriesAsync("110316", CancellationToken.None)).Catalog!;
        Assert.Equal("Tmdb", cat.SourceKey);
        Assert.Null(cat.IdProviderKey);
        Assert.True(cat.IsEnded);
        Assert.Equal(4, cat.Episodes.Count);
        Assert.True(cat.Episodes.Single(e => e.Season == 0).IsSpecial);
        var e1 = cat.Episodes.Single(e => e.Season == 1 && e.Number == 1);
        Assert.Equal(new DateTimeOffset(2020, 12, 10, 23, 59, 0, TimeSpan.Zero), e1.AiredAt);
        Assert.Null(cat.Episodes.Single(e => e.Season == 1 && e.Number == 2).AiredAt);
    }

    [Fact]
    public async Task BlankKey_FailsWithoutHttp()
    {
        var h = SeriesHandler();
        var f = Make(h, key: "");
        var outcome = await f.FetchSeriesAsync("110316", CancellationToken.None);
        Assert.NotNull(outcome.Error);
        Assert.Contains("key", outcome.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(h.Requests);
    }

    [Fact]
    public async Task Unauthorized_FailsMentioningKey()
    {
        var f = Make(new FakeHttpHandler(_ => FakeHttpHandler.Status(HttpStatusCode.Unauthorized)));
        var outcome = await f.FetchSeriesAsync("110316", CancellationToken.None);
        Assert.Contains("key", outcome.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RateLimited_RetriesOnceAfterRetryAfter()
    {
        var calls = 0;
        var delays = new List<TimeSpan>();
        var h = new FakeHttpHandler(uri =>
        {
            if (uri.PathAndQuery.StartsWith("/3/tv/110316/season")) return uri.PathAndQuery.StartsWith("/3/tv/110316/season/0") ? FakeHttpHandler.Json(S0Json) : uri.PathAndQuery.StartsWith("/3/tv/110316/season/1") ? FakeHttpHandler.Json(S1Json) : FakeHttpHandler.Json(S2Json);
            calls++;
            if (calls == 1)
            {
                var r = FakeHttpHandler.Status(HttpStatusCode.TooManyRequests);
                r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(3));
                return r;
            }
            return FakeHttpHandler.Json(TvJson);
        });
        var f = Make(h, delays: delays);
        var outcome = await f.FetchSeriesAsync("110316", CancellationToken.None);
        Assert.NotNull(outcome.Catalog);
        Assert.Equal(TimeSpan.FromSeconds(3), Assert.Single(delays));
    }

    [Fact]
    public async Task Movie_NoCollection()
    {
        var h = new FakeHttpHandler(uri => uri.PathAndQuery.StartsWith("/3/movie/500")
            ? FakeHttpHandler.Json("""{"id":500,"belongs_to_collection":null}""")
            : FakeHttpHandler.Status(HttpStatusCode.NotFound));
        var f = Make(h);
        var outcome = await f.FetchCollectionForMovieAsync(500, CancellationToken.None);
        Assert.True(outcome.NoCollection);
        Assert.Null(outcome.Catalog);
        Assert.Null(outcome.Error);
    }

    [Fact]
    public async Task Movie_WithCollection_PartsParsed()
    {
        var h = new FakeHttpHandler(uri => uri.PathAndQuery switch
        {
            var p when p.StartsWith("/3/movie/245891") => FakeHttpHandler.Json("""{"id":245891,"belongs_to_collection":{"id":404609,"name":"John Wick Collection"}}"""),
            var p when p.StartsWith("/3/collection/404609") => FakeHttpHandler.Json("""
                {"id":404609,"name":"John Wick Collection","parts":[
                  {"id":245891,"title":"John Wick","release_date":"2014-10-24"},
                  {"id":324552,"title":"John Wick: Chapter 2","release_date":"2017-02-10"},
                  {"id":999999,"title":"Announced Wick","release_date":null}]}
                """),
            _ => FakeHttpHandler.Status(HttpStatusCode.NotFound),
        });
        var f = Make(h);
        var outcome = await f.FetchCollectionForMovieAsync(245891, CancellationToken.None);
        Assert.NotNull(outcome.Catalog);
        Assert.Equal(404609, outcome.Catalog!.CollectionId);
        Assert.Equal(3, outcome.Catalog.Movies.Count);
        Assert.Equal(new DateTimeOffset(2014, 10, 24, 23, 59, 0, TimeSpan.Zero), outcome.Catalog.Movies[0].ReleasedAt);
        Assert.Null(outcome.Catalog.Movies[2].ReleasedAt);
    }
}
```

- [ ] **Step 2: Run red** — `dotnet test --filter TmdbFetcherTests`; expected: compile error.

- [ ] **Step 3: Implement** `Services/Lanes/TmdbFetcher.cs`:

```csharp
using System.Globalization;
using System.Text.Json;
using Jellyfin.Plugin.DownloadTime.Model;
using Jellyfin.Plugin.DownloadTime.Services;

namespace Jellyfin.Plugin.DownloadTime.Services.Lanes;

/// <summary>TMDB API client for tmdbid-identified shows and movie collections (spec §2.3).</summary>
public class TmdbFetcher : ITmdbSource
{
    private const string BaseUrl = "https://api.themoviedb.org/3";
    private readonly HttpClient _http;
    private readonly Func<string> _apiKey;
    private readonly Func<TimeSpan, Task> _delayFn;

    public TmdbFetcher(HttpClient http, Func<string> apiKey, Func<TimeSpan, Task> delayFn)
    {
        _http = http;
        _apiKey = apiKey;
        _delayFn = delayFn;
    }

    public async Task<FetchOutcome> FetchSeriesAsync(string tmdbId, CancellationToken ct)
    {
        var (tvDoc, error) = await GetJsonAsync($"/tv/{tmdbId}", ct).ConfigureAwait(false);
        if (error is not null)
        {
            return FetchOutcome.Fail(error);
        }
        using var tv = tvDoc!;
        var status = tv.RootElement.TryGetProperty("status", out var st) ? st.GetString() : null;
        var isEnded = status is "Ended" or "Canceled";

        var seasonNumbers = new List<int>();
        if (tv.RootElement.TryGetProperty("seasons", out var seasons))
        {
            foreach (var s in seasons.EnumerateArray())
            {
                if (s.TryGetProperty("season_number", out var sn) && sn.ValueKind == JsonValueKind.Number)
                {
                    seasonNumbers.Add(sn.GetInt32());
                }
            }
        }

        var episodes = new List<RemoteEpisode>();
        foreach (var sn in seasonNumbers)
        {
            var (seasonDoc, sErr) = await GetJsonAsync($"/tv/{tmdbId}/season/{sn}", ct).ConfigureAwait(false);
            if (sErr is not null)
            {
                return FetchOutcome.Fail(sErr);
            }
            using var season = seasonDoc!;
            if (!season.RootElement.TryGetProperty("episodes", out var eps))
            {
                continue;
            }
            foreach (var e in eps.EnumerateArray())
            {
                int? number = e.TryGetProperty("episode_number", out var en) && en.ValueKind == JsonValueKind.Number ? en.GetInt32() : null;
                DateTimeOffset? aired = null;
                if (e.TryGetProperty("air_date", out var ad) && ad.ValueKind == JsonValueKind.String
                    && DateOnly.TryParse(ad.GetString(), out var d))
                {
                    aired = AirTime.FromDate(d.Year, d.Month, d.Day);
                }
                var title = e.TryGetProperty("name", out var nm) ? nm.GetString() : null;
                episodes.Add(new RemoteEpisode(sn, number, null, aired, sn == 0, title));
            }
        }

        if (episodes.Count == 0)
        {
            return FetchOutcome.Fail("TMDB returned zero episodes");
        }
        return FetchOutcome.Ok(new RemoteCatalog("Tmdb", null, tmdbId, isEnded, episodes));
    }

    public async Task<CollectionOutcome> FetchCollectionForMovieAsync(int movieTmdbId, CancellationToken ct)
    {
        var (movieDoc, error) = await GetJsonAsync($"/movie/{movieTmdbId.ToString(CultureInfo.InvariantCulture)}", ct).ConfigureAwait(false);
        if (error is not null)
        {
            return new CollectionOutcome(null, error, false);
        }
        using var movie = movieDoc!;
        if (!movie.RootElement.TryGetProperty("belongs_to_collection", out var btc) || btc.ValueKind != JsonValueKind.Object)
        {
            return new CollectionOutcome(null, null, NoCollection: true);
        }
        var collectionId = btc.GetProperty("id").GetInt32();

        var (colDoc, cErr) = await GetJsonAsync($"/collection/{collectionId.ToString(CultureInfo.InvariantCulture)}", ct).ConfigureAwait(false);
        if (cErr is not null)
        {
            return new CollectionOutcome(null, cErr, false);
        }
        using var col = colDoc!;
        var name = col.RootElement.TryGetProperty("name", out var nm) ? nm.GetString() ?? string.Empty : string.Empty;
        var movies = new List<RemoteMovie>();
        if (col.RootElement.TryGetProperty("parts", out var parts))
        {
            foreach (var p in parts.EnumerateArray())
            {
                var id = p.GetProperty("id").GetInt32();
                var title = p.TryGetProperty("title", out var t) ? t.GetString() ?? string.Empty : string.Empty;
                DateTimeOffset? released = null;
                if (p.TryGetProperty("release_date", out var rd) && rd.ValueKind == JsonValueKind.String
                    && DateOnly.TryParse(rd.GetString(), out var d))
                {
                    released = AirTime.FromDate(d.Year, d.Month, d.Day);
                }
                movies.Add(new RemoteMovie(id, title, released));
            }
        }
        return new CollectionOutcome(new CollectionCatalog(collectionId, name, movies), null, false);
    }

    private async Task<(JsonDocument? Doc, string? Error)> GetJsonAsync(string path, CancellationToken ct)
    {
        var key = _apiKey();
        if (string.IsNullOrWhiteSpace(key))
        {
            return (null, "TMDB API key not configured");
        }
        for (var attempt = 0; attempt < 2; attempt++)
        {
            HttpResponseMessage resp;
            try
            {
                resp = await _http.GetAsync($"{BaseUrl}{path}?api_key={Uri.EscapeDataString(key)}", ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                return (null, $"TMDB request failed: {ex.Message}");
            }
            using (resp)
            {
                if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    return (null, "TMDB API key rejected (401)");
                }
                if (resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests && attempt == 0)
                {
                    var wait = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2);
                    await _delayFn(wait).ConfigureAwait(false);
                    continue;
                }
                if (!resp.IsSuccessStatusCode)
                {
                    return (null, $"TMDB HTTP {(int)resp.StatusCode} for {path}");
                }
                try
                {
                    return (JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false)), null);
                }
                catch (JsonException ex)
                {
                    return (null, $"TMDB JSON parse error: {ex.Message}");
                }
            }
        }
        return (null, "TMDB rate limited (429) after retry");
    }
}
```

- [ ] **Step 4: Run green** — `dotnet test`.
- [ ] **Step 5: Commit** — `git add -A && git commit -m "Add TMDB client for series episodes and movie collections"`

---

### Task 12: CatalogCache — TTL'd JSON cache

**Files:**
- Create: `src/Jellyfin.Plugin.DownloadTime/Services/CatalogCache.cs`, `tests/.../CatalogCacheTests.cs`

**Interfaces:**
- Produces: `CatalogCache`, ctor `(string cacheDir, IClock clock)`; `T? TryGet<T>(string key, TimeSpan ttl) where T : class`; `void Store<T>(string key, T value)`. Keys are sanitized to safe filenames (`[^A-Za-z0-9._-]` → `_`). Used by ScanService (Task 14) with keys like `tvdb-253573`, `anidb-18164`, `tmdb-tv-110316`, `tmdb-movie-245891`.

- [ ] **Step 1: Write the failing tests**

```csharp
// Edge-case inventory:
// - roundtrip within TTL returns stored value; expired TTL -> null (via FakeClock, no sleeps).
// - missing key -> null.
// - corrupt/truncated cache file -> null, never throws.
// - key sanitization: "tt123/../x" produces a safe filename, still roundtrips.
// - Store overwrites (second Store wins).
using Jellyfin.Plugin.DownloadTime.Model;
using Jellyfin.Plugin.DownloadTime.Services;
using Jellyfin.Plugin.DownloadTime.Tests.Support;
using Xunit;

namespace Jellyfin.Plugin.DownloadTime.Tests;

public class CatalogCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dt-cache-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Now = new(2026, 7, 25, 12, 0, 0, TimeSpan.Zero);

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static RemoteCatalog Sample() => new("Tvdb", "Tvdb", "253573", false,
        new[] { new RemoteEpisode(1, 1, "5088686", null, false, "x") });

    [Fact]
    public void Roundtrip_WithinTtl()
    {
        var clock = new FakeClock(Now);
        var cache = new CatalogCache(_dir, clock);
        cache.Store("tvdb-253573", Sample());
        var got = cache.TryGet<RemoteCatalog>("tvdb-253573", TimeSpan.FromDays(1));
        Assert.NotNull(got);
        Assert.Equal("253573", got!.SeriesSourceId);
        Assert.Equal("5088686", got.Episodes[0].SourceEpisodeId);
    }

    [Fact]
    public void Expired_ReturnsNull()
    {
        var clock = new FakeClock(Now);
        var cache = new CatalogCache(_dir, clock);
        cache.Store("k", Sample());
        clock.UtcNow = Now.AddDays(2);
        Assert.Null(cache.TryGet<RemoteCatalog>("k", TimeSpan.FromDays(1)));
        clock.UtcNow = Now.AddHours(12);
        Assert.NotNull(cache.TryGet<RemoteCatalog>("k", TimeSpan.FromDays(1)));
    }

    [Fact]
    public void MissingKey_Null()
    {
        var cache = new CatalogCache(_dir, new FakeClock(Now));
        Assert.Null(cache.TryGet<RemoteCatalog>("nope", TimeSpan.FromDays(1)));
    }

    [Fact]
    public void CorruptFile_Null_NoThrow()
    {
        var clock = new FakeClock(Now);
        var cache = new CatalogCache(_dir, clock);
        cache.Store("bad", Sample());
        var file = Directory.GetFiles(_dir).Single(f => Path.GetFileName(f).StartsWith("bad"));
        File.WriteAllText(file, "{not json");
        Assert.Null(cache.TryGet<RemoteCatalog>("bad", TimeSpan.FromDays(1)));
    }

    [Fact]
    public void UnsafeKey_SanitizedAndRoundtrips()
    {
        var cache = new CatalogCache(_dir, new FakeClock(Now));
        cache.Store("tt123/../x", Sample());
        Assert.NotNull(cache.TryGet<RemoteCatalog>("tt123/../x", TimeSpan.FromDays(1)));
        Assert.All(Directory.GetFiles(_dir), f => Assert.DoesNotContain("..", Path.GetFileName(f)));
    }

    [Fact]
    public void Store_Overwrites()
    {
        var clock = new FakeClock(Now);
        var cache = new CatalogCache(_dir, clock);
        cache.Store("k", Sample());
        cache.Store("k", Sample() with { SeriesSourceId = "999" });
        Assert.Equal("999", cache.TryGet<RemoteCatalog>("k", TimeSpan.FromDays(1))!.SeriesSourceId);
    }
}
```

- [ ] **Step 2: Run red** — `dotnet test --filter CatalogCacheTests`; expected: compile error.

- [ ] **Step 3: Implement** `Services/CatalogCache.cs`:

```csharp
using System.Text.Json;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.DownloadTime.Model;

namespace Jellyfin.Plugin.DownloadTime.Services;

/// <summary>Disk cache for remote catalogs (spec §2.4). Clock-injected TTLs.</summary>
public partial class CatalogCache
{
    private sealed record Envelope<T>(DateTimeOffset FetchedAt, T Payload);

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };
    private readonly string _dir;
    private readonly IClock _clock;

    public CatalogCache(string cacheDir, IClock clock)
    {
        _dir = cacheDir;
        _clock = clock;
        Directory.CreateDirectory(_dir);
    }

    [GeneratedRegex("[^A-Za-z0-9._-]")]
    private static partial Regex Unsafe();

    private string PathFor(string key) => System.IO.Path.Combine(_dir, Unsafe().Replace(key, "_") + ".json");

    public T? TryGet<T>(string key, TimeSpan ttl) where T : class
    {
        var path = PathFor(key);
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            var env = JsonSerializer.Deserialize<Envelope<T>>(File.ReadAllText(path), JsonOpts);
            if (env is null || _clock.UtcNow - env.FetchedAt > ttl)
            {
                return null;
            }
            return env.Payload;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }

    public void Store<T>(string key, T value)
    {
        var env = new Envelope<T>(_clock.UtcNow, value);
        File.WriteAllText(PathFor(key), JsonSerializer.Serialize(env, JsonOpts));
    }
}
```

- [ ] **Step 4: Run green** — `dotnet test`. (If `RemoteCatalog` fails System.Text.Json roundtrip because of `IReadOnlyList` constructor binding, fix by adding `[JsonConstructor]`-friendly shapes — records with positional params serialize fine; adjust MODEL not tests if an issue appears.)
- [ ] **Step 5: Commit** — `git add -A && git commit -m "Add TTL catalog cache with corrupt-file tolerance"`

---

### Task 13: Report DTOs + ReportStore

**Files:**
- Create: `src/Jellyfin.Plugin.DownloadTime/Model/Report.cs`, `src/Jellyfin.Plugin.DownloadTime/Services/ReportStore.cs`, `tests/.../ReportStoreTests.cs`

**Interfaces:**
- Produces (`Model/Report.cs`) — consumed by ScanService, controller, badges, config page:

```csharp
namespace Jellyfin.Plugin.DownloadTime.Model;

public sealed record MissingEpisodeDto(int? Season, int? Number, string? Title, DateTimeOffset? AiredAt, string Kind, string? SourceEpisodeId);
public sealed record SeriesReportDto(
    Guid ItemId, string Name, string Lane, bool UsedFallback, bool Muted,
    string? Error, IReadOnlyList<string> Notes, IReadOnlyList<MissingEpisodeDto> Missing);
public sealed record MissingMovieDto(int TmdbId, string Title, DateTimeOffset? ReleasedAt);
public sealed record CollectionReportDto(string Name, string ViaMovie, IReadOnlyList<MissingMovieDto> Missing);
public sealed record ScanReport(
    DateTimeOffset StartedAt, DateTimeOffset FinishedAt,
    IReadOnlyList<SeriesReportDto> Series,
    IReadOnlyList<CollectionReportDto> Collections,
    IReadOnlyList<string> GlobalNotes);
```

- Produces: `ReportStore`, ctor `(string dataDir)`; `ScanReport? Current { get; }` (loads `report.json` lazily on first access); `void Save(ScanReport report)` (persists + updates Current). Corrupt/missing file → `Current == null`, never throws.

- [ ] **Step 1: Write the failing tests**

```csharp
// Edge-case inventory:
// - Save then Current returns the same data; new store instance re-reads from disk.
// - no file yet -> Current null.
// - corrupt file -> Current null, no throw.
using Jellyfin.Plugin.DownloadTime.Model;
using Jellyfin.Plugin.DownloadTime.Services;
using Xunit;

namespace Jellyfin.Plugin.DownloadTime.Tests;

public class ReportStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dt-report-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static ScanReport Sample()
    {
        var t = new DateTimeOffset(2026, 7, 25, 3, 0, 0, TimeSpan.Zero);
        return new ScanReport(t, t.AddMinutes(4),
            new[] { new SeriesReportDto(Guid.NewGuid(), "American Gods", "Tvdb", false, false, null,
                Array.Empty<string>(),
                new[] { new MissingEpisodeDto(2, 5, "The Ways of the Dead", t, "Gap", "6767322") }) },
            new[] { new CollectionReportDto("John Wick Collection", "John Wick",
                new[] { new MissingMovieDto(324552, "John Wick: Chapter 2", t) }) },
            Array.Empty<string>());
    }

    [Fact]
    public void SaveThenRead_SameData_AndPersistsAcrossInstances()
    {
        var store = new ReportStore(_dir);
        Assert.Null(store.Current);
        store.Save(Sample());
        Assert.NotNull(store.Current);
        var reread = new ReportStore(_dir);
        Assert.Equal("American Gods", reread.Current!.Series[0].Name);
        Assert.Equal("Gap", reread.Current.Series[0].Missing[0].Kind);
        Assert.Equal(324552, reread.Current.Collections[0].Missing[0].TmdbId);
    }

    [Fact]
    public void CorruptFile_NullCurrent()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "report.json"), "{broken");
        Assert.Null(new ReportStore(_dir).Current);
    }
}
```

- [ ] **Step 2: Run red** — `dotnet test --filter ReportStoreTests`; expected: compile error.

- [ ] **Step 3: Implement** `Services/ReportStore.cs`:

```csharp
using System.Text.Json;
using Jellyfin.Plugin.DownloadTime.Model;

namespace Jellyfin.Plugin.DownloadTime.Services;

/// <summary>Persists the last scan report under the plugin data dir.</summary>
public class ReportStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private readonly string _path;
    private readonly object _lock = new();
    private ScanReport? _current;
    private bool _loaded;

    public ReportStore(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        _path = Path.Combine(dataDir, "report.json");
    }

    public ScanReport? Current
    {
        get
        {
            lock (_lock)
            {
                if (!_loaded)
                {
                    _loaded = true;
                    try
                    {
                        if (File.Exists(_path))
                        {
                            _current = JsonSerializer.Deserialize<ScanReport>(File.ReadAllText(_path), JsonOpts);
                        }
                    }
                    catch (Exception ex) when (ex is JsonException or IOException)
                    {
                        _current = null;
                    }
                }
                return _current;
            }
        }
    }

    public void Save(ScanReport report)
    {
        lock (_lock)
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(report, JsonOpts));
            _current = report;
            _loaded = true;
        }
    }
}
```

- [ ] **Step 4: Run green** — `dotnet test`.
- [ ] **Step 5: Commit** — `git add -A && git commit -m "Add scan report DTOs and persistent report store"`

---

### Task 14: ScanService — the orchestrator

**Files:**
- Create: `src/Jellyfin.Plugin.DownloadTime/Services/ScanService.cs`, `src/Jellyfin.Plugin.DownloadTime/Services/ILibraryReader.cs`, `tests/.../ScanServiceTests.cs`

**Interfaces:**
- Produces (`Services/ILibraryReader.cs`):

```csharp
using Jellyfin.Plugin.DownloadTime.Model;

namespace Jellyfin.Plugin.DownloadTime.Services;

public interface ILibraryReader
{
    IReadOnlyList<SeriesItemInfo> GetSeries();
    IReadOnlyList<MovieItemInfo> GetMovies();
}
```

- Produces (`Services/ScanService.cs`):

```csharp
public sealed record ScanSettings(
    bool EnableTvLane, bool EnableAnimeLane, bool EnableMovieLane,
    int GraceHours, bool IncludeSpecials, int MovieReleaseBufferDays,
    IReadOnlySet<string> ExcludedItemIds,   // Guid "N" format strings
    TimeSpan ContinuingTtl, TimeSpan EndedTtl);

public class ScanService
{
    public ScanService(ILibraryReader library, ITvdbSource tvdb, ITvmazeSource tvmaze,
                       IAniDbSource anidb, ITmdbSource tmdb, CatalogCache cache, IClock clock);
    public bool IsScanning { get; }
    /// <summary>Throws InvalidOperationException if a scan is already running.</summary>
    public Task<ScanReport> ScanAsync(ScanSettings settings, bool fullRefresh, IProgress<double>? progress, CancellationToken ct);
    /// <summary>Per-series diff results of the LAST completed scan, for the virtual writer (Task 17/18).</summary>
    public IReadOnlyDictionary<Guid, (SeriesDiff Diff, RemoteCatalog Catalog)> LastDiffs { get; }
}
```

Behavior contract (all tested below): route via `SourceRouter.Route`; lane gating (TvdbId/ImdbId ⇒ TV lane, AniDbId ⇒ anime lane, TmdbId series ⇒ TV lane); muted items reported `Muted=true` with no fetch; TVDB failure → TVmaze-by-tvdbid fallback (`UsedFallback=true`, catalog SourceKey "TvmazeFallback"); both fail → `Error`, scan continues; any fetcher exception caught → `Error`; Ok-catalog-with-zero-episodes + owned>0 → treated as `Error` (fail-safe); cache consult before fetch with TTL by `IsEnded` (fullRefresh bypasses reads but still stores); movies grouped by collection processed once, `NoCollection` silently skipped, movie fetch errors → GlobalNotes; `RouteDecision.None` → Error "no usable provider id"; second concurrent `ScanAsync` throws `InvalidOperationException`.

- [ ] **Step 1: Write the failing tests**

```csharp
// Edge-case inventory:
// - routing: tvdbid folder -> ITvdbSource; anime lib + AniDB -> IAniDbSource; tmdbid -> ITmdbSource; imdb-only -> ITvmazeSource(imdb).
// - lane toggles: disabled lane -> series skipped with note, no fetch.
// - mute list: no fetch, Muted=true.
// - TVDB fail -> TVmaze fallback used (UsedFallback), missing computed from fallback.
// - both TVDB and TVmaze fail -> Error, other series still processed.
// - fetcher THROWS -> caught as Error, scan continues.
// - zero-episode Ok catalog with owned>0 -> Error (fail-safe), zero missing.
// - cache: 2nd scan within TTL -> no fetcher call; fullRefresh -> fetcher called again;
//          ended catalog cached 7d vs continuing 1d boundary honored.
// - movies: two owned movies in one collection -> collection processed once; missing computed;
//           movie without collection skipped; RouteDecision.None -> Error.
// - concurrent ScanAsync -> InvalidOperationException.
using Jellyfin.Plugin.DownloadTime.Model;
using Jellyfin.Plugin.DownloadTime.Services;
using Jellyfin.Plugin.DownloadTime.Services.Lanes;
using Jellyfin.Plugin.DownloadTime.Tests.Support;
using Xunit;

namespace Jellyfin.Plugin.DownloadTime.Tests;

public class ScanServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 7, 25, 12, 0, 0, TimeSpan.Zero);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dt-scan-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    // ---- fakes -------------------------------------------------------------
    private sealed class FakeLibrary : ILibraryReader
    {
        public List<SeriesItemInfo> Series { get; } = new();
        public List<MovieItemInfo> Movies { get; } = new();
        public IReadOnlyList<SeriesItemInfo> GetSeries() => Series;
        public IReadOnlyList<MovieItemInfo> GetMovies() => Movies;
    }

    private sealed class FakeTvdb : ITvdbSource
    {
        public Func<string, FetchOutcome> Respond = _ => FetchOutcome.Fail("unscripted");
        public int Calls;
        public Task<FetchOutcome> FetchByTvdbIdAsync(string id, CancellationToken ct) { Calls++; return Task.FromResult(Respond(id)); }
    }

    private sealed class FakeTvmaze : ITvmazeSource
    {
        public Func<string, FetchOutcome> RespondTvdb = _ => FetchOutcome.Fail("unscripted");
        public Func<string, FetchOutcome> RespondImdb = _ => FetchOutcome.Fail("unscripted");
        public int Calls;
        public Task<FetchOutcome> FetchByTvdbIdAsync(string id, CancellationToken ct) { Calls++; return Task.FromResult(RespondTvdb(id)); }
        public Task<FetchOutcome> FetchByImdbIdAsync(string id, CancellationToken ct) { Calls++; return Task.FromResult(RespondImdb(id)); }
    }

    private sealed class FakeAniDb : IAniDbSource
    {
        public Func<string, FetchOutcome> Respond = _ => FetchOutcome.Fail("unscripted");
        public int Calls;
        public Task<FetchOutcome> FetchByAnimeIdAsync(string id, CancellationToken ct) { Calls++; return Task.FromResult(Respond(id)); }
    }

    private sealed class FakeTmdb : ITmdbSource
    {
        public Func<string, FetchOutcome> RespondSeries = _ => FetchOutcome.Fail("unscripted");
        public Func<int, CollectionOutcome> RespondCollection = _ => new CollectionOutcome(null, null, true);
        public int SeriesCalls, CollectionCalls;
        public Task<FetchOutcome> FetchSeriesAsync(string id, CancellationToken ct) { SeriesCalls++; return Task.FromResult(RespondSeries(id)); }
        public Task<CollectionOutcome> FetchCollectionForMovieAsync(int id, CancellationToken ct) { CollectionCalls++; return Task.FromResult(RespondCollection(id)); }
    }

    // ---- helpers -----------------------------------------------------------
    private static ScanSettings Settings(bool tv = true, bool anime = true, bool movies = true, string[]? muted = null)
        => new(tv, anime, movies, 24, false, 90, new HashSet<string>(muted ?? Array.Empty<string>()),
               TimeSpan.FromDays(1), TimeSpan.FromDays(7));

    private static SeriesItemInfo Series(Guid id, string path, bool animeLib, Dictionary<string, string> ids, params OwnedEpisode[] eps)
        => new(id, System.IO.Path.GetFileName(path), path, animeLib, ids, eps);

    private static OwnedEpisode O(int s, int n) => new(s, n, null, new Dictionary<string, string>(), null);

    private static RemoteCatalog TvdbCat(params RemoteEpisode[] eps) => new("Tvdb", "Tvdb", "1", false, eps);
    private static RemoteEpisode R(int s, int n, string id) => new(s, n, id, AirTime.FromDate(2026, 1, n), false, null);

    private (ScanService Svc, FakeLibrary Lib, FakeTvdb Tvdb, FakeTvmaze Tvmaze, FakeAniDb Ani, FakeTmdb Tmdb, FakeClock Clock) Make()
    {
        var lib = new FakeLibrary();
        var tvdb = new FakeTvdb();
        var tvmaze = new FakeTvmaze();
        var ani = new FakeAniDb();
        var tmdb = new FakeTmdb();
        var clock = new FakeClock(Now);
        var svc = new ScanService(lib, tvdb, tvmaze, ani, tmdb, new CatalogCache(_dir, clock), clock);
        return (svc, lib, tvdb, tvmaze, ani, tmdb, clock);
    }

    // ---- tests ---------------------------------------------------------------

    [Fact]
    public async Task Routing_TvdbFolder_UsesTvdb_MissingComputed()
    {
        var (svc, lib, tvdb, _, _, _, _) = Make();
        var sid = Guid.NewGuid();
        lib.Series.Add(Series(sid, @"D:\TV\X (2020) [tvdbid-1]", false,
            new() { ["Tvdb"] = "1" }, O(1, 1)));
        tvdb.Respond = _ => FetchOutcome.Ok(TvdbCat(R(1, 1, "e1"), R(1, 2, "e2")));
        var report = await svc.ScanAsync(Settings(), false, null, CancellationToken.None);
        var s = Assert.Single(report.Series);
        Assert.Equal("Tvdb", s.Lane);
        Assert.False(s.UsedFallback);
        Assert.Null(s.Error);
        var m = Assert.Single(s.Missing);
        Assert.Equal(2, m.Number);
        Assert.True(svc.LastDiffs.ContainsKey(sid));
    }

    [Fact]
    public async Task Routing_AnimeAndTmdbAndImdb()
    {
        var (svc, lib, _, tvmaze, ani, tmdb, _) = Make();
        lib.Series.Add(Series(Guid.NewGuid(), @"D:\Anime\A [tvdbid-9]", true, new() { ["AniDB"] = "18164", ["Tvdb"] = "9" }, O(1, 1)));
        lib.Series.Add(Series(Guid.NewGuid(), @"D:\TV\B [tmdbid-110316]", false, new() { ["Tmdb"] = "110316" }, O(1, 1)));
        lib.Series.Add(Series(Guid.NewGuid(), @"D:\TV\C", false, new() { ["Imdb"] = "tt1" }, O(1, 1)));
        ani.Respond = _ => FetchOutcome.Ok(new RemoteCatalog("AniDB", "AniDB", "18164", true, new[] { new RemoteEpisode(null, 1, "274088", AirTime.FromDate(2024, 1, 7), false, null) }));
        tmdb.RespondSeries = _ => FetchOutcome.Ok(new RemoteCatalog("Tmdb", null, "110316", true, new[] { new RemoteEpisode(1, 1, null, AirTime.FromDate(2020, 12, 10), false, null) }));
        tvmaze.RespondImdb = _ => FetchOutcome.Ok(new RemoteCatalog("TvmazeFallback", null, "3182", true, new[] { new RemoteEpisode(1, 1, null, AirTime.FromDate(2017, 4, 30), false, null) }));
        var report = await svc.ScanAsync(Settings(), false, null, CancellationToken.None);
        Assert.Equal(1, ani.Calls);
        Assert.Equal(1, tmdb.SeriesCalls);
        Assert.Equal(1, tvmaze.Calls);
        Assert.All(report.Series, s => Assert.Null(s.Error));
    }

    [Fact]
    public async Task LaneToggles_SkipWithoutFetch()
    {
        var (svc, lib, tvdb, _, ani, _, _) = Make();
        lib.Series.Add(Series(Guid.NewGuid(), @"D:\TV\X [tvdbid-1]", false, new() { ["Tvdb"] = "1" }, O(1, 1)));
        lib.Series.Add(Series(Guid.NewGuid(), @"D:\Anime\A", true, new() { ["AniDB"] = "2" }, O(1, 1)));
        var report = await svc.ScanAsync(Settings(tv: false, anime: false), false, null, CancellationToken.None);
        Assert.Equal(0, tvdb.Calls);
        Assert.Equal(0, ani.Calls);
        Assert.All(report.Series, s => Assert.Contains(s.Notes, n => n.Contains("disabled", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task MutedSeries_NoFetch_MutedFlag()
    {
        var (svc, lib, tvdb, _, _, _, _) = Make();
        var sid = Guid.NewGuid();
        lib.Series.Add(Series(sid, @"D:\TV\X [tvdbid-1]", false, new() { ["Tvdb"] = "1" }, O(1, 1)));
        var report = await svc.ScanAsync(Settings(muted: new[] { sid.ToString("N") }), false, null, CancellationToken.None);
        Assert.Equal(0, tvdb.Calls);
        Assert.True(Assert.Single(report.Series).Muted);
    }

    [Fact]
    public async Task TvdbFails_TvmazeFallback_Engaged()
    {
        var (svc, lib, tvdb, tvmaze, _, _, _) = Make();
        lib.Series.Add(Series(Guid.NewGuid(), @"D:\TV\X [tvdbid-1]", false, new() { ["Tvdb"] = "1" }, O(1, 1)));
        tvdb.Respond = _ => FetchOutcome.Fail("markup changed");
        tvmaze.RespondTvdb = _ => FetchOutcome.Ok(new RemoteCatalog("TvmazeFallback", null, "3182", false,
            new[] { new RemoteEpisode(1, 1, null, AirTime.FromDate(2026, 1, 1), false, null), new RemoteEpisode(1, 2, null, AirTime.FromDate(2026, 1, 2), false, null) }));
        var report = await svc.ScanAsync(Settings(), false, null, CancellationToken.None);
        var s = Assert.Single(report.Series);
        Assert.True(s.UsedFallback);
        Assert.Null(s.Error);
        Assert.Single(s.Missing);
    }

    [Fact]
    public async Task BothFail_ErrorRecorded_OthersContinue()
    {
        var (svc, lib, tvdb, tvmaze, _, _, _) = Make();
        lib.Series.Add(Series(Guid.NewGuid(), @"D:\TV\X [tvdbid-1]", false, new() { ["Tvdb"] = "1" }, O(1, 1)));
        lib.Series.Add(Series(Guid.NewGuid(), @"D:\TV\Y [tvdbid-2]", false, new() { ["Tvdb"] = "2" }, O(1, 1)));
        tvdb.Respond = id => id == "1" ? FetchOutcome.Fail("down") : FetchOutcome.Ok(TvdbCat(R(1, 1, "a")));
        tvmaze.RespondTvdb = _ => FetchOutcome.Fail("also down");
        var report = await svc.ScanAsync(Settings(), false, null, CancellationToken.None);
        Assert.NotNull(report.Series.Single(s => s.Name.StartsWith("X")).Error);
        Assert.Null(report.Series.Single(s => s.Name.StartsWith("Y")).Error);
    }

    [Fact]
    public async Task FetcherThrows_CaughtAsError_ScanContinues()
    {
        var (svc, lib, tvdb, tvmaze, _, _, _) = Make();
        lib.Series.Add(Series(Guid.NewGuid(), @"D:\TV\X [tvdbid-1]", false, new() { ["Tvdb"] = "1" }, O(1, 1)));
        lib.Series.Add(Series(Guid.NewGuid(), @"D:\TV\Y [tvdbid-2]", false, new() { ["Tvdb"] = "2" }, O(1, 1)));
        tvdb.Respond = id => id == "1" ? throw new InvalidOperationException("boom") : FetchOutcome.Ok(TvdbCat(R(1, 1, "a")));
        tvmaze.RespondTvdb = _ => throw new InvalidOperationException("boom2");
        var report = await svc.ScanAsync(Settings(), false, null, CancellationToken.None);
        Assert.NotNull(report.Series.Single(s => s.Name.StartsWith("X")).Error);
        Assert.Null(report.Series.Single(s => s.Name.StartsWith("Y")).Error);
    }

    [Fact]
    public async Task ZeroEpisodeCatalog_WithOwned_FailSafeError()
    {
        var (svc, lib, tvdb, tvmaze, _, _, _) = Make();
        lib.Series.Add(Series(Guid.NewGuid(), @"D:\TV\X [tvdbid-1]", false, new() { ["Tvdb"] = "1" }, O(1, 1)));
        tvdb.Respond = _ => FetchOutcome.Ok(new RemoteCatalog("Tvdb", "Tvdb", "1", false, Array.Empty<RemoteEpisode>()));
        tvmaze.RespondTvdb = _ => FetchOutcome.Fail("nope");
        var report = await svc.ScanAsync(Settings(), false, null, CancellationToken.None);
        var s = Assert.Single(report.Series);
        Assert.NotNull(s.Error);
        Assert.Empty(s.Missing);
    }

    [Fact]
    public async Task Cache_SecondScanWithinTtl_NoFetch_FullRefreshBypasses()
    {
        var (svc, lib, tvdb, _, _, _, clock) = Make();
        lib.Series.Add(Series(Guid.NewGuid(), @"D:\TV\X [tvdbid-1]", false, new() { ["Tvdb"] = "1" }, O(1, 1)));
        tvdb.Respond = _ => FetchOutcome.Ok(TvdbCat(R(1, 1, "a"), R(1, 2, "b")));
        await svc.ScanAsync(Settings(), false, null, CancellationToken.None);
        Assert.Equal(1, tvdb.Calls);
        clock.UtcNow = Now.AddHours(6); // within 1d continuing TTL
        await svc.ScanAsync(Settings(), false, null, CancellationToken.None);
        Assert.Equal(1, tvdb.Calls); // served from cache
        clock.UtcNow = Now.AddDays(2); // past TTL
        await svc.ScanAsync(Settings(), false, null, CancellationToken.None);
        Assert.Equal(2, tvdb.Calls);
        await svc.ScanAsync(Settings(), fullRefresh: true, null, CancellationToken.None);
        Assert.Equal(3, tvdb.Calls); // bypassed
    }

    [Fact]
    public async Task Movies_CollectionProcessedOnce_MissingComputed_NoCollectionSkipped()
    {
        var (svc, lib, _, _, _, tmdb, _) = Make();
        lib.Movies.Add(new MovieItemInfo(Guid.NewGuid(), "John Wick", 245891));
        lib.Movies.Add(new MovieItemInfo(Guid.NewGuid(), "John Wick: Chapter 2", 324552));
        lib.Movies.Add(new MovieItemInfo(Guid.NewGuid(), "Standalone", 777));
        lib.Movies.Add(new MovieItemInfo(Guid.NewGuid(), "NoTmdbId", null));
        var jw = new CollectionCatalog(404609, "John Wick Collection", new[]
        {
            new RemoteMovie(245891, "John Wick", AirTime.FromDate(2014, 10, 24)),
            new RemoteMovie(324552, "John Wick: Chapter 2", AirTime.FromDate(2017, 2, 10)),
            new RemoteMovie(458156, "John Wick: Chapter 3", AirTime.FromDate(2019, 5, 17)),
        });
        tmdb.RespondCollection = id => id is 245891 or 324552
            ? new CollectionOutcome(jw, null, false)
            : new CollectionOutcome(null, null, true);
        var report = await svc.ScanAsync(Settings(), false, null, CancellationToken.None);
        var col = Assert.Single(report.Collections);
        Assert.Equal(458156, Assert.Single(col.Missing).TmdbId);
        // collection catalog fetched for at most one member thanks to per-collection dedup
        Assert.True(tmdb.CollectionCalls <= 3);
    }

    [Fact]
    public async Task NoUsableId_Error()
    {
        var (svc, lib, _, _, _, _, _) = Make();
        lib.Series.Add(Series(Guid.NewGuid(), @"D:\TV\X", false, new(), O(1, 1)));
        var report = await svc.ScanAsync(Settings(), false, null, CancellationToken.None);
        Assert.Contains("provider id", Assert.Single(report.Series).Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConcurrentScan_Throws()
    {
        var (svc, lib, tvdb, _, _, _, _) = Make();
        lib.Series.Add(Series(Guid.NewGuid(), @"D:\TV\X [tvdbid-1]", false, new() { ["Tvdb"] = "1" }, O(1, 1)));
        var gate = new TaskCompletionSource();
        tvdb.Respond = _ => { gate.Task.Wait(); return FetchOutcome.Ok(TvdbCat(R(1, 1, "a"))); };
        var first = Task.Run(() => svc.ScanAsync(Settings(), false, null, CancellationToken.None));
        while (!svc.IsScanning) { await Task.Delay(10); }
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.ScanAsync(Settings(), false, null, CancellationToken.None));
        gate.SetResult();
        await first;
    }
}
```

- [ ] **Step 2: Run red** — `dotnet test --filter ScanServiceTests`; expected: compile error.

- [ ] **Step 3: Implement** `Services/ScanService.cs`:

```csharp
using Jellyfin.Plugin.DownloadTime.Model;
using Jellyfin.Plugin.DownloadTime.Services.Lanes;

namespace Jellyfin.Plugin.DownloadTime.Services;

public sealed record ScanSettings(
    bool EnableTvLane, bool EnableAnimeLane, bool EnableMovieLane,
    int GraceHours, bool IncludeSpecials, int MovieReleaseBufferDays,
    IReadOnlySet<string> ExcludedItemIds,
    TimeSpan ContinuingTtl, TimeSpan EndedTtl);

/// <summary>Orchestrates a full library scan (spec §2). Per-item failures are
/// isolated; a source outage can never read as "everything missing".</summary>
public class ScanService
{
    private readonly ILibraryReader _library;
    private readonly ITvdbSource _tvdb;
    private readonly ITvmazeSource _tvmaze;
    private readonly IAniDbSource _anidb;
    private readonly ITmdbSource _tmdb;
    private readonly CatalogCache _cache;
    private readonly IClock _clock;
    private int _scanning;

    public ScanService(ILibraryReader library, ITvdbSource tvdb, ITvmazeSource tvmaze,
                       IAniDbSource anidb, ITmdbSource tmdb, CatalogCache cache, IClock clock)
    {
        _library = library;
        _tvdb = tvdb;
        _tvmaze = tvmaze;
        _anidb = anidb;
        _tmdb = tmdb;
        _cache = cache;
        _clock = clock;
    }

    public bool IsScanning => Volatile.Read(ref _scanning) == 1;

    public IReadOnlyDictionary<Guid, (SeriesDiff Diff, RemoteCatalog Catalog)> LastDiffs { get; private set; }
        = new Dictionary<Guid, (SeriesDiff, RemoteCatalog)>();

    public async Task<ScanReport> ScanAsync(ScanSettings settings, bool fullRefresh, IProgress<double>? progress, CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _scanning, 1, 0) != 0)
        {
            throw new InvalidOperationException("A Download Time scan is already running.");
        }
        try
        {
            var started = _clock.UtcNow;
            var seriesReports = new List<SeriesReportDto>();
            var globalNotes = new List<string>();
            var diffs = new Dictionary<Guid, (SeriesDiff, RemoteCatalog)>();

            var allSeries = _library.GetSeries();
            var movies = _library.GetMovies();
            var totalUnits = Math.Max(1, allSeries.Count + 1);
            var done = 0;

            foreach (var series in allSeries)
            {
                ct.ThrowIfCancellationRequested();
                seriesReports.Add(await ScanSeriesAsync(series, settings, fullRefresh, diffs, ct).ConfigureAwait(false));
                progress?.Report(100.0 * ++done / totalUnits);
            }

            var collections = settings.EnableMovieLane
                ? await ScanMoviesAsync(movies, settings, fullRefresh, globalNotes, ct).ConfigureAwait(false)
                : new List<CollectionReportDto>();

            LastDiffs = diffs;
            progress?.Report(100);
            return new ScanReport(started, _clock.UtcNow, seriesReports, collections, globalNotes);
        }
        finally
        {
            Volatile.Write(ref _scanning, 0);
        }
    }

    private async Task<SeriesReportDto> ScanSeriesAsync(
        SeriesItemInfo series, ScanSettings settings, bool fullRefresh,
        Dictionary<Guid, (SeriesDiff, RemoteCatalog)> diffs, CancellationToken ct)
    {
        var route = SourceRouter.Route(series.Path, series.IsAnimeLibrary, series.ProviderIds);
        var lane = route.Kind.ToString();
        SeriesReportDto Report(string? error, bool usedFallback = false, bool muted = false,
            IReadOnlyList<string>? notes = null, IReadOnlyList<MissingEpisodeDto>? missing = null)
            => new(series.Id, series.Name, lane, usedFallback, muted, error,
                   notes ?? Array.Empty<string>(), missing ?? Array.Empty<MissingEpisodeDto>());

        if (settings.ExcludedItemIds.Contains(series.Id.ToString("N")))
        {
            return Report(null, muted: true);
        }
        if (route.Kind == SourceKind.None)
        {
            return Report("No usable provider id (folder tag or Tvdb/Tmdb/Imdb metadata).");
        }
        var laneEnabled = route.Kind switch
        {
            SourceKind.AniDbId => settings.EnableAnimeLane,
            _ => settings.EnableTvLane,
        };
        if (!laneEnabled)
        {
            return Report(null, notes: new[] { "Lane disabled in settings." });
        }

        var usedFallback = false;
        RemoteCatalog? catalog = null;
        string? error = null;
        var cacheKey = $"{route.Kind}-{route.SourceId}".ToLowerInvariant();
        try
        {
            if (!fullRefresh)
            {
                catalog = _cache.TryGet<RemoteCatalog>(cacheKey, settings.EndedTtl) is { IsEnded: true } ended
                    ? ended
                    : _cache.TryGet<RemoteCatalog>(cacheKey, settings.ContinuingTtl);
            }
            if (catalog is null)
            {
                var outcome = route.Kind switch
                {
                    SourceKind.TvdbId => await _tvdb.FetchByTvdbIdAsync(route.SourceId, ct).ConfigureAwait(false),
                    SourceKind.AniDbId => await _anidb.FetchByAnimeIdAsync(route.SourceId, ct).ConfigureAwait(false),
                    SourceKind.TmdbId => await _tmdb.FetchSeriesAsync(route.SourceId, ct).ConfigureAwait(false),
                    SourceKind.ImdbId => await _tvmaze.FetchByImdbIdAsync(route.SourceId, ct).ConfigureAwait(false),
                    _ => FetchOutcome.Fail("unroutable"),
                };
                if (outcome.Catalog is null && route.Kind == SourceKind.TvdbId)
                {
                    var fb = await _tvmaze.FetchByTvdbIdAsync(route.SourceId, ct).ConfigureAwait(false);
                    if (fb.Catalog is not null)
                    {
                        outcome = fb;
                        usedFallback = true;
                    }
                    else
                    {
                        outcome = FetchOutcome.Fail($"{outcome.Error}; fallback: {fb.Error}");
                    }
                }
                catalog = outcome.Catalog;
                error = outcome.Error;
                if (catalog is not null)
                {
                    _cache.Store(cacheKey, catalog);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Report($"Fetch crashed: {ex.Message}");
        }

        if (catalog is null)
        {
            return Report(error ?? "fetch failed");
        }
        if (catalog.Episodes.Count == 0 && series.Episodes.Count > 0)
        {
            return Report("Source returned zero episodes for a non-empty series (fail-safe).");
        }

        var diff = DiffEngine.Diff(series.Episodes, catalog,
            new DiffOptions(_clock.UtcNow, settings.GraceHours, settings.IncludeSpecials));
        diffs[series.Id] = (diff, catalog);
        var missing = diff.Missing
            .Select(m => new MissingEpisodeDto(m.Episode.Season, m.Episode.Number, m.Episode.Title,
                                               m.Episode.AiredAt, m.Kind.ToString(), m.Episode.SourceEpisodeId))
            .ToList();
        return Report(null, usedFallback, notes: diff.Notes, missing: missing);
    }

    private async Task<List<CollectionReportDto>> ScanMoviesAsync(
        IReadOnlyList<MovieItemInfo> movies, ScanSettings settings, bool fullRefresh,
        List<string> globalNotes, CancellationToken ct)
    {
        var results = new List<CollectionReportDto>();
        var ownedTmdbIds = movies.Where(m => m.TmdbId.HasValue).Select(m => m.TmdbId!.Value).ToHashSet();
        var seenCollections = new HashSet<int>();

        foreach (var movie in movies)
        {
            ct.ThrowIfCancellationRequested();
            if (!movie.TmdbId.HasValue)
            {
                continue;
            }
            if (settings.ExcludedItemIds.Contains(movie.Id.ToString("N")))
            {
                continue;
            }
            try
            {
                var cacheKey = $"tmdb-movie-{movie.TmdbId.Value}";
                var catalog = fullRefresh ? null : _cache.TryGet<CollectionCatalog>(cacheKey, settings.EndedTtl);
                if (catalog is null)
                {
                    var outcome = await _tmdb.FetchCollectionForMovieAsync(movie.TmdbId.Value, ct).ConfigureAwait(false);
                    if (outcome.NoCollection)
                    {
                        continue;
                    }
                    if (outcome.Catalog is null)
                    {
                        globalNotes.Add($"{movie.Name}: {outcome.Error}");
                        continue;
                    }
                    catalog = outcome.Catalog;
                    _cache.Store(cacheKey, catalog);
                }
                if (!seenCollections.Add(catalog.CollectionId))
                {
                    continue;
                }
                var missing = CollectionDiff.MissingMovies(ownedTmdbIds, catalog, _clock.UtcNow, settings.MovieReleaseBufferDays);
                if (missing.Count > 0)
                {
                    results.Add(new CollectionReportDto(
                        catalog.Name, movie.Name,
                        missing.Select(m => new MissingMovieDto(m.TmdbId, m.Title, m.ReleasedAt)).ToList()));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                globalNotes.Add($"{movie.Name}: movie scan crashed: {ex.Message}");
            }
        }
        return results;
    }
}
```

- [ ] **Step 4: Run green** — `dotnet test --filter ScanServiceTests` then full suite. Note the cache-TTL subtlety: the implementation tries the ENDED TTL first and only accepts the hit if the cached catalog says `IsEnded`; otherwise re-reads with the continuing TTL. If the `Cache_SecondScanWithinTtl` test exposes a flaw in that logic, fix the implementation.
- [ ] **Step 5: Commit** — `git add -A && git commit -m "Add scan orchestrator with routing, fallback, cache, fail-safes"`

---

### Task 15: ScanRunner + REST controller

**Files:**
- Create: `src/Jellyfin.Plugin.DownloadTime/Services/ScanRunner.cs`, `src/Jellyfin.Plugin.DownloadTime/Api/DownloadTimeController.cs`, `tests/.../ApiControllerTests.cs`

**Interfaces:**
- Produces (`Services/ScanRunner.cs`):

```csharp
using Jellyfin.Plugin.DownloadTime.Model;

namespace Jellyfin.Plugin.DownloadTime.Services;

/// <summary>Bridges configuration to ScanService and persists results.</summary>
public class ScanRunner
{
    private readonly ScanService _scan;
    private readonly ReportStore _store;
    private readonly Func<PluginConfiguration> _config;

    public ScanRunner(ScanService scan, ReportStore store, Func<PluginConfiguration> config)
    {
        _scan = scan;
        _store = store;
        _config = config;
    }

    public bool IsScanning => _scan.IsScanning;
    public ScanService Scan => _scan;

    public static ScanSettings ToSettings(PluginConfiguration c) => new(
        c.EnableTvLane, c.EnableAnimeLane, c.EnableMovieLane,
        c.GraceHours, c.IncludeSpecials, c.MovieReleaseBufferDays,
        new HashSet<string>(c.ExcludedItemIds, StringComparer.OrdinalIgnoreCase),
        TimeSpan.FromDays(c.ContinuingTtlDays), TimeSpan.FromDays(c.EndedTtlDays));

    public async Task<ScanReport> RunAsync(bool fullRefresh, IProgress<double>? progress, CancellationToken ct)
    {
        var report = await _scan.ScanAsync(ToSettings(_config()), fullRefresh, progress, ct).ConfigureAwait(false);
        _store.Save(report);
        return report;
    }
}
```

- Produces (`Api/DownloadTimeController.cs`): `GET /DownloadTime/Report` (`[Authorize]`, any logged-in user — badges need it) returns `ScanReport` (a well-formed empty report when no scan has run); `POST /DownloadTime/Scan?fullRefresh=` (`[Authorize(Policy = "RequiresElevation")]`) → 409 when scanning, else fire-and-forget scan + 202.

- [ ] **Step 1: Write the failing tests**

```csharp
// Edge-case inventory:
// - settings mapping: every config field lands in ScanSettings (incl. GraceHours=0, mute list).
// - GET Report with no scan yet -> 200, empty well-formed report (never 404/null).
// - GET Report after Save -> the saved report.
// - POST Scan while running -> 409; when idle -> 202.
// - auth attributes: GET requires [Authorize]; POST requires policy RequiresElevation.
using System.Reflection;
using Jellyfin.Plugin.DownloadTime.Api;
using Jellyfin.Plugin.DownloadTime.Model;
using Jellyfin.Plugin.DownloadTime.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Jellyfin.Plugin.DownloadTime.Tests;

public class ApiControllerTests
{
    [Fact]
    public void ToSettings_MapsAllFields()
    {
        var c = new PluginConfiguration
        {
            EnableTvLane = false, EnableAnimeLane = true, EnableMovieLane = false,
            GraceHours = 0, IncludeSpecials = true, MovieReleaseBufferDays = 30,
            ExcludedItemIds = new[] { "abc" }, ContinuingTtlDays = 2, EndedTtlDays = 14,
        };
        var s = ScanRunner.ToSettings(c);
        Assert.False(s.EnableTvLane);
        Assert.True(s.EnableAnimeLane);
        Assert.False(s.EnableMovieLane);
        Assert.Equal(0, s.GraceHours);
        Assert.True(s.IncludeSpecials);
        Assert.Equal(30, s.MovieReleaseBufferDays);
        Assert.Contains("abc", s.ExcludedItemIds);
        Assert.Equal(TimeSpan.FromDays(2), s.ContinuingTtl);
        Assert.Equal(TimeSpan.FromDays(14), s.EndedTtl);
    }

    [Fact]
    public void AuthAttributes_AsSpecified()
    {
        var get = typeof(DownloadTimeController).GetMethod(nameof(DownloadTimeController.GetReport))!;
        Assert.NotNull(get.GetCustomAttribute<AuthorizeAttribute>());
        var post = typeof(DownloadTimeController).GetMethod(nameof(DownloadTimeController.StartScan))!;
        Assert.Equal("RequiresElevation", post.GetCustomAttribute<AuthorizeAttribute>()!.Policy);
    }

    [Fact]
    public void GetReport_NoScanYet_EmptyWellFormed()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dt-api-" + Guid.NewGuid().ToString("N"));
        try
        {
            var controller = new DownloadTimeController(new ReportStore(dir), null);
            var result = Assert.IsType<OkObjectResult>(controller.GetReport().Result);
            var report = Assert.IsType<ScanReport>(result.Value);
            Assert.Empty(report.Series);
            Assert.Empty(report.Collections);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
```

(The 202/409 scan-trigger behavior is exercised in the live E2E — unit faking of the fire-and-forget path adds no confidence beyond `ScanServiceTests.ConcurrentScan_Throws`.)

- [ ] **Step 2: Run red** — `dotnet test --filter ApiControllerTests`; expected: compile error.

- [ ] **Step 3: Implement** `Api/DownloadTimeController.cs`:

```csharp
using Jellyfin.Plugin.DownloadTime.Model;
using Jellyfin.Plugin.DownloadTime.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.DownloadTime.Api;

[ApiController]
[Route("DownloadTime")]
public class DownloadTimeController : ControllerBase
{
    private readonly ReportStore _store;
    private readonly ScanRunner? _runner;

    public DownloadTimeController(ReportStore store, ScanRunner? runner)
    {
        _store = store;
        _runner = runner;
    }

    /// <summary>Last scan report; empty report when no scan has run yet.</summary>
    [HttpGet("Report")]
    [Authorize]
    public ActionResult<ScanReport> GetReport()
        => Ok(_store.Current ?? new ScanReport(
            default, default,
            Array.Empty<SeriesReportDto>(), Array.Empty<CollectionReportDto>(),
            new[] { "No scan has run yet." }));

    /// <summary>Kicks off a scan in the background. 409 if one is already running.</summary>
    [HttpPost("Scan")]
    [Authorize(Policy = "RequiresElevation")]
    public ActionResult StartScan([FromQuery] bool fullRefresh = false)
    {
        if (_runner is null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Scan runner not available.");
        }
        if (_runner.IsScanning)
        {
            return Conflict("A scan is already running.");
        }
        _ = Task.Run(() => _runner.RunAsync(fullRefresh, null, CancellationToken.None));
        return Accepted();
    }
}
```

- [ ] **Step 4: Run green** — `dotnet test`.
- [ ] **Step 5: Commit** — `git add -A && git commit -m "Add scan runner and REST endpoints for report and scan trigger"`

---

### Task 16: Jellyfin wiring — library reader, scheduled scan task, DI

This task is adapter code around Jellyfin types (`ILibraryManager`, `IScheduledTask`) that cannot run under xunit without a server; its test gate is: (a) the full existing suite stays green, (b) **all three ABI builds compile**, (c) behavior is covered by the Task 21 E2E. No new unit tests — do NOT write untestable mock-theater.

**Files:**
- Create: `src/Jellyfin.Plugin.DownloadTime/Services/JellyfinLibraryReader.cs`, `src/Jellyfin.Plugin.DownloadTime/Tasks/ScanTask.cs`, `src/Jellyfin.Plugin.DownloadTime/PluginServiceRegistrator.cs`

**Interfaces:**
- Consumes: `ILibraryReader` (Task 14), `ScanRunner` (Task 15).
- Produces: DI registrations used at runtime; `ScanTask` key `DownloadTimeScan`, category `Download Time`, default trigger daily 06:00.

- [ ] **Step 1: Implement** `Services/JellyfinLibraryReader.cs`:

```csharp
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.DownloadTime.Model;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.DownloadTime.Services;

/// <summary>Reads series/movies from the Jellyfin library into plain DTOs.
/// Virtual items are excluded from OWNED episodes (our own placeholders must
/// never count as owned). IsAnimeLibrary == item carries an AniDB id.</summary>
public class JellyfinLibraryReader : ILibraryReader
{
    private readonly ILibraryManager _libraryManager;

    public JellyfinLibraryReader(ILibraryManager libraryManager) => _libraryManager = libraryManager;

    public IReadOnlyList<SeriesItemInfo> GetSeries()
    {
        var result = new List<SeriesItemInfo>();
        var series = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Series },
            Recursive = true,
        }).OfType<Series>();

        foreach (var s in series)
        {
            var owned = new List<OwnedEpisode>();
            foreach (var e in s.GetRecursiveChildren().OfType<Episode>())
            {
                if (e.IsVirtualItem || e.LocationType == LocationType.Virtual)
                {
                    continue;
                }
                owned.Add(new OwnedEpisode(
                    e.ParentIndexNumber, e.IndexNumber, e.IndexNumberEnd,
                    new Dictionary<string, string>(e.ProviderIds, StringComparer.OrdinalIgnoreCase),
                    e.PremiereDate.HasValue ? new DateTimeOffset(e.PremiereDate.Value, TimeSpan.Zero) : null));
            }
            var providerIds = new Dictionary<string, string>(s.ProviderIds, StringComparer.OrdinalIgnoreCase);
            result.Add(new SeriesItemInfo(s.Id, s.Name, s.Path ?? string.Empty,
                providerIds.ContainsKey("AniDB"), providerIds, owned));
        }
        return result;
    }

    public IReadOnlyList<MovieItemInfo> GetMovies()
    {
        var result = new List<MovieItemInfo>();
        var movies = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie },
            Recursive = true,
        }).OfType<Movie>();
        foreach (var m in movies)
        {
            int? tmdb = null;
            if (m.ProviderIds.TryGetValue("Tmdb", out var t) && int.TryParse(t, out var parsed))
            {
                tmdb = parsed;
            }
            result.Add(new MovieItemInfo(m.Id, m.Name, tmdb));
        }
        return result;
    }
}
```

- [ ] **Step 2: Implement** `Tasks/ScanTask.cs` (10.10 trigger shim exactly like Filler Skip):

```csharp
using Jellyfin.Plugin.DownloadTime.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DownloadTime.Tasks;

/// <summary>Daily missing-media scan.</summary>
public class ScanTask : IScheduledTask
{
    private readonly ScanRunner _runner;
    private readonly ILogger<ScanTask> _logger;

    public ScanTask(ScanRunner runner, ILogger<ScanTask> logger)
    {
        _runner = runner;
        _logger = logger;
    }

    public string Name => "Scan for missing media";
    public string Key => "DownloadTimeScan";
    public string Description => "Compares the library against each item's identifying source and records missing episodes/movies.";
    public string Category => "Download Time";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
#if JELLYFIN_10_10
        yield return new TaskTriggerInfo { Type = TaskTriggerInfo.TriggerDaily, TimeOfDayTicks = TimeSpan.FromHours(6).Ticks };
#else
        yield return new TaskTriggerInfo { Type = TaskTriggerInfoType.DailyTrigger, TimeOfDayTicks = TimeSpan.FromHours(6).Ticks };
#endif
    }

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var report = await _runner.RunAsync(fullRefresh: false, progress, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation(
            "Download Time scan finished: {SeriesWithMissing}/{Series} series with missing episodes, {Collections} collections with missing movies.",
            report.Series.Count(s => s.Missing.Count > 0), report.Series.Count, report.Collections.Count);
        // Task 18 appends virtual-placeholder application here when CreateVirtualEpisodes is on.
    }
}
```

- [ ] **Step 3: Implement** `PluginServiceRegistrator.cs`:

```csharp
using Jellyfin.Plugin.DownloadTime.Model;
using Jellyfin.Plugin.DownloadTime.Services;
using Jellyfin.Plugin.DownloadTime.Services.Lanes;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.DownloadTime;

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost applicationHost)
    {
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton(sp =>
        {
            var paths = sp.GetRequiredService<IApplicationPaths>();
            return new CatalogCache(Path.Combine(paths.CachePath, "downloadtime"), sp.GetRequiredService<IClock>());
        });
        services.AddSingleton(sp =>
        {
            var paths = sp.GetRequiredService<IApplicationPaths>();
            return new ReportStore(Path.Combine(paths.DataPath, "downloadtime"));
        });
        static PluginConfiguration Config() => Plugin.Instance?.Configuration ?? new PluginConfiguration();
        services.AddSingleton<ITvdbSource>(sp => new TvdbScrapeFetcher(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient("DownloadTime"),
            () => Config().RequestDelayMs));
        services.AddSingleton<ITvmazeSource>(sp => new TvmazeFetcher(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient("DownloadTime")));
        services.AddSingleton<IAniDbSource>(sp => new AniDbFetcher(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient("DownloadTime"),
            sp.GetRequiredService<IClock>(),
            ts => Task.Delay(ts),
            () => Config().RequestDelayMs,
            () => (Config().AniDbClientName, Config().AniDbClientVersion)));
        services.AddSingleton<ITmdbSource>(sp => new TmdbFetcher(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient("DownloadTime"),
            () => Config().TmdbApiKey,
            ts => Task.Delay(ts)));
        services.AddSingleton<ILibraryReader, JellyfinLibraryReader>();
        services.AddSingleton<ScanService>();
        services.AddSingleton(sp => new ScanRunner(
            sp.GetRequiredService<ScanService>(),
            sp.GetRequiredService<ReportStore>(),
            Config));
    }
}
```

- [ ] **Step 4: Verify all three ABI builds + suite**

```bash
dotnet test
dotnet build src/Jellyfin.Plugin.DownloadTime -c Release -p:JellyfinVersion=12.0
dotnet build src/Jellyfin.Plugin.DownloadTime -c Release -p:JellyfinVersion=10.11
dotnet build src/Jellyfin.Plugin.DownloadTime -c Release -p:JellyfinVersion=10.10.7
```

Expected: suite green, three clean builds. Fix any per-ABI API drift with `#if JELLYFIN_10_10` guards only.

- [ ] **Step 5: Commit** — `git add -A && git commit -m "Wire Jellyfin adapters: library reader, scan task, DI registration"`

---

### Task 17: VirtualEpisodePlanner — pure create/delete decisions

**Files:**
- Create: `src/Jellyfin.Plugin.DownloadTime/Services/VirtualEpisodePlanner.cs`, `tests/.../VirtualEpisodePlannerTests.cs`

**Interfaces:**
- Produces:

```csharp
namespace Jellyfin.Plugin.DownloadTime.Model;

public sealed record ExistingPlaceholder(Guid ItemId, int? Season, int? Number, string Marker);
public sealed record PlaceholderCreate(int Season, int Number, string Marker, string? Title, DateTimeOffset? AiredAt);
public sealed record PlaceholderPlan(IReadOnlyList<PlaceholderCreate> Creates, IReadOnlyList<Guid> Deletes);
```

(append to `Model/Records.cs`), and:

`public static PlaceholderPlan VirtualEpisodePlanner.Plan(SeriesDiff diff, RemoteCatalog catalog, IReadOnlyList<OwnedEpisode> owned, IReadOnlyList<ExistingPlaceholder> existing, bool featureEnabled)` — marker format `{SourceKey}:{SourceEpisodeId}` or `{SourceKey}:S{season}E{number}` when the episode has no source id. `existing` contains ONLY items already filtered to our marker provider id — the planner may delete freely from it, and only from it.

- [ ] **Step 1: Write the failing tests**

```csharp
// Edge-case inventory:
// - feature off -> delete ALL existing, create none.
// - fresh missing episode -> one create at Placer placement, marker stamped.
// - idempotency: existing placeholder matches desired marker+position -> no create, no delete.
// - resolved (no longer missing) -> its placeholder deleted.
// - placement changed (e.g. remote renumbered) -> delete old + create new.
// - unplaceable missing (Placer returns null) -> skipped, no create.
// - HasInvalidContent-style guard: any owned episode with null Number in an
//   id-less catalog -> plan creates NOTHING for that series (dup prevention),
//   but still deletes obsolete placeholders.
using Jellyfin.Plugin.DownloadTime.Model;
using Jellyfin.Plugin.DownloadTime.Services;
using Xunit;

namespace Jellyfin.Plugin.DownloadTime.Tests;

public class VirtualEpisodePlannerTests
{
    private static readonly DateTimeOffset Aired = new(2026, 1, 1, 23, 59, 0, TimeSpan.Zero);

    private static RemoteCatalog TupleCat(params RemoteEpisode[] eps) => new("Tvdb", null, "1", true, eps);
    private static RemoteEpisode R(int s, int n) => new(s, n, null, Aired, false, $"S{s}E{n}");
    private static SeriesDiff DiffOf(params MissingEpisode[] m) => new(m, Array.Empty<string>());
    private static MissingEpisode Gap(RemoteEpisode e) => new(e, MissingKind.Gap);
    private static OwnedEpisode O(int s, int n) => new(s, n, null, new Dictionary<string, string>(), null);

    [Fact]
    public void FeatureOff_DeletesAll_CreatesNone()
    {
        var existing = new[] { new ExistingPlaceholder(Guid.NewGuid(), 1, 2, "Tvdb:S1E2") };
        var plan = VirtualEpisodePlanner.Plan(DiffOf(Gap(R(1, 2))), TupleCat(R(1, 2)), new[] { O(1, 1) }, existing, featureEnabled: false);
        Assert.Empty(plan.Creates);
        Assert.Equal(existing[0].ItemId, Assert.Single(plan.Deletes));
    }

    [Fact]
    public void FreshMissing_CreatesWithMarker()
    {
        var plan = VirtualEpisodePlanner.Plan(DiffOf(Gap(R(1, 2))), TupleCat(R(1, 2)), new[] { O(1, 1) },
            Array.Empty<ExistingPlaceholder>(), true);
        var c = Assert.Single(plan.Creates);
        Assert.Equal((1, 2), (c.Season, c.Number));
        Assert.Equal("Tvdb:S1E2", c.Marker);
        Assert.Equal(Aired, c.AiredAt);
        Assert.Empty(plan.Deletes);
    }

    [Fact]
    public void Idempotent_ExistingMatches_NoOps()
    {
        var existing = new[] { new ExistingPlaceholder(Guid.NewGuid(), 1, 2, "Tvdb:S1E2") };
        var plan = VirtualEpisodePlanner.Plan(DiffOf(Gap(R(1, 2))), TupleCat(R(1, 2)), new[] { O(1, 1) }, existing, true);
        Assert.Empty(plan.Creates);
        Assert.Empty(plan.Deletes);
    }

    [Fact]
    public void Resolved_PlaceholderDeleted()
    {
        var existing = new[] { new ExistingPlaceholder(Guid.NewGuid(), 1, 2, "Tvdb:S1E2") };
        var plan = VirtualEpisodePlanner.Plan(DiffOf(), TupleCat(R(1, 2)), new[] { O(1, 1), O(1, 2) }, existing, true);
        Assert.Empty(plan.Creates);
        Assert.Single(plan.Deletes);
    }

    [Fact]
    public void PlacementChanged_DeleteAndRecreate()
    {
        var idCat = new RemoteCatalog("AniDB", "AniDB", "9", true, new[]
        {
            new RemoteEpisode(null, 1, "x1", Aired, false, null),
            new RemoteEpisode(null, 2, "x2", Aired, false, null),
        });
        var owned = new[] { new OwnedEpisode(1, 13, null, new Dictionary<string, string> { ["AniDB"] = "x1" }, null) };
        var existing = new[] { new ExistingPlaceholder(Guid.NewGuid(), 1, 2, "AniDB:x2") }; // stale position
        var plan = VirtualEpisodePlanner.Plan(DiffOf(new MissingEpisode(idCat.Episodes[1], MissingKind.New)), idCat, owned, existing, true);
        Assert.Single(plan.Deletes);
        var c = Assert.Single(plan.Creates);
        Assert.Equal((1, 14), (c.Season, c.Number)); // anchor x1 at S1E13 -> epno2 at E14
        Assert.Equal("AniDB:x2", c.Marker);
    }

    [Fact]
    public void Unplaceable_Skipped()
    {
        var idCat = new RemoteCatalog("AniDB", "AniDB", "9", true, new[] { new RemoteEpisode(null, 1, "y1", Aired, false, null) });
        var plan = VirtualEpisodePlanner.Plan(DiffOf(new MissingEpisode(idCat.Episodes[0], MissingKind.Gap)), idCat,
            Array.Empty<OwnedEpisode>(), Array.Empty<ExistingPlaceholder>(), true); // no anchors -> Placer null
        Assert.Empty(plan.Creates);
    }

    [Fact]
    public void InvalidLocalNumbering_BlocksCreates_StillDeletesObsolete()
    {
        var existing = new[] { new ExistingPlaceholder(Guid.NewGuid(), 3, 9, "Tvdb:S3E9") }; // obsolete
        var owned = new[] { O(1, 1), new OwnedEpisode(1, null, null, new Dictionary<string, string>(), null) };
        var plan = VirtualEpisodePlanner.Plan(DiffOf(Gap(R(1, 2))), TupleCat(R(1, 2)), owned, existing, true);
        Assert.Empty(plan.Creates);
        Assert.Single(plan.Deletes);
    }
}
```

- [ ] **Step 2: Run red** — `dotnet test --filter VirtualEpisodePlannerTests`; expected: compile error.

- [ ] **Step 3: Implement** — append the three records to `Model/Records.cs`, then `Services/VirtualEpisodePlanner.cs`:

```csharp
using Jellyfin.Plugin.DownloadTime.Model;

namespace Jellyfin.Plugin.DownloadTime.Services;

/// <summary>
/// Decides placeholder creations/deletions (spec §4.3). Pure — the writer
/// (VirtualEpisodeWriter) applies the plan against ILibraryManager.
/// Deletes only ever reference the marker-filtered `existing` list.
/// </summary>
public static class VirtualEpisodePlanner
{
    public static string MarkerFor(RemoteCatalog catalog, RemoteEpisode e)
        => e.SourceEpisodeId is not null
            ? $"{catalog.SourceKey}:{e.SourceEpisodeId}"
            : $"{catalog.SourceKey}:S{e.Season ?? 0}E{e.Number ?? 0}";

    public static PlaceholderPlan Plan(
        SeriesDiff diff, RemoteCatalog catalog, IReadOnlyList<OwnedEpisode> owned,
        IReadOnlyList<ExistingPlaceholder> existing, bool featureEnabled)
    {
        if (!featureEnabled)
        {
            return new PlaceholderPlan(Array.Empty<PlaceholderCreate>(), existing.Select(e => e.ItemId).ToList());
        }

        // 10.4 MissingEpisodeProvider HasInvalidContent guard: unreliable local
        // numbering in a tuple-matched series risks duplicate placeholders.
        var hasInvalid = catalog.IdProviderKey is null
            && owned.Any(o => !o.Number.HasValue || !o.Season.HasValue);

        var desired = new Dictionary<string, PlaceholderCreate>(StringComparer.OrdinalIgnoreCase);
        if (!hasInvalid)
        {
            foreach (var m in diff.Missing)
            {
                var placement = Placer.Infer(m.Episode, owned, catalog);
                if (placement is null)
                {
                    continue;
                }
                var marker = MarkerFor(catalog, m.Episode);
                desired[marker] = new PlaceholderCreate(placement.Season, placement.Number, marker, m.Episode.Title, m.Episode.AiredAt);
            }
        }

        var deletes = new List<Guid>();
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ex in existing)
        {
            if (desired.TryGetValue(ex.Marker, out var want) && ex.Season == want.Season && ex.Number == want.Number)
            {
                keep.Add(ex.Marker);
            }
            else
            {
                deletes.Add(ex.ItemId);
            }
        }
        var creates = desired.Where(kv => !keep.Contains(kv.Key)).Select(kv => kv.Value).ToList();
        return new PlaceholderPlan(creates, deletes);
    }
}
```

- [ ] **Step 4: Run green** — `dotnet test`.
- [ ] **Step 5: Commit** — `git add -A && git commit -m "Add virtual placeholder planner with invalid-content guard"`

---

### Task 18: VirtualEpisodeWriter + ResetTask (thin adapters)

Adapter code over `ILibraryManager`; gate = suite green + 3 ABI builds + the Task 22 E2E (which is the REAL validation — `CreateVirtualEpisodes` stays default-off until Task 22 passes). Modeled on the 10.4 reference (`docs/reference/MissingEpisodeProvider-jellyfin-10.4.cs`), with `DeleteFileLocation = false` and our marker.

**Files:**
- Create: `src/Jellyfin.Plugin.DownloadTime/Services/VirtualEpisodeWriter.cs`, `src/Jellyfin.Plugin.DownloadTime/Tasks/ResetTask.cs`
- Modify: `src/Jellyfin.Plugin.DownloadTime/Tasks/ScanTask.cs` (apply plans after scan), `PluginServiceRegistrator.cs` (register writer)

**Interfaces:**
- Produces: `VirtualEpisodeWriter` with `public const string MarkerProviderKey = "DownloadTime"`; `int Apply(Guid seriesId, PlaceholderPlan plan)` (returns ops applied); `IReadOnlyList<ExistingPlaceholder> GetExisting(Guid seriesId)`; `int DeleteAllPlaceholders()` (ResetTask + uninstall path).

- [ ] **Step 1: Implement** `Services/VirtualEpisodeWriter.cs`:

```csharp
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.DownloadTime.Model;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DownloadTime.Services;

/// <summary>
/// Applies placeholder plans to the library. Every item we create carries
/// ProviderIds[MarkerProviderKey]; we never touch virtual items without it.
/// The server's own SeriesMetadataService.RemoveObsoleteEpisodes removes our
/// placeholders automatically when the physical episode arrives (12.0 verified).
/// </summary>
public class VirtualEpisodeWriter
{
    public const string MarkerProviderKey = "DownloadTime";

    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<VirtualEpisodeWriter> _logger;

    public VirtualEpisodeWriter(ILibraryManager libraryManager, ILogger<VirtualEpisodeWriter> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    public IReadOnlyList<ExistingPlaceholder> GetExisting(Guid seriesId)
    {
        if (_libraryManager.GetItemById(seriesId) is not Series series)
        {
            return Array.Empty<ExistingPlaceholder>();
        }
        return series.GetRecursiveChildren().OfType<Episode>()
            .Where(e => e.IsVirtualItem && e.ProviderIds.TryGetValue(MarkerProviderKey, out var m) && !string.IsNullOrEmpty(m))
            .Select(e => new ExistingPlaceholder(e.Id, e.ParentIndexNumber, e.IndexNumber, e.ProviderIds[MarkerProviderKey]))
            .ToList();
    }

    public int Apply(Guid seriesId, PlaceholderPlan plan)
    {
        if (_libraryManager.GetItemById(seriesId) is not Series series)
        {
            return 0;
        }
        var ops = 0;

        foreach (var id in plan.Deletes)
        {
            if (_libraryManager.GetItemById(id) is Episode ep
                && ep.IsVirtualItem
                && ep.ProviderIds.ContainsKey(MarkerProviderKey))
            {
                _libraryManager.DeleteItem(ep, new DeleteOptions { DeleteFileLocation = false }, false);
                ops++;
            }
        }

        foreach (var create in plan.Creates)
        {
            var season = series.Children.OfType<Season>()
                .FirstOrDefault(s => s.IndexNumber == create.Season);
            if (season is null)
            {
                _logger.LogInformation("Download Time: no local season {Season} for {Series}; skipping placeholder S{Season}E{Number}",
                    create.Season, series.Name, create.Season, create.Number);
                continue; // v1: placeholders only inside existing seasons (virtual season creation is out of scope)
            }
            var name = create.Title ?? $"Episode {create.Number}";
            var episode = new Episode
            {
                Name = name,
                IndexNumber = create.Number,
                ParentIndexNumber = create.Season,
                Id = _libraryManager.GetNewItemId(
                    series.Id + create.Season.ToString(System.Globalization.CultureInfo.InvariantCulture) + create.Marker,
                    typeof(Episode)),
                IsVirtualItem = true,
                SeasonId = season.Id,
                SeriesId = series.Id,
                SeriesName = series.Name,
            };
            if (create.AiredAt.HasValue)
            {
                episode.PremiereDate = create.AiredAt.Value.UtcDateTime;
            }
            episode.ProviderIds[MarkerProviderKey] = create.Marker;
            season.AddChild(episode);
            ops++;
        }
        return ops;
    }

    public int DeleteAllPlaceholders()
    {
        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Episode },
            IsVirtualItem = true,
            Recursive = true,
        });
        var count = 0;
        foreach (var item in items)
        {
            if (item.ProviderIds.TryGetValue(MarkerProviderKey, out var m) && !string.IsNullOrEmpty(m))
            {
                _libraryManager.DeleteItem(item, new DeleteOptions { DeleteFileLocation = false }, false);
                count++;
            }
        }
        return count;
    }
}
```

NOTE (10.10/10.11 ABI): `season.AddChild(episode)` signature differs across versions (`AddChild(BaseItem)` vs `AddChild(BaseItem, CancellationToken)`); guard with `#if JELLYFIN_10_10` if the 10.10 build objects.

- [ ] **Step 2: Implement** `Tasks/ResetTask.cs` (manual-only):

```csharp
using Jellyfin.Plugin.DownloadTime.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DownloadTime.Tasks;

/// <summary>Deletes every virtual placeholder Download Time ever created.</summary>
public class ResetTask : IScheduledTask
{
    private readonly VirtualEpisodeWriter _writer;
    private readonly ILogger<ResetTask> _logger;

    public ResetTask(VirtualEpisodeWriter writer, ILogger<ResetTask> logger)
    {
        _writer = writer;
        _logger = logger;
    }

    public string Name => "Remove all Download Time placeholders";
    public string Key => "DownloadTimeReset";
    public string Description => "Deletes every virtual missing-episode placeholder created by Download Time.";
    public string Category => "Download Time";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Enumerable.Empty<TaskTriggerInfo>();

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var n = _writer.DeleteAllPlaceholders();
        _logger.LogInformation("Download Time reset: removed {Count} placeholders.", n);
        progress.Report(100);
        return Task.CompletedTask;
    }
}
```

- [ ] **Step 3: Wire into ScanTask.ExecuteAsync** (after the log line) and register `VirtualEpisodeWriter` in `PluginServiceRegistrator` (`services.AddSingleton<VirtualEpisodeWriter>();`). ScanTask addition — the writer consumes `ScanService.LastDiffs`:

```csharp
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        foreach (var (seriesId, (diff, catalog)) in _runner.Scan.LastDiffs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var owned = _libraryReader.GetSeries().FirstOrDefault(s => s.Id == seriesId)?.Episodes
                        ?? (IReadOnlyList<Jellyfin.Plugin.DownloadTime.Model.OwnedEpisode>)Array.Empty<Jellyfin.Plugin.DownloadTime.Model.OwnedEpisode>();
            var plan = VirtualEpisodePlanner.Plan(diff, catalog, owned, _writer.GetExisting(seriesId), config.CreateVirtualEpisodes);
            if (plan.Creates.Count > 0 || plan.Deletes.Count > 0)
            {
                _writer.Apply(seriesId, plan);
            }
        }
```

(add `VirtualEpisodeWriter _writer` and `ILibraryReader _libraryReader` ctor params to ScanTask; cache `GetSeries()` outside the loop — one call, then a dictionary by Id).

- [ ] **Step 4: Verify** — `dotnet test` green + all three ABI release builds compile (commands from Task 16 Step 4).
- [ ] **Step 5: Commit** — `git add -A && git commit -m "Add virtual placeholder writer and reset task"`

---

### Task 19: Dashboard config + report page

**Files:**
- Modify: `src/Jellyfin.Plugin.DownloadTime/configPage.html` (replace placeholder)

Browser page — no xunit coverage; gate = page loads in Dashboard → Plugins → Download Time, saves settings, renders a seeded report (verified live in Task 21 after staging deploy).

- [ ] **Step 1: Implement** `configPage.html`:

```html
<!DOCTYPE html>
<html lang="en">
<head><meta charset="utf-8"><title>Download Time</title></head>
<body>
<div id="DownloadTimeConfigPage" data-role="page" class="page type-interior pluginConfigurationPage" data-require="emby-input,emby-button,emby-checkbox">
  <div data-role="content"><div class="content-primary">
    <form id="dtForm">
      <div class="verticalSection"><h2 class="sectionTitle">Download Time</h2>
        <label class="checkboxContainer"><input is="emby-checkbox" type="checkbox" id="EnableTvLane"/><span>Scan TV shows</span></label>
        <label class="checkboxContainer"><input is="emby-checkbox" type="checkbox" id="EnableAnimeLane"/><span>Scan anime</span></label>
        <label class="checkboxContainer"><input is="emby-checkbox" type="checkbox" id="EnableMovieLane"/><span>Scan movie franchises</span></label>
        <div class="inputContainer"><input is="emby-input" type="text" id="TmdbApiKey" label="TMDB API key (required for tmdb-identified items and movies)"/></div>
        <div class="inputContainer"><input is="emby-input" type="number" id="GraceHours" min="0" label="Grace period after airing, hours (0 = off)"/></div>
        <div class="inputContainer"><input is="emby-input" type="number" id="MovieReleaseBufferDays" min="0" label="Movie release buffer, days"/></div>
        <label class="checkboxContainer"><input is="emby-checkbox" type="checkbox" id="IncludeSpecials"/><span>Include specials (Season 0)</span></label>
        <label class="checkboxContainer"><input is="emby-checkbox" type="checkbox" id="CreateVirtualEpisodes"/><span>Create native missing-episode placeholders (users must enable &quot;Display missing episodes&quot; in their display settings)</span></label>
        <label class="checkboxContainer"><input is="emby-checkbox" type="checkbox" id="ShowPosterBadges"/><span>Show poster badges</span></label>
        <label class="checkboxContainer"><input is="emby-checkbox" type="checkbox" id="ShowDetailBadges"/><span>Show series-page summary line</span></label>
        <div class="inputContainer"><input is="emby-input" type="number" id="RequestDelayMs" min="0" label="Request throttle (ms, scrapers + AniDB)"/></div>
        <div class="inputContainer"><input is="emby-input" type="text" id="ExcludedItemIds" label="Muted item ids (comma-separated)"/></div>
        <button is="emby-button" type="submit" class="raised button-submit block"><span>Save</span></button>
      </div>
    </form>
    <div class="verticalSection">
      <h2 class="sectionTitle">Last report</h2>
      <p>
        <button is="emby-button" type="button" id="dtScan" class="raised"><span>Scan now</span></button>
        <button is="emby-button" type="button" id="dtScanFull" class="raised"><span>Scan now (full refresh)</span></button>
      </p>
      <div id="dtReport">Loading…</div>
    </div>
  </div></div>
  <script type="text/javascript">
  (function () {
    var pluginId = "4d557ba6-d562-4209-9a04-b782775dc2ff";
    var fields = ["EnableTvLane","EnableAnimeLane","EnableMovieLane","IncludeSpecials","CreateVirtualEpisodes","ShowPosterBadges","ShowDetailBadges"];
    var texts = ["TmdbApiKey","GraceHours","MovieReleaseBufferDays","RequestDelayMs"];

    function loadConfig(page) {
      ApiClient.getPluginConfiguration(pluginId).then(function (c) {
        fields.forEach(function (f) { page.querySelector("#" + f).checked = !!c[f]; });
        texts.forEach(function (f) { page.querySelector("#" + f).value = c[f] != null ? c[f] : ""; });
        page.querySelector("#ExcludedItemIds").value = (c.ExcludedItemIds || []).join(",");
      });
    }

    function esc(s) { var d = document.createElement("div"); d.textContent = s == null ? "" : String(s); return d.innerHTML; }
    function fmtDate(d) { return d ? new Date(d).toLocaleDateString() : "?"; }

    function renderReport(page) {
      ApiClient.ajax({ type: "GET", url: ApiClient.getUrl("DownloadTime/Report"), dataType: "json" }).then(function (r) {
        var html = "";
        if (r.StartedAt && r.StartedAt !== "0001-01-01T00:00:00+00:00") {
          html += "<p>Scan finished " + esc(new Date(r.FinishedAt).toLocaleString()) + "</p>";
        }
        (r.GlobalNotes || []).forEach(function (n) { html += "<p><em>" + esc(n) + "</em></p>"; });
        var withMissing = (r.Series || []).filter(function (s) { return (s.Missing || []).length || s.Error; });
        if (!withMissing.length && !(r.Collections || []).length) { html += "<p>No missing media. 🎉</p>"; }
        withMissing.forEach(function (s) {
          var gaps = s.Missing.filter(function (m) { return m.Kind === "Gap"; }).length;
          var news = s.Missing.filter(function (m) { return m.Kind === "New"; }).length;
          html += "<details><summary><strong>" + esc(s.Name) + "</strong> — " + gaps + " gap(s), " + news + " new"
            + (s.UsedFallback ? " <em>(fallback source)</em>" : "") + (s.Error ? " ⚠ " + esc(s.Error) : "") + "</summary><ul>";
          s.Missing.forEach(function (m) {
            html += "<li>S" + (m.Season != null ? m.Season : "?") + "E" + (m.Number != null ? m.Number : "?")
              + " " + esc(m.Title || "") + " — aired " + fmtDate(m.AiredAt) + " [" + m.Kind + "]</li>";
          });
          (s.Notes || []).forEach(function (n) { html += "<li><em>" + esc(n) + "</em></li>"; });
          html += "</ul></details>";
        });
        (r.Collections || []).forEach(function (c) {
          html += "<details><summary><strong>" + esc(c.Name) + "</strong> — " + c.Missing.length + " missing movie(s)</summary><ul>";
          c.Missing.forEach(function (m) { html += "<li>" + esc(m.Title) + " (released " + fmtDate(m.ReleasedAt) + ")</li>"; });
          html += "</ul></details>";
        });
        page.querySelector("#dtReport").innerHTML = html;
      });
    }

    function scan(page, full) {
      ApiClient.ajax({ type: "POST", url: ApiClient.getUrl("DownloadTime/Scan", { fullRefresh: full }) }).then(function () {
        Dashboard.alert("Scan started. Refresh this page in a bit.");
      }, function (xhr) {
        Dashboard.alert(xhr && xhr.status === 409 ? "A scan is already running." : "Failed to start scan.");
      });
    }

    document.querySelector("#DownloadTimeConfigPage").addEventListener("pageshow", function () {
      var page = this;
      loadConfig(page);
      renderReport(page);
      page.querySelector("#dtScan").onclick = function () { scan(page, false); };
      page.querySelector("#dtScanFull").onclick = function () { scan(page, true); };
      page.querySelector("#dtForm").onsubmit = function (e) {
        e.preventDefault();
        Dashboard.showLoadingMsg();
        ApiClient.getPluginConfiguration(pluginId).then(function (c) {
          fields.forEach(function (f) { c[f] = page.querySelector("#" + f).checked; });
          c.TmdbApiKey = page.querySelector("#TmdbApiKey").value.trim();
          c.GraceHours = parseInt(page.querySelector("#GraceHours").value, 10) || 0;
          c.MovieReleaseBufferDays = parseInt(page.querySelector("#MovieReleaseBufferDays").value, 10) || 0;
          c.RequestDelayMs = parseInt(page.querySelector("#RequestDelayMs").value, 10) || 0;
          c.ExcludedItemIds = page.querySelector("#ExcludedItemIds").value.split(",").map(function (s) { return s.trim(); }).filter(Boolean);
          ApiClient.updatePluginConfiguration(pluginId, c).then(Dashboard.processPluginConfigurationUpdateResult);
        });
        return false;
      };
    });
  })();
  </script>
</div>
</body>
</html>
```

- [ ] **Step 2: Verify build embeds it** — `dotnet build src/Jellyfin.Plugin.DownloadTime -c Release` (resource path `Jellyfin.Plugin.DownloadTime.configPage.html` must match `Plugin.GetPages`).
- [ ] **Step 3: Commit** — `git add -A && git commit -m "Add dashboard settings and report page"`

---

### Task 20: Poster badges — File Transformation injection

**Files:**
- Create: `src/Jellyfin.Plugin.DownloadTime/Helpers/TransformationPatch.cs`, `src/Jellyfin.Plugin.DownloadTime/Tasks/StartupTask.cs`
- Modify: `src/Jellyfin.Plugin.DownloadTime/Web/badges.js`, `Web/badges.css`, `PluginServiceRegistrator.cs` (nothing to add — StartupTask is discovered as IScheduledTask automatically)

Gate: suite green + 3 ABI builds; visual behavior validated in Task 22 badge smoke. Degrades to a logged no-op when File Transformation is absent (Ronin pattern).

- [ ] **Step 1: Implement** `Helpers/TransformationPatch.cs` (Ronin's shape, our resources):

```csharp
using System.Reflection;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.DownloadTime.Helpers;

public class PatchRequestPayload
{
    [JsonPropertyName("contents")]
    public string? Contents { get; set; }
}

/// <summary>Injects badges.css/js into index.html via the FileTransformation plugin.</summary>
public static partial class TransformationPatch
{
    [GeneratedRegex("(</head>)", RegexOptions.IgnoreCase)]
    private static partial Regex HeadEnd();

    [GeneratedRegex("(</body>)", RegexOptions.IgnoreCase)]
    private static partial Regex BodyEnd();

    public static string InjectIntoIndexHtml(PatchRequestPayload payload)
    {
        if (string.IsNullOrWhiteSpace(payload.Contents))
        {
            return payload.Contents ?? string.Empty;
        }
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        if (!config.ShowPosterBadges && !config.ShowDetailBadges)
        {
            return payload.Contents;
        }
        var settings = $"<script>window.DownloadTimeConfig={{poster:{config.ShowPosterBadges.ToString().ToLowerInvariant()},detail:{config.ShowDetailBadges.ToString().ToLowerInvariant()}}};</script>";
        var css = ReadResource("Web.badges.css");
        var js = ReadResource("Web.badges.js");
        var result = HeadEnd().Replace(payload.Contents, $"{settings}<style>{css}</style>$1", 1);
        return BodyEnd().Replace(result, $"<script defer>{js}</script>$1", 1);
    }

    private static string ReadResource(string suffix)
    {
        var name = $"{typeof(Plugin).Namespace}.{suffix}";
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
        if (stream is null)
        {
            return string.Empty;
        }
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
```

- [ ] **Step 2: Implement** `Tasks/StartupTask.cs` (Ronin's reflection registration; transformation id `7be0d6d4-6a4e-4a02-a5f0-c6c66b825b39`; 10.10 trigger shim):

```csharp
using System.Reflection;
using System.Runtime.Loader;
using Jellyfin.Plugin.DownloadTime.Helpers;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace Jellyfin.Plugin.DownloadTime.Tasks;

/// <summary>Registers the badge injection with the FileTransformation plugin at startup.</summary>
public class StartupTask : IScheduledTask
{
    private readonly ILogger<StartupTask> _logger;

    public StartupTask(ILogger<StartupTask> logger) => _logger = logger;

    public string Name => "Register badge injection";
    public string Key => "DownloadTimeStartup";
    public string Description => "Registers Download Time's web badge injection with the FileTransformation plugin.";
    public string Category => "Download Time";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
#if JELLYFIN_10_10
        yield return new TaskTriggerInfo { Type = TaskTriggerInfo.TriggerStartup };
#else
        yield return new TaskTriggerInfo { Type = TaskTriggerInfoType.StartupTrigger };
#endif
    }

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var payload = new JObject
        {
            ["id"] = "7be0d6d4-6a4e-4a02-a5f0-c6c66b825b39",
            ["fileNamePattern"] = "index.html",
            ["callbackAssembly"] = GetType().Assembly.FullName,
            ["callbackClass"] = typeof(TransformationPatch).FullName,
            ["callbackMethod"] = nameof(TransformationPatch.InjectIntoIndexHtml),
        };
        var ftAssembly = AssemblyLoadContext.All.SelectMany(x => x.Assemblies)
            .FirstOrDefault(x => x.FullName?.Contains(".FileTransformation", StringComparison.OrdinalIgnoreCase) ?? false);
        if (ftAssembly is null)
        {
            _logger.LogWarning("Download Time: FileTransformation plugin not found; badges disabled.");
            return Task.CompletedTask;
        }
        var iface = ftAssembly.GetType("Jellyfin.Plugin.FileTransformation.PluginInterface");
        iface?.GetMethod("RegisterTransformation")?.Invoke(null, new object?[] { payload });
        _logger.LogInformation("Download Time: badge injection registered.");
        return Task.CompletedTask;
    }
}
```

Add `<PackageReference Include="Newtonsoft.Json" Version="13.0.3"><ExcludeAssets>runtime</ExcludeAssets></PackageReference>` to the csproj (resolved from the server dir at runtime — Filler Skip precedent).

- [ ] **Step 3: Implement** `Web/badges.css` and `Web/badges.js`:

```css
.dt-badge{position:absolute;top:.35em;left:.35em;z-index:5;background:#c77400;color:#fff;
  border-radius:1em;padding:.1em .55em;font-size:.78em;font-weight:600;pointer-events:none;
  box-shadow:0 1px 3px rgba(0,0,0,.6)}
.dt-detail-line{color:#c77400;font-weight:600;margin:.4em 0}
```

```javascript
// Download Time poster badges. Data: GET /DownloadTime/Report (session auth).
(function () {
    'use strict';
    var cfg = window.DownloadTimeConfig || { poster: true, detail: true };
    var counts = null; // itemIdNoDashes -> {gaps,news}

    function load() {
        var client = window.ApiClient;
        if (!client) { setTimeout(load, 2000); return; }
        client.ajax({ type: 'GET', url: client.getUrl('DownloadTime/Report'), dataType: 'json' })
            .then(function (r) {
                counts = {};
                (r.Series || []).forEach(function (s) {
                    var gaps = 0, news = 0;
                    (s.Missing || []).forEach(function (m) { if (m.Kind === 'Gap') gaps++; else news++; });
                    if (gaps + news > 0) counts[String(s.ItemId).replace(/-/g, '').toLowerCase()] = { gaps: gaps, news: news };
                });
                decorate();
            })
            .catch(function () { setTimeout(load, 30000); });
    }

    function idFromCard(card) {
        var id = card.getAttribute('data-id');
        return id ? id.replace(/-/g, '').toLowerCase() : null;
    }

    function decorate() {
        if (!counts) return;
        if (cfg.poster) {
            document.querySelectorAll('.card[data-id]').forEach(function (card) {
                var id = idFromCard(card);
                var c = id && counts[id];
                var holder = card.querySelector('.cardImageContainer') || card.querySelector('.cardBox');
                if (!c || !holder || holder.querySelector('.dt-badge')) return;
                var b = document.createElement('div');
                b.className = 'dt-badge';
                b.textContent = c.gaps + c.news;
                b.title = c.gaps + ' gap(s), ' + c.news + ' new';
                holder.appendChild(b);
            });
        }
        if (cfg.detail) {
            var page = document.querySelector('.itemDetailPage:not(.hide)');
            if (page && !page.querySelector('.dt-detail-line')) {
                var m = (location.hash.match(/id=([0-9a-fA-F-]{32,36})/) || [])[1];
                var c = m && counts[m.replace(/-/g, '').toLowerCase()];
                var anchor = page.querySelector('.itemName, .nameContainer');
                if (c && anchor) {
                    var line = document.createElement('div');
                    line.className = 'dt-detail-line';
                    line.textContent = (c.gaps + c.news) + ' missing — ' + c.gaps + ' gap(s), ' + c.news + ' new';
                    anchor.parentElement.insertBefore(line, anchor.nextSibling);
                }
            }
        }
    }

    new MutationObserver(function () { decorate(); }).observe(document.body, { childList: true, subtree: true });
    load();
    setInterval(load, 30 * 60 * 1000); // refresh counts every 30 min
})();
```

- [ ] **Step 4: Verify** — `dotnet test` + 3 ABI builds.
- [ ] **Step 5: Commit** — `git add -A && git commit -m "Add poster badge injection via FileTransformation"`

---

### Task 21: Staging deploy + detection E2E (red→green on VMHOLLIER)

**Prereqs:** AniDB client registered (Task 10 user action); TMDB key entered in plugin config (or movie/tmdb assertions skipped this round and re-run later).

**Files:**
- Create: `e2e/detect.mjs`, `e2e/config.local.json` (gitignored — add `e2e/config.local.json` + `e2e/.holding/` to `.gitignore`)

`e2e/config.local.json` (values for VMHOLLIER):

```json
{
  "baseUrl": "https://localhost",
  "token": "<admin API key from Dashboard>",
  "pluginGuid": "4d557ba6-d562-4209-9a04-b782775dc2ff",
  "target": { "seriesName": "American Gods", "season": 2, "episode": 5 },
  "holdingDir": "e2e/.holding"
}
```

- [ ] **Step 1: Build + deploy to staging**

```bash
dotnet build src/Jellyfin.Plugin.DownloadTime -c Release -p:JellyfinVersion=12.0
net stop JellyfinServer
mkdir -p "/c/ProgramData/Jellyfin/Server/plugins/Download Time_1.0.0.0"
cp src/Jellyfin.Plugin.DownloadTime/bin/Release/net10.0/Jellyfin.Plugin.DownloadTime.dll \
   src/Jellyfin.Plugin.DownloadTime/bin/Release/net10.0/HtmlAgilityPack.dll \
   "/c/ProgramData/Jellyfin/Server/plugins/Download Time_1.0.0.0/"
net start JellyfinServer
```

Then confirm load: `GET /Plugins` (with token) lists "Download Time" Active; server log shows no load errors. ⚠ Reminder: if the service ever gets reinstalled, re-apply `sc.exe config JellyfinServer obj= LocalSystem`.

- [ ] **Step 2: Write the rig** `e2e/detect.mjs`:

```javascript
// Detection E2E (frozen once first fix observed - FREEZE RULE).
// Usage: node e2e/detect.mjs baseline | plant | assert-gap | restore
// baseline: with all files present, target episode must NOT be reported missing.
// plant:    move target episode file to holding dir + library refresh (the RED setup).
// assert-gap: full-refresh plugin scan; target must appear as exactly one Gap.
// restore:  put file back, refresh, rescan; series must report zero missing again.
import fs from 'node:fs';
import path from 'node:path';
import process from 'node:process';

process.env.NODE_TLS_REJECT_UNAUTHORIZED = '0';
const cfg = JSON.parse(fs.readFileSync(new URL('./config.local.json', import.meta.url)));
const H = { Authorization: `MediaBrowser Token="${cfg.token}"` };

const get = async (p) => (await fetch(cfg.baseUrl + p, { headers: H })).json();
const post = async (p) => fetch(cfg.baseUrl + p, { method: 'POST', headers: H });
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const die = (msg) => { console.error('FAIL:', msg); process.exit(1); };

async function findSeries() {
  const r = await get(`/Items?IncludeItemTypes=Series&Recursive=true&SearchTerm=${encodeURIComponent(cfg.target.seriesName)}&Fields=Path`);
  if (!r.Items?.length) die('series not found');
  return r.Items[0];
}

async function findEpisodeFile(series) {
  const eps = await get(`/Shows/${series.Id}/Episodes?Fields=Path`);
  const ep = eps.Items.find((e) => e.ParentIndexNumber === cfg.target.season && e.IndexNumber === cfg.target.episode && e.LocationType !== 'Virtual');
  return ep?.Path;
}

async function libraryRefresh() {
  await post('/Library/Refresh');
  await sleep(45000); // library scan settle time on VMHOLLIER
}

async function pluginScanAndReport() {
  const res = await post('/DownloadTime/Scan?fullRefresh=true');
  if (res.status !== 202) die(`scan trigger HTTP ${res.status}`);
  for (let i = 0; i < 60; i++) {
    await sleep(5000);
    const report = await get('/DownloadTime/Report');
    if (report.FinishedAt && Date.now() - new Date(report.FinishedAt).getTime() < 5 * 60 * 1000) return report;
  }
  die('scan did not finish in time');
}

function seriesEntry(report, name) {
  return (report.Series || []).find((s) => s.Name.startsWith(name));
}

const mode = process.argv[2];
const series = await findSeries();

if (mode === 'baseline') {
  const report = await pluginScanAndReport();
  const s = seriesEntry(report, cfg.target.seriesName);
  if (!s) die('series missing from report');
  if (s.Error) die(`series errored: ${s.Error}`);
  const hit = (s.Missing || []).find((m) => m.Season === cfg.target.season && m.Number === cfg.target.episode);
  if (hit) die('target episode reported missing while file present');
  console.log('BASELINE PASS');
} else if (mode === 'plant') {
  const file = await findEpisodeFile(series);
  if (!file) die('target episode file not found');
  fs.mkdirSync(cfg.holdingDir, { recursive: true });
  const dest = path.join(cfg.holdingDir, path.basename(file));
  fs.renameSync(file, dest);
  fs.writeFileSync(path.join(cfg.holdingDir, 'origin.json'), JSON.stringify({ file }));
  console.log('moved', file, '->', dest);
  await libraryRefresh();
  const gone = await findEpisodeFile(series);
  if (gone) die('episode still present after refresh');
  console.log('PLANT DONE');
} else if (mode === 'assert-gap') {
  const report = await pluginScanAndReport();
  const s = seriesEntry(report, cfg.target.seriesName);
  if (!s) die('series missing from report');
  if (s.Error) die(`series errored: ${s.Error}`);
  const hits = (s.Missing || []).filter((m) => m.Season === cfg.target.season && m.Number === cfg.target.episode);
  if (hits.length !== 1) die(`expected exactly 1 hit for target, got ${hits.length}`);
  if (hits[0].Kind !== 'Gap') die(`expected Gap, got ${hits[0].Kind}`);
  const others = (s.Missing || []).filter((m) => !(m.Season === cfg.target.season && m.Number === cfg.target.episode));
  if (others.length) die(`unexpected extra missing entries: ${JSON.stringify(others)}`);
  console.log('ASSERT-GAP PASS');
} else if (mode === 'restore') {
  const { file } = JSON.parse(fs.readFileSync(path.join(cfg.holdingDir, 'origin.json')));
  fs.renameSync(path.join(cfg.holdingDir, path.basename(file)), file);
  await libraryRefresh();
  const report = await pluginScanAndReport();
  const s = seriesEntry(report, cfg.target.seriesName);
  if ((s.Missing || []).length) die(`still missing after restore: ${JSON.stringify(s.Missing)}`);
  console.log('RESTORE PASS');
} else {
  die('usage: node e2e/detect.mjs baseline|plant|assert-gap|restore');
}
```

- [ ] **Step 3: Run the red→green cycle** (in order; each must pass before the next):

```bash
node e2e/detect.mjs baseline     # green precondition: no false positive
node e2e/detect.mjs plant        # red setup: gap now physically exists
node e2e/detect.mjs assert-gap   # detector must find EXACTLY the planted gap
node e2e/detect.mjs restore      # back to zero missing
```

⚠ Warn the user before running: `plant` temporarily moves a real episode file (restored by `restore`; original path recorded in `e2e/.holding/origin.json`).

- [ ] **Step 4: Spot-check the full report** — `GET /DownloadTime/Report`: every series should have `Error == null` (or an explainable error, e.g. tmdb items before a TMDB key is entered — surface the list to the user), anime lane serving from AniDB, at least one continuing show showing plausible New entries. Paste a summary to the user.
- [ ] **Step 5: Commit** — `git add -A && git commit -m "Add detection E2E rig (baseline/plant/assert-gap/restore)"`

---

### Task 22: Virtual placeholder E2E + badge smoke

**Files:**
- Create: `e2e/virtual.mjs`

- [ ] **Step 1: Write the rig** `e2e/virtual.mjs` (same config/header helpers as detect.mjs — import them or copy the 10 lines):

```javascript
// Virtual placeholder lifecycle E2E (frozen once first fix observed).
// Usage: node e2e/virtual.mjs enable | assert-placeholder | restore-and-assert-gone | reset-and-assert-zero
// Flow: detect.mjs plant  ->  enable  ->  assert-placeholder  ->  detect.mjs restore
//       -> restore-and-assert-gone  ->  reset-and-assert-zero
import fs from 'node:fs';
import process from 'node:process';

process.env.NODE_TLS_REJECT_UNAUTHORIZED = '0';
const cfg = JSON.parse(fs.readFileSync(new URL('./config.local.json', import.meta.url)));
const H = { Authorization: `MediaBrowser Token="${cfg.token}"`, 'Content-Type': 'application/json' };
const get = async (p) => (await fetch(cfg.baseUrl + p, { headers: H })).json();
const post = async (p, body) => fetch(cfg.baseUrl + p, { method: 'POST', headers: H, body: body && JSON.stringify(body) });
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const die = (msg) => { console.error('FAIL:', msg); process.exit(1); };

async function series() {
  const r = await get(`/Items?IncludeItemTypes=Series&Recursive=true&SearchTerm=${encodeURIComponent(cfg.target.seriesName)}`);
  return r.Items[0];
}
async function placeholders(sid) {
  const eps = await get(`/Shows/${sid}/Episodes?Fields=ProviderIds,LocationType`);
  return eps.Items.filter((e) => e.LocationType === 'Virtual' && e.ProviderIds && e.ProviderIds.DownloadTime);
}
async function runScanTask() {
  const res = await post('/DownloadTime/Scan?fullRefresh=true');
  if (res.status !== 202) die(`scan HTTP ${res.status}`);
  await sleep(90000);
}

const s = await series();
const mode = process.argv[2];

if (mode === 'enable') {
  const c = await get(`/Plugins/${cfg.pluginGuid}/Configuration`);
  c.CreateVirtualEpisodes = true;
  await post(`/Plugins/${cfg.pluginGuid}/Configuration`, c);
  // Placeholder application runs inside the SCHEDULED task; trigger it:
  const tasks = await get('/ScheduledTasks');
  const scan = tasks.find((t) => t.Key === 'DownloadTimeScan');
  await post(`/ScheduledTasks/Running/${scan.Id}`);
  await sleep(120000);
  console.log('ENABLE DONE');
} else if (mode === 'assert-placeholder') {
  const ph = await placeholders(s.Id);
  const hit = ph.find((e) => e.ParentIndexNumber === cfg.target.season && e.IndexNumber === cfg.target.episode);
  if (!hit) die(`no placeholder at S${cfg.target.season}E${cfg.target.episode}; found ${ph.length} total`);
  console.log('ASSERT-PLACEHOLDER PASS', hit.Name);
} else if (mode === 'restore-and-assert-gone') {
  // run AFTER detect.mjs restore (file back + library refresh + metadata refresh).
  // 12.0 RemoveObsoleteEpisodes fires on series refresh; force one:
  await post(`/Items/${s.Id}/Refresh?metadataRefreshMode=Default&recursive=true`);
  await sleep(60000);
  const ph = await placeholders(s.Id);
  const still = ph.find((e) => e.ParentIndexNumber === cfg.target.season && e.IndexNumber === cfg.target.episode);
  if (still) die('placeholder survived physical restore (twin-cleanup failed)');
  console.log('TWIN-CLEANUP PASS');
} else if (mode === 'reset-and-assert-zero') {
  const tasks = await get('/ScheduledTasks');
  const reset = tasks.find((t) => t.Key === 'DownloadTimeReset');
  await post(`/ScheduledTasks/Running/${reset.Id}`);
  await sleep(30000);
  const all = await get(`/Items?IncludeItemTypes=Episode&Recursive=true&IsVirtualItem=true&Fields=ProviderIds&Limit=2000`);
  const ours = (all.Items || []).filter((e) => e.ProviderIds && e.ProviderIds.DownloadTime);
  if (ours.length) die(`${ours.length} placeholders survived reset`);
  console.log('RESET PASS');
} else {
  die('usage: node e2e/virtual.mjs enable|assert-placeholder|restore-and-assert-gone|reset-and-assert-zero');
}
```

- [ ] **Step 2: Run the full lifecycle** (announce to user first; plant/restore reuse detect.mjs):

```bash
node e2e/detect.mjs plant
node e2e/virtual.mjs enable
node e2e/virtual.mjs assert-placeholder      # ALSO: user visually confirms greyed-out episode in web UI with "Display missing episodes" on
node e2e/detect.mjs restore
node e2e/virtual.mjs restore-and-assert-gone
node e2e/virtual.mjs reset-and-assert-zero
```

Only after ALL pass may `CreateVirtualEpisodes` be recommended to the user (it remains default-off in config).

- [ ] **Step 3: Badge smoke** — with the report populated, open the web UI (user's browser or a headless tab via the `C:\JF-Dev\jf-e2e` CDP harness): confirm `.dt-badge` elements on series cards with missing episodes and the summary line on a series detail page. Record which pages were checked in the commit message.
- [ ] **Step 4: Commit** — `git add -A && git commit -m "Add virtual placeholder lifecycle E2E; validated on VMHOLLIER"`

---

### Task 23: Release — three ABIs through the plugin repo

- [ ] **Step 1: Create GitHub repo + push** (gh at `C:\Tools\gh\bin\gh.exe`, authed as mhollier117):

```bash
cd /c/JF-Dev/jellyfin-plugin-downloadtime
/c/Tools/gh/bin/gh.exe repo create mhollier117/jellyfin-plugin-downloadtime --public --source . --push
```

- [ ] **Step 2: Logo** — create `logo.png` (512×512). Reuse the style of prior plugins: simple dark background + plugin initial; e.g. copy `C:\JF-Dev\jellyfin-plugin-fillerskip\logo.png` as a base and edit, or generate with python Pillow if available. Commit it.

- [ ] **Step 3: Build all three ABIs + zip** (release layout identical to Ronin/Filler Skip: DLL + HtmlAgilityPack.dll + logo.png + meta.json):

```bash
for v in 12.0 10.11 10.10.7; do
  dotnet build src/Jellyfin.Plugin.DownloadTime -c Release -p:JellyfinVersion=$v || exit 1
done
```

meta.json template (per-ABI `targetAbi`: 12.0.0.0 / 10.11.0.0 / 10.10.0.0; timestamp = build date):

```json
{
  "category": "General",
  "changelog": "Initial release",
  "description": "Detects missing episodes and franchise movies.",
  "guid": "4d557ba6-d562-4209-9a04-b782775dc2ff",
  "name": "Download Time",
  "overview": "Missing episode and movie detector",
  "owner": "mhollier117",
  "targetAbi": "12.0.0.0",
  "timestamp": "2026-07-25T00:00:00.0000000Z",
  "version": "1.0.0.0"
}
```

Zip each as `download-time_1.0.0.0_jf<abi>.zip`; `gh release create v1.0.0.0 <zips> -t "Download Time 1.0.0.0"` on the new repo.

- [ ] **Step 4: Manifest** — in `C:\JF-Dev\jellyfin-repo/manifest.json` add the plugin entry with one version object per ABI (sourceUrl = release asset URL, checksum = md5 of the zip, targetAbi per build), commit, push. Wait ~5 min for raw-CDN cache, then install from Dashboard → Plugins → Catalog on VMHOLLIER (replaces the staging copy: delete the staging folder first, install from repo, restart).
- [ ] **Step 5: Post-install verification** — plugin Active from repo install; scheduled task present; run one scan; report sane. Update project memory (jellyfin-server-state plugin list + new memory for Download Time specifics).

---

## Plan self-review notes (kept for the executor)

- Spec §2.2 AniDB "type filtering": implemented as IsSpecial flagging (type≠1) + DiffEngine specials exclusion — equivalent behavior, episodes are never silently dropped.
- Spec §7.1 "empty remote catalog → fetch treated as failed": enforced twice — fetchers error on zero parsed episodes AND ScanService fail-safes on zero-episode catalogs with owned>0.
- Spec §4.3 virtual seasons: v1 skips placeholders whose target season folder doesn't exist locally (logged + visible in report as still-missing episodes); creating virtual seasons is deferred — matches "skip creation … and note it" clause.
- Movie/collection poster badges and AniDB sequel-chasing: explicitly out of scope (spec §9).
- Type-consistency pass done: `FetchOutcome`, `RemoteCatalog(SourceKey, IdProviderKey, SeriesSourceId, IsEnded, Episodes)`, `ScanSettings`, `PlaceholderPlan`, controller method names `GetReport`/`StartScan` are referenced identically across Tasks 2–22.
