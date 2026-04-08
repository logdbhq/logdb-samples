using System.Diagnostics;
using LogDB.Extensions.Logging;
using LogDB.OpenTelemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

/// <summary>
/// Distributed Tracing with LogDB SDK (ILogger) + ActivitySource
///
/// Uses Microsoft.Extensions.Logging (AddLogDB) for logs and
/// AddLogDBExporter() for trace spans. Simplest integration:
/// just ILogger + ActivitySource, no third-party logging framework.
///
/// Architecture:
///   API Server → User Service → PostgreSQL + Redis
///              → Billing Service → Stripe + Invoice DB
///              → Audit Service → Kafka publish
/// </summary>

var apiKey = Environment.GetEnvironmentVariable("LOGDB_API_KEY", EnvironmentVariableTarget.User) ?? "demo-key";

var apiSource = new ActivitySource("eShop.API");
var userSource = new ActivitySource("eShop.UserService");
var billingSource = new ActivitySource("eShop.BillingService");
var auditSource = new ActivitySource("eShop.AuditService");

// ── Host with ILogger (LogDB) + OTel tracing ─────────────────
var builder = Host.CreateDefaultBuilder();
builder.ConfigureLogging((_, logging) =>
{
    logging.ClearProviders();
    logging.AddConsole();
    logging.AddLogDB(options =>
    {
        options.ApiKey = apiKey;
        options.EnableBatching = true;
        options.BatchSize = 200;
        options.FlushInterval = TimeSpan.FromSeconds(2);
    });
});

builder.ConfigureServices((_, services) =>
{
    services.AddOpenTelemetry()
        .ConfigureResource(r =>
        {
            r.AddService("eShop.API", serviceVersion: "1.0.0");
            r.AddAttributes(new[] { new KeyValuePair<string, object>("deployment.environment", "demo") });
        })
        .WithTracing(tracing =>
        {
            tracing.AddSource(apiSource.Name);
            tracing.AddSource(userSource.Name);
            tracing.AddSource(billingSource.Name);
            tracing.AddSource(auditSource.Name);
            tracing.AddLogDBExporter(o =>
            {
                o.ApiKey = apiKey;
                o.DefaultCollection = "sdk-tracing-demo";
                o.EnableDebugLogging = true;
            });
        });
});

var host = builder.Build();
var loggerFactory = host.Services.GetRequiredService<ILoggerFactory>();
var tracerProvider = host.Services.GetService<TracerProvider>();

// ── Scenario 1: User registration (branching tree) ───────────

Console.WriteLine("\n== LogDB SDK (ILogger) + Distributed Tracing ==\n");
Console.WriteLine("--> Scenario 1: User registration — 4 services, parallel billing + audit");

