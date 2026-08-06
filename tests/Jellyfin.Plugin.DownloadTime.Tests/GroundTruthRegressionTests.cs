// Regression fixture: every season-0 item the 1.3.8.0 report still called a
// "special" (430 rows), hand-reviewed and labelled EXTRA / SPECIAL /
// UNCERTAIN (313 / 95 / 22). Source: scratchpad/specials-review.
//
// The contract this file enforces:
//  * ZERO false demotions — an item labelled SPECIAL must never be hidden.
//    A false Extra hides real content, which is the one outcome the user
//    cannot tolerate; the build fails on any regression here.
//  * A recall floor on EXTRA-labelled items, so the rule stack cannot quietly
//    rot without someone noticing.
// UNCERTAIN rows are reported but never asserted in either direction.
using System.Text.Json;
using Jellyfin.Plugin.DownloadTime.Model;
using Jellyfin.Plugin.DownloadTime.Services;
using Xunit;
using Xunit.Abstractions;

namespace Jellyfin.Plugin.DownloadTime.Tests;

public class GroundTruthRegressionTests
{
    private readonly ITestOutputHelper _out;

    public GroundTruthRegressionTests(ITestOutputHelper output) => _out = output;

    private sealed record Row(
        int idx, string Series, string Lane, int? Season, int? Number, string? Title,
        int? Runtime, string? AiredAt, string? EntryName, int? Abs, string? SrcId, string Label);

    private static List<Row> Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "specials-ground-truth.json");
        return JsonSerializer.Deserialize<List<Row>>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    private static RemoteEpisode ToEpisode(Row r)
    {
        DateTimeOffset? aired = DateTimeOffset.TryParse(r.AiredAt, out var d) ? d : null;
        return new RemoteEpisode(
            0, r.Number, r.SrcId, aired, true, r.Title,
            AbsoluteNumber: r.Abs, EntryName: r.EntryName,
            RuntimeMinutes: r.Runtime,
            SourceTypeCode: r.Lane.StartsWith("AniDB", StringComparison.Ordinal) ? "2" : null);
    }

    /// <summary>Classifies the fixture exactly the way ScanService does.</summary>
    private static Dictionary<int, ContentKind> ClassifyAll(List<Row> rows)
    {
        var options = new ClassifierOptions(ContentClassifier.DefaultExtraPatterns, 15);
        var verdicts = new Dictionary<int, ContentKind>();
        foreach (var series in rows.GroupBy(r => r.Series))
        {
            var episodes = series.ToDictionary(r => r.idx, ToEpisode);
            var batches = SeasonZeroBatches.Analyze(episodes.Values.ToList(), series.Key, Array.Empty<string>(), options);
            foreach (var (idx, episode) in episodes)
            {
                verdicts[idx] = batches.Classify(episode, options);
            }
        }
        return verdicts;
    }

    [Fact]
    public void NoSpecialLabelledItemIsEverDemoted()
    {
        var rows = Load();
        var verdicts = ClassifyAll(rows);
        var falseDemotions = rows
            .Where(r => r.Label == "SPECIAL" && verdicts[r.idx] == ContentKind.Extra)
            .ToList();
        foreach (var r in falseDemotions)
        {
            _out.WriteLine($"FALSE DEMOTION: {r.Series} | {r.Title} | runtime={r.Runtime}");
        }
        Assert.Empty(falseDemotions);
    }

    [Fact]
    public void ExtrasRecall_MeetsTheMeasuredFloor()
    {
        var rows = Load();
        var verdicts = ClassifyAll(rows);
        var extras = rows.Where(r => r.Label == "EXTRA").ToList();
        var caught = extras.Count(r => verdicts[r.idx] == ContentKind.Extra);
        _out.WriteLine($"extras caught {caught}/{extras.Count} ({100.0 * caught / extras.Count:F1}%)");
        // 170/313 as shipped in 1.4.0.0. The review's own stack measured 188,
        // the difference being library-specific phrases ("top gear top 41",
        // "the murder house", "bunkhouse") that were deliberately NOT baked in
        // as global patterns — those items are part of the residual that needs
        // real category tags rather than title guesswork. Floor sits just
        // below the shipped number: improvements are free, rot fails the build.
        Assert.True(caught >= 168, $"extras recall regressed: {caught}/{extras.Count}");
    }

    [Fact]
    public void PrecisionAndRecall_Reported()
    {
        var rows = Load();
        var verdicts = ClassifyAll(rows);
        var demoted = rows.Where(r => verdicts[r.idx] == ContentKind.Extra).ToList();
        var byLabel = demoted.GroupBy(r => r.Label).ToDictionary(g => g.Key, g => g.Count());
        var extras = rows.Count(r => r.Label == "EXTRA");
        byLabel.TryGetValue("EXTRA", out var tp);
        byLabel.TryGetValue("SPECIAL", out var fp);
        byLabel.TryGetValue("UNCERTAIN", out var unc);
        _out.WriteLine($"fires={demoted.Count} EXTRA={tp} SPECIAL={fp} UNCERTAIN={unc}");
        _out.WriteLine($"precision(vs labelled)={100.0 * tp / Math.Max(1, tp + fp):F1}%  recall={100.0 * tp / extras:F1}%");
        Assert.Equal(0, fp);
    }

    [Theory] // rows the review singled out as must-stay-Special
    [InlineData("5th Year: Bad Business Ideas")]
    [InlineData("5th Year: Power Tripping")]
    [InlineData("5th Year: Mushroom Tea")]
    [InlineData("5th Year: House Rules")]
    public void WorkaholicsFifthYear_StaysSpecial(string title)
    {
        var rows = Load();
        var verdicts = ClassifyAll(rows);
        var row = rows.Single(r => r.Series == "Workaholics" && r.Title == title);
        Assert.Equal(ContentKind.Special, verdicts[row.idx]);
    }
}
