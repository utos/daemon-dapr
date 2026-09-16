using System.Text.Json;
using Common.Workflow;

namespace UnitTests.Identity;

public class SemanticVersionTests
{
    [Theory]
    [InlineData("1.0.0")]
    [InlineData("0.0.0")]
    [InlineData("10.20.30")]
    [InlineData("1.0.0-rc.1")]
    [InlineData("1.0.0-alpha")]
    [InlineData("1.0.0+build.5")]
    [InlineData("1.0.0-rc.1+build.5")]
    public void Accepts_valid_versions(string version)
    {
        Assert.True(SemanticVersion.TryParse(version, out var parsed));
        Assert.Equal(version, parsed.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1.0")]
    [InlineData("1")]
    [InlineData("v1.0.0")]
    [InlineData("1.0.0.0")]
    [InlineData("1.0.0-")]
    [InlineData("not-a-version")]
    // Matches the digit pattern but overflows an int, which the pattern alone cannot catch.
    [InlineData("99999999999999999999.0.0")]
    public void Rejects_invalid_versions(string? version)
    {
        Assert.False(SemanticVersion.TryParse(version, out _));
    }

    [Fact]
    public void Parse_throws_on_an_invalid_version()
    {
        Assert.Throws<FormatException>(() => SemanticVersion.Parse("1.0"));
    }

    [Theory]
    // Numeric core compares numerically, not as text — the case the integration suite pins.
    [InlineData("1.10.0", "1.9.0")]
    [InlineData("2.0.0", "1.99.99")]
    [InlineData("1.0.1", "1.0.0")]
    // A release outranks its own prerelease.
    [InlineData("1.0.0", "1.0.0-rc.1")]
    // Alphanumeric identifiers outrank numeric ones.
    [InlineData("1.0.0-alpha", "1.0.0-1")]
    [InlineData("1.0.0-rc.2", "1.0.0-rc.1")]
    // A longer identifier set wins when every preceding identifier is equal.
    [InlineData("1.0.0-rc.1.1", "1.0.0-rc.1")]
    public void Orders_by_precedence(string higher, string lower)
    {
        var left = SemanticVersion.Parse(higher);
        var right = SemanticVersion.Parse(lower);

        Assert.True(left > right);
        Assert.True(right < left);
        Assert.Equal(higher, new[] { right, left }.Max()!.ToString());
    }

    [Fact]
    public void Build_metadata_is_preserved_but_takes_no_part_in_precedence()
    {
        var withBuild = SemanticVersion.Parse("1.0.0+build.5");
        var withoutBuild = SemanticVersion.Parse("1.0.0");

        Assert.Equal("1.0.0+build.5", withBuild.ToString());
        Assert.Equal(0, withBuild.CompareTo(withoutBuild));
    }

    /// <summary>
    /// Equality has to agree with <see cref="SemanticVersion.CompareTo"/>, because this type is
    /// half of a dictionary key: two versions that compare equal must also hash equal, or the
    /// definition store would hold the same workflow twice.
    /// </summary>
    [Fact]
    public void Equality_ignores_build_metadata_like_comparison_does()
    {
        var withBuild = SemanticVersion.Parse("1.0.0+build.5");
        var withoutBuild = SemanticVersion.Parse("1.0.0");

        Assert.Equal(withoutBuild, withBuild);
        Assert.Equal(withoutBuild.GetHashCode(), withBuild.GetHashCode());
    }

    [Fact]
    public void Prerelease_is_not_equal_to_its_release()
    {
        Assert.NotEqual(SemanticVersion.Parse("1.0.0"), SemanticVersion.Parse("1.0.0-rc.1"));
    }

    [Fact]
    public void Json_is_a_plain_string_not_an_object()
    {
        Assert.Equal("\"1.2.3-rc.1\"", JsonSerializer.Serialize(SemanticVersion.Parse("1.2.3-rc.1")));
    }

    [Fact]
    public void Json_round_trips_build_metadata()
    {
        // The default encoder escapes '+' as +, so this asserts the round-trip rather than
        // the literal text — the escaped form is still a plain JSON string.
        var version = SemanticVersion.Parse("1.2.3-rc.1+build.5");
        var json = JsonSerializer.Serialize(version);

        var restored = JsonSerializer.Deserialize<SemanticVersion>(json);

        Assert.Equal(version, restored);
        Assert.Equal("1.2.3-rc.1+build.5", restored!.ToString());
    }

    [Fact]
    public void Exposes_its_parts()
    {
        var version = SemanticVersion.Parse("1.2.3-rc.1+build.5");

        Assert.Equal(1, version.Major);
        Assert.Equal(2, version.Minor);
        Assert.Equal(3, version.Patch);
        Assert.Equal("rc.1", version.Prerelease);
        Assert.Equal("build.5", version.BuildMetadata);
        Assert.True(version.IsPrerelease);
        Assert.False(SemanticVersion.Parse("1.2.3").IsPrerelease);
    }
}
