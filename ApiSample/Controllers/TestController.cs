using Microsoft.AspNetCore.Mvc;
using RateLimiter.Attributes;
using RateLimiter.Options;

namespace ApiSample.Controllers
{
    [ApiController]
    [Route("api/test")]
    public class TestController : ControllerBase
    {
        // ---------- FIXED WINDOW ----------
        [HttpGet("fixed/ip")]
        [RateLimit(Algorithm = Algorithm.FixedWindow, Calls = 5, PeriodSeconds = 10, Scope = RateLimitScope.PerIp)]
        public IActionResult FixedIp() => Ok("fixed/ip OK");

        [HttpGet("fixed/user")]
        [RateLimit(Algorithm = Algorithm.FixedWindow, Calls = 3, PeriodSeconds = 10, Scope = RateLimitScope.PerUser, UserIdClaim = "sub")]
        public IActionResult FixedUser() => Ok("fixed/user OK");

        [HttpGet("fixed/global")]
        [RateLimit(Algorithm = Algorithm.FixedWindow, Calls = 20, PeriodSeconds = 10, Scope = RateLimitScope.Global)]
        public IActionResult FixedGlobal() => Ok("fixed/global OK");


        // ---------- SLIDING LOG ----------
        [HttpGet("sliding/ip")]
        [RateLimit(Algorithm = Algorithm.SlidingLog, Calls = 5, PeriodSeconds = 10, Scope = RateLimitScope.PerIp)]
        public IActionResult SlidingIp() => Ok("sliding/ip OK");

        [HttpGet("sliding/user")]
        [RateLimit(Algorithm = Algorithm.SlidingLog, Calls = 3, PeriodSeconds = 10, Scope = RateLimitScope.PerUser, UserIdClaim = "sub")]
        public IActionResult SlidingUser() => Ok("sliding/user OK");

        [HttpGet("sliding/global")]
        [RateLimit(Algorithm = Algorithm.SlidingLog, Calls = 20, PeriodSeconds = 10, Scope = RateLimitScope.Global)]
        public IActionResult SlidingGlobal() => Ok("sliding/global OK");


        // ---------- TOKEN BUCKET ----------
        [HttpGet("token/ip")]
        [RateLimit(Algorithm = Algorithm.TokenBucket, Calls = 5, PeriodSeconds = 10, Scope = RateLimitScope.PerIp)]
        public IActionResult TokenIp() => Ok("token/ip OK");

        [HttpGet("token/user")]
        [RateLimit(Algorithm = Algorithm.TokenBucket, Calls = 3, PeriodSeconds = 10, Scope = RateLimitScope.PerUser, UserIdClaim = "sub")]
        public IActionResult TokenUser() => Ok("token/user OK");

        [HttpGet("token/global")]
        [RateLimit(Algorithm = Algorithm.TokenBucket, Calls = 20, PeriodSeconds = 10, Scope = RateLimitScope.Global)]
        public IActionResult TokenGlobal() => Ok("token/global OK");


        // ---------- HYBRID ----------
        [HttpGet("hybrid/ip")]
        [RateLimit(Algorithm = Algorithm.Hybrid, Calls = 5, PeriodSeconds = 10, Scope = RateLimitScope.PerIp)]
        public IActionResult HybridIp() => Ok("hybrid/ip OK");

        [HttpGet("hybrid/user")]
        [RateLimit(Algorithm = Algorithm.Hybrid, Calls = 3, PeriodSeconds = 10, Scope = RateLimitScope.PerUser, UserIdClaim = "sub")]
        public IActionResult HybridUser() => Ok("hybrid/user OK");

        [HttpGet("hybrid/global")]
        [RateLimit(Algorithm = Algorithm.Hybrid, Calls = 20, PeriodSeconds = 10, Scope = RateLimitScope.Global)]
        public IActionResult HybridGlobal() => Ok("hybrid/global OK");
    }
}
