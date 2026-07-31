using Common.Workflow;

namespace UnitTests.Identity;

/// <summary>
/// A reference is what a caller asked for, so it renders only the segments it carries. These
/// strings reach clients in "definition not found" messages, where a dangling ':' from an
/// omitted version reads as a daemon bug rather than as "you didn't name a version".
/// </summary>
public class WorkflowReferenceTests
{
    [Fact]
    public void A_name_only_reference_renders_without_a_version_separator()
    {
        Assert.Equal("send-email", new WorkflowReference("send-email").ToString());
    }

    [Theory]
    [InlineData("send-email", "1.0.0", null, null, "send-email:1.0.0")]
    [InlineData("send-email", null, "acme", null, "acme/send-email")]
    [InlineData("send-email", "1.0.0", "acme", null, "acme/send-email:1.0.0")]
    [InlineData("send-email", "1.0.0", "acme", "registry.example.com",
        "registry.example.com/acme/send-email:1.0.0")]
    public void Renders_only_the_segments_it_carries(
        string name, string? version, string? @namespace, string? registry, string expected)
    {
        var reference = new WorkflowReference(name, version, @namespace, registry);

        Assert.Equal(expected, reference.ToString());
    }

    [Fact]
    public void A_digest_pin_renders_after_the_version()
    {
        var reference = new WorkflowReference("send-email", "1.0.0", Digest: "sha256:abc");

        Assert.Equal("send-email:1.0.0@sha256:abc", reference.ToString());
    }

    [Fact]
    public void An_empty_optional_segment_renders_like_an_absent_one()
    {
        var reference = new WorkflowReference("send-email", "", "", "");

        Assert.Equal("send-email", reference.ToString());
    }
}