using (var api = apiSource.StartActivity("POST /api/users/register", ActivityKind.Server))
{
    api?.SetTag("http.method", "POST");
    api?.SetTag("http.route", "/api/users/register");
    api?.SetTag("service.name", "eShop.API");
    Console.WriteLine($"    Trace ID: {Activity.Current?.TraceId}");

    var apiLog = loggerFactory.CreateLogger("eShop.API");
    apiLog.LogInformation("User registration: {Email}", "eve@example.com");

    // API → User Service
    using (var userCall = apiSource.StartActivity("gRPC UserService/Register", ActivityKind.Client))
    {
        userCall?.SetTag("rpc.system", "grpc");
        userCall?.SetTag("service.name", "eShop.API");

        using (var userSvc = userSource.StartActivity("RegisterUser", ActivityKind.Server))
        {
            userSvc?.SetTag("user.email", "eve@example.com");
            userSvc?.SetTag("service.name", "eShop.UserService");

            var userLog = loggerFactory.CreateLogger("eShop.UserService");
            userLog.LogInformation("Creating user account for {Email}", "eve@example.com");

            // Check if email exists (Redis cache)
            using (var cache = userSource.StartActivity("GET user:eve@example.com", ActivityKind.Client))
            {
                cache?.SetTag("db.system", "redis");
                cache?.SetTag("db.operation", "GET");
                cache?.SetTag("service.name", "eShop.UserService");
                await Task.Delay(2);
                cache?.SetTag("cache.hit", false);
                userLog.LogDebug("Email not in cache, checking database");
            }

            // Check DB
            using (var db = userSource.StartActivity("SELECT * FROM users WHERE email = $1", ActivityKind.Client))
            {
                db?.SetTag("db.system", "postgresql");
                db?.SetTag("db.name", "user_db");
                db?.SetTag("service.name", "eShop.UserService");
                await Task.Delay(12);
                userLog.LogDebug("Email not registered, proceeding");
            }

            // Insert user
            using (var insert = userSource.StartActivity("INSERT INTO users", ActivityKind.Client))
            {
                insert?.SetTag("db.system", "postgresql");
                insert?.SetTag("db.name", "user_db");
                insert?.SetTag("service.name", "eShop.UserService");
                await Task.Delay(18);
                userLog.LogInformation("User created: ID=USR-4001, email={Email}", "eve@example.com");
            }

            // Cache the new user
            using (var cacheSet = userSource.StartActivity("SET user:eve@example.com", ActivityKind.Client))
            {
                cacheSet?.SetTag("db.system", "redis");
                cacheSet?.SetTag("db.operation", "SET");
                cacheSet?.SetTag("service.name", "eShop.UserService");
                await Task.Delay(2);
            }

            userSvc?.SetStatus(ActivityStatusCode.Ok);
        }
    }

    // API → Billing Service (parallel with audit)
    var billingTask = Task.Run(async () =>
    {
        using var billCall = apiSource.StartActivity("gRPC BillingService/CreateAccount", ActivityKind.Client);
        billCall?.SetTag("rpc.system", "grpc");
        billCall?.SetTag("service.name", "eShop.API");

        using var billSvc = billingSource.StartActivity("CreateBillingAccount", ActivityKind.Server);
        billSvc?.SetTag("user.id", "USR-4001");
        billSvc?.SetTag("service.name", "eShop.BillingService");

        var billLog = loggerFactory.CreateLogger("eShop.BillingService");
        billLog.LogInformation("Creating billing account for USR-4001");

        // Setup Stripe customer
        using (var stripe = billingSource.StartActivity("POST stripe.com/v1/customers", ActivityKind.Client))
        {
            stripe?.SetTag("http.method", "POST");
            stripe?.SetTag("peer.service", "Stripe");
            stripe?.SetTag("service.name", "eShop.BillingService");
            await Task.Delay(95);
            stripe?.SetTag("http.status_code", 200);
            stripe?.SetTag("stripe.customer_id", "cus_abc123");
            billLog.LogInformation("Stripe customer created: cus_abc123");
        }

        // Store in billing DB
        using (var db = billingSource.StartActivity("INSERT INTO billing_accounts", ActivityKind.Client))
        {
            db?.SetTag("db.system", "postgresql");
            db?.SetTag("db.name", "billing_db");
            db?.SetTag("service.name", "eShop.BillingService");
            await Task.Delay(10);
            billLog.LogInformation("Billing account persisted");
        }

        billSvc?.SetStatus(ActivityStatusCode.Ok);
    });

    // API → Audit Service (parallel with billing)
    var auditTask = Task.Run(async () =>
    {
        using var auditCall = apiSource.StartActivity("gRPC AuditService/Log", ActivityKind.Client);
        auditCall?.SetTag("rpc.system", "grpc");
        auditCall?.SetTag("service.name", "eShop.API");

        using var auditSvc = auditSource.StartActivity("LogAuditEvent", ActivityKind.Server);
        auditSvc?.SetTag("audit.event", "user.registered");
        auditSvc?.SetTag("audit.actor", "eve@example.com");
        auditSvc?.SetTag("service.name", "eShop.AuditService");

        var auditLog = loggerFactory.CreateLogger("eShop.AuditService");
        auditLog.LogInformation("Audit: user.registered for eve@example.com");

        // Publish to Kafka
        using (var kafka = auditSource.StartActivity("Produce audit.events", ActivityKind.Producer))
        {
            kafka?.SetTag("messaging.system", "kafka");
            kafka?.SetTag("messaging.destination", "audit.events");
            kafka?.SetTag("service.name", "eShop.AuditService");
            await Task.Delay(12);
            auditLog.LogDebug("Audit event published to Kafka topic audit.events");
        }

        auditSvc?.SetStatus(ActivityStatusCode.Ok);
    });

    await Task.WhenAll(billingTask, auditTask);

    api?.SetTag("http.status_code", 201);
    api?.SetStatus(ActivityStatusCode.Ok);
    apiLog.LogInformation("User registration complete: USR-4001");
}

