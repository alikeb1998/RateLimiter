using StackExchange.Redis;
using NUnit.Framework;
using System;
using System.Linq;
using System.Text;
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
        /// Unique per-test prefix to isolate keys across parallel tests.
        /// Example: "rl:TestId1234:"
        /// </summary>
        public static string TestPrefix()
        {
            // Use NUnit's unique Test ID and sanitize to [A-Za-z0-9]
            var id = TestContext.CurrentContext?.Test?.ID ?? Guid.NewGuid().ToString("N");
            var sb = new StringBuilder(id.Length);
            foreach (var ch in id)
                if (char.IsLetterOrDigit(ch)) sb.Append(ch);

            return $"rl:{sb}:";
        }

        /// <summary>
        /// Helper to build a namespaced key for the current test.
        /// </summary>
        public static string BuildKey(string suffix) => TestPrefix() + suffix;

        /// <summary>
        /// Delete all keys for the current test prefix (rl:&lt;testId&gt;:*).
        /// Use this in [TearDown] instead of global flushes.
        /// </summary>
        public static Task DeleteCurrentTestKeysAsync() => DeleteByPrefixAsync(TestPrefix());

        /// <summary>
        /// Delete keys by a specific prefix via SCAN + DEL (no admin required).
        /// </summary>
        public static async Task DeleteByPrefixAsync(string prefix)
        {
            var mux = await ConnectionMultiplexer.ConnectAsync(BaseConn);
            try
            {
                var db = mux.GetDatabase();
                foreach (var ep in mux.GetEndPoints())
                {
                    var server = mux.GetServer(ep);
                    var keys = server.Keys(pattern: prefix + "*", pageSize: 1000).ToArray();
                    if (keys.Length > 0)
                        await db.KeyDeleteAsync(keys.Select(k => (RedisKey)k).ToArray());
                }
            }
            finally
            {
                mux.Dispose();
            }
        }

        /// <summary>
        /// (Legacy) Flush only rl:* keys. Prefer DeleteCurrentTestKeysAsync() in new tests.
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
        /// Prefer prefix deletes for most tests.
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
