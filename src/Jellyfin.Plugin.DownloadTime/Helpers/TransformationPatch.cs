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
