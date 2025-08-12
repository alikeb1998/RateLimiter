using Microsoft.Extensions.Options;
using NUnit.Framework;
using RateLimiter.Core.Stores;
using RateLimiter.Options;

namespace RateLimiter.Tests;

[TestFixture]
public class StoreScenariosTests
{
    // Ensure Redis is clean before each test
    [SetUp]
    public async Task Setup() => await RedisHelper.FlushRateLimiterKeysAsync();

    // Helper: create store with desired FailOpen behavior
    private static RateLimiter.Core.Stores.RedisRateLimitStore MakeStore(bool failOpen)
    {
        var opts = Microsoft.Extensions.Options.Options.Create(new RateLimiter.Options.RateLimiterOptions
        {
            RedisConfiguration   = RateLimiter.Tests.RedisHelper.BaseConn,
            DefaultAlgorithm     = RateLimiter.Options.Algorithm.Hybrid,
            DefaultCalls         = 5,
            DefaultPeriodSeconds = 10,
            DefaultScope         = RateLimiter.Options.RateLimitScope.PerIp,
            FailOpen             = failOpen,
            WarmUpInitialCapacity= 5
        });

        return new RateLimiter.Core.Stores.RedisRateLimitStore(opts);
    }


    // Sanity: verify the REAL lua scripts are present in test bin
    [OneTimeSetUp]
    public void VerifyLuaExists()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "LuaScripts");
        var required = new[]
        {
            "rate_limit.lua",        // FixedWindow
            "sliding_log_pure.lua",  // SlidingLog (pure)
            "token_bucket.lua",      // TokenBucket
            "sliding_log.lua",       // Hybrid (your TB+log)
            "warm_up.lua"
        };
        foreach (var f in required)
        {
            var path = Path.Combine(dir, f);
            Assert.That(File.Exists(path), Is.True,
                $"Missing Lua script '{f}' in {dir}. Make sure tests .csproj copies LuaScripts/* from the core project.");
        }
    }

    // ---------------- FIXED WINDOW ----------------

    [Test]
    public async Task FixedWindow_PerKey_AllowsUpToLimit_ThenBlocks_ThenRecovers()
    {
        // per-IP simulated by unique key
        var store = MakeStore(failOpen: false);
        var key = "rl:ip:1.1.1.1";

        // allow first 5 within 10s
        for (int i = 0; i < 5; i++)
            Assert.That(await store.TryIncrementAsync(key, 5, 10, Algorithm.FixedWindow), Is.True, $"req {i+1}");

        // 6th blocked
        Assert.That(await store.TryIncrementAsync(key, 5, 10, Algorithm.FixedWindow), Is.False);

        // after window expires, allow again
        await Task.Delay(TimeSpan.FromSeconds(11));
        Assert.That(await store.TryIncrementAsync(key, 5, 10, Algorithm.FixedWindow), Is.True);
    }

    [Test]
    public async Task FixedWindow_Global_SharedCounter()
    {
        // global simulated by one shared key
        var store = MakeStore(false);
        var key = "rl:global";

        for (int i = 0; i < 20; i++)
            Assert.That(await store.TryIncrementAsync(key, 20, 10, Algorithm.FixedWindow), Is.True, $"global {i+1}");

        Assert.That(await store.TryIncrementAsync(key, 20, 10, Algorithm.FixedWindow), Is.False, "21st should block");
    }

    // ---------------- SLIDING LOG ----------------

    [Test]
    public async Task SlidingLog_PerKey_AllowsUpToLimit_ThenBlocks_ThenRecovers()
    {
        var store = MakeStore(false);
        var key = "rl:ip:2.2.2.2";

        for (int i = 0; i < 5; i++)
            Assert.That(await store.TryIncrementAsync(key, 5, 10, Algorithm.SlidingLog), Is.True);

        Assert.That(await store.TryIncrementAsync(key, 5, 10, Algorithm.SlidingLog), Is.False);

        await Task.Delay(TimeSpan.FromSeconds(11));
        Assert.That(await store.TryIncrementAsync(key, 5, 10, Algorithm.SlidingLog), Is.True);
    }

    [Test]
    public async Task SlidingLog_PerUser_IsolatedByKey()
    {
        // users A/B simulated by different keys
        var store = MakeStore(false);
        var userA = "rl:user:A";
        var userB = "rl:user:B";

        for (int i = 0; i < 3; i++)
            Assert.That(await store.TryIncrementAsync(userA, 3, 10, Algorithm.SlidingLog), Is.True, $"A {i+1}");

        Assert.That(await store.TryIncrementAsync(userA, 3, 10, Algorithm.SlidingLog), Is.False, "A exceeds");

        Assert.That(await store.TryIncrementAsync(userB, 3, 10, Algorithm.SlidingLog), Is.True, "B unaffected");
    }

    // ---------------- TOKEN BUCKET ----------------

    [Test]
    public async Task TokenBucket_BurstThenRefill()
    {
        var store = MakeStore(false);
        var key = "rl:ip:3.3.3.3";

        // burst: 5 immediate allowed
        for (int i = 0; i < 5; i++)
            Assert.That(await store.TryIncrementAsync(key, 5, 10, Algorithm.TokenBucket), Is.True, $"burst {i+1}");

        // then block when empty
        Assert.That(await store.TryIncrementAsync(key, 5, 10, Algorithm.TokenBucket), Is.False);

        // after ~period, should refill
        await Task.Delay(TimeSpan.FromSeconds(10));
        Assert.That(await store.TryIncrementAsync(key, 5, 10, Algorithm.TokenBucket), Is.True);
    }

    // ---------------- HYBRID ----------------

    [Test]
    public async Task Hybrid_AllowsUpToLimit_ThenBlocks_ThenRecovers()
    {
        var store = MakeStore(false);
        var key = "rl:ip:4.4.4.4";

        for (int i = 0; i < 5; i++)
            Assert.That(await store.TryIncrementAsync(key, 5, 10, Algorithm.Hybrid), Is.True);

        Assert.That(await store.TryIncrementAsync(key, 5, 10, Algorithm.Hybrid), Is.False);

        await Task.Delay(TimeSpan.FromSeconds(11));
        Assert.That(await store.TryIncrementAsync(key, 5, 10, Algorithm.Hybrid), Is.True);
    }

    [Test]
    public async Task WarmUp_SeedsCapacity()
    {
        var store = MakeStore(false);
        // prefill a bucket and ensure subsequent call passes
        await store.WarmUpAsync("rl:bucket:testwarm", capacity: 10, expirySeconds: 10);
        Assert.That(await store.TryIncrementAsync("rl:ip:warmcase", 5, 10, Algorithm.Hybrid), Is.True);
    }

    // ---------------- FAIL-OPEN / FAIL-CLOSE ----------------

    [Test]
    public async Task FailOpen_True_AllowsOnScriptError()
    {
        var store = MakeStore(true);
        // wipe script cache to force NOSCRIPT when store calls EVALSHA
        await RedisHelper.TryScriptFlushAsync();

        var ok = await store.TryIncrementAsync("rl:ip:5.5.5.5", 5, 10, Algorithm.Hybrid);
        Assert.That(ok, Is.True, "with FailOpen=true store should allow on Redis/Lua error");
    }

    [Test]
    public async Task FailOpen_False_BlocksOnScriptError()
    {
        var store = MakeStore(false);
        await RedisHelper.TryScriptFlushAsync();

        var ok = await store.TryIncrementAsync("rl:ip:6.6.6.6", 5, 10, Algorithm.Hybrid);
        Assert.That(ok, Is.False, "with FailOpen=false store should block on Redis/Lua error");
    }

    // ---------------- CONSTRUCTOR VALIDATION ----------------

    [Test]
    public void Throws_WhenRedisConfigurationMissing()
    {
        var bad = Microsoft.Extensions.Options.Options.Create(
            new RateLimiter.Options.RateLimiterOptions
            {
                RedisConfiguration = "",
                FailOpen = true
            });

        Assert.Throws<ArgumentException>(
            () => new RateLimiter.Core.Stores.RedisRateLimitStore(bad));
    }

}
