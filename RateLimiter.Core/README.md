# RedGate.RedisRateLimiter

Attribute‑based, **distributed** rate limiter for ASP.NET Core (.NET 8) backed by **Redis + Lua**.  
Supports **Fixed Window**, **Sliding Log**, **Token Bucket**, and a **Hybrid** (Token‑Bucket + Sliding Log) algorithm.

- Zero in‑memory state (safe behind load balancers / across multiple app instances).
- Simple **`[RateLimit]` attribute** on actions/controllers.
- Works with real client IPs when behind proxies (via `UseForwardedHeaders`).
- Production‑grade options: **Fail‑Open / Fail‑Close**, warm‑up, per‑route overrides.
- Ships Lua scripts with the package; no manual script install required.

> 🧪 Heavily integration‑tested against real Redis. No mocks, real Lua.

---

## Install

```bash
dotnet add package RedGate.RedisRateLimiter
```

---

## Quick start (ASP.NET Core .NET 8)

**`appsettings.json`**

```jsonc
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "AllowedHosts": "*",
  "Redis": {
    "Connection": "127.0.0.1:6379"
  },
  "RateLimiter": {
    "RedisConfiguration": "127.0.0.1:6379",
    "DefaultAlgorithm": "Hybrid",           // FixedWindow | SlidingLog | TokenBucket | Hybrid
    "DefaultCalls": 5,
    "DefaultPeriodSeconds": 60,
    "DefaultScope": "PerIp",                 // PerIp | PerUser | Global
    "FailOpen": true,                        // true = allow on Redis/Lua error; false = block on error
    "WarmUpInitialCapacity": 10
  }
}
```

**`Program.cs`**

```csharp
using Microsoft.AspNetCore.HttpOverrides;
using StackExchange.Redis;
using RateLimiter.Extensions; // AddRateLimiter(), UseRateLimiter()

var builder = WebApplication.CreateBuilder(args);

builder.Configuration
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddEnvironmentVariables();

// Redis (optional here; package creates its own connection too if you only set RateLimiter:RedisConfiguration)
builder.Services.AddSingleton<IConnectionMultiplexer>(sp =>
    ConnectionMultiplexer.Connect(builder.Configuration["Redis:Connection"]!));
builder.Services.AddScoped(sp => sp.GetRequiredService<IConnectionMultiplexer>().GetDatabase());

// Register the rate limiter (reads "RateLimiter" section)
RateLimiter.Extensions.RateLimiterExtensions.AddRateLimiter(builder.Services, builder.Configuration);

// Controllers + Swagger
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Required when the app runs behind reverse proxies to get the real client IP
builder.Services.AddHttpContextAccessor();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

// Forwarded headers (X-Forwarded-For / X-Forwarded-Proto) => RemoteIpAddress becomes client IP
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
});

// Rate limiter middleware (must be before MVC)
app.UseRateLimiter();

app.MapControllers();
app.Run();
```

**Sample controller**

```csharp
using Microsoft.AspNetCore.Mvc;
using RateLimiter.Attributes;
using RateLimiter.Options;

[ApiController]
[Route("api/test")]
public class TestController : ControllerBase
{
    // Fixed window per IP: 5 requests / 10s
    [HttpGet("fixed/ip")]
    [RateLimit(Algorithm = Algorithm.FixedWindow, Calls = 5, PeriodSeconds = 10, Scope = RateLimitScope.PerIp)]
    public IActionResult FixedIp() => Ok("fixed/ip OK");

    // Sliding log per user (JWT claim "sub"): 3 / 10s
    [HttpGet("sliding/user")]
    [RateLimit(Algorithm = Algorithm.SlidingLog, Calls = 3, PeriodSeconds = 10, Scope = RateLimitScope.PerUser, UserIdClaim = "sub")]
    public IActionResult SlidingUser() => Ok("sliding/user OK");

    // Token bucket global: 20 burst / 10s refill
    [HttpGet("token/global")]
    [RateLimit(Algorithm = Algorithm.TokenBucket, Calls = 20, PeriodSeconds = 10, Scope = RateLimitScope.Global)]
    public IActionResult TokenGlobal() => Ok("token/global OK");

    // Hybrid per IP: 5 / 10s; short bursts allowed, mid-term controlled
    [HttpGet("hybrid/ip")]
    [RateLimit(Algorithm = Algorithm.Hybrid, Calls = 5, PeriodSeconds = 10, Scope = RateLimitScope.PerIp)]
    public IActionResult HybridIp() => Ok("hybrid/ip OK");
}
```

