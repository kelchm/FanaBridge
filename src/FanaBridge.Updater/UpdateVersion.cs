#nullable enable
using System;

namespace FanaBridge.Updater
{
    /// <summary>
    /// Product version used by the self-updater: a numeric <see cref="Version"/> plus an
    /// optional prerelease suffix (e.g. "preview"). Release tags may carry a leading
    /// <c>v</c>/<c>V</c>; comparison treats a no-suffix release as newer than any suffix
    /// with the same numeric part so CI previews never outrank a matching release.
    /// </summary>
    public readonly struct UpdateVersion : IComparable<UpdateVersion>, IEquatable<UpdateVersion>
    {
        /// <summary>Numeric components; never null for a successfully parsed value.</summary>
        public Version Numeric { get; }

        /// <summary>Prerelease label after the first <c>-</c>, or null when absent.</summary>
        public string? Suffix { get; }

        private UpdateVersion(Version numeric, string? suffix)
        {
            Numeric = numeric;
            Suffix = suffix;
        }

        /// <summary>
        /// Parses <paramref name="text"/> into an <see cref="UpdateVersion"/>. Accepts an
        /// optional leading <c>v</c>/<c>V</c>, requires at least Major.Minor, and splits the
        /// first <c>-</c> into numeric + suffix. Returns false for null/empty/garbage,
        /// single-component versions, or negative components.
        /// </summary>
        public static bool TryParse(string? text, out UpdateVersion version)
        {
            version = default;
            if (string.IsNullOrWhiteSpace(text))
                return false;

            string s = text!.Trim();
            if (s.Length > 0 && (s[0] == 'v' || s[0] == 'V'))
                s = s.Substring(1);
            if (s.Length == 0)
                return false;

            string numericPart;
            string? suffix;
            int dash = s.IndexOf('-');
            if (dash < 0)
            {
                numericPart = s;
                suffix = null;
            }
            else
            {
                numericPart = s.Substring(0, dash);
                suffix = s.Substring(dash + 1);
                // Empty suffix after a trailing dash is not a useful version label.
                if (suffix.Length == 0)
                    return false;
            }

            if (string.IsNullOrEmpty(numericPart))
                return false;

            // System.Version accepts a bare major ("1"); we require ≥2 components so
            // product versions stay Major.Minor[.Build[.Revision]].
            if (!Version.TryParse(numericPart, out Version? parsed) || parsed == null)
                return false;
            if (parsed.Minor < 0)
                return false;

            version = new UpdateVersion(parsed, suffix);
            return true;
        }

        /// <summary>
        /// Orders by numeric components first (missing Build/Revision treated as 0 so
        /// <c>1.2 == 1.2.0</c>); when equal, no-suffix beats any suffix; two suffixes
        /// compare ordinal-ignore-case.
        /// </summary>
        public int CompareTo(UpdateVersion other)
        {
            int n = Normalize(Numeric).CompareTo(Normalize(other.Numeric));
            if (n != 0)
                return n;

            bool aHas = Suffix != null;
            bool bHas = other.Suffix != null;
            if (!aHas && !bHas)
                return 0;
            // Release (no suffix) is newer than any prerelease of the same number.
            if (!aHas)
                return 1;
            if (!bHas)
                return -1;
            return string.Compare(Suffix, other.Suffix, StringComparison.OrdinalIgnoreCase);
        }

        /// <inheritdoc />
        public bool Equals(UpdateVersion other) => CompareTo(other) == 0;

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is UpdateVersion other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            Version n = Normalize(Numeric);
            int h = n.GetHashCode();
            if (Suffix != null)
                h = (h * 397) ^ StringComparer.OrdinalIgnoreCase.GetHashCode(Suffix);
            return h;
        }

        /// <summary>Formats as <c>0.6.0</c> or <c>0.6.0-preview</c>.</summary>
        public override string ToString()
        {
            Version n = Numeric ?? new Version(0, 0);
            return Suffix == null ? n.ToString() : n + "-" + Suffix;
        }

        /// <summary>
        /// Missing Version components are -1; treat them as 0 for equality/order.
        /// A null input (default-constructed struct — <c>Numeric</c> is a reference
        /// type) normalizes to 0.0.0.0 so comparisons never throw.
        /// </summary>
        private static Version Normalize(Version? v)
        {
            if (v == null)
                return new Version(0, 0, 0, 0);
            int build = v.Build < 0 ? 0 : v.Build;
            int rev = v.Revision < 0 ? 0 : v.Revision;
            return new Version(v.Major, v.Minor, build, rev);
        }
    }
}
