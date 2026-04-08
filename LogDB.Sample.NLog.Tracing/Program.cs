using System.Diagnostics;
using LogDB.Client.Models;
using LogDB.NLog;
using LogDB.OpenTelemetry;
using Microsoft.Extensions.DependencyInjection;
using NLog;
using NLog.Config;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

/// <summary>
/// Distributed Tracing with NLog + LogDB
///
/// NLog handles logging. Spans are created with ActivitySource (pure .NET)
/// and exported via AddLogDBExporter(). NLog's LogDB target automatically
/// captures TraceId/SpanId from Activity.Current.
///
/// Architecture:
///   Web App → Search Service → Elasticsearch
///                             → Cache (Redis)
///           → Analytics Service → ClickHouse
/// </summary>

var apiKey = Environment.GetEnvironmentVariable("LOGDB_API_KEY", EnvironmentVariableTarget.User) ?? "demo-key";

// ── ActivitySources ──────────────────────────────────────────
var webSource = new ActivitySource("eShop.WebApp");
var searchSource = new ActivitySource("eShop.SearchService");
var analyticsSource = new ActivitySource("eShop.AnalyticsService");

// ── NLog: logs go to console + LogDB ─────────────────────────
var nlogConfig = new LoggingConfiguration();

// Console target
var consoleTarget = new NLog.Targets.ConsoleTarget("console")
{
    Layout = "${time} | ${level:uppercase=true:padding=-5} | ${message}"
};
nlogConfig.AddRule(NLog.LogLevel.Debug, NLog.LogLevel.Fatal, consoleTarget);

// LogDB target
nlogConfig.AddLogDBTargetWithRule(
    targetName: "logdb",
    apiKey: apiKey,
    defaultPayloadType: LogDBPayloadType.Log,
    minLevel: NLog.LogLevel.Debug,
    configure: target =>
    {
        target.DefaultApplication = "eShop.NLog.Tracing";
        target.DefaultEnvironment = "demo";
        target.DefaultCollection = "nlog-tracing-demo";
        target.EnableBatching = true;
        target.BatchSize = 200;
        target.FlushIntervalSeconds = 2;
    });

LogManager.Configuration = nlogConfig;
var logger = LogManager.GetLogger("eShop");

// ── OpenTelemetry: traces only (logs via NLog) ───────────────
var services = new ServiceCollection();
services.AddOpenTelemetry()
    .ConfigureResource(r =>
    {
        r.AddService("eShop.WebApp", serviceVersion: "1.0.0");
        r.AddAttributes(new[] { new KeyValuePair<string, object>("deployment.environment", "demo") });
    })
    .WithTracing(tracing =>
    {
        tracing.AddSource(webSource.Name);
        tracing.AddSource(searchSource.Name);
        tracing.AddSource(analyticsSource.Name);
        tracing.AddLogDBExporter(o =>
        {
            o.ApiKey = apiKey;
            o.DefaultCollection = "nlog-tracing-demo";
            o.EnableDebugLogging = true;
        });
    });

await using var provider = services.BuildServiceProvider();
var tracerProvider = provider.GetService<TracerProvider>();

// ── Scenario 1: Product search with cache + analytics ────────

Console.WriteLine("\n== NLog + Distributed Tracing ==\n");
Console.WriteLine("--> Scenario 1: Product search (cache miss → Elasticsearch → cache write)");