---

## How it works

### Storage model
- **Redis** is the single source of truth. No in-app counters → horizontally scalable.
- Each algorithm uses separate keys under a *base key* derived from scope:
  - `PerIp` → `rl:ip:{client-ip}`
  - `PerUser` → `rl:user:{user-id}` (from `UserIdClaim`, e.g. `"sub"`)
  - `Global` → `rl:global`

### Algorithms

#### 1) Fixed Window
- **Key**: `base:fw:{floor(now/window)}`
- **Logic**: Increment a counter for the current window; allow while `count <= limit`.
- **Pros**: Simple, cheap, predictable.
- **Cons**: Boundary bursts (edge of windows) may allow more than `limit` briefly.

#### 2) Sliding Log (pure)
- **Key**: ZSET `base:log` + counter `base:log:seq`
- **Logic**: Remove entries older than `window`; add a **unique** member (`now:seq`); allow while `ZCARD < limit`.
- **Pros**: Accurate per real time window; no boundary burst.
- **Cons**: ZSET maintenance; more Redis work for high QPS.

#### 3) Token Bucket
- **Keys**: `base:tb` (tokens), `base:tb:ts` (last updated timestamp)
- **Logic**: Refill `floor(elapsed * refillRate)`; allow while `tokens > 0` (consume 1).
- **Pros**: Great for bursts with bounded average rate.
- **Cons**: Needs careful rounding; if refill is too low, can starve.

#### 4) Hybrid (Token Bucket + Sliding Log)
- **Keys**: `base:bucket` (tokens + ts), `base:log` (+ `base:log:seq`)
- **Logic**: Allow **only if** *both* conditions hold:
  - **Tokens** available (short‑term burst control)
  - **Log count < capacity** in the window (mid‑term fairness)
- **Pros**: Real‑world friendly: absorbs micro-bursts but prevents sustained abuse.
- **Cons**: Highest Redis cost of the four (still usually fine).

---

## Configuration

### `RateLimiterOptions`

| Option | Type | Default | Description |
|---|---|---:|---|
| `RedisConfiguration` | `string` | — | Redis connection (e.g., `127.0.0.1:6379`). Supports full StackExchange.Redis syntax. |
| `DefaultAlgorithm` | `Algorithm` | `Hybrid` | Algorithm used when `[RateLimit]` doesn’t override it. |
| `DefaultCalls` | `int` | `5` | Default limit per window/bucket. |
| `DefaultPeriodSeconds` | `int` | `60` | Default period/window seconds. |
| `DefaultScope` | `RateLimitScope` | `PerIp` | Default scope (PerIp, PerUser, Global). |
| `FailOpen` | `bool` | `true` | **Fail‑Open** (allow) on Redis/Lua errors; set `false` for **Fail‑Close** (block). |
| `WarmUpInitialCapacity` | `int` | `10` | Seed capacity for warm‑up bucket on app start. |

> All of these can be overridden per‑endpoint using `[RateLimit(...)]`. If a property is omitted in the attribute, defaults apply.

### Fail‑Open vs Fail‑Close
- **Fail‑Open (true)**: If Redis is down or Lua fails, requests **pass**. Keeps service up; risk: abusive traffic may slip through.
- **Fail‑Close (false)**: On errors, respond **429**. Safer during attacks; risk: blocking legit traffic if Redis hiccups.

### Warm‑Up
On startup, the package warms a global bucket with `WarmUpInitialCapacity`. This prevents “thundering herd” of 0‑token buckets immediately after deploys/restarts.

---

## Using the attribute

```csharp
[RateLimit(Algorithm = Algorithm.SlidingLog, Calls = 10, PeriodSeconds = 30, Scope = RateLimitScope.PerUser, UserIdClaim = "sub")]
public IActionResult MyAction() => Ok();
```

