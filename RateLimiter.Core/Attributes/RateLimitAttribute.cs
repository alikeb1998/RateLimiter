using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RateLimiter.Core.Stores;
using RateLimiter.Options;

namespace RateLimiter.Attributes
{
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
    public class RateLimitAttribute : Attribute, IAsyncActionFilter
    {
        public int Calls { get; set; }                 // if 0 => use defaults
        public int PeriodSeconds { get; set; }         // if 0 => use defaults
        public RateLimitScope Scope { get; set; } = RateLimitScope.PerIp;
        public string? UserIdClaim { get; set; }
        // NOTE: non-nullable; default is Unspecified so it's a valid attribute argument
        public Algorithm Algorithm { get; set; } = Algorithm.Unspecified;

        public RateLimitAttribute() { }

        public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            var sp    = context.HttpContext.RequestServices;
            var opts  = sp.GetRequiredService<IOptions<RateLimiterOptions>>().Value; // for defaults only
            var store = sp.GetRequiredService<IRateLimitStore>();

            var calls  = Calls > 0 ? Calls : opts.DefaultCalls;
            var period = PeriodSeconds > 0 ? PeriodSeconds : opts.DefaultPeriodSeconds;
            var algorithm = (Algorithm != Algorithm.Unspecified) ? Algorithm : opts.DefaultAlgorithm;

            string key = Scope switch
            {
                RateLimitScope.PerUser when !string.IsNullOrEmpty(UserIdClaim)
                    => "rl:user:" + (context.HttpContext.User.FindFirst(UserIdClaim!)?.Value ?? "anon"),
                RateLimitScope.Global => "rl:global",
                _ => "rl:ip:" + (context.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown")
            };

            bool allowed = await store.TryIncrementAsync(key, calls, period, algorithm);

            // IMPORTANT: store already applied FailOpen/FailClose. Just honor the decision.
            if (!allowed)
            {
                context.HttpContext.Response.StatusCode = 429;
                context.HttpContext.Response.Headers["Retry-After"] = period.ToString();
                return;
            }

            await next();
        }
    }
}
