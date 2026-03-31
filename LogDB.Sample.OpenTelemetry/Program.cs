using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Threading.Tasks;
using LogDB.Client.Models;
using LogDB.OpenTelemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace LogDB.Sample.OpenTelemetry;

internal static class Program
{
    private static readonly ActivitySource ActivitySource = new("com.logdb.samples.ecommerce");
    private static readonly Meter Meter = new("com.logdb.samples.ecommerce", "1.0.0");
    
    private static readonly Counter<long> OrdersProcessed = Meter.CreateCounter<long>(
        "ecommerce.orders.processed",
        unit: "orders",
        description: "Number of orders processed successfully or failed");

    private static readonly Histogram<double> CheckoutDuration = Meter.CreateHistogram<double>(
        "ecommerce.checkout.duration_ms",
        unit: "ms",
        description: "Duration of the checkout flow");

    public static async Task Main(string[] args)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("======================================");
        Console.WriteLine("== LogDB OpenTelemetry Writer Demo == ");
        Console.WriteLine("======================================");
        Console.ResetColor();
        
        Console.WriteLine("This sample sends traces, metrics, and logs (no read/query path).");

        var serviceName = "eShop.CheckoutAPI";
        var envName = "development";

        var apiKey = GetRequiredApiKey();

        Console.WriteLine("Writer endpoint is auto-discovered by LogDB SDK.");
        Console.WriteLine();
        PrintStep("Configuring OpenTelemetry pipelines...");

        // 1. STANDARD OPENTELEMETRY SETUP
        var resourceBuilder = ResourceBuilder.CreateDefault()
            .AddService(serviceName, serviceVersion: "2.5.0")
            .AddTelemetrySdk()
            .AddAttributes(new[]
            {
                new KeyValuePair<string, object>("deployment.environment", envName),
                new KeyValuePair<string, object>("host.name", Environment.MachineName)
            });

        var services = new ServiceCollection();

        services.AddLogging(logging =>
        {
            logging.ClearProviders();
            logging.AddConsole();
            
            logging.AddOpenTelemetry(options =>
            {
                options.IncludeScopes = true;
                options.IncludeFormattedMessage = true;
                options.ParseStateValues = true;
                
                // --- LogDB Drop-In ---
                options.AddLogDBExporter(logDbOptions => {
                    logDbOptions.ApiKey = apiKey;
                    logDbOptions.DefaultCollection = "eshop-logs";
                    logDbOptions.EnableDebugLogging = true;
                });
            });
        });

        services.AddOpenTelemetry()
            .ConfigureResource(resource =>
            {
                resource.AddService(serviceName, serviceVersion: "2.5.0");
                resource.AddAttributes(new[]
                {
                    new KeyValuePair<string, object>("deployment.environment", envName)
                });
            })
            .WithTracing(tracing =>
            {
                tracing.AddSource(ActivitySource.Name);
                
                // --- LogDB Drop-In ---
                tracing.AddLogDBExporter(logDbOptions => {
                    logDbOptions.ApiKey = apiKey;
                    logDbOptions.DefaultCollection = "eshop-traces";
                    logDbOptions.EnableDebugLogging = true;
                });
            })
            .WithMetrics(metrics =>
            {
                metrics.AddMeter(Meter.Name);
                
                // --- LogDB Drop-In ---
                metrics.AddLogDBExporter(logDbOptions => {
                    logDbOptions.ApiKey = apiKey;
                    logDbOptions.DefaultCollection = "eshop-metrics";
                    logDbOptions.EnableDebugLogging = true;
                });
            });

        await using var provider = services.BuildServiceProvider();

        // 2. SIMULATE WORKLOAD
        var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("EShop.CheckoutWorker");
        var tracerProvider = provider.GetService<TracerProvider>();
        var meterProvider = provider.GetService<MeterProvider>();

        PrintStep("Simulating active shopping carts checking out...");
        
        var random = new Random();
        var users = new[] { "alice@example.com", "bob@example.com", "charlie@example.com" };

        for (int i = 0; i < 3; i++)
        {
            var userId = users[i];
            await SimulateEShopCheckoutAsync(userId, logger, random);
            await Task.Delay(500); // Simulate time between requests
        }

        Console.WriteLine("Telemetry queued. Flushing all three signal types...");

        await Task.Delay(TimeSpan.FromSeconds(1));

        // Flush logs — OTel's BatchLogRecordExportProcessor holds ILogger logs until flushed.
        // Without this, log records are silently dropped on exit.
        var loggerFactory = provider.GetRequiredService<ILoggerFactory>();
        if (loggerFactory is IDisposable disposableLoggerFactory)
        {
            disposableLoggerFactory.Dispose();
        }

        tracerProvider?.ForceFlush(5000);
        meterProvider?.ForceFlush(5000);

        Console.WriteLine("Flush completed.");
        Console.WriteLine();

