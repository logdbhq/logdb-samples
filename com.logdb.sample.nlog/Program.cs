using LogDB.NLog;
using LogDB.Client.Models;
using LogDB.Client.Services;
using com.logdb.logger.LogBuilders;
using NLog;
using NLog.Config;

string apiKey = Environment.GetEnvironmentVariable("LOGDB_API_KEY", EnvironmentVariableTarget.User);

WriteSection("LogDB NLog Writer Demo");
WriteInfo("This sample sends logs only (no read/query path).");
WriteInfo("Writer endpoint is auto-discovered by LogDB SDK.");

// SCENARIO 1: Simple tasks (No batching, immediate write)
var config1 = new LoggingConfiguration();
config1.AddLogDBTargetWithRule(
    targetName: "logdb_simple",
    apiKey: apiKey,
    defaultPayloadType: LogDBPayloadType.Log,
    minLevel: NLog.LogLevel.Info,
    configure: target =>
    {
        target.DefaultApplication = "com.logdb.sample.nlog";
        target.DefaultEnvironment = "demo";
        target.DefaultCollection = "nlog-events-simple";
        target.EnableBatching = false; // <-- Crucial for simple tasks
    });

LogManager.Configuration = config1;
var logger1 = LogManager.GetLogger("SimpleLogger");

try
{
    WriteStep("Scenario 1: Sending immediate NLog entries (Batching = false)...");
    
    logger1.Info("Simple NLog task started at {UtcNow}", DateTime.UtcNow);
    await Task.Delay(Random.Shared.Next(80, 250));
    var simpleEvent = new LogEventInfo(NLog.LogLevel.Info, "SimpleLogger", "Immediate single log processed");
    simpleEvent.Properties["Source"] = "com.logdb.sample.nlog.simple";
    logger1.Log(simpleEvent);

    LogManager.Flush(TimeSpan.FromSeconds(5));
    WriteSuccess("Scenario 1 immediate logs sent.");
}
catch (Exception ex)
{
    WriteError($"NLog Demo Scenario 1 failed: {ex.Message}");
}

// SCENARIO 2: High Volume (Batching = true)
WriteStep("\nScenario 2: Reconfiguring for high-volume (Batching = true)...");

var config2 = new LoggingConfiguration();
config2.AddLogDBTargetWithRule(
    targetName: "logdb_batched",
    apiKey: apiKey,
    defaultPayloadType: LogDBPayloadType.Log,
    minLevel: NLog.LogLevel.Info,
    configure: target =>
    {
        target.DefaultApplication = "com.logdb.sample.nlog";
        target.DefaultEnvironment = "demo";
        target.DefaultCollection = "nlog-events-batched";
        target.FlushIntervalSeconds = 2;
        target.BatchSize = 200;
        target.EnableBatching = true; // <-- Crucial for high volume
    });

LogManager.Configuration = config2;
var logger = LogManager.GetLogger("BatchedLogger");

try
{
    WriteStep("Sending high-volume NLog entries...");

    logger.Info("Batched NLog sample started at {UtcNow}", DateTime.UtcNow);

    await Task.Delay(Random.Shared.Next(100, 300));
    var structuredEvent = new LogEventInfo(NLog.LogLevel.Info, "Demo", "Payment {PaymentId} processed");
    structuredEvent.Properties["PaymentId"] = 12345;
    structuredEvent.Properties["Amount"] = 99.95;
    structuredEvent.Properties["Currency"] = "EUR";
    structuredEvent.Properties["Source"] = "com.logdb.sample.nlog";
    logger.Log(structuredEvent);

    await Task.Delay(Random.Shared.Next(80, 250));
    var cacheEvent = new LogEventInfo(NLog.LogLevel.Info, "Demo", "cache upsert");
    cacheEvent.Properties["LogDBType"] = LogDBPayloadType.Cache;
    cacheEvent.Properties["LogDBCacheKey"] = "sample:session:nlog:42";
    cacheEvent.Properties["LogDBCacheValue"] = "active";
    cacheEvent.Properties["LogDBTtlSeconds"] = 600;
    logger.Log(cacheEvent);

    await Task.Delay(Random.Shared.Next(80, 250));
    var beatEvent = new LogEventInfo(NLog.LogLevel.Info, "Demo", "heartbeat");
    beatEvent.Properties["LogDBType"] = LogDBPayloadType.Beat;
    beatEvent.Properties["LogDBMeasurement"] = "sample_worker_health";
    beatEvent.Properties["Tag.worker"] = "nlog-worker-1";
    beatEvent.Properties["Field.status"] = "ok";
    beatEvent.Properties["Field.latency_ms"] = 11.8;
    logger.Log(beatEvent);

    await Task.Delay(Random.Shared.Next(100, 300));
    try
    {
        throw new InvalidOperationException("Demo exception from NLog sample");
    }
    catch (Exception ex)
    {
        logger.Error(ex, "NLog sample exception logged");
    }

    LogManager.Flush(TimeSpan.FromSeconds(5));
    WriteSuccess("Logs sent and flushed.");
}
catch (Exception ex)
{
    WriteError($"NLog demo failed: {ex.Message}");
}
finally
{
    LogManager.Shutdown();
}

