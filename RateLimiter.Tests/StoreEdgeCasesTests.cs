using NUnit.Framework;
using RateLimiter.Core.Stores;
using RateLimiter.Options;
using Microsoft.Extensions.Options;
using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace RateLimiter.Tests;

[TestFixture]
public class StoreEdgeCasesTests
{
    [SetUp]
    public async Task Setup() => await RedisHelper.FlushRateLimiterKeysAsync();

    private static RedisRateLimitStore MakeStore(bool failOpen) =>
        new RedisRateLimitStore(Microsoft.Extensions.Options.Options.Create(new RateLimiterOptions
        {
            RedisConfiguration   = RedisHelper.BaseConn,
            DefaultAlgorithm     = Algorithm.Hybrid,
            DefaultCalls         = 5,
            DefaultPeriodSeconds = 10,
            DefaultScope         = RateLimitScope.PerIp,
            FailOpen             = failOpen,
            WarmUpInitialCapacity= 5
        }));

    [TearDown]
    public async Task Cleanup() => await RedisHelper.DeleteCurrentTestKeysAsync();
    
    // ---------- FIXED WINDOW ----------

    [Test]
    public async Task FixedWindow_ExactlyAtLimit_Allows_ButNextBlocks()
    {
        var store = MakeStore(false);
        var key = RedisHelper.BuildKey("fw:exact");
        const int limit=5, window=10;

        for (int i = 0; i < limit; i++)
            Assert.That(await store.TryIncrementAsync(key, limit, window, Algorithm.FixedWindow), Is.True, $"#{i+1}");

        Assert.That(await store.TryIncrementAsync(key, limit, window, Algorithm.FixedWindow), Is.False, "limit+1 should block");
    }

    [Test]
    public async Task FixedWindow_WindowBoundary_NewWindowAllows()
    {
        var store = MakeStore(false);
        var key = RedisHelper.BuildKey("fw:boundary");
        const int limit=3, window=5;

        for (int i = 0; i < limit; i++)
            Assert.That(await store.TryIncrementAsync(key, limit, window, Algorithm.FixedWindow), Is.True);

        Assert.That(await store.TryIncrementAsync(key, limit, window, Algorithm.FixedWindow), Is.False);

        await Task.Delay(TimeSpan.FromSeconds(window + 1));
        Assert.That(await store.TryIncrementAsync(key, limit, window, Algorithm.FixedWindow), Is.True, "new window should allow");
    }

    // ---------- SLIDING LOG ----------

    [Test]
    public async Task SlidingLog_SameSecond_UsesUniqueMembers_4thBlocks()
    {
        var store = MakeStore(false);
        var key = "rl:sl:samesecond";
        const int limit=3, window=10;

        for (int i = 0; i < limit; i++)
            Assert.That(await store.TryIncrementAsync(key, limit, window, Algorithm.SlidingLog), Is.True, $"#{i+1}");

        // no delay; still in same second for most environments
        Assert.That(await store.TryIncrementAsync(key, limit, window, Algorithm.SlidingLog), Is.False, "unique member logic should make 4th block");
    }

    [Test]
    public async Task SlidingLog_Slides_WhenOldEntryExpires()
    {
        var store = MakeStore(false);
        var key = "rl:sl:slides";
        const int limit=2, window=3;

        // 2 quick hits -> at cap
        Assert.That(await store.TryIncrementAsync(key, limit, window, Algorithm.SlidingLog), Is.True);
        await Task.Delay(TimeSpan.FromMilliseconds(300));
        Assert.That(await store.TryIncrementAsync(key, limit, window, Algorithm.SlidingLog), Is.True);

        // 3rd should block
        Assert.That(await store.TryIncrementAsync(key, limit, window, Algorithm.SlidingLog), Is.False);

        // wait slightly > window to evict the first hit
        await Task.Delay(TimeSpan.FromSeconds(window) + TimeSpan.FromMilliseconds(200));

        // now one slot should be free
        Assert.That(await store.TryIncrementAsync(key, limit, window, Algorithm.SlidingLog), Is.True);
    }

    // ---------- TOKEN BUCKET ----------

    [Test]
    public async Task TokenBucket_FractionalRefill_RoundsDown_ThenAllows()
    {
        var store = MakeStore(false);
        var key = RedisHelper.BuildKey("tb:fraction");
        const int cap=3, window=10; // refillRate=0.3 tokens/sec (floor)

        // drain
        for (int i = 0; i < cap; i++)
            Assert.That(await store.TryIncrementAsync(key, cap, window, Algorithm.TokenBucket), Is.True);

        // after 1s -> floor(1*0.3)=0 => still blocked
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.That(await store.TryIncrementAsync(key, cap, window, Algorithm.TokenBucket), Is.False);

        // after ~4s total -> floor(4*0.3)=1 => allowed
        await Task.Delay(TimeSpan.FromSeconds(3.2));
        Assert.That(await store.TryIncrementAsync(key, cap, window, Algorithm.TokenBucket), Is.True);
    }

