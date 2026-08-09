#nullable enable
using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace FanaBridge.Updates
{
    /// <summary>
    /// HTTP seams for the self-updater: the two fetch delegates
    /// <see cref="Updater.UpdateService"/> needs, backed by one lazily built
    /// process-wide <see cref="HttpClient"/>.
    ///
    /// Deliberately does NOT touch <c>ServicePointManager.SecurityProtocol</c>:
    /// net48 defaults to OS-selected TLS (SystemDefault), and OR-ing in Tls12
    /// would pin the whole SimHub process to TLS 1.2. Release asset downloads
    /// redirect from api.github.com to objects.githubusercontent.com;
    /// HttpClient follows that automatically and no auth headers are in play.
    /// </summary>
    internal static class GitHubHttpClient
    {
        /// <summary>Latest published (non-draft, non-prerelease) release.</summary>
        public const string LatestReleaseUrl =
            "https://api.github.com/repos/kelchm/FanaBridge/releases/latest";

        // Well above any plausible release zip (~1 MB today); a response bigger
        // than this is wrong regardless of what the feed claimed.
        private const long MaxResponseBytes = 50L * 1024 * 1024;

        private static readonly Lazy<HttpClient> Client = new Lazy<HttpClient>(() =>
        {
            var client = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(30),
                MaxResponseContentBufferSize = MaxResponseBytes,
            };
            // GitHub's API rejects requests without a User-Agent.
            client.DefaultRequestHeaders.UserAgent.ParseAdd("FanaBridge/" + BuildIdentity.Version);
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return client;
        });

        public static async Task<string> GetStringAsync(string url, CancellationToken ct)
        {
            using (var response = await Client.Value.GetAsync(url, ct).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            }
        }

        public static async Task<byte[]> GetBytesAsync(string url, CancellationToken ct)
        {
            using (var response = await Client.Value.GetAsync(url, ct).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            }
        }
    }
}
