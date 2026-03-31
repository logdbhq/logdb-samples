using LogDB.NLog;
using LogDB.Client.Models;
using LogDB.Client.Services;
using NLog;
using NLog.Config;

var apiKey = GetRequiredApiKey();

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
        target.DefaultApplication = "LogDB.Sample.NLog";
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
    var simpleEvent = new LogEventInfo(NLog.LogLevel.Info, "SimpleLogger", "Immediate single log processed");
    simpleEvent.Properties["Source"] = "LogDB.Sample.NLog.simple";
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
        target.DefaultApplication = "LogDB.Sample.NLog";
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

    var structuredEvent = new LogEventInfo(NLog.LogLevel.Info, "Demo", "Payment {PaymentId} processed");
    structuredEvent.Properties["PaymentId"] = 12345;
    structuredEvent.Properties["Amount"] = 99.95;
    structuredEvent.Properties["Currency"] = "EUR";
    structuredEvent.Properties["Source"] = "LogDB.Sample.NLog";
    logger.Log(structuredEvent);

    var cacheEvent = new LogEventInfo(NLog.LogLevel.Info, "Demo", "cache upsert");
    cacheEvent.Properties["LogDBType"] = LogDBPayloadType.Cache;
    cacheEvent.Properties["LogDBCacheKey"] = "sample:session:nlog:42";
    cacheEvent.Properties["LogDBCacheValue"] = "active";
    cacheEvent.Properties["LogDBTtlSeconds"] = 600;
    logger.Log(cacheEvent);

    var beatEvent = new LogEventInfo(NLog.LogLevel.Info, "Demo", "heartbeat");
    beatEvent.Properties["LogDBType"] = LogDBPayloadType.Beat;
    beatEvent.Properties["LogDBMeasurement"] = "sample_worker_health";
    beatEvent.Properties["Tag.worker"] = "nlog-worker-1";
    beatEvent.Properties["Field.status"] = "ok";
    beatEvent.Properties["Field.latency_ms"] = 11.8;
    logger.Log(beatEvent);

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
        target.DefaultApplication = "LogDB.Sample.NLog";
        target.DefaultEnvironment = "demo";
        target.DefaultCollection = "nlog-events-encrypted";
        target.EnableBatching = false;
    });

LogManager.Configuration = config3;
var encLogger = LogManager.GetLogger("EncryptedLogger");

try
{
    // Per-field encryption: only sensitive fields are encrypted
    var encEvent1 = new LogEventInfo(NLog.LogLevel.Info, "EncryptedLogger", "User authentication event");
    encEvent1.Properties["UserEmail"] = EncryptionService.Encrypt("user@example.com");        // encrypted
    encEvent1.Properties["IpAddress"] = EncryptionService.Encrypt("192.168.1.100");           // encrypted
    encEvent1.Properties["Source"] = "LogDB.Sample.NLog";                                  // plaintext
    encLogger.Log(encEvent1);

    // Full encryption: all fields encrypted
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

WriteInfo("Infrastructure builder demos are not included in the GitHub package build used by this sample.");

static string GetRequiredApiKey()
{
    var apiKey = Environment.GetEnvironmentVariable("LOGDB_API_KEY", EnvironmentVariableTarget.User);
    if (!string.IsNullOrWhiteSpace(apiKey))
        return apiKey;

    WriteError("LOGDB_API_KEY environment variable is required.");
    WriteInfo("Set LOGDB_API_KEY and rerun the sample.");
    Environment.Exit(1);
    return string.Empty;
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





