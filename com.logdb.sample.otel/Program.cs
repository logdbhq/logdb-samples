using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Threading.Tasks;
using LogDB.Client.Models;
using LogDB.OpenTelemetry;
using com.logdb.logger.LogBuilders;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace com.logdb.sample.otel;

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

    private static readonly Counter<long> PaymentAttempts = Meter.CreateCounter<long>(
        "ecommerce.payments.attempts",
        unit: "attempts",
        description: "Payment gateway call count by outcome");

    private static readonly Histogram<double> InventoryLookupDuration = Meter.CreateHistogram<double>(
        "ecommerce.inventory.lookup_ms",
        unit: "ms",
        description: "Inventory service response time");

    public static async Task Main(string[] args)
    {
        WriteSection("LogDB OpenTelemetry Writer Demo");
        WriteInfo("This sample sends traces, metrics, and logs (no read/query path).");
        WriteInfo("Writer endpoint is auto-discovered by LogDB SDK.");

        var serviceName = "eShop.CheckoutAPI";
        var envName = "development";

        string apiKey = Environment.GetEnvironmentVariable("LOGDB_API_KEY", EnvironmentVariableTarget.User);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            WriteError("Warning: LOGDB_API_KEY environment variable is not set. Using 'dummy-key'.");
            apiKey = "dummy-key";
        }

        WriteStep("Configuring OpenTelemetry pipelines (logs + traces + metrics)...");

        // ── STANDARD OPENTELEMETRY SETUP ──────────────────────────
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

        var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("EShop.CheckoutWorker");
        var tracerProvider = provider.GetService<TracerProvider>();
        var meterProvider = provider.GetService<MeterProvider>();

        // ── SCENARIO 1: Successful checkout (Alice) ──────────────
        WriteStep("Scenario 1: Successful checkout — alice@example.com");
        await SimulateCheckoutAsync("alice@example.com", "CART-A1B2C3", "ORD-50001", logger,
            paymentOutcome: PaymentOutcome.Success, inventorySlow: false);

        await Task.Delay(300);

        // ── SCENARIO 2: Payment declined (Bob) ───────────────────
        WriteStep("Scenario 2: Payment declined — bob@example.com");
        await SimulateCheckoutAsync("bob@example.com", "CART-D4E5F6", "ORD-50002", logger,
            paymentOutcome: PaymentOutcome.Declined, inventorySlow: false);

        await Task.Delay(300);

        // ── SCENARIO 3: Slow inventory + retry (Charlie) ─────────
        WriteStep("Scenario 3: Slow inventory lookup with warning — charlie@example.com");
        await SimulateCheckoutAsync("charlie@example.com", "CART-G7H8I9", "ORD-50003", logger,
            paymentOutcome: PaymentOutcome.Success, inventorySlow: true);

        await Task.Delay(300);

        // ── SCENARIO 4: Gateway timeout (Diana) ──────────────────
        WriteStep("Scenario 4: Payment gateway timeout — diana@example.com");
        await SimulateCheckoutAsync("diana@example.com", "CART-J0K1L2", "ORD-50004", logger,
            paymentOutcome: PaymentOutcome.Timeout, inventorySlow: false);

        await Task.Delay(300);

        // ── SCENARIO 5: Successful high-value order (Eve) ────────
        WriteStep("Scenario 5: High-value order with fraud check — eve@example.com");
        await SimulateCheckoutAsync("eve@example.com", "CART-M3N4O5", "ORD-50005", logger,
            paymentOutcome: PaymentOutcome.SuccessWithFraudCheck, inventorySlow: false);

        // ── FLUSH ────────────────────────────────────────────────
        WriteStep("Flushing all three signal types...");

        await Task.Delay(TimeSpan.FromSeconds(1));

        var loggerFactory = provider.GetRequiredService<ILoggerFactory>();
        if (loggerFactory is IDisposable disposableLoggerFactory)
        {
            disposableLoggerFactory.Dispose();
        }

        tracerProvider?.ForceFlush(5000);
        meterProvider?.ForceFlush(5000);

        WriteSuccess("Flush completed. All traces, metrics, and logs exported to LogDB.");
        Console.WriteLine();

        // ── INFRASTRUCTURE LOG TYPES ─────────────────────────────
        WriteStep("Sending infrastructure log types via SDK builders...");

        LogDockerEventBuilder.ApiKey = apiKey;
        LogDockerMetricBuilder.ApiKey = apiKey;
        LogIISEventBuilder.ApiKey = apiKey;
        LogNginxEventBuilder.ApiKey = apiKey;
        LogWindowsEventBuilder.ApiKey = apiKey;
        LogWindowsMetricBuilder.ApiKey = apiKey;

        await LogDockerEventBuilder.Create()
            .SetContainerName("eshop-checkout-api")
            .SetImage("eshop/checkout:2.5.0")
            .SetStream("stdout")
            .SetLevel("Info")
            .SetMessage("Checkout service started on port 8080")
            .SetHostName(Environment.MachineName)
            .SetComposeProject("eshop")
            .SetComposeService("checkout")
            .SetCollection("eshop-docker-events")
            .Log();

        await Task.Delay(Random.Shared.Next(80, 250));
        await LogDockerEventBuilder.Create()
            .SetContainerName("eshop-checkout-api")
            .SetImage("eshop/checkout:2.5.0")
            .SetStream("stderr")
            .SetLevel("Warning")
            .SetMessage("High memory pressure detected — GC pause 120ms")
            .SetHostName(Environment.MachineName)
            .SetComposeProject("eshop")
            .SetComposeService("checkout")
            .SetCollection("eshop-docker-events")
            .Log();

        await Task.Delay(Random.Shared.Next(80, 250));
        await LogDockerMetricBuilder.Create()
            .SetContainerName("eshop-checkout-api")
            .SetImage("eshop/checkout")
            .SetImageTag("2.5.0")
            .SetHostName(Environment.MachineName)
            .SetContainerState("running")
            .SetCpuUsagePercent(67.4)
            .SetMemoryUsageBytes(402_653_184)
            .SetMemoryLimitBytes(536_870_912)
            .SetNetworkRxBytes(12_582_912)
            .SetNetworkTxBytes(8_388_608)
            .SetPidsCurrent(14)
            .SetCollection("eshop-docker-metrics")
            .Log();

        await Task.Delay(Random.Shared.Next(80, 250));
        await LogNginxEventBuilder.Create()
            .SetLogType("access")
            .SetMethod("POST")
            .SetPath("/api/checkout")
            .SetStatusCode(200)
            .SetRequestTime(0.312)
            .SetRemoteAddress("203.0.113.42")
            .SetServerName("eshop.example.com")
            .SetUserAgent("Mozilla/5.0 (eShop Mobile App)")
            .SetCollection("eshop-nginx-events")
            .Log();

        await Task.Delay(Random.Shared.Next(80, 250));
        await LogNginxEventBuilder.Create()
            .SetLogType("access")
            .SetMethod("POST")
            .SetPath("/api/checkout")
            .SetStatusCode(504)
            .SetRequestTime(30.001)
            .SetRemoteAddress("198.51.100.17")
            .SetServerName("eshop.example.com")
            .SetUserAgent("Mozilla/5.0 (eShop Web)")
            .SetCollection("eshop-nginx-events")
            .Log();

        await Task.Delay(Random.Shared.Next(80, 250));
        await LogIISEventBuilder.Create()
            .SetMethod("POST")
            .SetUriStem("/api/checkout")
            .SetUriQuery("cartId=CART-A1B2C3")
            .SetStatus(200)
            .SetTimeTaken(312)
            .SetClientIp("203.0.113.42")
            .SetServerIp("10.10.5.10")
            .SetPort(443)
            .SetHost("eshop.example.com")
            .SetUserAgent("Mozilla/5.0 (eShop Mobile App)")
            .SetSiteName("eShop Checkout")
            .SetServerName("ESHOP-WEB-01")
            .SetBytesSent(847)
            .SetCollection("eshop-iis-events")
            .Log();

        await Task.Delay(Random.Shared.Next(80, 250));
        await LogIISEventBuilder.Create()
            .SetMethod("GET")
            .SetUriStem("/api/inventory/check")
            .SetStatus(504)
            .SetTimeTaken(30001)
            .SetClientIp("198.51.100.17")
            .SetServerIp("10.10.5.10")
            .SetPort(443)
            .SetHost("eshop.example.com")
            .SetUserAgent("Mozilla/5.0 (eShop Web)")
            .SetSiteName("eShop Checkout")
            .SetServerName("ESHOP-WEB-01")
            .SetCollection("eshop-iis-events")
            .Log();

        await Task.Delay(Random.Shared.Next(80, 250));
        await LogWindowsEventBuilder.Create()
            .SetEventId(4624)
            .SetProviderName("Microsoft-Windows-Security-Auditing")
            .SetChannel("Security")
            .SetLevel("Information")
            .SetComputer("ESHOP-WEB-01.corp.local")
            .SetMessage("An account was successfully logged on.")
            .SetUserId("CORP\\svc-checkout")
            .SetIpAddress("10.10.5.12")
            .SetCollection("eshop-windows-events")
            .Log();

        await Task.Delay(Random.Shared.Next(80, 250));
        await LogWindowsEventBuilder.Create()
            .SetEventId(4625)
            .SetProviderName("Microsoft-Windows-Security-Auditing")
            .SetChannel("Security")
            .SetLevel("Warning")
            .SetComputer("ESHOP-WEB-01.corp.local")
            .SetMessage("An account failed to log on. Reason: Unknown user name or bad password.")
            .SetUserId("CORP\\admin")
            .SetIpAddress("198.51.100.44")
            .SetCollection("eshop-windows-events")
            .Log();

        await Task.Delay(Random.Shared.Next(80, 250));
        await LogWindowsMetricBuilder.Create()
            .SetMeasurement("memory")
            .SetServerName(Environment.MachineName)
            .SetMemoryTotalGb(16.0)
            .SetMemoryUsedGb(13.8)
            .SetMemoryFreeGb(2.2)
            .SetMemoryUsagePercent(86.3)
            .SetCollection("eshop-host-metrics")
            .Log();

        WriteSuccess("Infrastructure log types sent (including warnings).");

        Console.WriteLine();
        WriteSection("Sample Complete");
        WriteInfo("Open LogDB to see correlated traces, metrics, and logs.");
        WriteInfo("Filter by CorrelationId to follow a single request across all signals.");
    }

    // ── Checkout simulation ──────────────────────────────────────

    private enum PaymentOutcome { Success, Declined, Timeout, SuccessWithFraudCheck }

    private static async Task SimulateCheckoutAsync(
        string userId, string cartId, string orderId,
        ILogger logger, PaymentOutcome paymentOutcome, bool inventorySlow)
    {
        var stopwatch = Stopwatch.StartNew();

        using var requestSpan = ActivitySource.StartActivity("POST /api/checkout", ActivityKind.Server);
        requestSpan?.SetTag("http.method", "POST");
        requestSpan?.SetTag("http.url", "/api/checkout");
        requestSpan?.SetTag("http.route", "/api/checkout");
        requestSpan?.SetTag("user.email", userId);
        requestSpan?.SetTag("cart.id", cartId);
        requestSpan?.SetTag("order.id", orderId);

        var correlationId = Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString();

        Activity.Current?.AddBaggage("cart.id", cartId);
        Activity.Current?.AddBaggage("user.id", userId);

        using (logger.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = correlationId,
            ["CartId"] = cartId,
            ["OrderId"] = orderId,
            ["UserId"] = userId
        }))
        {
            // ── Middleware logs (every request gets these) ────────
            logger.LogDebug("Rate limiter passed for {UserId}: 12/100 requests in current window", userId);
            logger.LogInformation("Incoming checkout request for user {UserId}, cart {CartId}", userId, cartId);
            logger.LogDebug("JWT token validated. Claims: sub={UserId}, role=customer, iss=auth.eshop.example.com", userId);

            try
            {
                await ValidateCartAsync(cartId, logger);
                await CheckInventoryAsync(orderId, logger, inventorySlow);
                await ProcessPaymentAsync(orderId, userId, logger, paymentOutcome);
                await DispatchToWarehouseAsync(orderId, logger);
                await SendConfirmationEmailAsync(orderId, userId, logger);

                requestSpan?.SetTag("http.status_code", 200);
                requestSpan?.SetStatus(ActivityStatusCode.Ok);
                logger.LogInformation("Checkout completed successfully. Order {OrderId} created for {UserId}.", orderId, userId);
                logger.LogDebug("Response serialized in 2ms. Content-Length: 847 bytes.");

                OrdersProcessed.Add(1,
                    new KeyValuePair<string, object?>("status", "success"),
                    new KeyValuePair<string, object?>("user.region", "eu-west-1"));
            }
            catch (TimeoutException ex)
            {
                requestSpan?.SetTag("http.status_code", 504);
                requestSpan?.SetStatus(ActivityStatusCode.Error, "Gateway timeout");
                requestSpan?.AddException(ex);

                logger.LogError(ex, "Checkout timed out for cart {CartId}. Downstream service unavailable.", cartId);
                logger.LogWarning("Initiating compensating transaction: releasing held inventory for {OrderId}", orderId);
                OrdersProcessed.Add(1,
                    new KeyValuePair<string, object?>("status", "timeout"),
                    new KeyValuePair<string, object?>("user.region", "eu-west-1"));
            }
            catch (InvalidOperationException ex)
            {
                requestSpan?.SetTag("http.status_code", 422);
                requestSpan?.SetStatus(ActivityStatusCode.Error, "Checkout aborted");
                requestSpan?.AddException(ex);

                logger.LogError(ex, "Checkout workflow failed for cart {CartId}. Transaction rolled back.", cartId);
                logger.LogWarning("Compensating transaction started: reversing reservation for {OrderId}", orderId);
                OrdersProcessed.Add(1,
                    new KeyValuePair<string, object?>("status", "failed"),
                    new KeyValuePair<string, object?>("user.region", "eu-west-1"));
            }
            finally
            {
                stopwatch.Stop();
                CheckoutDuration.Record(stopwatch.ElapsedMilliseconds,
                    new KeyValuePair<string, object?>("endpoint", "/api/checkout"));
                logger.LogDebug("Request completed in {DurationMs}ms", stopwatch.ElapsedMilliseconds);
            }
        }
    }

    // ── Child span: Cart validation ──────────────────────────────

    private static async Task ValidateCartAsync(string cartId, ILogger logger)
    {
        using var span = ActivitySource.StartActivity("ValidateCart", ActivityKind.Internal);
        span?.SetTag("cache.type", "redis");

        logger.LogDebug("Connecting to Redis cluster redis-primary.internal:6379");
        logger.LogDebug("Fetching cart {CartId} from Redis cache", cartId);
        await Task.Delay(35);

        int itemCount = 3;
        double cartTotal = 149.97;
        span?.SetTag("cart.items", itemCount);
        span?.SetTag("cart.total", cartTotal);

        logger.LogInformation("Cart {CartId} validated: {ItemCount} items, total EUR {Total:F2}", cartId, itemCount, cartTotal);
        logger.LogDebug("Cart item SKUs: [SKU-1001, SKU-2047, SKU-3082]. All active and purchasable.");
    }

    // ── Child span: Inventory check ──────────────────────────────

    private static async Task CheckInventoryAsync(string orderId, ILogger logger, bool simulateSlow)
    {
        using var span = ActivitySource.StartActivity("CheckInventory", ActivityKind.Client);
        span?.SetTag("peer.service", "InventoryService");
        span?.SetTag("rpc.system", "grpc");
        span?.SetTag("rpc.method", "CheckAvailability");

        var sw = Stopwatch.StartNew();

        logger.LogDebug("Opening gRPC channel to inventory-service.internal:5050");
        logger.LogDebug("Checking stock availability for 3 SKUs, order {OrderId}", orderId);

        if (simulateSlow)
        {
            logger.LogDebug("Awaiting inventory response for order {OrderId}...", orderId);
            await Task.Delay(2800);
            sw.Stop();

            span?.SetTag("inventory.latency_ms", sw.ElapsedMilliseconds);
            span?.AddEvent(new ActivityEvent("SlowResponse", tags: new ActivityTagsCollection
            {
                { "latency_ms", sw.ElapsedMilliseconds },
                { "threshold_ms", 2000 }
            }));

            logger.LogWarning(
                "Inventory service responded slowly ({LatencyMs}ms > 2000ms threshold) for order {OrderId}. Consider scaling inventory pods.",
                sw.ElapsedMilliseconds, orderId);
            logger.LogWarning("SLA breach: inventory P99 target is 500ms, observed {LatencyMs}ms", sw.ElapsedMilliseconds);
        }
        else
        {
            await Task.Delay(45);
            sw.Stop();
            span?.SetTag("inventory.latency_ms", sw.ElapsedMilliseconds);
        }

        InventoryLookupDuration.Record(sw.ElapsedMilliseconds,
            new KeyValuePair<string, object?>("order.id", orderId));

        span?.SetTag("inventory.in_stock", true);
        logger.LogInformation("All 3 items in stock for order {OrderId}. Warehouse: WH-EU-01.", orderId);
    }

    // ── Child span: Payment processing ───────────────────────────

    private static async Task ProcessPaymentAsync(string orderId, string userId, ILogger logger, PaymentOutcome outcome)
    {
        using var span = ActivitySource.StartActivity("Stripe /v1/charges", ActivityKind.Client);
        span?.SetTag("peer.service", "Stripe");
        span?.SetTag("order.id", orderId);
        span?.SetTag("payment.currency", "EUR");
        span?.SetTag("payment.amount", 149.97);

        logger.LogDebug("Resolving payment method for user {UserId}: card ending ****4242", userId);
        logger.LogInformation("Initiating credit card charge for order {OrderId}, amount EUR 149.97", orderId);

        switch (outcome)
        {
            case PaymentOutcome.Declined:
                await Task.Delay(180);
                span?.SetTag("payment.status", "declined");
                span?.SetTag("payment.decline_code", "insufficient_funds");
                PaymentAttempts.Add(1,
                    new KeyValuePair<string, object?>("status", "declined"),
                    new KeyValuePair<string, object?>("gateway", "stripe"));
                logger.LogWarning("Credit card declined for user {UserId}: insufficient_funds (card ****4242)", userId);
                logger.LogWarning("Retry policy exhausted: 0 of 0 retries configured for payment declines");
                throw new InvalidOperationException("Payment declined by bank: insufficient_funds.");

            case PaymentOutcome.Timeout:
                logger.LogDebug("Waiting for Stripe /v1/charges response...");
                await Task.Delay(5200);
                span?.SetTag("payment.status", "timeout");
                span?.AddEvent(new ActivityEvent("GatewayTimeout"));
                PaymentAttempts.Add(1,
                    new KeyValuePair<string, object?>("status", "timeout"),
                    new KeyValuePair<string, object?>("gateway", "stripe"));
                logger.LogError("Payment gateway timeout after 5000ms for order {OrderId}. Circuit breaker may open.", orderId);
                logger.LogError("Stripe API returned no response. Last known status: pending. Request ID: req_timeout_{OrderId}", orderId);
                throw new TimeoutException("Stripe /v1/charges timed out after 5000ms.");

            case PaymentOutcome.SuccessWithFraudCheck:
                await Task.Delay(120);

                using (var fraudSpan = ActivitySource.StartActivity("FraudCheck", ActivityKind.Internal))
                {
                    fraudSpan?.SetTag("fraud.provider", "Sift");
                    fraudSpan?.SetTag("fraud.score", 72);
                    fraudSpan?.SetTag("fraud.decision", "accept");
                    logger.LogDebug("Sending transaction to Sift fraud detection API");
                    logger.LogWarning("Fraud score elevated ({Score}/100) for order {OrderId}. Accepted with manual review flag.", 72, orderId);
                    logger.LogInformation("Fraud decision: ACCEPT with review. Reason: new device fingerprint + high-value order.");
                    await Task.Delay(95);
                }

                span?.SetTag("payment.status", "succeeded");
                span?.SetTag("payment.transaction_id", $"ch_{Guid.NewGuid().ToString("N")[..24]}");
                span?.SetTag("payment.fraud_reviewed", true);
                PaymentAttempts.Add(1,
                    new KeyValuePair<string, object?>("status", "success"),
                    new KeyValuePair<string, object?>("gateway", "stripe"));
                logger.LogInformation("Payment captured for {OrderId} (fraud-reviewed). Manual review queued.", orderId);
                break;

            default: // Success
                await Task.Delay(150);
                span?.SetTag("payment.status", "succeeded");
                span?.SetTag("payment.transaction_id", $"ch_{Guid.NewGuid().ToString("N")[..24]}");
                PaymentAttempts.Add(1,
                    new KeyValuePair<string, object?>("status", "success"),
                    new KeyValuePair<string, object?>("gateway", "stripe"));
                logger.LogInformation("Payment captured successfully for {OrderId}. Transaction confirmed.", orderId);
                break;
        }
    }

    // ── Child span: Warehouse dispatch ───────────────────────────

    private static async Task DispatchToWarehouseAsync(string orderId, ILogger logger)
    {
        using var span = ActivitySource.StartActivity("Publish order.created", ActivityKind.Producer);
        span?.SetTag("messaging.system", "rabbitmq");
        span?.SetTag("messaging.destination", "orders.exchange");
        span?.SetTag("messaging.operation", "publish");
        span?.SetTag("messaging.message_id", Guid.NewGuid().ToString("N")[..16]);

        logger.LogDebug("Connecting to RabbitMQ broker amqp://rabbitmq.internal:5672");
        logger.LogDebug("Serializing order {OrderId} payload (847 bytes)", orderId);
        await Task.Delay(25);

        logger.LogInformation("Order {OrderId} published to orders.exchange (routing key: order.created.eu)", orderId);
    }

    // ── Child span: Confirmation email ───────────────────────────

    private static async Task SendConfirmationEmailAsync(string orderId, string userId, ILogger logger)
    {
        using var span = ActivitySource.StartActivity("SendConfirmationEmail", ActivityKind.Client);
        span?.SetTag("peer.service", "SendGrid");
        span?.SetTag("email.to", userId);
        span?.SetTag("email.template", "order-confirmation");

        logger.LogDebug("Rendering email template 'order-confirmation' for {UserId}", userId);
        await Task.Delay(65);

        logger.LogInformation("Confirmation email queued via SendGrid for {UserId}, order {OrderId}", userId, orderId);
    }

    // ── Console helpers ──────────────────────────────────────────

    private static void WriteSection(string title)
    {
        var width = Math.Max(32, title.Length + 8);
        var border = new string('=', width);

        WriteWithColor($"\n{border}", ConsoleColor.Cyan);
        WriteWithColor($"== {title} ==", ConsoleColor.Cyan);
        WriteWithColor(border, ConsoleColor.Cyan);
    }

    private static void WriteStep(string message) => WriteWithColor($"--> {message}", ConsoleColor.Yellow);
    private static void WriteInfo(string message) => WriteWithColor(message, ConsoleColor.Gray);
    private static void WriteSuccess(string message) => WriteWithColor($"[OK] {message}", ConsoleColor.Green);
    private static void WriteError(string message) => WriteWithColor($"[!!] {message}", ConsoleColor.Red);

    private static void WriteWithColor(string message, ConsoleColor color)
    {
        var prev = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.WriteLine(message);
        Console.ForegroundColor = prev;
    }
}
