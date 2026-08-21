using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using DistSear.Coordinator.Search;
using DistSear.Coordinator.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Identity.Web;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace DistSear.Coordinator.Hosting;

public static class HardeningRegistration
{
    public const string SearchPolicy = "search";
    public const string WritePolicy = "write";

    /// <summary>
    /// Authentication with two schemes.
    ///
    /// Entra ID covers interactive callers, whose token carries their group memberships; API keys
    /// cover services, which have no user. Both are optional so the stack runs unauthenticated
    /// locally, but when authentication is off the coordinator treats every caller as anonymous —
    /// which means restricted documents are hidden rather than exposed.
    /// </summary>
    public static IServiceCollection AddSearchAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var entraSection = configuration.GetSection("AzureAd");
        var entraConfigured = entraSection.Exists() && !string.IsNullOrEmpty(entraSection["ClientId"]);

        var builder = services.AddAuthentication(options =>
        {
            options.DefaultScheme = entraConfigured
                ? JwtBearerDefaults.AuthenticationScheme
                : ApiKeyAuthenticationHandler.SchemeName;
        });

        builder.AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
            ApiKeyAuthenticationHandler.SchemeName,
            _ => { });

        if (entraConfigured)
        {
            builder.AddMicrosoftIdentityWebApi(entraSection);
        }

        services.AddAuthorization(options =>
        {
            // Reading is open to any authenticated caller; document-level filtering, not
            // authorisation, decides what they actually see.
            options.AddPolicy(SearchPolicy, policy => policy.RequireAuthenticatedUser());

            // Writing is a separate, explicitly granted capability.
            options.AddPolicy(
                WritePolicy,
                policy => policy.RequireAssertion(context =>
                    context.User.HasClaim("scope", "search.write")
                    || context.User.HasClaim(c => c.Type == "roles" && c.Value == "Search.Write")));
        });

        return services;
    }

    /// <summary>
    /// Rate limits partitioned per caller.
    ///
    /// A global limit would let one heavy client exhaust the budget for everyone, so the partition
    /// is the caller's identity. Search and write get separate buckets because a bulk load is
    /// expected to be bursty in a way that interactive search is not.
    /// </summary>
    public static IServiceCollection AddSearchRateLimiting(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var searchPermits = configuration.GetValue("DistSear:RateLimits:SearchPerMinute", 600);
        var writePermits = configuration.GetValue("DistSear:RateLimits:WritePerMinute", 120);
        var concurrency = configuration.GetValue("DistSear:RateLimits:ConcurrentSearches", 64);

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            options.OnRejected = async (context, cancellationToken) =>
            {
                // Telling the caller when to come back is what turns a rejection into backpressure
                // rather than a client-side retry storm.
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter =
                        ((int)retryAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture);
                }

                await context.HttpContext.Response.WriteAsJsonAsync(
                    new { error = "Rate limit exceeded." },
                    cancellationToken);
            };

            options.AddPolicy(SearchPolicy, context => RateLimitPartition.GetSlidingWindowLimiter(
                PartitionOf(context),
                _ => new SlidingWindowRateLimiterOptions
                {
                    PermitLimit = searchPermits,
                    Window = TimeSpan.FromMinutes(1),

                    // Segmenting the window stops a caller spending a whole minute's budget in the
                    // instant the window rolls over.
                    SegmentsPerWindow = 6,
                    QueueLimit = 0
                }));

            options.AddPolicy(WritePolicy, context => RateLimitPartition.GetSlidingWindowLimiter(
                PartitionOf(context),
                _ => new SlidingWindowRateLimiterOptions
                {
                    PermitLimit = writePermits,
                    Window = TimeSpan.FromMinutes(1),
                    SegmentsPerWindow = 6,
                    QueueLimit = 0
                }));

            // A cluster-wide ceiling on simultaneous fan-outs, protecting the index nodes from a
            // thundering herd no per-caller limit would catch.
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(_ =>
                RateLimitPartition.GetConcurrencyLimiter(
                    "global",
                    _ => new ConcurrencyLimiterOptions
                    {
                        PermitLimit = concurrency,
                        QueueLimit = concurrency * 2,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst
                    }));
        });

        return services;
    }

    /// <summary>Identifies the caller for rate-limit purposes, falling back to remote address.</summary>
    private static string PartitionOf(HttpContext context)
    {
        if (context.Request.Headers.TryGetValue(ApiKeyAuthenticationHandler.HeaderName, out var key)
            && !string.IsNullOrEmpty(key))
        {
            // The key itself is never used as a partition value; its hash is, so keys do not leak
            // into metrics or logs through limiter diagnostics.
            return "key:" + ApiKeyHasher.Hash(key!);
        }

        var subject = context.User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? context.User.FindFirstValue("oid");

        return subject is not null
            ? "user:" + subject
            : "ip:" + (context.Connection.RemoteIpAddress?.ToString() ?? "unknown");
    }

    /// <summary>
    /// Tracing and metrics.
    ///
    /// A fan-out query is hard to reason about from logs alone, because the interesting question is
    /// almost always "which shard was slow". Spans per shard make that visible directly instead of
    /// requiring it to be inferred from timestamps.
    /// </summary>
    public static IServiceCollection AddSearchTelemetry(
        this IServiceCollection services,
        IConfiguration configuration,
        string serviceName)
    {
        var otel = services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName))
            .WithTracing(tracing => tracing
                .AddSource(SearchCoordinator.ActivitySource.Name)
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation())
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation());

        // Application Insights is wired only when a connection string is present, so the same build
        // runs locally with no exporter configured.
        if (!string.IsNullOrEmpty(configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
        {
            otel.UseAzureMonitor();
        }

        return services;
    }
}
