using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RateLimiter.Core.Stores;
using RateLimiter.Options;

namespace RateLimiter.Extensions
{
    public static class RateLimiterExtensions
    {
        public static IServiceCollection AddRateLimiter(this IServiceCollection services, IConfiguration config)
        {
            services.Configure<RateLimiterOptions>(config.GetSection("RateLimiter"));
            services.AddSingleton<IRateLimitStore, RedisRateLimitStore>();
            services.AddHttpContextAccessor();
            return services;
        }

        public static IApplicationBuilder UseRateLimiter(this IApplicationBuilder app)
        {
            app.UseForwardedHeaders();
            return app;
        }
    }
}