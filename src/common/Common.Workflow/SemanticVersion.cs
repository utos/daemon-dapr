using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Common.Workflow.Serialization;

namespace Common.Workflow;

/// <summary>
/// A semantic version, as MAJOR.MINOR.PATCH with optional prerelease and build metadata.
/// An instance can only exist for a version that parses, so "is this a valid version" is
/// answered once, at the boundary, rather than re-derived by every caller.
///
/// Ordering follows semver precedence: numeric core first, then a prerelease ranks below its
/// release (1.0.0-rc.1 &lt; 1.0.0), comparing dot-separated identifiers with numeric ones ranking
/// below alphanumeric. Build metadata takes no part in precedence.
///
/// The converter is declared on the type, following <see cref="ValueMap"/>, so the persisted
/// form is the plain version string wherever this is serialized rather than depending on the
/// caller's <see cref="System.Text.Json.JsonSerializerOptions"/>.
/// </summary>
[JsonConverter(typeof(SemanticVersionJsonConverter))]
public sealed record SemanticVersion : IComparable<SemanticVersion>
{
    private static readonly Regex Pattern =
        new(
            @"^(\d+)\.(\d+)\.(\d+)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$",
            RegexOptions.Compiled);

    private SemanticVersion(int major, int minor, int patch, string? prerelease, string? buildMetadata)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        Prerelease = prerelease;
        BuildMetadata = buildMetadata;
    }

    public int Major { get; }

    public int Minor { get; }

    public int Patch { get; }

    /// <summary>The prerelease identifiers, without the leading '-'. Null when this is a release.</summary>
    public string? Prerelease { get; }

    /// <summary>
    /// Build metadata, without the leading '+'. Preserved so <see cref="ToString"/> round-trips
    /// the authored text, but ignored by both comparison and equality, per semver.
    /// </summary>
    public string? BuildMetadata { get; }

    public bool IsPrerelease => Prerelease is not null;

    public static SemanticVersion Parse(string version) =>
        TryParse(version, out var parsed)
            ? parsed
            : throw new FormatException($"'{version}' is not a valid semantic version.");

    public static bool TryParse([NotNullWhen(true)] string? version,
        [NotNullWhen(true)] out SemanticVersion? parsed)
    {
        parsed = null;

        if (string.IsNullOrEmpty(version))
            return false;

        var match = Pattern.Match(version);
        if (!match.Success)
            return false;

        // The pattern only guarantees the parts are digits, not that they fit in an int.
        if (!int.TryParse(match.Groups[1].Value, out var major)
            || !int.TryParse(match.Groups[2].Value, out var minor)
            || !int.TryParse(match.Groups[3].Value, out var patch))
            return false;

        parsed = new SemanticVersion(
            major,
            minor,
            patch,
            match.Groups[4].Success ? match.Groups[4].Value : null,
            match.Groups[5].Success ? match.Groups[5].Value : null);

        return true;
    }

    public override string ToString()
    {
        var core = $"{Major}.{Minor}.{Patch}";

        if (Prerelease is not null)
            core += "-" + Prerelease;

        if (BuildMetadata is not null)
            core += "+" + BuildMetadata;

        return core;
    }

    public int CompareTo(SemanticVersion? other)
    {
        if (other is null) return 1;

        var core = (Major, Minor, Patch).CompareTo((other.Major, other.Minor, other.Patch));

        return core != 0 ? core : ComparePrerelease(Prerelease, other.Prerelease);
    }

    /// <summary>
    /// Equality is precedence equality, so it agrees with <see cref="CompareTo"/>: two versions
    /// differing only in build metadata are the same version. The record-synthesised equality
    /// would have compared it, which matters because this type is part of a dictionary key.
    /// </summary>
    public bool Equals(SemanticVersion? other) =>
        other is not null
        && Major == other.Major
        && Minor == other.Minor
        && Patch == other.Patch
        && Prerelease == other.Prerelease;

    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, Prerelease);

    public static bool operator <(SemanticVersion? left, SemanticVersion? right) =>
        Comparer<SemanticVersion>.Default.Compare(left, right) < 0;

    public static bool operator >(SemanticVersion? left, SemanticVersion? right) =>
        Comparer<SemanticVersion>.Default.Compare(left, right) > 0;

    public static bool operator <=(SemanticVersion? left, SemanticVersion? right) =>
        Comparer<SemanticVersion>.Default.Compare(left, right) <= 0;

    public static bool operator >=(SemanticVersion? left, SemanticVersion? right) =>
        Comparer<SemanticVersion>.Default.Compare(left, right) >= 0;

    private static int ComparePrerelease(string? left, string? right)
    {
        // A version without a prerelease outranks one with it.
        if (left is null && right is null) return 0;
        if (left is null) return 1;
        if (right is null) return -1;

        var leftIds = left.Split('.');
        var rightIds = right.Split('.');

        for (var i = 0; i < Math.Min(leftIds.Length, rightIds.Length); i++)
        {
            var comparison = ComparePrereleaseIdentifier(leftIds[i], rightIds[i]);
            if (comparison != 0) return comparison;
        }

        // A larger set of identifiers outranks a smaller one when all preceding are equal.
        return leftIds.Length.CompareTo(rightIds.Length);
    }

    private static int ComparePrereleaseIdentifier(string left, string right)
    {
        var leftIsNumeric = int.TryParse(left, out var leftNumber);
        var rightIsNumeric = int.TryParse(right, out var rightNumber);

        // Numeric identifiers always have lower precedence than alphanumeric ones.
        if (leftIsNumeric && rightIsNumeric) return leftNumber.CompareTo(rightNumber);
        if (leftIsNumeric) return -1;
        if (rightIsNumeric) return 1;

        return string.CompareOrdinal(left, right);
    }
}
