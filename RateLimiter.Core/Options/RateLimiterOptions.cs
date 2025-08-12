using System;
using System.Collections.Generic;

namespace RateLimiter.Options
{
    public enum RateLimitScope
    {
        PerIp,
        PerUser,
        Global
    }

    public enum Algorithm
    {
        Unspecified = 0,
        FixedWindow,
        SlidingLog,
        TokenBucket,
        Hybrid
    }

    public class RoleLimit
    {
        public string Role { get; set; }
        public int Calls { get; set; }
        public int PeriodSeconds { get; set; }
    }


    public class RateLimiterOptions
    {
        public Algorithm DefaultAlgorithm { get; set; } = Algorithm.Hybrid;
        public int DefaultCalls { get; set; } = 100;
        public int DefaultPeriodSeconds { get; set; } = 60;
        public RateLimitScope DefaultScope { get; set; } = RateLimitScope.PerIp;
        public bool FailOpen { get; set; } = true;
        public List<RoleLimit> RoleLimits { get; set; } = new();

        public string AzureAppConfigConnection { get; set; }

        // Redis
        public string RedisConfiguration { get; set; }

        // Warm-up config
        public int WarmUpInitialCapacity { get; set; } = 100;
    }
}