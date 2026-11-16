using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace LacertMCP.Cli.Update
{
    /// <summary>
    /// Minimal SemVer value for comparing the running build against a GitHub
    /// release tag. Accepts <c>x.y.z</c> with an optional leading <c>v</c>,
    /// optional 4th numeric part (AssemblyVersion form), optional
    /// <c>-prerelease</c>, and ignores <c>+build</c> metadata (the
    /// <c>+sha7</c> stamp from Directory.Build.props).
    /// </summary>
    public sealed class ReleaseVersion : IComparable<ReleaseVersion>
    {
        private static readonly Regex Shape = new Regex(
            @"^[vV]?(\d{1,9})\.(\d{1,9})\.(\d{1,9})(?:\.\d{1,9})?(?:-([0-9A-Za-z.\-]+))?(?:\+[0-9A-Za-z.\-]*)?\z",
            RegexOptions.CultureInvariant);

        private ReleaseVersion(int major, int minor, int patch, string prerelease)
        {
            Major = major;
            Minor = minor;
            Patch = patch;
            Prerelease = prerelease;
        }

        public int Major { get; }
        public int Minor { get; }
        public int Patch { get; }

        /// <summary>Empty for a stable release.</summary>
        public string Prerelease { get; }

        public static bool TryParse(string? text, out ReleaseVersion version)
        {
            version = null!;
            if (string.IsNullOrWhiteSpace(text)) return false;
            var m = Shape.Match(text!.Trim());
            if (!m.Success) return false;
            version = new ReleaseVersion(
                int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
                int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture),
                m.Groups[4].Success ? m.Groups[4].Value : string.Empty);
            return true;
        }

        public int CompareTo(ReleaseVersion? other)
        {
            if (other is null) return 1;
            var c = Major.CompareTo(other.Major);
            if (c != 0) return c;
            c = Minor.CompareTo(other.Minor);
            if (c != 0) return c;
            c = Patch.CompareTo(other.Patch);
            if (c != 0) return c;

            // SemVer §11: a version without a prerelease outranks one with.
            var aStable = Prerelease.Length == 0;
            var bStable = other.Prerelease.Length == 0;
            if (aStable || bStable) return aStable == bStable ? 0 : (aStable ? 1 : -1);

            var a = Prerelease.Split('.');
            var b = other.Prerelease.Split('.');
            for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
            {
                var aNum = long.TryParse(a[i], NumberStyles.None, CultureInfo.InvariantCulture, out var an);
                var bNum = long.TryParse(b[i], NumberStyles.None, CultureInfo.InvariantCulture, out var bn);
                if (aNum && bNum) c = an.CompareTo(bn);
                else if (aNum) c = -1;          // numeric identifiers sort before alphanumeric
                else if (bNum) c = 1;
                else c = string.CompareOrdinal(a[i], b[i]);
                if (c != 0) return c;
            }
            return a.Length.CompareTo(b.Length);
        }

        public override string ToString()
            => Prerelease.Length == 0
                ? string.Format(CultureInfo.InvariantCulture, "{0}.{1}.{2}", Major, Minor, Patch)
                : string.Format(CultureInfo.InvariantCulture, "{0}.{1}.{2}-{3}", Major, Minor, Patch, Prerelease);
    }
}