        PrintStep("Infrastructure builder demos are not included in the GitHub package build used by this sample.");
    }

    private static string GetRequiredApiKey()
    {
        var apiKey = Environment.GetEnvironmentVariable("LOGDB_API_KEY", EnvironmentVariableTarget.User);
        if (!string.IsNullOrWhiteSpace(apiKey))
            return apiKey;

        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine("Error: LOGDB_API_KEY environment variable is required.");
        Console.ResetColor();
        Console.WriteLine("Set LOGDB_API_KEY and rerun the sample.");
        Environment.Exit(1);
        return string.Empty;
    }

    private static void PrintStep(string message)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.Write("--> ");
        Console.ResetColor();
        Console.WriteLine(message);
    }

    private static async Task SimulateEShopCheckoutAsync(string userId, ILogger logger, Random random)
    {
        var cartId = $"CART-{Guid.NewGuid().ToString().Substring(0, 6).ToUpper()}";
        var orderId = $"ORD-{random.Next(10000, 99999)}";
        var stopwatch = Stopwatch.StartNew();

        // 1. Root Span for the entire Web Request
        using var requestSpan = ActivitySource.StartActivity("POST /api/checkout", ActivityKind.Server);
        requestSpan?.SetTag("http.method", "POST");
        requestSpan?.SetTag("http.url", "/api/checkout");
        requestSpan?.SetTag("user.email", userId);
        
        // Use OTel Baggage to propagate contextual data down to all child spans automatically
        Activity.Current?.AddBaggage("cart.id", cartId);
        Activity.Current?.AddBaggage("user.id", userId);

        // This log will automatically have TraceId and SpanId attached by the OTel Logger integration!
        using (logger.BeginScope(new Dictionary<string, object> { ["CartId"] = cartId }))
        {
            logger.LogInformation("Incoming checkout request for user {UserId}", userId);

            try
            {
                await ValidateCartContentsAsync(cartId, logger, random);
                await ProcessPaymentAsync(orderId, userId, logger, random);
                await DispatchToWarehouseAsync(orderId, logger, random);

                requestSpan?.SetStatus(ActivityStatusCode.Ok);
                logger.LogInformation("Checkout completed successfully. Order {OrderId} created.", orderId);
                
                OrdersProcessed.Add(1, new KeyValuePair<string, object?>("status", "success"));
            }
            catch (Exception ex)
            {
                requestSpan?.SetStatus(ActivityStatusCode.Error, "Checkout aborted due to failure");
                requestSpan?.AddException(ex);
                
                logger.LogError(ex, "Checkout workflow failed for cart {CartId}. Transaction rolled back.", cartId);
                OrdersProcessed.Add(1, new KeyValuePair<string, object?>("status", "failed"));
            }
            finally
            {
                stopwatch.Stop();
                CheckoutDuration.Record(stopwatch.ElapsedMilliseconds, new KeyValuePair<string, object?>("endpoint", "/api/checkout"));
            }
        }
    }

    private static async Task ValidateCartContentsAsync(string cartId, ILogger logger, Random random)
    {
        // Child Span 1
        using var span = ActivitySource.StartActivity("ValidateCart", ActivityKind.Internal);
        
        logger.LogDebug("Fetching shopping cart state from Redis cache");
        await Task.Delay(random.Next(20, 50)); 
        
        int itemCount = random.Next(1, 4);
        span?.SetTag("cart.items", itemCount);

        if (itemCount == 0)
        {
            throw new InvalidOperationException("Shopping cart is empty.");
        }

        logger.LogInformation("Cart {CartId} contains {Count} items. Proceeding.", cartId, itemCount);
    }

    private static async Task ProcessPaymentAsync(string orderId, string userId, ILogger logger, Random random)
    {
        // Child Span 2 - representing a remote call
        using var span = ActivitySource.StartActivity("Stripe /v1/charges", ActivityKind.Client);
        span?.SetTag("peer.service", "Stripe");
        span?.SetTag("order.id", orderId);
        
        logger.LogInformation("Initiating credit card charge for order {OrderId}", orderId);
        await Task.Delay(random.Next(100, 300));

        if (random.NextDouble() > 0.8) // 20% decline rate
        {
            logger.LogWarning("Credit card was declined by gateway for {UserId}", userId);
            throw new InvalidOperationException("Payment declined by bank.");
        }

        span?.SetTag("payment.status", "succeeded");
        span?.SetTag("payment.transaction_id", $"ch_{Guid.NewGuid().ToString().Replace("-", "")}");
        
        logger.LogInformation("Payment captured successfully for {OrderId}", orderId);
    }

    private static async Task DispatchToWarehouseAsync(string orderId, ILogger logger, Random random)
    {
        // Child Span 3 - asynchronous queuing
        using var span = ActivitySource.StartActivity("Publish order.created event", ActivityKind.Producer);
        span?.SetTag("messaging.system", "rabbitmq");
        span?.SetTag("messaging.destination", "orders.exchange");
        
        logger.LogDebug("Serializing order details and publishing to message broker");
        await Task.Delay(random.Next(10, 40));
        
        logger.LogInformation("Order {OrderId} dispatched to warehouse queue", orderId);
    }
}