    [Test]
    public async Task TokenBucket_Refill_DoesNotExceedCapacity()
    {
        var store = MakeStore(false);
        var key = "rl:tb:cap";
        const int cap=2, window=2; // refillRate=1 token/sec

        // consume 1 (leave 1 in bucket)
        Assert.That(await store.TryIncrementAsync(key, cap, window, Algorithm.TokenBucket), Is.True);

        // wait long enough to potentially overfill
        await Task.Delay(TimeSpan.FromSeconds(5));

        // should still allow only up to capacity without exceeding
        Assert.That(await store.TryIncrementAsync(key, cap, window, Algorithm.TokenBucket), Is.True);
        Assert.That(await store.TryIncrementAsync(key, cap, window, Algorithm.TokenBucket), Is.True);

        // next should block (cap reached again)
        Assert.That(await store.TryIncrementAsync(key, cap, window, Algorithm.TokenBucket), Is.False);
    }

    // ---------- HYBRID (TB + SL) ----------

    [Test]
    public async Task Hybrid_LogCapBlocks_EvenIfTokensRefilled()
    {
        var store = MakeStore(false);
        var key = RedisHelper.BuildKey("hy:logcap");
        const int cap=2, window=10;

        // hit cap quickly
        Assert.That(await store.TryIncrementAsync(key, cap, window, Algorithm.Hybrid), Is.True);
        Assert.That(await store.TryIncrementAsync(key, cap, window, Algorithm.Hybrid), Is.True);

        // wait to allow tokens to refill (refill >= 1/sec in your hybrid)
        await Task.Delay(TimeSpan.FromSeconds(3));

        // but sliding-log is still at count=2 within the 10s window -> should block
        Assert.That(await store.TryIncrementAsync(key, cap, window, Algorithm.Hybrid), Is.False);
    }

    [Test]
    public async Task Hybrid_Recovers_AfterWindowEviction()
    {
        var store = MakeStore(false);
        var key = RedisHelper.BuildKey("hy:recover");
        const int cap=3, window=4;

        for (int i = 0; i < cap; i++)
            Assert.That(await store.TryIncrementAsync(key, cap, window, Algorithm.Hybrid), Is.True);

        Assert.That(await store.TryIncrementAsync(key, cap, window, Algorithm.Hybrid), Is.False);

        await Task.Delay(TimeSpan.FromSeconds(window) + TimeSpan.FromMilliseconds(200));

        Assert.That(await store.TryIncrementAsync(key, cap, window, Algorithm.Hybrid), Is.True);
    }

    // ---------- WARM-UP ----------

    [Test]
    public async Task WarmUp_Expiry_ResetsOnNextUse()
    {
        var store = MakeStore(false);
        var bucket = RedisHelper.BuildKey("bucket:warm-expire");

        await store.WarmUpAsync(bucket, capacity: 2, expirySeconds: 2);

        // consume both tokens via hybrid
        Assert.That(await store.TryIncrementAsync("rl:ip:warm-expire", 2, 2, Algorithm.Hybrid), Is.True);
        Assert.That(await store.TryIncrementAsync("rl:ip:warm-expire", 2, 2, Algorithm.Hybrid), Is.True);
        Assert.That(await store.TryIncrementAsync("rl:ip:warm-expire", 2, 2, Algorithm.Hybrid), Is.False);

        // after expiry, bucket keys vanish -> next call should see fresh capacity again
        await Task.Delay(TimeSpan.FromSeconds(3));
        Assert.That(await store.TryIncrementAsync("rl:ip:warm-expire", 2, 2, Algorithm.Hybrid), Is.True);
    }

    // ---------- FAIL-OPEN/CLOSE (NOSCRIPT) ----------

    [Test]
    public async Task FailOpenFalse_Blocks_OnScriptFlush()
    {
        var store = MakeStore(failOpen: false);              // 1) create store (loads scripts)

        if (!await RedisHelper.TryScriptFlushAsync())        // 2) NOW flush -> Redis forgets SHAs
            Assert.Ignore("Admin not available; skipping NOSCRIPT scenario.");

        var key = RedisHelper.BuildKey("hy:failclose");      // 3) call after flush => NOSCRIPT
        var ok  = await store.TryIncrementAsync(key, 5, 10, Algorithm.Hybrid);

        Assert.That(ok, Is.False, "With FailOpen=false, NOSCRIPT should block.");
    }

    [Test]
    public async Task FailOpenTrue_Allows_OnScriptFlush()
    {
        var store = MakeStore(failOpen: true);

        if (!await RedisHelper.TryScriptFlushAsync())
            Assert.Ignore("Admin not available; skipping NOSCRIPT scenario.");

        var key = RedisHelper.BuildKey("hy:failopen");
        var ok  = await store.TryIncrementAsync(key, 5, 10, Algorithm.Hybrid);

        Assert.That(ok, Is.True, "With FailOpen=true, NOSCRIPT should allow.");
    }

 
}
