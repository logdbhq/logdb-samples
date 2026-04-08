using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Threading.Tasks;
using LogDB.OpenTelemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace com.logdb.sample.distributed_tracing;

/// <summary>
/// Distributed Tracing Sample for LogDB
///
/// Produces a rich, branching span tree that exercises the trace explorer:
///
///   API Gateway
///   ├── Auth Service  (JWT validation + Redis session lookup)
///   └── Order Service
///       ├── Cart Service  (Redis cache hit/miss + DB fallback)
///       ├── Inventory Service  (parallel stock checks per warehouse)
///       │   ├── Warehouse-EU  (DB query)
///       │   └── Warehouse-US  (DB query, slow)
///       ├── Pricing Service  (discount + tax, parallel)
///       │   ├── Discount Engine  (internal)
///       │   └── Tax Calculator  (external API)
///       ├── Payment Service
///       │   ├── Fraud Check  (ML scoring)
///       │   └── Stripe Charge  (external API, may fail + retry)
///       └── [async] Notification Service
///           ├── Email via SendGrid
///           └── Push via Firebase
/// </summary>
internal static class Program
{
    private static readonly ActivitySource GatewaySource = new("eShop.APIGateway");
    private static readonly ActivitySource AuthSource = new("eShop.AuthService");
    private static readonly ActivitySource OrderSource = new("eShop.OrderService");
    private static readonly ActivitySource CartSource = new("eShop.CartService");
    private static readonly ActivitySource InventorySource = new("eShop.InventoryService");
    private static readonly ActivitySource PricingSource = new("eShop.PricingService");
    private static readonly ActivitySource PaymentSource = new("eShop.PaymentService");
    private static readonly ActivitySource NotificationSource = new("eShop.NotificationService");

    private static readonly Meter OrderMeter = new("eShop.OrderService", "1.0.0");
    private static readonly Counter<long> OrdersCounter = OrderMeter.CreateCounter<long>("orders.total");

