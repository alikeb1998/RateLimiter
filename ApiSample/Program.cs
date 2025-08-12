using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.HttpOverrides;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// 1. Load configuration
builder.Configuration
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddEnvironmentVariables();

// 2. Add Redis
builder.Services.AddSingleton<IConnectionMultiplexer>(sp =>
    ConnectionMultiplexer.Connect(builder.Configuration["Redis:Connection"]!));
builder.Services.AddScoped(sp =>
    sp.GetRequiredService<IConnectionMultiplexer>().GetDatabase());

// 3. Add your Rate Limiter (core library)
RateLimiter.Extensions.RateLimiterExtensions.AddRateLimiter(builder.Services, builder.Configuration);


// 4. Add controllers and Swagger
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Required when the app runs behind reverse proxies to get the real client IP
builder.Services.AddHttpContextAccessor();

var app = builder.Build();

// 5. Global exception handler
app.UseExceptionHandler(errorApp =>
{
    errorApp.Run(async context =>
    {
        context.Response.StatusCode = 500;
        context.Response.ContentType = "application/json";
        var errorFeature = context.Features.Get<IExceptionHandlerFeature>();
        var ex = errorFeature?.Error;
        await context.Response.WriteAsJsonAsync(new 
        {
            Message = "An unexpected error occurred.",
            Detail = builder.Environment.IsDevelopment() ? ex?.Message : null
        });
    });
});

// 6. Enable Swagger in Development only
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// 7. HTTPS redirect
app.UseHttpsRedirection();

// 8. Forwarded headers (if behind proxy/load‑balancer)
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
});

// 9. Rate Limiting (fully qualified)
RateLimiter.Extensions.RateLimiterExtensions.UseRateLimiter(app);
// 10. Authorization (if any)
app.UseAuthorization();

// 11. Routing to controllers
app.MapControllers();

app.Run();
public partial class Program { }

