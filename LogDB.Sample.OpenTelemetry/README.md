# LogDB OpenTelemetry Sample

Drop-in replacement sample showing how to export **logs**, **traces**, and **metrics** from standard OpenTelemetry pipelines to LogDB.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- A LogDB API key (get one from the LogDB dashboard or via the Auth API)

## Setup

1. Set your API key as an environment variable:

```bash
# Linux / macOS
export LOGDB_API_KEY="your-api-key"

# Windows (PowerShell)
$env:LOGDB_API_KEY = "your-api-key"

# Windows (cmd)
set LOGDB_API_KEY=your-api-key
```

2. Run the sample:

```bash
dotnet run --project com.logdb.sample.otel
```

## What it does

Simulates an **e-commerce checkout service** that processes four orders. Each order flows through:

1. **ProcessOrder** (parent span) — orchestrates the full order lifecycle
2. **ValidatePayment** (child span) — calls a payment gateway (20% simulated failure rate)
3. **DeductInventory** (child span) — reserves stock in a warehouse (10% simulated failure rate)

### Signals exported to LogDB

| Signal | What's recorded |
|---------|----------------------------------------------|
| **Traces** | Parent/child spans with `order.id`, `user.id`, `payment.gateway`, `payment.status`, `inventory.warehouse` attributes |
| **Metrics** | `orders_processed_total` (counter) and `order_processing_duration_ms` (histogram) |
| **Logs** | Structured log events with correlation to active traces via `TraceId`/`SpanId` |

All three signals are automatically correlated through OpenTelemetry context propagation.

## How LogDB plugs in

The **only** LogDB-specific code is the `AddLogDBExporter()` call on each pipeline:

```csharp
// Logs
logging.AddOpenTelemetry(options =>
{
    options.AddLogDBExporter(logDbOptions => {
        logDbOptions.ApiKey = apiKey;
        logDbOptions.DefaultCollection = "samples";
    });
});

// Traces
.WithTracing(tracing =>
{
    tracing.AddSource("your.activity.source");
    tracing.AddLogDBExporter(logDbOptions => { /* same options */ });
})

// Metrics
.WithMetrics(metrics =>
{
    metrics.AddMeter("your.meter.name");
    metrics.AddLogDBExporter(logDbOptions => { /* same options */ });
})
```

Everything else is standard OpenTelemetry — `ActivitySource`, `Meter`, `Counter<T>`, `Histogram<T>`, `ILogger`, resource attributes, etc. No vendor lock-in.

## Configuration

| Environment variable | Purpose | Default |
|----------------------|---------|---------|
| `LOGDB_API_KEY` | API key for authentication | _(required)_ |
| `LOGDB_SERVICE_URL` | Override the writer gRPC endpoint | Auto-discovered |
| `LOGDB_DEFAULT_COLLECTION` | Target collection name | `"samples"` |

## Expected output

```
[otel] Starting OpenTelemetry 'Drop-in Replacement' Sample...
[otel] Simulating a batch of e-commerce orders...
info: CheckoutWorker[0] Received new order ORD-4921 from user usr_janedoe
info: CheckoutWorker[0] Payment validation successful for ORD-4921
info: CheckoutWorker[0] Inventory deducted for ORD-4921
info: CheckoutWorker[0] Successfully completed order ORD-4921
...
[otel] Flushing telemetry...
[otel] Sample completed. Check LogDB UI to see your correlated Traces, Metrics, and Logs!
```

## Next steps

- Browse your traces, metrics, and logs in the LogDB UI or VS Code extension
- See the [LogDB OpenTelemetry docs](https://docs.logdb.site/sdk/otel) for configuration reference
- Check the other samples in this solution: `com.logdb.sample.serilog`, `com.logdb.sample.nlog`, `com.logdb.sample.sdk`
