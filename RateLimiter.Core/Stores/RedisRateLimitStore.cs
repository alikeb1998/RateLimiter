using System;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using RateLimiter.Options;
using StackExchange.Redis;

namespace RateLimiter.Core.Stores
{
    public interface IRateLimitStore
    {
        Task<bool> TryIncrementAsync(string key, int calls, int periodSeconds, Algorithm algorithm);
        Task WarmUpAsync(string bucketKey, int capacity, int expirySeconds);
    }

    public class RedisRateLimitStore : IRateLimitStore
    {
        private readonly IDatabase _db;
        private readonly bool _failOpen;

        private readonly byte[] _fixedSha;       // rate_limit.lua (Fixed Window)
        private readonly byte[] _slidingSha;     // sliding_log_pure.lua (Pure Sliding Log)
        private readonly byte[] _tokenSha;       // token_bucket.lua (Token Bucket)
        private readonly byte[] _hybridSha;      // sliding_log.lua (Hybrid TB + Sliding)
        private readonly byte[] _warmUpSha;      // warm_up.lua

        public RedisRateLimitStore(IOptions<RateLimiterOptions> optsAccessor)
        {
            var opts = optsAccessor.Value ?? throw new ArgumentNullException(nameof(optsAccessor));
            if (string.IsNullOrWhiteSpace(opts.RedisConfiguration))
                throw new ArgumentException("RedisConfiguration must be set", nameof(opts.RedisConfiguration));

            _failOpen = opts.FailOpen;

            var mux = ConnectionMultiplexer.Connect(opts.RedisConfiguration);
            _db = mux.GetDatabase();

            var server     = mux.GetServer(mux.GetEndPoints()[0]);
            var scriptsDir = Path.Combine(AppContext.BaseDirectory, "LuaScripts");

            string fixedPath   = Path.Combine(scriptsDir, "rate_limit.lua");         // Fixed window
            string slidingPath = Path.Combine(scriptsDir, "sliding_log_pure.lua");   // Pure sliding-log
            string tokenPath   = Path.Combine(scriptsDir, "token_bucket.lua");       // Token bucket
            string hybridPath  = Path.Combine(scriptsDir, "sliding_log.lua");        // Hybrid (TB + log)
            string warmPath    = Path.Combine(scriptsDir, "warm_up.lua");

            if (!File.Exists(fixedPath))   throw new FileNotFoundException($"Missing Lua script: {fixedPath}");
            if (!File.Exists(slidingPath)) throw new FileNotFoundException($"Missing Lua script: {slidingPath}");
            if (!File.Exists(tokenPath))   throw new FileNotFoundException($"Missing Lua script: {tokenPath}");
            if (!File.Exists(hybridPath))  throw new FileNotFoundException($"Missing Lua script: {hybridPath}");
            if (!File.Exists(warmPath))    throw new FileNotFoundException($"Missing Lua script: {warmPath}");

            _fixedSha   = server.ScriptLoad(File.ReadAllText(fixedPath));
            _slidingSha = server.ScriptLoad(File.ReadAllText(slidingPath));
            _tokenSha   = server.ScriptLoad(File.ReadAllText(tokenPath));
            _hybridSha  = server.ScriptLoad(File.ReadAllText(hybridPath));
            _warmUpSha  = server.ScriptLoad(File.ReadAllText(warmPath));

            // Initial warm-up (global bucket)
            var initKeys = new RedisKey[]   { "rl:bucket" };
            var initArgs = new RedisValue[] { opts.WarmUpInitialCapacity, opts.DefaultPeriodSeconds };
            _db.ScriptEvaluate(_warmUpSha, initKeys, initArgs);
        }

        public async Task<bool> TryIncrementAsync(string key, int calls, int periodSeconds, Algorithm algorithm)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            try
            {
                switch (algorithm)
                {
                    case Algorithm.FixedWindow:
                    {
                        // Base key only; script computes window id with Redis TIME
                        var keys = new RedisKey[]   { key }; // e.g. "rl:global"
                        var args = new RedisValue[] { calls, periodSeconds }; // limit, window

                        var rr = await _db.ScriptEvaluateAsync(_fixedSha, keys, args);

                        if (rr.Type == ResultType.MultiBulk)
                            return (int)((RedisResult[])rr)[0] == 1; // {allowed,count}

                        return (int)rr == 1; // if your script returns a single int
                    }


                    case Algorithm.SlidingLog:
                    {
                        // KEYS[1]=log; ARGV[1]=max; ARGV[2]=window; ARGV[3]=now
                        var keys = new RedisKey[]   { $"{key}:log" };
                        var args = new RedisValue[] { calls, periodSeconds, now };
                        var rr   = await _db.ScriptEvaluateAsync(_slidingSha, keys, args);
                        var arr  = (RedisResult[])rr;
                        return (int)arr[0] == 1;
                    }

                    case Algorithm.TokenBucket:
                    {
                        // KEYS[1]=bucket; KEYS[2]=ts
                        // ARGV[1]=capacity; ARGV[2]=refillRate (can be fractional); ARGV[3]=ttl=period; ARGV[4]=now
                        var keys = new RedisKey[] { $"{key}:tb", $"{key}:tb:ts" };
                        // fractional refill rate as culture-invariant string
                        var refillRate = ((double)calls / Math.Max(1, periodSeconds))
                                         .ToString("G17", CultureInfo.InvariantCulture);
                        var args = new RedisValue[] { calls, refillRate, periodSeconds, now };
                        var rr   = await _db.ScriptEvaluateAsync(_tokenSha, keys, args);
                        var arr  = (RedisResult[])rr;
                        return (int)arr[0] == 1;
                    }

                    default: // Algorithm.Hybrid
                    { 
                        // KEYS[1]=bucket; KEYS[2]=log; ARGV[1]=capacity; ARGV[2]=refill(int); ARGV[3]=window; ARGV[4]=now
                        var keys = new RedisKey[] { $"{key}:bucket", $"{key}:log" };
                        // integer refill to avoid float issues in hybrid
                        var refill = Math.Max(1, (int)Math.Ceiling((double)calls / Math.Max(1, periodSeconds)));
                        var args   = new RedisValue[] { calls, refill, periodSeconds, now };
                        var rr     = await _db.ScriptEvaluateAsync(_hybridSha, keys, args);
                        var arr    = (RedisResult[])rr;
                        return (int)arr[0] == 1;
                    }
                }
            }
            catch
            {
                // Apply Fail-Open from settings here (store returns final decision)
                return _failOpen;
            }
        }

        public Task WarmUpAsync(string bucketKey, int capacity, int expirySeconds)
        {
            var keys = new RedisKey[]   { bucketKey };
            var args = new RedisValue[] { capacity, expirySeconds };
            return _db.ScriptEvaluateAsync(_warmUpSha, keys, args);
        }
    }
}
