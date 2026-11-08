using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace TrailerEditor.Search
{
    /// <summary>Downloads a web page as text. An interface so tests don't use the network.</summary>
    public interface IHttpFetcher
    {
        Task<string> GetStringAsync(string url, CancellationToken cancellationToken);
    }

    /// <summary>Fetches pages the way YouTubeChecker does (browser-like headers, EU consent cookie for YouTube).</summary>
    public class HttpFetcher : IHttpFetcher
    {
        private readonly HttpClient _http;

        public HttpFetcher()
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                UseCookies = false
            };
            _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        }

        public async Task<string> GetStringAsync(string url, CancellationToken cancellationToken)
        {
            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            {
                request.Headers.TryAddWithoutValidation(
                    "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
                request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
                if (url.IndexOf("youtube.com", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    // Skip the EU cookie consent page so we get the real page.
                    request.Headers.TryAddWithoutValidation("Cookie", "SOCS=CAI; CONSENT=YES+1");
                }

                using (var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        // The URL is left out on purpose: it may contain an API key.
                        throw new HttpRequestException("HTTP " + (int)response.StatusCode);
                    }

                    return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                }
            }
        }
    }
}
