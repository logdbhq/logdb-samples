using Serilog;
using Serilog.Context;
using Serilog.Events;
using LogDB.Serilog;
using LogDB.Client.Services;

string apiKey = Environment.GetEnvironmentVariable("LOGDB_API_KEY", EnvironmentVariableTarget.User);

WriteSection("LogDB Serilog Writer Demo");
WriteInfo("This sample sends logs only (no read/query path).");
WriteInfo("Writer endpoint is auto-discovered by LogDB SDK.");

// SCENARIO 1: Simple tasks (No batching, immediate write).
// Ideal for low-volume applications or serverless functions where you need logs exported instantly.
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .Enrich.FromLogContext()
    .WriteTo.LogDB(options =>
    {
        options.ApiKey = apiKey;
        options.DefaultApplication = "LogDB.Sample.Serilog";
        options.DefaultEnvironment = "demo";
        options.DefaultCollection = "serilog-events-simple";
        options.DefaultPayloadType = LogDBPayloadType.Log;
        options.EnableBatching = false; // <-- Crucial for simple tasks
    })
    .CreateLogger();

try
{
    WriteStep("Scenario 1: Sending immediate Serilog entries (Batching = false)...");

    Log.Information("Simple task started at {UtcNow}", DateTime.UtcNow);
    using (LogContext.PushProperty("Source", "LogDB.Sample.Serilog.simple"))
    {
        Log.Information("Immediate single log processed");
    }

    Log.CloseAndFlush();
    WriteSuccess("Scenario 1 immediate logs sent.");
}
catch (Exception ex)
{
    WriteError($"Serilog Demo Scenario 1 failed: {ex.Message}");
}

// SCENARIO 2: High Volume (Batching = true)
// Ideal for standard web applications processing thousands of requests per second.
WriteStep("Scenario 2: Reconfiguring for high-volume (Batching = true)...");

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .Enrich.FromLogContext()
    .WriteTo.LogDB(options =>
    {
        options.ApiKey = apiKey;
        options.DefaultApplication = "LogDB.Sample.Serilog";
        options.DefaultEnvironment = "demo";
        options.DefaultCollection = "serilog-events-batched";
        options.DefaultPayloadType = LogDBPayloadType.Log;
        options.EnableBatching = true;   // <-- Crucial for high volume
        options.BatchSize = 200;
        options.FlushInterval = TimeSpan.FromSeconds(2);
    })
    .CreateLogger();

try
{
    WriteStep("Sending high-volume Serilog entries...");

    Log.Information("Batched Serilog sample started at {UtcNow}", DateTime.UtcNow);

    using (LogContext.PushProperty("Source", "LogDB.Sample.Serilog"))
    using (LogContext.PushProperty("PaymentId", 98765))
    using (LogContext.PushProperty("Amount", 49.90))
    {
        Log.Information("Payment processed");
    }

    Log.ForContext("LogDBType", LogDBPayloadType.Cache)
        .ForContext("LogDBCacheKey", "sample:session:serilog:42")
        .ForContext("LogDBCacheValue", "active")
        .ForContext("LogDBTtlSeconds", 600)
        .Information("cache upsert");

    Log.ForContext("LogDBType", LogDBPayloadType.Beat)
        .ForContext("LogDBMeasurement", "sample_worker_health")
        .ForContext("Tag.worker", "serilog-worker-1")
        .ForContext("Field.status", "ok")
        .ForContext("Field.latency_ms", 14.2)
        .Information("heartbeat");

    try
    {
        throw new InvalidOperationException("Demo exception from Serilog sample");
    }
    catch (Exception ex)
    {
        Log.Error(ex, "Serilog sample exception logged");
    }

    WriteSuccess("Logs sent. Flushing...");
}
catch (Exception ex)
{
    WriteError($"Serilog demo failed: {ex.Message}");
}
finally
{
    Log.CloseAndFlush();
    WriteSuccess("Flush completed.");
}

// SCENARIO 3: Encrypted logs via Serilog
// Encrypt sensitive fields with EncryptionService.Encrypt() before passing them as Serilog context properties.
// Requires LOGDB_SECRET_KEY environment variable to be set.
WriteStep("\nScenario 3: Reconfiguring for encrypted logging...");

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .Enrich.FromLogContext()
    .WriteTo.LogDB(options =>
    {
        options.ApiKey = apiKey;
        options.DefaultApplication = "LogDB.Sample.Serilog";
        options.DefaultEnvironment = "demo";
        options.DefaultCollection = "serilog-events-encrypted";
        options.DefaultPayloadType = LogDBPayloadType.Log;
        options.EnableBatching = false;
    })
    .CreateLogger();

try
{
    // Per-field encryption: only sensitive fields are encrypted
    using (LogContext.PushProperty("UserEmail", EncryptionService.Encrypt("customer@example.com")))
    using (LogContext.PushProperty("IpAddress", EncryptionService.Encrypt("10.0.0.50")))
    using (LogContext.PushProperty("Source", "LogDB.Sample.Serilog"))
    {
        Log.Information("Order placed by customer");
    }

    // Full encryption: all fields encrypted
    using (LogContext.PushProperty("UserEmail", EncryptionService.Encrypt("gdpr-user@example.com")))
    {
        Log.Information(EncryptionService.Encrypt("GDPR data export completed for user 42"));
    }

    WriteSuccess("Encrypted logs sent via Serilog.");
}
catch (Exception ex)
{
    WriteError($"Encrypted log demo failed: {ex.Message}");
}
finally
{
    Log.CloseAndFlush();
}

WriteInfo("Infrastructure builder demos are not included in the GitHub package build used by this sample.");

static void WriteSection(string title)
{
    var width = Math.Max(32, title.Length + 8);
    var border = new string('=', width);

    WriteWithColor($"\n{border}", ConsoleColor.Cyan);
    WriteWithColor($"== {title} ==", ConsoleColor.Cyan);
    WriteWithColor(border, ConsoleColor.Cyan);
}

static void WriteStep(string message) => WriteWithColor($"--> {message}", ConsoleColor.Yellow);
static void WriteInfo(string message) => WriteWithColor(message, ConsoleColor.Gray);
static void WriteSuccess(string message) => WriteWithColor(message, ConsoleColor.Green);
static void WriteError(string message) => WriteWithColor(message, ConsoleColor.Red);

static void WriteWithColor(string message, ConsoleColor color)
{
    var originalColor = Console.ForegroundColor;
    Console.ForegroundColor = color;
    Console.WriteLine(message);
    Console.ForegroundColor = originalColor;
}



