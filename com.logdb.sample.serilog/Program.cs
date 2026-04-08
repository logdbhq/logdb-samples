using Serilog;
using Serilog.Context;
using Serilog.Events;
using LogDB.Serilog;
using LogDB.Client.Services;
using com.logdb.logger.LogBuilders;

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
        options.DefaultApplication = "com.logdb.sample.serilog";
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
    await Task.Delay(Random.Shared.Next(80, 250));
    using (LogContext.PushProperty("Source", "com.logdb.sample.serilog.simple"))
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
        options.DefaultApplication = "com.logdb.sample.serilog";
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

    await Task.Delay(Random.Shared.Next(100, 300));
    using (LogContext.PushProperty("Source", "com.logdb.sample.serilog"))
    using (LogContext.PushProperty("PaymentId", 98765))
    using (LogContext.PushProperty("Amount", 49.90))
    {
        Log.Information("Payment processed");
    }

    await Task.Delay(Random.Shared.Next(80, 250));
    Log.ForContext("LogDBType", LogDBPayloadType.Cache)
        .ForContext("LogDBCacheKey", "sample:session:serilog:42")
        .ForContext("LogDBCacheValue", "active")
        .ForContext("LogDBTtlSeconds", 600)
        .Information("cache upsert");

    await Task.Delay(Random.Shared.Next(80, 250));
    Log.ForContext("LogDBType", LogDBPayloadType.Beat)
        .ForContext("LogDBMeasurement", "sample_worker_health")
        .ForContext("Tag.worker", "serilog-worker-1")
        .ForContext("Field.status", "ok")
        .ForContext("Field.latency_ms", 14.2)
        .Information("heartbeat");

    await Task.Delay(Random.Shared.Next(100, 300));
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
        options.DefaultApplication = "com.logdb.sample.serilog";
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
    using (LogContext.PushProperty("Source", "com.logdb.sample.serilog"))
    {
        Log.Information("Order placed by customer");
    }

    // Full encryption: all fields encrypted
    await Task.Delay(Random.Shared.Next(100, 300));
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

// SCENARIO 4: Infrastructure log types via SDK builders
// These typed models let you build custom exporters for Windows Events, Docker, IIS, Nginx, etc.
// Each model converts to a standard Log via .ToLog() and routes server-side by _sys_type attribute.
WriteStep("\nScenario 4: Infrastructure log types (via SDK builders)...");

LogWindowsEventBuilder.ApiKey = apiKey;
LogDockerEventBuilder.ApiKey = apiKey;
LogDockerMetricBuilder.ApiKey = apiKey;
LogIISEventBuilder.ApiKey = apiKey;
LogNginxEventBuilder.ApiKey = apiKey;
LogWindowsMetricBuilder.ApiKey = apiKey;

try
{
    await Task.Delay(Random.Shared.Next(100, 300));
    WriteStep("Sending a Windows Event...");
    await LogWindowsEventBuilder.Create()
        .SetEventId(4625)
        .SetProviderName("Microsoft-Windows-Security-Auditing")
        .SetChannel("Security")
        .SetLevel("Warning")
        .SetComputer("DC-01.corp.local")
        .SetMessage("An account failed to log on.")
        .SetIpAddress("10.0.0.55")
        .SetCollection("serilog-windows-events")
        .Log();

    await Task.Delay(Random.Shared.Next(80, 250));
    WriteStep("Sending a Docker container log...");
    await LogDockerEventBuilder.Create()
        .SetContainerName("api-gateway")
        .SetImage("nginx:latest")
        .SetStream("stderr")
        .SetLevel("Error")
        .SetMessage("upstream timed out (110: Connection timed out)")
        .SetHostName("docker-host-01")
        .SetComposeProject("myapp")
        .SetComposeService("gateway")
        .SetCollection("serilog-docker-events")
        .Log();

    await Task.Delay(Random.Shared.Next(80, 250));
    WriteStep("Sending a Docker container metric...");
    await LogDockerMetricBuilder.Create()
        .SetContainerName("api-gateway")
        .SetImage("nginx")
        .SetImageTag("latest")
        .SetHostName("docker-host-01")
        .SetContainerState("running")
        .SetCpuUsagePercent(32.7)
        .SetMemoryUsageBytes(134_217_728)
        .SetMemoryLimitBytes(268_435_456)
        .SetMemoryUsagePercent(50.0)
        .SetNetworkRxBytes(2_097_152)
        .SetNetworkTxBytes(1_048_576)
        .SetPidsCurrent(8)
        .SetCollection("serilog-docker-metrics")
        .Log();

    await Task.Delay(Random.Shared.Next(80, 250));
    WriteStep("Sending an IIS access log...");
    await LogIISEventBuilder.Create()
        .SetMethod("GET")
        .SetUriStem("/api/products")
        .SetStatus(200)
        .SetTimeTaken(142)
        .SetClientIp("203.0.113.42")
        .SetSiteName("Default Web Site")
        .SetServerName("IIS-WEB-01")
        .SetCollection("serilog-iis-events")
        .Log();

    await Task.Delay(Random.Shared.Next(80, 250));
    WriteStep("Sending an Nginx access log...");
    await LogNginxEventBuilder.Create()
        .SetLogType("access")
        .SetMethod("POST")
        .SetPath("/api/orders")
        .SetStatusCode(201)
        .SetRequestTime(0.045)
        .SetRemoteAddress("198.51.100.23")
        .SetServerName("api.example.com")
        .SetCollection("serilog-nginx-events")
        .Log();

    await Task.Delay(Random.Shared.Next(80, 250));
    WriteStep("Sending a Windows host metric...");
    await LogWindowsMetricBuilder.Create()
        .SetMeasurement("cpu")
        .SetServerName("DC-01")
        .SetCpuUsagePercent(54.2)
        .SetCpuIdlePercent(45.8)
        .SetCpuCoreCount(4)
        .SetCollection("serilog-windows-metrics")
        .Log();

    await Task.Delay(Random.Shared.Next(100, 300));
    WriteStep("Sending encrypted infrastructure logs...");

    await LogIISEventBuilder.Create()
        .SetMethod("POST")
        .SetUriStem("/api/users/update")
        .SetUriQuery("token=secret123", isEncrypted: true)
        .SetStatus(200)
        .SetClientIp("203.0.113.42", isEncrypted: true)
        .SetUsername("alice@example.com", isEncrypted: true)
        .SetUserAgent("Mozilla/5.0", isEncrypted: true)
        .SetSiteName("PCI API")
        .SetCollection("serilog-iis-encrypted")
        .Log();

    await Task.Delay(Random.Shared.Next(80, 250));
    await LogNginxEventBuilder.Create()
        .SetLogType("access")
        .SetMethod("GET")
        .SetPath("/api/account")
        .SetStatusCode(200)
        .SetRemoteAddress("198.51.100.23", isEncrypted: true)
        .SetUserAgent("BankApp/2.1", isEncrypted: true)
        .SetReferer("https://bank.example.com", isEncrypted: true)
        .SetServerName("api.bank.example.com")
        .SetCollection("serilog-nginx-encrypted")
        .Log();

    WriteSuccess("Infrastructure log types sent (including encrypted).");
}
catch (Exception ex)
{
    WriteError($"Infrastructure log types demo failed: {ex.Message}");
}

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