- `Algorithm` – choose one of `FixedWindow`, `SlidingLog`, `TokenBucket`, `Hybrid`.
- `Calls` / `PeriodSeconds` – per‑endpoint override of limits.
- `Scope` – `PerIp`, `PerUser`, or `Global`.
- `UserIdClaim` – claim type to identify the user for `PerUser` (e.g., `"sub"`, `"nameidentifier"`).

If `Algorithm` is omitted, `DefaultAlgorithm` is used.

---

## Real client IP behind proxies

If your app is behind a reverse proxy / load balancer, enable forwarded headers so `RemoteIpAddress` reflects the **origin** client:

```csharp
builder.Services.AddHttpContextAccessor();

app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
});
```

> The attribute uses `HttpContext.Connection.RemoteIpAddress`. The forwarded headers middleware updates this based on `X-Forwarded-For`.

---

## Redis (Docker)

```yaml
# docker-compose.yml
version: "3.8"
services:
  redis:
    image: redis:7-alpine
    ports: ["6379:6379"]
    command: ["redis-server", "--save", "", "--appendonly", "no"]
```

Run:
```bash
docker compose up -d
```

Update your `appsettings.json` (`RedisConfiguration` and/or `Redis:Connection`) to `127.0.0.1:6379` (or container name inside Docker networks).

---

## Samples

### Fixed Window per IP
```csharp
[RateLimit(Algorithm = Algorithm.FixedWindow, Calls = 100, PeriodSeconds = 60, Scope = RateLimitScope.PerIp)]
public IActionResult GetUsers() => Ok();
```

### Sliding Log per User
```csharp
[Authorize]
[RateLimit(Algorithm = Algorithm.SlidingLog, Calls = 20, PeriodSeconds = 60, Scope = RateLimitScope.PerUser, UserIdClaim = "sub")]
public IActionResult GetProfile() => Ok();
```

### Token Bucket global (burst 200 / avg 200 per minute)
```csharp
[RateLimit(Algorithm = Algorithm.TokenBucket, Calls = 200, PeriodSeconds = 60, Scope = RateLimitScope.Global)]
public IActionResult Export() => Ok();
```

### Hybrid per IP (burst 10, ensure fair over 30s)
```csharp
[RateLimit(Algorithm = Algorithm.Hybrid, Calls = 10, PeriodSeconds = 30, Scope = RateLimitScope.PerIp)]
public IActionResult Search() => Ok();
```

---

## Troubleshooting

- **Always 429**  
  Verify Redis is reachable; check that your clocks aren’t wildly skewed; ensure forwarded headers are configured if you’re behind a proxy (so PerIp keys don’t collapse to one value).

- **Everything allowed even under load**  
  You probably have `FailOpen = true` and Redis is unavailable or scripts aren’t loading. Set `FailOpen = false` to be strict, and check logs for Lua errors.

- **First request of the window blocks**  
  Mismatched Lua arguments. Ensure you’re on the latest package and didn’t override scripts. Our Lua scripts are loaded and executed by the library automatically.

- **NOSCRIPT tests**  
  `SCRIPT FLUSH` only affects EVALSHA. If your environment uses plain EVAL (by text), there’s nothing to flush—skip NOSCRIPT tests or enable SHA mode via options (if you expose that).

---

## Performance notes

- All operations are single round‑trip Lua scripts; no client‑side races.
- Hybrid does the most work (bucket + log maintenance) but remains efficient for typical API rates.
- For very high QPS, prefer Token Bucket or Fixed Window. Consider sharded keys and Redis Cluster.

---

## Security / Safety

- The library never trusts client IP headers directly; use ASP.NET Core’s `UseForwardedHeaders` to set `RemoteIpAddress` safely.
- For `PerUser`, choose a stable user identifier claim (e.g., `sub`).

---

## License

**MIT** — free for commercial and personal use.

---

## Contributing

PRs welcome! Please include:
- Integration tests against Redis for new/changed behaviors
- Algorithm justification if you tweak Lua
- Docs updates for any new options

---

## Acknowledgements

- Inspired by prior work on distributed rate limiting using Redis + Lua.
- Built for modern ASP.NET Core (.NET 8), tested on Windows, Linux, Docker.

