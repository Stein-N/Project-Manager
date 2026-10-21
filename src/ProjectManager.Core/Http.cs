// HTTP GET for the version lookups, the GitHub API and the update download.

namespace ProjectManager.Core;

public static class Http
{
    private const string UserAgent = "MC-Mod-Project-Manager";

    private static readonly HttpClient Client = CreateClient(TimeSpan.FromSeconds(60));
    private static readonly HttpClient Downloads = CreateClient(TimeSpan.FromSeconds(600));

    private static HttpClient CreateClient(TimeSpan timeout)
    {
        var handler = new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(10) };
        var client = new HttpClient(handler) { Timeout = timeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return client;
    }

    /// <param name="Link">The <c>Link</c> header (GitHub pagination).</param>
    public sealed record Response(int Status, string Body, string? Link);

    public static Response Get(string url, params (string Key, string Value)[] headers)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        foreach (var (k, v) in headers) request.Headers.TryAddWithoutValidation(k, v);
        try
        {
            using var response = Client.Send(request);
            using var reader = new StreamReader(response.Content.ReadAsStream());
            var body = reader.ReadToEnd();
            var link = response.Headers.TryGetValues("link", out var values) ? string.Join(", ", values) : null;
            return new Response((int)response.StatusCode, body, link);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
        {
            throw new PmException($"Request failed ({url}): {e.Message}", e);
        }
    }

    /// <summary>The body of a successful request; an error for anything but 200.</summary>
    public static string GetOk(string url)
    {
        var r = Get(url);
        return r.Status switch
        {
            200 => r.Body,
            404 => throw new PmException($"not found ({url})"),
            var s => throw new PmException($"HTTP {s} ({url})"),
        };
    }

    /// <summary>Downloads <paramref name="url"/> into <paramref name="file"/>.</summary>
    public static void Download(string url, string file)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = Downloads.Send(request, HttpCompletionOption.ResponseHeadersRead);
            if ((int)response.StatusCode != 200)
                throw new PmException($"Download failed: HTTP {(int)response.StatusCode}");
            using var input = response.Content.ReadAsStream();
            using var output = File.Create(file);
            input.CopyTo(output, 64 * 1024);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            throw new PmException($"Download failed ({url}): {e.Message}", e);
        }
    }
}
