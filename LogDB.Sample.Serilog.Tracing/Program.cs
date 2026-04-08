using System.Diagnostics;
using LogDB.OpenTelemetry;
using LogDB.Serilog;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Context;

/// <summary>
/// Distributed Tracing with Serilog + LogDB
///
/// Shows that you can use Serilog for logging and still get full distributed
/// tracing. Spans are created with System.Diagnostics.ActivitySource (built
/// into .NET) and exported via AddLogDBExporter(). Serilog automatically
/// picks up TraceId/SpanId from Activity.Current.
///
/// Architecture:
///   API Gateway → Order Service → Payment Service (with Stripe)
///                               → Shipping Service (with DB query)
/// </summary>

var apiKey = Environment.GetEnvironmentVariable("LOGDB_API_KEY", EnvironmentVariableTarget.User) ?? "demo-key";

// ── ActivitySources (one per service) ────────────────────────
var gatewaySource = new ActivitySource("eShop.Gateway");
var orderSource = new ActivitySource("eShop.OrderService");
var paymentSource = new ActivitySource("eShop.PaymentService");
var shippingSource = new ActivitySource("eShop.ShippingService");

// ── Serilog: logs go to console + LogDB ──────────────────────
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .Enrich.FromLogContext()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss.fff} {Level:u3}] {Message:lj}{NewLine}")
    .WriteTo.LogDB(options =>
    {
        options.ApiKey = apiKey;
        options.DefaultApplication = "eShop.Serilog.Tracing";
        options.DefaultEnvironment = "demo";
        options.DefaultCollection = "serilog-tracing-demo";
        options.EnableBatching = true;
        options.BatchSize = 200;
        options.FlushInterval = TimeSpan.FromSeconds(2);
    })
    .CreateLogger();

// ── OpenTelemetry: traces exported to LogDB ──────────────────
// Note: we only configure tracing here, NOT OTel logging.
// Logs go through Serilog, traces go through OTel — they share
// the same TraceId via Activity.Current.
var services = new ServiceCollection();
services.AddOpenTelemetry()
    .ConfigureResource(r =>
    {
        r.AddService("eShop.Gateway", serviceVersion: "1.0.0");
        r.AddAttributes(new[] { new KeyValuePair<string, object>("deployment.environment", "demo") });
    })
    .WithTracing(tracing =>
    {
        tracing.AddSource(gatewaySource.Name);
        tracing.AddSource(orderSource.Name);
        tracing.AddSource(paymentSource.Name);
        tracing.AddSource(shippingSource.Name);
        tracing.AddLogDBExporter(o =>
        {
            o.ApiKey = apiKey;
            o.DefaultCollection = "serilog-tracing-demo";
            o.EnableDebugLogging = true;
        });
    });

await using var provider = services.BuildServiceProvider();
var tracerProvider = provider.GetService<TracerProvider>();

// ── Run scenarios ────────────────────────────────────────────

Console.WriteLine("\n== Serilog + Distributed Tracing ==\n");