    public static async Task Main(string[] args)
    {
        WriteSection("LogDB Distributed Tracing Sample");
        WriteInfo("Rich multi-service traces with branching, parallelism, retries, and fan-out.");

        var apiKey = Environment.GetEnvironmentVariable("LOGDB_API_KEY", EnvironmentVariableTarget.User);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            WriteError("LOGDB_API_KEY not set. Using 'demo-key'.");
            apiKey = "demo-key";
        }

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
                options.AddLogDBExporter(o =>
                {
                    o.ApiKey = apiKey;
                    o.DefaultCollection = "distributed-tracing-demo";
                    o.EnableDebugLogging = true;
                });
            });
        });

        services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddAttributes(new[]
            {
                new KeyValuePair<string, object>("deployment.environment", "demo")
            }))
            .WithTracing(tracing =>
            {
                tracing.AddSource(GatewaySource.Name);
                tracing.AddSource(AuthSource.Name);
                tracing.AddSource(OrderSource.Name);
                tracing.AddSource(CartSource.Name);
                tracing.AddSource(InventorySource.Name);
                tracing.AddSource(PricingSource.Name);
                tracing.AddSource(PaymentSource.Name);
                tracing.AddSource(NotificationSource.Name);

                tracing.AddLogDBExporter(o =>
                {
                    o.ApiKey = apiKey;
                    o.DefaultCollection = "distributed-tracing-demo";
                    o.EnableDebugLogging = true;
                });
            })
            .WithMetrics(metrics =>
            {
                metrics.AddMeter(OrderMeter.Name);
                metrics.AddLogDBExporter(o =>
                {
                    o.ApiKey = apiKey;
                    o.DefaultCollection = "distributed-tracing-metrics";
                });
            });

        await using var provider = services.BuildServiceProvider();
        var loggerFactory = provider.GetRequiredService<ILoggerFactory>();
        var tracerProvider = provider.GetService<TracerProvider>();
        var meterProvider = provider.GetService<MeterProvider>();

        // ── Scenario 1: Full successful checkout (deep tree) ─────────
        WriteStep("Scenario 1: Full checkout — 8 services, parallel branches, cache + DB");
        await SimulateCheckout(loggerFactory, "alice@example.com", "CART-A1", "ORD-7001",
            cartCached: true, warehouseUsSlow: false, paymentSuccess: true, fraudScore: 15);
        await Task.Delay(400);

        // ── Scenario 2: Cache miss + slow warehouse + payment retry ──
        WriteStep("Scenario 2: Cache miss, slow US warehouse, payment retry then success");
        await SimulateCheckout(loggerFactory, "bob@example.com", "CART-B2", "ORD-7002",
            cartCached: false, warehouseUsSlow: true, paymentSuccess: true, fraudScore: 45);
        await Task.Delay(400);

        // ── Scenario 3: High fraud score → payment declined ──────────
        WriteStep("Scenario 3: High fraud score, payment blocked — error propagation");
        await SimulateCheckout(loggerFactory, "charlie@example.com", "CART-C3", "ORD-7003",
            cartCached: true, warehouseUsSlow: false, paymentSuccess: false, fraudScore: 92);

        // ── Flush ────────────────────────────────────────────────────
        WriteStep("Flushing all signals...");
        await Task.Delay(1000);
        if (loggerFactory is IDisposable d) d.Dispose();
        tracerProvider?.ForceFlush(5000);
        meterProvider?.ForceFlush(5000);

        WriteSuccess("All traces exported to LogDB.");
        Console.WriteLine();
        WriteSection("What to look for");
        WriteInfo("1. Paste a trace ID into the Trace Explorer.");
        WriteInfo("2. Look for parallel branches (Inventory checks EU+US side by side).");
        WriteInfo("3. Look for subtrees (Pricing has Discount + Tax children).");
        WriteInfo("4. Scenario 2: cache miss adds a DB fallback child under Cart Service.");
        WriteInfo("5. Scenario 2: payment retry shows two Stripe spans under Payment.");
        WriteInfo("6. Scenario 3: error propagates from Fraud Check → Payment → Order → Gateway.");
    }

    // ════════════════════════════════════════════════════════════════
    //  Main checkout flow
    // ════════════════════════════════════════════════════════════════

    private static async Task SimulateCheckout(
        ILoggerFactory lf, string user, string cartId, string orderId,
        bool cartCached, bool warehouseUsSlow, bool paymentSuccess, int fraudScore)
    {
        // ── API Gateway ──────────────────────────────────────────────
        using var gw = GatewaySource.StartActivity("POST /api/checkout", ActivityKind.Server);
        gw?.SetTag("http.method", "POST");
        gw?.SetTag("http.url", "https://api.eshop.com/api/checkout");
        gw?.SetTag("http.route", "/api/checkout");
        gw?.SetTag("user.email", user);
        gw?.SetTag("service.name", "eShop.APIGateway");

        var traceId = Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString();
        WriteInfo($"  Trace ID: {traceId}");

        var gwLog = lf.CreateLogger("eShop.APIGateway");
        gwLog.LogInformation("Incoming checkout from {User} cart {CartId}", user, cartId);

        try
        {
            // Gateway → Auth Service
            await CallAuthService(lf, user);

            // Gateway → Order Service
            using var gwToOrder = GatewaySource.StartActivity("gRPC OrderService/CreateOrder", ActivityKind.Client);
            gwToOrder?.SetTag("rpc.system", "grpc");
            gwToOrder?.SetTag("rpc.service", "OrderService");
            gwToOrder?.SetTag("service.name", "eShop.APIGateway");

            await RunOrderService(lf, user, cartId, orderId, cartCached, warehouseUsSlow, paymentSuccess, fraudScore);

            gwToOrder?.SetTag("rpc.grpc.status_code", 0);
            gw?.SetTag("http.status_code", 200);
            gw?.SetStatus(ActivityStatusCode.Ok);
            gwLog.LogInformation("Checkout completed: order {OrderId}", orderId);
            OrdersCounter.Add(1, new KeyValuePair<string, object?>("status", "success"));
        }
        catch (Exception ex)
        {
            gw?.SetTag("http.status_code", 500);
            gw?.SetStatus(ActivityStatusCode.Error, ex.Message);
            gw?.AddException(ex);
            gwLog.LogError(ex, "Checkout failed for {CartId}", cartId);
            OrdersCounter.Add(1, new KeyValuePair<string, object?>("status", "error"));
        }
    }

    // ── Auth Service ─────────────────────────────────────────────────

    private static async Task CallAuthService(ILoggerFactory lf, string user)
    {
        using var client = GatewaySource.StartActivity("gRPC AuthService/Validate", ActivityKind.Client);
        client?.SetTag("rpc.system", "grpc");
        client?.SetTag("rpc.service", "AuthService");
        client?.SetTag("service.name", "eShop.APIGateway");

        using var server = AuthSource.StartActivity("ValidateToken", ActivityKind.Server);
        server?.SetTag("auth.method", "JWT");
        server?.SetTag("service.name", "eShop.AuthService");

        var log = lf.CreateLogger("eShop.AuthService");
        log.LogDebug("Validating JWT for {User}", user);
        await Task.Delay(8);

        // Auth → Redis session check
        using var redis = AuthSource.StartActivity("GET session:{userId}", ActivityKind.Client);
        redis?.SetTag("db.system", "redis");
        redis?.SetTag("db.operation", "GET");
        redis?.SetTag("net.peer.name", "redis-primary.internal");
        redis?.SetTag("service.name", "eShop.AuthService");

        await Task.Delay(3);
        redis?.SetTag("cache.hit", true);
        log.LogDebug("Session cache hit for {User}", user);

        server?.SetStatus(ActivityStatusCode.Ok);
        log.LogInformation("Auth validated: {User}, role=customer", user);
    }

    // ── Order Service ────────────────────────────────────────────────

    private static async Task RunOrderService(
        ILoggerFactory lf, string user, string cartId, string orderId,
        bool cartCached, bool warehouseUsSlow, bool paymentSuccess, int fraudScore)
    {
        using var span = OrderSource.StartActivity("CreateOrder", ActivityKind.Server);
        span?.SetTag("order.id", orderId);
        span?.SetTag("order.cart_id", cartId);
        span?.SetTag("service.name", "eShop.OrderService");

        var log = lf.CreateLogger("eShop.OrderService");
        log.LogInformation("Processing order {OrderId} for {User}", orderId, user);

        // 1. Cart Service — fetch cart
        var cartItems = await CallCartService(lf, cartId, cartCached);

        // 2. Inventory Service — parallel warehouse checks
        await CallInventoryService(lf, orderId, warehouseUsSlow);

        // 3. Pricing Service — parallel discount + tax
        await CallPricingService(lf, orderId, cartItems);

        // 4. Payment Service — fraud check + charge (may retry)
        await CallPaymentService(lf, orderId, user, paymentSuccess, fraudScore);

        log.LogInformation("Payment confirmed for {OrderId}", orderId);

        // 5. Async: publish to queue → Notification Service
        using (var pub = OrderSource.StartActivity("Publish order.confirmed", ActivityKind.Producer))
        {
            pub?.SetTag("messaging.system", "rabbitmq");
            pub?.SetTag("messaging.destination", "orders.confirmed");
            pub?.SetTag("service.name", "eShop.OrderService");
            log.LogInformation("Publishing order.confirmed for {OrderId}", orderId);
            await Task.Delay(8);
        }

        await RunNotificationService(lf, orderId, user);

        span?.SetStatus(ActivityStatusCode.Ok);
        log.LogInformation("Order {OrderId} completed", orderId);
    }

    // ── Cart Service ─────────────────────────────────────────────────

    private static async Task<int> CallCartService(ILoggerFactory lf, string cartId, bool cached)
    {
        using var client = OrderSource.StartActivity("gRPC CartService/GetCart", ActivityKind.Client);
        client?.SetTag("rpc.system", "grpc");
        client?.SetTag("rpc.service", "CartService");
        client?.SetTag("service.name", "eShop.OrderService");

        using var server = CartSource.StartActivity("GetCart", ActivityKind.Server);
        server?.SetTag("cart.id", cartId);
        server?.SetTag("service.name", "eShop.CartService");

        var log = lf.CreateLogger("eShop.CartService");

        // Cache lookup
        using (var cacheSpan = CartSource.StartActivity("GET cart:{cartId}", ActivityKind.Client))
        {
            cacheSpan?.SetTag("db.system", "redis");
            cacheSpan?.SetTag("db.operation", "GET");
            cacheSpan?.SetTag("service.name", "eShop.CartService");
            await Task.Delay(2);
            cacheSpan?.SetTag("cache.hit", cached);

            if (cached)
            {
                log.LogDebug("Cart {CartId} found in cache (3 items)", cartId);
            }
            else
            {
                log.LogDebug("Cache miss for cart {CartId}", cartId);
            }
        }

        if (!cached)
        {
            // DB fallback — this child span only appears on cache miss
            using var dbSpan = CartSource.StartActivity("SELECT * FROM carts WHERE id = $1", ActivityKind.Client);
            dbSpan?.SetTag("db.system", "postgresql");
            dbSpan?.SetTag("db.name", "cart_db");
            dbSpan?.SetTag("db.statement", "SELECT * FROM carts WHERE id = $1");
            dbSpan?.SetTag("service.name", "eShop.CartService");

            log.LogDebug("Loading cart {CartId} from database", cartId);
            await Task.Delay(25);
            log.LogInformation("Cart {CartId} loaded from DB (3 items, EUR 249.97)", cartId);

            // Write-through cache update
            using var cacheWrite = CartSource.StartActivity("SET cart:{cartId}", ActivityKind.Client);
            cacheWrite?.SetTag("db.system", "redis");
            cacheWrite?.SetTag("db.operation", "SET");
            cacheWrite?.SetTag("service.name", "eShop.CartService");
            await Task.Delay(2);
            log.LogDebug("Cart {CartId} written to cache (TTL 300s)", cartId);
        }

        server?.SetTag("cart.items", 3);
        server?.SetTag("cart.total", 249.97);
        server?.SetStatus(ActivityStatusCode.Ok);
        return 3;
    }

    // ── Inventory Service (parallel warehouse checks) ────────────────

    private static async Task CallInventoryService(ILoggerFactory lf, string orderId, bool usSlow)
    {
        using var client = OrderSource.StartActivity("gRPC InventoryService/ReserveStock", ActivityKind.Client);
        client?.SetTag("rpc.system", "grpc");
        client?.SetTag("rpc.service", "InventoryService");
        client?.SetTag("service.name", "eShop.OrderService");

        using var server = InventorySource.StartActivity("ReserveStock", ActivityKind.Server);
        server?.SetTag("order.id", orderId);
        server?.SetTag("service.name", "eShop.InventoryService");

        var log = lf.CreateLogger("eShop.InventoryService");
        log.LogInformation("Checking stock across warehouses for {OrderId}", orderId);

        // Parallel warehouse queries — these run concurrently
        var euTask = CheckWarehouse(lf, "WH-EU-01", "inventory_eu", orderId, 35);
        var usTask = CheckWarehouse(lf, "WH-US-01", "inventory_us", orderId, usSlow ? 1800 : 50);

        await Task.WhenAll(euTask, usTask);

        log.LogInformation("Stock reserved from WH-EU-01 for {OrderId}", orderId);
        server?.SetTag("inventory.warehouse", "WH-EU-01");
        server?.SetTag("inventory.reserved_items", 3);
        server?.SetStatus(ActivityStatusCode.Ok);
    }

    private static async Task CheckWarehouse(ILoggerFactory lf, string warehouse, string dbName, string orderId, int delayMs)
    {
        using var span = InventorySource.StartActivity($"SELECT stock FROM {warehouse}", ActivityKind.Client);
        span?.SetTag("db.system", "postgresql");
        span?.SetTag("db.name", dbName);
        span?.SetTag("db.statement", $"SELECT quantity FROM stock WHERE sku = ANY($1) FOR UPDATE");
        span?.SetTag("inventory.warehouse", warehouse);
        span?.SetTag("service.name", "eShop.InventoryService");

        var log = lf.CreateLogger("eShop.InventoryService");

        if (delayMs > 500)
        {
            span?.AddEvent(new ActivityEvent("SlowQuery", tags: new ActivityTagsCollection
            {
                { "db.latency_ms", delayMs },
                { "db.wait_reason", "row_lock_contention" }
            }));
            log.LogWarning("Slow query on {Warehouse}: row lock contention ({DelayMs}ms)", warehouse, delayMs);
        }

        await Task.Delay(delayMs);
        span?.SetTag("inventory.in_stock", true);
        log.LogDebug("{Warehouse}: all items available for {OrderId}", warehouse, orderId);
    }

    // ── Pricing Service (parallel discount + tax) ────────────────────

    private static async Task CallPricingService(ILoggerFactory lf, string orderId, int itemCount)
    {
        using var client = OrderSource.StartActivity("gRPC PricingService/Calculate", ActivityKind.Client);
        client?.SetTag("rpc.system", "grpc");
        client?.SetTag("rpc.service", "PricingService");
        client?.SetTag("service.name", "eShop.OrderService");

        using var server = PricingSource.StartActivity("CalculatePrice", ActivityKind.Server);
        server?.SetTag("order.id", orderId);
        server?.SetTag("pricing.items", itemCount);
        server?.SetTag("service.name", "eShop.PricingService");

        var log = lf.CreateLogger("eShop.PricingService");
        log.LogDebug("Calculating final price for {OrderId}", orderId);

        // Parallel: discount engine + tax calculator
        var discountTask = Task.Run(async () =>
        {
            using var discountSpan = PricingSource.StartActivity("ApplyDiscounts", ActivityKind.Internal);
            discountSpan?.SetTag("discount.coupon_code", "SUMMER10");
            discountSpan?.SetTag("discount.percent", 10);
            discountSpan?.SetTag("service.name", "eShop.PricingService");
            log.LogDebug("Evaluating coupon SUMMER10");
            await Task.Delay(15);
            log.LogInformation("Discount applied: 10% off (SUMMER10) for {OrderId}", orderId);
        });

        var taxTask = Task.Run(async () =>
        {
            using var taxSpan = PricingSource.StartActivity("POST https://tax-api.example.com/v2/calculate", ActivityKind.Client);
            taxSpan?.SetTag("http.method", "POST");
            taxSpan?.SetTag("http.url", "https://tax-api.example.com/v2/calculate");
            taxSpan?.SetTag("peer.service", "TaxJar");
            taxSpan?.SetTag("tax.jurisdiction", "DE");
            taxSpan?.SetTag("service.name", "eShop.PricingService");
            log.LogDebug("Requesting tax calculation from TaxJar (jurisdiction: DE)");
            await Task.Delay(45);
            taxSpan?.SetTag("http.status_code", 200);
            taxSpan?.SetTag("tax.rate", 19.0);
            taxSpan?.SetTag("tax.amount", 42.75);
            log.LogInformation("Tax calculated: EUR 42.75 (19% VAT) for {OrderId}", orderId);
        });

        await Task.WhenAll(discountTask, taxTask);

        server?.SetTag("pricing.subtotal", 249.97);
        server?.SetTag("pricing.discount", 25.00);
        server?.SetTag("pricing.tax", 42.75);
        server?.SetTag("pricing.total", 267.72);
        server?.SetStatus(ActivityStatusCode.Ok);
        log.LogInformation("Final price: EUR 267.72 for {OrderId}", orderId);
    }

    // ── Payment Service (fraud + charge with optional retry) ─────────

    private static async Task CallPaymentService(
        ILoggerFactory lf, string orderId, string user, bool success, int fraudScore)
    {
        using var client = OrderSource.StartActivity("gRPC PaymentService/Charge", ActivityKind.Client);
        client?.SetTag("rpc.system", "grpc");
        client?.SetTag("rpc.service", "PaymentService");
        client?.SetTag("service.name", "eShop.OrderService");

        using var server = PaymentSource.StartActivity("ChargeCard", ActivityKind.Server);
        server?.SetTag("order.id", orderId);
        server?.SetTag("payment.amount", 267.72);
        server?.SetTag("payment.currency", "EUR");
        server?.SetTag("service.name", "eShop.PaymentService");

        var log = lf.CreateLogger("eShop.PaymentService");
        log.LogInformation("Processing payment EUR 267.72 for {OrderId}", orderId);

        // Fraud check (always runs)
        using (var fraudSpan = PaymentSource.StartActivity("FraudCheck", ActivityKind.Internal))
        {
            fraudSpan?.SetTag("fraud.provider", "Sift");
            fraudSpan?.SetTag("fraud.score", fraudScore);
            fraudSpan?.SetTag("service.name", "eShop.PaymentService");

            log.LogDebug("Running fraud detection via Sift ML model");
            await Task.Delay(30);

            if (fraudScore > 80)
            {
                fraudSpan?.SetTag("fraud.decision", "block");
                fraudSpan?.SetStatus(ActivityStatusCode.Error, "Fraud score too high");
                log.LogError("Fraud check BLOCKED: score {Score}/100 for {OrderId}", fraudScore, orderId);

                server?.SetStatus(ActivityStatusCode.Error, "Blocked by fraud detection");
                throw new InvalidOperationException($"Payment blocked: fraud score {fraudScore}/100");
            }

            var decision = fraudScore > 60 ? "review" : "accept";
            fraudSpan?.SetTag("fraud.decision", decision);
            if (fraudScore > 60)
                log.LogWarning("Fraud score elevated: {Score}/100, flagged for manual review", fraudScore);
            else
                log.LogDebug("Fraud check passed: score {Score}/100", fraudScore);
        }

        // Stripe charge (may retry on transient failure)
        if (!success)
        {
            // Simulate a declined payment (no retry for declines)
            await ChargeStripe(lf, orderId, "declined", 200);
            server?.SetStatus(ActivityStatusCode.Error, "Payment declined");
            throw new InvalidOperationException("Payment declined: insufficient_funds");
        }

        if (fraudScore > 40)
        {
            // Simulate first attempt fails (timeout), retry succeeds
            await ChargeStripe(lf, orderId, "timeout", 3000);
            log.LogWarning("Stripe timeout on first attempt for {OrderId}, retrying...", orderId);
            await ChargeStripe(lf, orderId, "success", 150);
            log.LogInformation("Stripe charge succeeded on retry for {OrderId}", orderId);
        }
        else
        {
            await ChargeStripe(lf, orderId, "success", 120);
            log.LogInformation("Stripe charge succeeded for {OrderId}", orderId);
        }

        server?.SetTag("payment.transaction_id", $"ch_{Guid.NewGuid().ToString("N")[..24]}");
        server?.SetStatus(ActivityStatusCode.Ok);
    }

    private static async Task ChargeStripe(ILoggerFactory lf, string orderId, string outcome, int delayMs)
    {
        using var span = PaymentSource.StartActivity("POST https://api.stripe.com/v1/charges", ActivityKind.Client);
        span?.SetTag("http.method", "POST");
        span?.SetTag("http.url", "https://api.stripe.com/v1/charges");
        span?.SetTag("peer.service", "Stripe");
        span?.SetTag("service.name", "eShop.PaymentService");

        var log = lf.CreateLogger("eShop.PaymentService");
        await Task.Delay(delayMs);

        switch (outcome)
        {
            case "success":
                span?.SetTag("http.status_code", 200);
                span?.SetTag("payment.status", "succeeded");
                break;
            case "declined":
                span?.SetTag("http.status_code", 402);
                span?.SetTag("payment.status", "declined");
                span?.SetTag("payment.decline_code", "insufficient_funds");
                span?.SetStatus(ActivityStatusCode.Error, "Card declined");
                span?.AddException(new InvalidOperationException("Card declined: insufficient_funds"));
                log.LogError("Stripe: card declined for {OrderId}", orderId);
                break;
            case "timeout":
                span?.SetTag("http.status_code", 504);
                span?.SetTag("payment.status", "timeout");
                span?.SetStatus(ActivityStatusCode.Error, "Gateway timeout");
                span?.AddEvent(new ActivityEvent("GatewayTimeout"));
                log.LogError("Stripe: gateway timeout after {DelayMs}ms for {OrderId}", delayMs, orderId);
                break;
        }
    }

    // ── Notification Service (async consumer, fan-out) ────────────────

    private static async Task RunNotificationService(ILoggerFactory lf, string orderId, string user)
    {
        using var consumer = NotificationSource.StartActivity("Consume order.confirmed", ActivityKind.Consumer);
        consumer?.SetTag("messaging.system", "rabbitmq");
        consumer?.SetTag("messaging.source", "orders.confirmed");
        consumer?.SetTag("service.name", "eShop.NotificationService");

        var log = lf.CreateLogger("eShop.NotificationService");
        log.LogDebug("Consumed order.confirmed for {OrderId}", orderId);

        // Fan-out: email + push notification in parallel
        var emailTask = Task.Run(async () =>
        {
            using var emailSpan = NotificationSource.StartActivity("POST https://api.sendgrid.com/v3/mail/send", ActivityKind.Client);
            emailSpan?.SetTag("http.method", "POST");
            emailSpan?.SetTag("peer.service", "SendGrid");
            emailSpan?.SetTag("email.to", user);
            emailSpan?.SetTag("email.template", "order-confirmation-v2");
            emailSpan?.SetTag("service.name", "eShop.NotificationService");
            await Task.Delay(75);
            emailSpan?.SetTag("http.status_code", 202);
            log.LogInformation("Email sent to {User} for {OrderId}", user, orderId);
        });

        var pushTask = Task.Run(async () =>
        {
            using var pushSpan = NotificationSource.StartActivity("POST https://fcm.googleapis.com/v1/send", ActivityKind.Client);
            pushSpan?.SetTag("http.method", "POST");
            pushSpan?.SetTag("peer.service", "Firebase");
            pushSpan?.SetTag("push.token", "device-token-abc");
            pushSpan?.SetTag("push.title", "Order Confirmed");
            pushSpan?.SetTag("service.name", "eShop.NotificationService");
            await Task.Delay(50);
            pushSpan?.SetTag("http.status_code", 200);
            log.LogInformation("Push notification sent for {OrderId}", orderId);
        });

        await Task.WhenAll(emailTask, pushTask);
        consumer?.SetStatus(ActivityStatusCode.Ok);
    }

    // ── Console helpers ──────────────────────────────────────────────

    private static void WriteSection(string title)
    {
        var border = new string('=', Math.Max(32, title.Length + 8));
        WriteWithColor($"\n{border}", ConsoleColor.Cyan);
        WriteWithColor($"== {title} ==", ConsoleColor.Cyan);
        WriteWithColor(border, ConsoleColor.Cyan);
    }

    private static void WriteStep(string msg) => WriteWithColor($"\n--> {msg}", ConsoleColor.Yellow);
    private static void WriteInfo(string msg) => WriteWithColor($"    {msg}", ConsoleColor.Gray);
    private static void WriteSuccess(string msg) => WriteWithColor($"[OK] {msg}", ConsoleColor.Green);
    private static void WriteError(string msg) => WriteWithColor($"[!!] {msg}", ConsoleColor.Red);

    private static void WriteWithColor(string message, ConsoleColor color)
    {
        var prev = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.WriteLine(message);
        Console.ForegroundColor = prev;
    }
}
