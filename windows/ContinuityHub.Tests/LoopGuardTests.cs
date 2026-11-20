using ContinuityHub.Core;
using Xunit;

namespace ContinuityHub.Tests;

public class LoopGuardTests
{
    [Fact]
    public void Drops_own_origin_duplicates_and_stale()
    {
        var g = new LoopGuard("hub");
        Assert.False(g.AcceptIncoming("a", "hub", 100));          // our own echo
        Assert.True(g.AcceptIncoming("a", "phone", 100));
        Assert.False(g.AcceptIncoming("a", "phone", 100));        // duplicate id
        Assert.False(g.AcceptIncoming("b", "phone", 50));         // older than newest from same origin
        Assert.True(g.AcceptIncoming("c", "phone", 150));
        Assert.True(g.AcceptIncoming("d", "tablet", 1));          // other origin: independent clock
    }

    [Fact]
    public void Applied_remote_content_is_not_rebroadcast_but_later_local_copy_is()
    {
        var g = new LoopGuard("hub");
        var a = LoopGuard.Hash("A"); var b = LoopGuard.Hash("B");

        g.MarkAppliedRemote(a);
        Assert.False(g.ShouldBroadcastLocal(a)); // OS echo of what we just wrote
        Assert.True(g.ShouldBroadcastLocal(b));  // user copies B
        Assert.False(g.ShouldBroadcastLocal(b)); // same content reported twice
        Assert.True(g.ShouldBroadcastLocal(a));  // user copies A again on purpose
    }
}
