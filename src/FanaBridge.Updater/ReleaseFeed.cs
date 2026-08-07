#nullable enable
using System;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace FanaBridge.Updater
{
    /// <summary>
    /// Parsed GitHub release metadata used by the self-updater. A release can be
    /// reportable to the user even when it cannot be self-installed (missing zip asset
    /// or digest → notify-only with a manual download link).
    /// </summary>
    public sealed class ReleaseInfo
    {
        /// <summary>GitHub tag name, e.g. <c>v0.7.0</c>.</summary>
        public string TagName { get; }

        /// <summary>Version string with a leading <c>v</c>/<c>V</c> stripped, e.g. <c>0.7.0</c>.</summary>
        public string Version { get; }

        /// <summary>HTML URL of the release page (manual download fallback).</summary>
        public string HtmlUrl { get; }

        /// <summary>Exact zip asset name when found, otherwise null.</summary>
        public string? ZipName { get; }

        /// <summary>browser_download_url of the zip asset when found.</summary>
        public string? ZipUrl { get; }

        /// <summary>Asset size in bytes from the API, or 0 when unknown.</summary>
        public long ZipSizeBytes { get; }

        /// <summary>
        /// 64 lowercase hex characters of the asset's GitHub <c>digest</c> field,
        /// without the <c>sha256:</c> prefix; null when missing or malformed.
        /// </summary>
        public string? DigestHex { get; }

        /// <summary>True when zip URL and a valid digest are both present for self-install.</summary>
        public bool CanSelfInstall { get; }

        /// <summary>Human-readable reason when <see cref="CanSelfInstall"/> is false; null otherwise.</summary>
        public string? InstallBlockedReason { get; }

        /// <summary>Creates an immutable release snapshot.</summary>
        public ReleaseInfo(
            string tagName,
            string version,
            string htmlUrl,
            string? zipName,
            string? zipUrl,
            long zipSizeBytes,
            string? digestHex,
            bool canSelfInstall,
            string? installBlockedReason)
        {
            TagName = tagName ?? throw new ArgumentNullException(nameof(tagName));
            Version = version ?? throw new ArgumentNullException(nameof(version));
            HtmlUrl = htmlUrl ?? throw new ArgumentNullException(nameof(htmlUrl));
            ZipName = zipName;
            ZipUrl = zipUrl;
            ZipSizeBytes = zipSizeBytes;
            DigestHex = digestHex;
            CanSelfInstall = canSelfInstall;
            InstallBlockedReason = installBlockedReason;
        }
    }

    /// <summary>
    /// Parses GitHub Releases API JSON into <see cref="ReleaseInfo"/>.
    /// Note: GET /repos/{owner}/{repo}/releases/latest excludes drafts and prereleases
    /// by GitHub semantics — that is intentional for the self-updater feed.
    /// </summary>
    public static class ReleaseFeed
    {
        // GitHub asset digests are "sha256:" + 64 hex digits (immutable upload-time hash).
        private static readonly Regex DigestPattern =
            new Regex(@"^sha256:([0-9a-fA-F]{64})$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

        /// <summary>
        /// Parses a GET /repos/{owner}/{repo}/releases/latest response body.
        /// Returns null with a non-null error ONLY for structurally unusable
        /// responses (malformed JSON, missing/unparseable tag_name, missing html_url).
        /// A parseable release with a missing/ambiguous zip asset or a missing/
        /// malformed digest returns a <see cref="ReleaseInfo"/> with
        /// <see cref="ReleaseInfo.CanSelfInstall"/>=false and a human-readable
        /// <see cref="ReleaseInfo.InstallBlockedReason"/> (notify-only mode), NOT an error.
        /// </summary>
        public static ReleaseInfo? Parse(string json, out string? error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(json))
            {
                error = "Release feed response is empty.";
                return null;
            }

            JObject root;
            try
            {
                root = JObject.Parse(json);
            }
            catch (Exception ex)
            {
                error = "Release feed JSON is malformed: " + ex.Message;
                return null;
            }

            string? tagName = root.Value<string>("tag_name");
            if (string.IsNullOrWhiteSpace(tagName))
            {
                error = "Release feed is missing tag_name.";
                return null;
            }

            // Version string for the UI/asset name: strip a single leading v/V only.
            string version = tagName!;
            if (version.Length > 0 && (version[0] == 'v' || version[0] == 'V'))
                version = version.Substring(1);

            if (!UpdateVersion.TryParse(tagName, out _))
            {
                error = "Release feed tag_name is not a parseable version: " + tagName;
                return null;
            }

            string? htmlUrl = root.Value<string>("html_url");
            if (string.IsNullOrWhiteSpace(htmlUrl))
            {
                error = "Release feed is missing html_url.";
                return null;
            }

            string expectedZip = "FanaBridge-" + version + ".zip";
            string? zipName = null;
            string? zipUrl = null;
            long zipSize = 0;
            string? digestRaw = null;

            JToken? assetsToken = root["assets"];
            if (assetsToken is JArray assets)
            {
                foreach (JToken asset in assets)
                {
                    if (asset is not JObject ao)
                        continue;
                    string? name = ao.Value<string>("name");
                    // Exact asset name — GitHub enforces unique names per release.
                    if (!string.Equals(name, expectedZip, StringComparison.Ordinal))
                        continue;

                    zipName = name;
                    zipUrl = ao.Value<string>("browser_download_url");
                    zipSize = ao.Value<long?>("size") ?? 0;
                    digestRaw = ao.Value<string>("digest");
                    break;
                }
            }

            string? digestHex = null;
            string? blocked = null;

            if (zipName == null || string.IsNullOrWhiteSpace(zipUrl))
            {
                blocked = "Release asset '" + expectedZip + "' was not found; open the release page to install manually.";
            }
            else
            {
                Match m = DigestPattern.Match(digestRaw ?? string.Empty);
                if (!m.Success)
                {
                    blocked = "Release asset digest is missing or malformed; open the release page to install manually.";
                }
                else
                {
                    digestHex = m.Groups[1].Value.ToLowerInvariant();
                }
            }

            bool canInstall = blocked == null && digestHex != null && !string.IsNullOrWhiteSpace(zipUrl);
            return new ReleaseInfo(
                tagName: tagName!,
                version: version,
                htmlUrl: htmlUrl!,
                zipName: zipName,
                zipUrl: canInstall ? zipUrl : zipUrl,
                zipSizeBytes: zipSize,
                digestHex: digestHex,
                canSelfInstall: canInstall,
                installBlockedReason: canInstall ? null : blocked);
        }
    }
}