await Task.Delay(300);

// ── Scenario 2: Billing failure ──────────────────────────────

Console.WriteLine("\n--> Scenario 2: User registration with Stripe failure — error in subtree");

using (var api = apiSource.StartActivity("POST /api/users/register", ActivityKind.Server))
{
    api?.SetTag("http.method", "POST");
    api?.SetTag("service.name", "eShop.API");
    Console.WriteLine($"    Trace ID: {Activity.Current?.TraceId}");

    var apiLog = loggerFactory.CreateLogger("eShop.API");
    apiLog.LogInformation("User registration: {Email}", "frank@example.com");

    // User Service — succeeds
    using (var userCall = apiSource.StartActivity("gRPC UserService/Register", ActivityKind.Client))
    {
        userCall?.SetTag("service.name", "eShop.API");
        using (var userSvc = userSource.StartActivity("RegisterUser", ActivityKind.Server))
        {
            userSvc?.SetTag("service.name", "eShop.UserService");
            var userLog = loggerFactory.CreateLogger("eShop.UserService");
            using (var db = userSource.StartActivity("INSERT INTO users", ActivityKind.Client))
            {
                db?.SetTag("db.system", "postgresql");
                db?.SetTag("service.name", "eShop.UserService");
                await Task.Delay(15);
            }
            userLog.LogInformation("User created: USR-4002");
            userSvc?.SetStatus(ActivityStatusCode.Ok);
        }
    }

    // Billing — Stripe fails
    try
    {
        using var billCall = apiSource.StartActivity("gRPC BillingService/CreateAccount", ActivityKind.Client);
        billCall?.SetTag("service.name", "eShop.API");

        using var billSvc = billingSource.StartActivity("CreateBillingAccount", ActivityKind.Server);
        billSvc?.SetTag("service.name", "eShop.BillingService");

        var billLog = loggerFactory.CreateLogger("eShop.BillingService");
        billLog.LogInformation("Creating billing account for USR-4002");

        using (var stripe = billingSource.StartActivity("POST stripe.com/v1/customers", ActivityKind.Client))
        {
            stripe?.SetTag("peer.service", "Stripe");
            stripe?.SetTag("service.name", "eShop.BillingService");
            await Task.Delay(2000);
            stripe?.SetTag("http.status_code", 503);
            stripe?.SetStatus(ActivityStatusCode.Error, "Stripe unavailable");
            stripe?.AddException(new HttpRequestException("Stripe API returned 503"));
            billLog.LogError("Stripe API unavailable: 503 Service Unavailable");
        }

        billSvc?.SetStatus(ActivityStatusCode.Error, "Failed to create Stripe customer");
        throw new InvalidOperationException("Billing setup failed: Stripe unavailable");
    }
    catch (Exception ex)
    {
        api?.SetTag("http.status_code", 500);
        api?.SetStatus(ActivityStatusCode.Error, ex.Message);
        apiLog.LogError(ex, "Registration partially failed: billing setup error for frank@example.com");
    }
}

// Flush
Console.WriteLine("\n--> Flushing...");
await Task.Delay(500);
tracerProvider?.ForceFlush(5000);

// ILoggerFactory doesn't have Flush — disposal handles it
if (loggerFactory is IDisposable disposable) disposable.Dispose();

Console.WriteLine("\n[OK] All traces and logs exported to LogDB.");
Console.WriteLine("    ILogger logs carry TraceId/SpanId automatically via Activity.Current.");
Console.WriteLine("    Open Trace Explorer and paste a Trace ID to see the span tree.");
