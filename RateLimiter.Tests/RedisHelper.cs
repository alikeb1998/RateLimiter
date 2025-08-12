using StackExchange.Redis;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace RateLimiter.Tests
{
    public static class RedisHelper
    {
        // Base connection (override with RL_REDIS env var)
        public static string BaseConn =>
            Environment.GetEnvironmentVariable("RL_REDIS") ?? "127.0.0.1:6379";

        // Admin connection: append allowAdmin=true if not present
        private static string AdminConn =>
            BaseConn.IndexOf("allowAdmin", StringComparison.OrdinalIgnoreCase) >= 0
                ? BaseConn
                : BaseConn + ",allowAdmin=true";

        /// <summary>
        /// Flush only our rate limiter keys (rl:*) without admin commands.
        /// Uses SCAN + DEL. Works even when admin mode is disabled.
        /// </summary>
        public static async Task FlushRateLimiterKeysAsync()
        {
            var mux = await ConnectionMultiplexer.ConnectAsync(BaseConn);
            try
            {
                var db = mux.GetDatabase();
                foreach (var ep in mux.GetEndPoints())
                {
                    var server = mux.GetServer(ep);
                    // SCAN through rl:* and delete in batches
                    var batch = server.Keys(pattern: "rl:*", pageSize: 1000).ToArray();
                    if (batch.Length == 0) continue;
                    await db.KeyDeleteAsync(batch.Select(k => (RedisKey)k).ToArray());
                }
            }
            finally
            {
                mux.Dispose();
            }
        }

        /// <summary>
        /// Full FLUSHALL (admin). Returns true if succeeded, false if admin not available.
        /// Prefer FlushRateLimiterKeysAsync() for most tests.
        /// </summary>
        public static async Task<bool> TryFlushAllAsync()
        {
            try
            {
                var mux = await ConnectionMultiplexer.ConnectAsync(AdminConn);
                try
                {
                    foreach (var ep in mux.GetEndPoints())
                    {
                        var server = mux.GetServer(ep);
                        await server.FlushAllDatabasesAsync();
                    }
                }
                finally { mux.Dispose(); }
                return true;
            }
            catch (RedisCommandException)
            {
                return false; // admin disabled
            }
        }

        /// <summary>
        /// SCRIPT FLUSH (admin) to force NOSCRIPT errors. Returns true if flushed.
        /// </summary>
        public static async Task<bool> TryScriptFlushAsync()
        {
            try
            {
                var mux = await ConnectionMultiplexer.ConnectAsync(AdminConn);
                try
                {
                    foreach (var ep in mux.GetEndPoints())
                    {
                        var server = mux.GetServer(ep);
                        await server.ScriptFlushAsync();
                    }
                }
                finally { mux.Dispose(); }
                return true;
            }
            catch (RedisCommandException)
            {
                return false; // admin disabled
            }
        }
    }
}
