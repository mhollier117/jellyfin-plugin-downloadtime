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