// SCENARIO 3: Encrypted logs via NLog
// Encrypt sensitive fields with EncryptionService.Encrypt() before passing them as NLog properties.
// Requires LOGDB_SECRET_KEY environment variable to be set.
WriteStep("\nScenario 3: Reconfiguring for encrypted logging...");

var config3 = new LoggingConfiguration();
config3.AddLogDBTargetWithRule(
    targetName: "logdb_encrypted",
    apiKey: apiKey,
    defaultPayloadType: LogDBPayloadType.Log,
    minLevel: NLog.LogLevel.Info,
    configure: target =>
    {
        target.DefaultApplication = "com.logdb.sample.nlog";
        target.DefaultEnvironment = "demo";
        target.DefaultCollection = "nlog-events-encrypted";
        target.EnableBatching = false;
    });

LogManager.Configuration = config3;
var encLogger = LogManager.GetLogger("EncryptedLogger");

try
{
    // Per-field encryption: only sensitive fields are encrypted
    await Task.Delay(Random.Shared.Next(100, 300));
    var encEvent1 = new LogEventInfo(NLog.LogLevel.Info, "EncryptedLogger", "User authentication event");
    encEvent1.Properties["UserEmail"] = EncryptionService.Encrypt("user@example.com");        // encrypted
    encEvent1.Properties["IpAddress"] = EncryptionService.Encrypt("192.168.1.100");           // encrypted
    encEvent1.Properties["Source"] = "com.logdb.sample.nlog";                                  // plaintext
    encLogger.Log(encEvent1);

    // Full encryption: all fields encrypted
    await Task.Delay(Random.Shared.Next(80, 250));
    var encEvent2 = new LogEventInfo(NLog.LogLevel.Info, "EncryptedLogger",
        EncryptionService.Encrypt("Sensitive PII data processed"));                            // encrypted message
    encEvent2.Properties["UserEmail"] = EncryptionService.Encrypt("jane@example.com");
    encLogger.Log(encEvent2);

    LogManager.Flush(TimeSpan.FromSeconds(5));
    WriteSuccess("Encrypted logs sent via NLog.");
}
catch (Exception ex)
{
    WriteError($"Encrypted log demo failed: {ex.Message}");
}
finally
{
    LogManager.Shutdown();
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
        .SetEventId(7036)
        .SetProviderName("Service Control Manager")
        .SetChannel("System")
        .SetLevel("Information")
        .SetComputer("APP-SERVER-01")
        .SetMessage("The Windows Update service entered the running state.")
        .SetCollection("nlog-windows-events")
        .Log();

    await Task.Delay(Random.Shared.Next(80, 250));
    WriteStep("Sending a Docker container log...");
    await LogDockerEventBuilder.Create()
        .SetContainerName("postgres-db")
        .SetImage("postgres:16")
        .SetStream("stdout")
        .SetLevel("Info")
        .SetMessage("database system is ready to accept connections")
        .SetHostName("docker-host-02")
        .SetComposeProject("backend")
        .SetComposeService("db")
        .SetCollection("nlog-docker-events")
        .Log();

    await Task.Delay(Random.Shared.Next(80, 250));
    WriteStep("Sending a Docker container metric...");
    await LogDockerMetricBuilder.Create()
        .SetContainerName("postgres-db")
        .SetImage("postgres")
        .SetImageTag("16")
        .SetHostName("docker-host-02")
        .SetContainerState("running")
        .SetCpuUsagePercent(18.3)
        .SetMemoryUsageBytes(536_870_912)
        .SetMemoryLimitBytes(1_073_741_824)
        .SetMemoryUsagePercent(50.0)
        .SetNetworkRxBytes(4_194_304)
        .SetNetworkTxBytes(3_145_728)
        .SetPidsCurrent(22)
        .SetCollection("nlog-docker-metrics")
        .Log();

    await Task.Delay(Random.Shared.Next(80, 250));
    WriteStep("Sending an IIS access log...");
    await LogIISEventBuilder.Create()
        .SetMethod("POST")
        .SetUriStem("/api/users/login")
        .SetStatus(401)
        .SetTimeTaken(35)
        .SetClientIp("198.51.100.10")
        .SetHost("auth.example.com")
        .SetServerName("IIS-AUTH-01")
        .SetCollection("nlog-iis-events")
        .Log();

    await Task.Delay(Random.Shared.Next(80, 250));
    WriteStep("Sending an Nginx error log...");
    await LogNginxEventBuilder.Create()
        .SetLogType("error")
        .SetSeverity("error")
        .SetMessage("connect() failed (111: Connection refused) while connecting to upstream")
        .SetUpstream("http://127.0.0.1:8080")
        .SetHostName("proxy-01")
        .SetServerName("api.example.com")
        .SetCollection("nlog-nginx-events")
        .Log();

    await Task.Delay(Random.Shared.Next(80, 250));
    WriteStep("Sending a Windows host metric...");
    await LogWindowsMetricBuilder.Create()
        .SetMeasurement("cpu")
        .SetServerName("APP-SERVER-01")
        .SetCpuUsagePercent(41.6)
        .SetCpuIdlePercent(58.4)
        .SetCpuCoreCount(8)
        .SetCollection("nlog-windows-metrics")
        .Log();

    WriteSuccess("Infrastructure log types sent.");
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