using (var web = webSource.StartActivity("GET /api/products/search", ActivityKind.Server))
{
    web?.SetTag("http.method", "GET");
    web?.SetTag("http.route", "/api/products/search");
    web?.SetTag("http.url", "/api/products/search?q=headphones&category=audio");
    web?.SetTag("service.name", "eShop.WebApp");
    Console.WriteLine($"    Trace ID: {Activity.Current?.TraceId}");

    logger.Info("Product search: query='headphones', category='audio'");

    // Web → Search Service
    using (var searchCall = webSource.StartActivity("gRPC SearchService/Query", ActivityKind.Client))
    {
        searchCall?.SetTag("rpc.system", "grpc");
        searchCall?.SetTag("service.name", "eShop.WebApp");

        using (var search = searchSource.StartActivity("SearchProducts", ActivityKind.Server))
        {
            search?.SetTag("search.query", "headphones");
            search?.SetTag("search.category", "audio");
            search?.SetTag("service.name", "eShop.SearchService");

            logger.Debug("Checking Redis cache for search results");

            // Cache lookup (miss)
            using (var cache = searchSource.StartActivity("GET search:headphones:audio", ActivityKind.Client))
            {
                cache?.SetTag("db.system", "redis");
                cache?.SetTag("db.operation", "GET");
                cache?.SetTag("service.name", "eShop.SearchService");
                await Task.Delay(2);
                cache?.SetTag("cache.hit", false);
                logger.Debug("Cache miss for search query");
            }

            // Elasticsearch query
            using (var es = searchSource.StartActivity("POST /products/_search", ActivityKind.Client))
            {
                es?.SetTag("db.system", "elasticsearch");
                es?.SetTag("db.operation", "search");
                es?.SetTag("db.statement", "{\"query\":{\"bool\":{\"must\":[{\"match\":{\"name\":\"headphones\"}},{\"term\":{\"category\":\"audio\"}}]}}}");
                es?.SetTag("service.name", "eShop.SearchService");
                logger.Debug("Querying Elasticsearch index 'products'");
                await Task.Delay(85);
                es?.SetTag("search.hits", 24);
                logger.Info("Elasticsearch returned 24 products in 85ms");
            }

            // Write to cache
            using (var cacheWrite = searchSource.StartActivity("SETEX search:headphones:audio 300", ActivityKind.Client))
            {
                cacheWrite?.SetTag("db.system", "redis");
                cacheWrite?.SetTag("db.operation", "SETEX");
                cacheWrite?.SetTag("service.name", "eShop.SearchService");
                await Task.Delay(2);
                logger.Debug("Cached search results (TTL 300s)");
            }

            search?.SetTag("search.results", 24);
            search?.SetStatus(ActivityStatusCode.Ok);
            logger.Info("Search completed: 24 results for 'headphones'");
        }
    }

    // Web → Analytics Service (fire-and-forget, parallel to response)
    using (var analyticsCall = webSource.StartActivity("gRPC AnalyticsService/Track", ActivityKind.Client))
    {
        analyticsCall?.SetTag("rpc.system", "grpc");
        analyticsCall?.SetTag("service.name", "eShop.WebApp");

        using (var analytics = analyticsSource.StartActivity("TrackSearchEvent", ActivityKind.Server))
        {
            analytics?.SetTag("analytics.event_type", "product_search");
            analytics?.SetTag("service.name", "eShop.AnalyticsService");
            logger.Debug("Recording search event in analytics");

            using (var ch = analyticsSource.StartActivity("INSERT INTO search_events", ActivityKind.Client))
            {
                ch?.SetTag("db.system", "clickhouse");
                ch?.SetTag("db.name", "analytics");
                ch?.SetTag("db.statement", "INSERT INTO search_events (query, category, results, ts) VALUES (?, ?, ?, ?)");
                ch?.SetTag("service.name", "eShop.AnalyticsService");
                await Task.Delay(8);
                logger.Debug("Search event persisted to ClickHouse");
            }

            analytics?.SetStatus(ActivityStatusCode.Ok);
        }
    }

    web?.SetTag("http.status_code", 200);
    web?.SetStatus(ActivityStatusCode.Ok);
    logger.Info("Response sent: 200 OK, 24 products");
}

await Task.Delay(300);

// ── Scenario 2: Search with Elasticsearch timeout ────────────

Console.WriteLine("\n--> Scenario 2: Elasticsearch timeout — error propagation");

using (var web = webSource.StartActivity("GET /api/products/search", ActivityKind.Server))
{
    web?.SetTag("http.method", "GET");
    web?.SetTag("service.name", "eShop.WebApp");
    Console.WriteLine($"    Trace ID: {Activity.Current?.TraceId}");

    logger.Info("Product search: query='wireless earbuds'");
    try
    {
        using (var searchCall = webSource.StartActivity("gRPC SearchService/Query", ActivityKind.Client))
        {
            searchCall?.SetTag("service.name", "eShop.WebApp");

            using (var search = searchSource.StartActivity("SearchProducts", ActivityKind.Server))
            {
                search?.SetTag("search.query", "wireless earbuds");
                search?.SetTag("service.name", "eShop.SearchService");

                // Cache miss
                using (var cache = searchSource.StartActivity("GET search:wireless earbuds", ActivityKind.Client))
                {
                    cache?.SetTag("db.system", "redis");
                    cache?.SetTag("cache.hit", false);
                    cache?.SetTag("service.name", "eShop.SearchService");
                    await Task.Delay(2);
                }

                // Elasticsearch timeout
                using (var es = searchSource.StartActivity("POST /products/_search", ActivityKind.Client))
                {
                    es?.SetTag("db.system", "elasticsearch");
                    es?.SetTag("service.name", "eShop.SearchService");
                    await Task.Delay(5000);
                    es?.SetStatus(ActivityStatusCode.Error, "Request timed out");
                    es?.AddEvent(new ActivityEvent("Timeout", tags: new ActivityTagsCollection
                    {
                        { "timeout_ms", 5000 },
                        { "cluster", "es-prod-01" }
                    }));
                    logger.Error("Elasticsearch timeout after 5000ms on cluster es-prod-01");
                }
                search?.SetStatus(ActivityStatusCode.Error, "Search backend unavailable");
                throw new TimeoutException("Elasticsearch timed out");
            }
        }
    }
    catch (Exception ex)
    {
        web?.SetTag("http.status_code", 504);
        web?.SetStatus(ActivityStatusCode.Error, ex.Message);
        logger.Error(ex, "Search request failed: {ErrorMessage}", ex.Message);
    }
}

// Flush
Console.WriteLine("\n--> Flushing...");
await Task.Delay(500);
LogManager.Flush(TimeSpan.FromSeconds(5));
tracerProvider?.ForceFlush(5000);

Console.WriteLine("\n[OK] All traces and logs exported to LogDB.");
Console.WriteLine("    NLog logs carry TraceId/SpanId automatically via Activity.Current.");
Console.WriteLine("    Open Trace Explorer and paste a Trace ID to see the span tree.");
