using Execution.Domain.Interpreter;

namespace UnitTests.Interpreter;

public class PromiseResolverTests
{
    private static BranchOutcome Ok(object? result = null)
    {
        return new BranchOutcome(true, result);
    }

    private static readonly BranchOutcome Failed = new(false, null);

    // ---- all -----------------------------------------------------------

    [Fact]
    public void All_waits_for_every_branch_then_returns_them_keyed_by_name()
    {
        var resolver = new PromiseResolver(PromiseMode.All, 0, 2);

        Assert.IsType<PromiseProgress.Pending>(resolver.Start());
        Assert.IsType<PromiseProgress.Pending>(resolver.Observe("pricing", Ok(10)));

        var resolved = Assert.IsType<PromiseProgress.Resolved>(resolver.Observe("shipping", Ok(20)));

        Assert.Equal(2, resolved.Results.Count);
        Assert.Equal(10, resolved.Results["pricing"]);
        Assert.Equal(20, resolved.Results["shipping"]);
    }

    [Fact]
    public void All_gives_up_on_the_first_branch_that_fails()
    {
        var resolver = new PromiseResolver(PromiseMode.All, 0, 3);

        Assert.IsType<PromiseProgress.Pending>(resolver.Observe("pricing", Ok(10)));

        var failure = Assert.IsType<PromiseProgress.BranchFailed>(resolver.Observe("shipping", Failed));

        // The branch's own error is what should surface, not a message invented by the policy.
        Assert.Equal("shipping", failure.BranchName);
    }

    // ---- any -----------------------------------------------------------

    [Fact]
    public void Any_resolves_on_the_first_success_alone()
    {
        var resolver = new PromiseResolver(PromiseMode.Any, 0, 3);

        Assert.IsType<PromiseProgress.Pending>(resolver.Observe("slow", Failed));

        var resolved = Assert.IsType<PromiseProgress.Resolved>(resolver.Observe("fast", Ok("winner")));

        Assert.Equal("winner", Assert.Single(resolved.Results).Value);
        Assert.Equal("fast", Assert.Single(resolved.Results).Key);
    }

    [Fact]
    public void Any_fails_only_once_every_branch_has_failed()
    {
        var resolver = new PromiseResolver(PromiseMode.Any, 0, 2);

        Assert.IsType<PromiseProgress.Pending>(resolver.Observe("a", Failed));

        var failed = Assert.IsType<PromiseProgress.Failed>(resolver.Observe("b", Failed));
        Assert.Equal("All branches failed (mode: any)", failed.Reason);
    }

    // ---- race ----------------------------------------------------------

    [Fact]
    public void Race_takes_whichever_branch_settles_first()
    {
        var resolver = new PromiseResolver(PromiseMode.Race, 0, 3);

        var resolved = Assert.IsType<PromiseProgress.Resolved>(resolver.Observe("fast", Ok("first")));

        Assert.Equal("fast", Assert.Single(resolved.Results).Key);
    }

    [Fact]
    public void Race_propagates_a_failure_that_settles_first()
    {
        var resolver = new PromiseResolver(PromiseMode.Race, 0, 3);

        var failure = Assert.IsType<PromiseProgress.BranchFailed>(resolver.Observe("fast", Failed));

        Assert.Equal("fast", failure.BranchName);
    }

    // ---- count ---------------------------------------------------------

    [Fact]
    public void Count_resolves_as_soon_as_enough_branches_succeed()
    {
        var resolver = new PromiseResolver(PromiseMode.Count, 2, 4);

        Assert.IsType<PromiseProgress.Pending>(resolver.Observe("a", Ok(1)));

        var resolved = Assert.IsType<PromiseProgress.Resolved>(resolver.Observe("b", Ok(2)));

        Assert.Equal(2, resolved.Results.Count);
        Assert.DoesNotContain("c", resolved.Results.Keys);
    }

    [Fact]
    public void Count_tolerates_failures_while_the_target_is_still_reachable()
    {
        var resolver = new PromiseResolver(PromiseMode.Count, 2, 4);

        Assert.IsType<PromiseProgress.Pending>(resolver.Observe("a", Failed));
        Assert.IsType<PromiseProgress.Pending>(resolver.Observe("b", Ok(1)));
        Assert.IsType<PromiseProgress.Resolved>(resolver.Observe("c", Ok(2)));
    }

    [Fact]
    public void Count_fails_as_soon_as_the_target_becomes_unreachable()
    {
        var resolver = new PromiseResolver(PromiseMode.Count, 3, 4);

        Assert.IsType<PromiseProgress.Pending>(resolver.Observe("a", Failed));

        // Two failures out of four leaves two, which cannot make three — no point waiting.
        var failed = Assert.IsType<PromiseProgress.Failed>(resolver.Observe("b", Failed));
        Assert.Equal("Cannot reach required count 3 (mode: count)", failed.Reason);
    }

    [Fact]
    public void Count_resolves_on_the_last_branch_when_every_one_is_needed()
    {
        var resolver = new PromiseResolver(PromiseMode.Count, 3, 3);

        Assert.IsType<PromiseProgress.Pending>(resolver.Observe("a", Ok(1)));
        Assert.IsType<PromiseProgress.Pending>(resolver.Observe("b", Ok(2)));
        Assert.IsType<PromiseProgress.Resolved>(resolver.Observe("c", Ok(3)));
    }

    // ---- degenerate ----------------------------------------------------

    [Fact]
    public void All_over_no_branches_resolves_empty()
    {
        var resolved = Assert.IsType<PromiseProgress.Resolved>(
            new PromiseResolver(PromiseMode.All, 0, 0).Start());

        Assert.Empty(resolved.Results);
    }

    [Fact]
    public void Any_over_no_branches_has_nothing_that_can_succeed()
    {
        var failed = Assert.IsType<PromiseProgress.Failed>(
            new PromiseResolver(PromiseMode.Any, 0, 0).Start());

        Assert.Equal("All branches failed (mode: any)", failed.Reason);
    }

    [Fact]
    public void Race_over_no_branches_has_nothing_to_settle()
    {
        Assert.IsType<PromiseProgress.Failed>(new PromiseResolver(PromiseMode.Race, 0, 0).Start());
    }

    [Fact]
    public void Count_of_zero_is_already_satisfied()
    {
        Assert.IsType<PromiseProgress.Resolved>(new PromiseResolver(PromiseMode.Count, 0, 3).Start());
    }
}
