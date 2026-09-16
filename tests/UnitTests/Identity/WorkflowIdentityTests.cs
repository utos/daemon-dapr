using System.Text.Json;
using Common.Workflow;

namespace UnitTests.Identity;

public class WorkflowIdentityTests
{
    [Theory]
    [InlineData("send-email:1.0.0")]
    [InlineData("acme/send-email:1.0.0")]
    [InlineData("registry.example.com/acme/send-email:1.0.0")]
    [InlineData("send-email:1.0.0-rc.1")]
    public void Round_trips_through_its_canonical_string(string identity)
    {
        Assert.True(WorkflowIdentity.TryParse(identity, out var parsed));
        Assert.Equal(identity, parsed.ToString());
        Assert.Equal(parsed, WorkflowIdentity.Parse(identity));
    }

    [Fact]
    public void Parses_each_segment_into_its_own_part()
    {
        var identity = WorkflowIdentity.Parse("registry.example.com/acme/send-email:1.2.3");

        Assert.Equal("registry.example.com", identity.Registry);
        Assert.Equal("acme", identity.Namespace);
        Assert.Equal("send-email", identity.Name);
        Assert.Equal(SemanticVersion.Parse("1.2.3"), identity.Version);
    }

    [Fact]
    public void A_two_segment_path_is_namespace_and_name_not_registry_and_name()
    {
        var identity = WorkflowIdentity.Parse("acme/send-email:1.0.0");

        Assert.Equal("acme", identity.Namespace);
        Assert.Null(identity.Registry);
    }

    /// <summary>
    /// The version separator is found from the right, so a registry carrying a port does not
    /// get split at its own colon.
    /// </summary>
    [Fact]
    public void A_registry_port_does_not_confuse_the_version_separator()
    {
        var identity = WorkflowIdentity.Parse("localhost:5000/acme/send-email:1.0.0");

        Assert.Equal("localhost:5000", identity.Registry);
        Assert.Equal("send-email", identity.Name);
        Assert.Equal(SemanticVersion.Parse("1.0.0"), identity.Version);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("send-email")]
    [InlineData("send-email:")]
    [InlineData("send-email:not-a-version")]
    [InlineData("too/many/segments/here:1.0.0")]
    [InlineData("acme//send-email:1.0.0")]
    [InlineData("localhost:5000/send-email")]
    public void Rejects_malformed_identities(string? identity)
    {
        Assert.False(WorkflowIdentity.TryParse(identity, out _));
    }

    /// <summary>
    /// An absent optional segment and an empty one must produce the same identity, or the
    /// definition store could hold two entries for one workflow.
    /// </summary>
    [Fact]
    public void Empty_optional_segments_normalise_to_absent()
    {
        var fromEmpty = WorkflowIdentity.From(
            new WorkflowMetadata("send-email", "1.0.0", Namespace: "", Registry: ""));
        var fromNull = WorkflowIdentity.From(new WorkflowMetadata("send-email", "1.0.0"));

        Assert.Null(fromEmpty.Namespace);
        Assert.Null(fromEmpty.Registry);
        Assert.Equal(fromNull, fromEmpty);
        Assert.Equal(fromNull.GetHashCode(), fromEmpty.GetHashCode());
    }

    [Fact]
    public void Works_as_a_dictionary_key()
    {
        var store = new Dictionary<WorkflowIdentity, string>
        {
            [WorkflowIdentity.Parse("acme/send-email:1.0.0")] = "first"
        };

        store[WorkflowIdentity.Parse("acme/send-email:1.0.0")] = "second";

        Assert.Single(store);
        Assert.Equal("second", store[WorkflowIdentity.Parse("acme/send-email:1.0.0")]);
        Assert.False(store.ContainsKey(WorkflowIdentity.Parse("acme/send-email:1.0.1")));
    }

    [Fact]
    public void Name_key_drops_the_version_but_keeps_the_prefix()
    {
        Assert.Equal("registry.example.com/acme/send-email",
            WorkflowIdentity.Parse("registry.example.com/acme/send-email:1.0.0").NameKey);
        Assert.Equal("send-email", WorkflowIdentity.Parse("send-email:1.0.0").NameKey);
    }

    [Fact]
    public void From_rejects_metadata_the_validator_would_have_rejected()
    {
        Assert.False(WorkflowIdentity.TryFrom(new WorkflowMetadata("send-email", "not-a-version"), out _));
        Assert.False(WorkflowIdentity.TryFrom(new WorkflowMetadata("", "1.0.0"), out _));
        Assert.Throws<FormatException>(() =>
            WorkflowIdentity.From(new WorkflowMetadata("send-email", "1.0")));
    }

    [Fact]
    public void Create_rejects_segments_that_would_not_survive_the_round_trip()
    {
        var version = SemanticVersion.Parse("1.0.0");

        Assert.Throws<ArgumentException>(() => WorkflowIdentity.Create("a/b", version));
        Assert.Throws<ArgumentException>(() => WorkflowIdentity.Create("a:b", version));
        Assert.Throws<ArgumentException>(() => WorkflowIdentity.Create("name", version, @namespace: "a/b"));
        Assert.Throws<ArgumentException>(() => WorkflowIdentity.Create("", version));
    }

    [Fact]
    public void Json_round_trips_as_its_canonical_string()
    {
        var identity = WorkflowIdentity.Parse("acme/send-email:1.0.0");
        var json = JsonSerializer.Serialize(identity);

        Assert.Equal("\"acme/send-email:1.0.0\"", json);
        Assert.Equal(identity, JsonSerializer.Deserialize<WorkflowIdentity>(json));
    }
}
