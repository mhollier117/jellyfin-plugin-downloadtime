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
