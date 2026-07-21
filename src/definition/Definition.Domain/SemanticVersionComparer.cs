namespace Definition.Domain;

/// <summary>
/// Orders semantic versions so "latest loaded" picks the highest one. Compares
/// MAJOR.MINOR.PATCH numerically, then ranks a prerelease below its release
/// (1.0.0-rc.1 &lt; 1.0.0). Build metadata is ignored, per semver. Anything unparseable
/// sorts below every valid version so a malformed entry can never win.
/// </summary>
public sealed class SemanticVersionComparer : IComparer<string>
{
    public static readonly SemanticVersionComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        var left = Parse(x);
        var right = Parse(y);

        if (left is null && right is null) return string.CompareOrdinal(x, y);
        if (left is null) return -1;
        if (right is null) return 1;

        var core = left.Value.Core.CompareTo(right.Value.Core);
        if (core != 0) return core;

        return ComparePrerelease(left.Value.Prerelease, right.Value.Prerelease);
    }

    private static ((int Major, int Minor, int Patch) Core, string? Prerelease)? Parse(string? version)
    {
        if (string.IsNullOrEmpty(version)) return null;

        // Strip build metadata — it takes no part in precedence.
        var core = version.Split('+', 2)[0];

        var dash = core.IndexOf('-');
        var prerelease = dash >= 0 ? core[(dash + 1)..] : null;
        if (dash >= 0) core = core[..dash];

        var parts = core.Split('.');
        if (parts.Length != 3) return null;

        if (!int.TryParse(parts[0], out var major)
            || !int.TryParse(parts[1], out var minor)
            || !int.TryParse(parts[2], out var patch))
            return null;

        return ((major, minor, patch), prerelease);
    }

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