// Scenario 1: Successful order
Console.WriteLine("--> Scenario 1: Successful order with shipping");
using (var gw = gatewaySource.StartActivity("POST /api/orders", ActivityKind.Server))
{
    gw?.SetTag("http.method", "POST");
    gw?.SetTag("http.route", "/api/orders");
    gw?.SetTag("service.name", "eShop.Gateway");
    var traceId = Activity.Current?.TraceId.ToString();
    Console.WriteLine($"    Trace ID: {traceId}");

    Log.Information("Incoming order request from {User}", "alice@example.com");

    // Gateway → Order Service
    using (var orderCall = gatewaySource.StartActivity("gRPC OrderService/Create", ActivityKind.Client))
    {
        orderCall?.SetTag("rpc.system", "grpc");
        orderCall?.SetTag("service.name", "eShop.Gateway");

        using (var order = orderSource.StartActivity("CreateOrder", ActivityKind.Server))
        {
            order?.SetTag("order.id", "ORD-5001");
            order?.SetTag("service.name", "eShop.OrderService");

            using (LogContext.PushProperty("OrderId", "ORD-5001"))
            {
                Log.Information("Processing order {OrderId}", "ORD-5001");

                // Order → Payment
                using (var payCall = orderSource.StartActivity("gRPC PaymentService/Charge", ActivityKind.Client))
                {
                    payCall?.SetTag("service.name", "eShop.OrderService");

                    using (var pay = paymentSource.StartActivity("ChargeCard", ActivityKind.Server))
                    {
                        pay?.SetTag("payment.amount", 199.99);
                        pay?.SetTag("service.name", "eShop.PaymentService");
                        Log.Information("Charging EUR {Amount} for order {OrderId}", 199.99, "ORD-5001");

                        using (var stripe = paymentSource.StartActivity("POST stripe.com/v1/charges", ActivityKind.Client))
                        {
                            stripe?.SetTag("peer.service", "Stripe");
                            stripe?.SetTag("service.name", "eShop.PaymentService");
                            await Task.Delay(120);
                            stripe?.SetTag("http.status_code", 200);
                            Log.Information("Stripe charge succeeded");
                        }
                        pay?.SetStatus(ActivityStatusCode.Ok);
                    }
                }

                // Order → Shipping (parallel)
                using (var shipCall = orderSource.StartActivity("gRPC ShippingService/Schedule", ActivityKind.Client))
                {
                    shipCall?.SetTag("service.name", "eShop.OrderService");

                    using (var ship = shippingSource.StartActivity("ScheduleShipment", ActivityKind.Server))
                    {
                        ship?.SetTag("shipping.carrier", "DHL");
                        ship?.SetTag("shipping.warehouse", "WH-EU-01");
                        ship?.SetTag("service.name", "eShop.ShippingService");
                        Log.Information("Scheduling shipment from {Warehouse}", "WH-EU-01");

                        using (var db = shippingSource.StartActivity("SELECT * FROM shipments", ActivityKind.Client))
                        {
                            db?.SetTag("db.system", "postgresql");
                            db?.SetTag("db.name", "shipping_db");
                            db?.SetTag("service.name", "eShop.ShippingService");
                            await Task.Delay(30);
                        }

                        using (var db2 = shippingSource.StartActivity("INSERT INTO shipments", ActivityKind.Client))
                        {
                            db2?.SetTag("db.system", "postgresql");
                            db2?.SetTag("db.name", "shipping_db");
                            db2?.SetTag("service.name", "eShop.ShippingService");
                            await Task.Delay(15);
                        }

                        Log.Information("Shipment scheduled: tracking {TrackingId}", "DHL-EU-99001");
                        ship?.SetStatus(ActivityStatusCode.Ok);
                    }
                }

                Log.Information("Order {OrderId} completed successfully", "ORD-5001");
                order?.SetStatus(ActivityStatusCode.Ok);
            }
        }
    }
    gw?.SetTag("http.status_code", 200);
    gw?.SetStatus(ActivityStatusCode.Ok);
}

await Task.Delay(300);

// Scenario 2: Payment failure
Console.WriteLine("\n--> Scenario 2: Payment declined — error propagation");
using (var gw = gatewaySource.StartActivity("POST /api/orders", ActivityKind.Server))
{
    gw?.SetTag("http.method", "POST");
    gw?.SetTag("service.name", "eShop.Gateway");
    Console.WriteLine($"    Trace ID: {Activity.Current?.TraceId}");

    Log.Information("Incoming order from {User}", "bob@example.com");
    try
    {
        using (var orderCall = gatewaySource.StartActivity("gRPC OrderService/Create", ActivityKind.Client))
        {
            orderCall?.SetTag("service.name", "eShop.Gateway");

            using (var order = orderSource.StartActivity("CreateOrder", ActivityKind.Server))
            {
                order?.SetTag("order.id", "ORD-5002");
                order?.SetTag("service.name", "eShop.OrderService");
                Log.Information("Processing order {OrderId}", "ORD-5002");

                using (var payCall = orderSource.StartActivity("gRPC PaymentService/Charge", ActivityKind.Client))
                {
                    payCall?.SetTag("service.name", "eShop.OrderService");

                    using (var pay = paymentSource.StartActivity("ChargeCard", ActivityKind.Server))
                    {
                        pay?.SetTag("service.name", "eShop.PaymentService");

                        using (var stripe = paymentSource.StartActivity("POST stripe.com/v1/charges", ActivityKind.Client))
                        {
                            stripe?.SetTag("peer.service", "Stripe");
                            stripe?.SetTag("service.name", "eShop.PaymentService");
                            await Task.Delay(180);
                            stripe?.SetTag("http.status_code", 402);
                            stripe?.SetStatus(ActivityStatusCode.Error, "Card declined");
                            Log.Error("Stripe declined: insufficient_funds for {OrderId}", "ORD-5002");
                        }
                        pay?.SetStatus(ActivityStatusCode.Error, "Payment declined");
                        throw new InvalidOperationException("Payment declined");
                    }
                }
            }
        }
    }
    catch (Exception ex)
    {
        gw?.SetTag("http.status_code", 500);
        gw?.SetStatus(ActivityStatusCode.Error, ex.Message);
        Log.Error(ex, "Order failed for bob@example.com");
    }
}

// Flush
Console.WriteLine("\n--> Flushing...");
await Task.Delay(500);
Log.CloseAndFlush();
tracerProvider?.ForceFlush(5000);

Console.WriteLine("\n[OK] All traces and logs exported to LogDB.");
Console.WriteLine("    Serilog logs carry TraceId/SpanId automatically.");
Console.WriteLine("    Open Trace Explorer and paste a Trace ID to see the span tree.");
