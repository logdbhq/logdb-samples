using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LogDB.Client.Models;
using LogDB.Client.Services;
using LogDB.Extensions.Logging;

string apiKey = Environment.GetEnvironmentVariable("LOGDB_API_KEY", EnvironmentVariableTarget.User);
var readerServiceUrl = await LogDBReaderExtensions.DiscoverReaderServiceUrlAsync();
if (!string.IsNullOrWhiteSpace(readerServiceUrl))
{
    WriteSuccess($"Reader endpoint discovered via SDK: {readerServiceUrl}");
}
else
{
    WriteWarn("Reader endpoint not pre-resolved; SDK runtime discovery/fallback will be used.");
}

// ============================================================
// SCENARIO 1: Simple / Immediate (No batching)
// Ideal for low-volume apps or serverless functions.
// ============================================================
WriteStep("Scenario 1: Immediate writes (Batching = false)...");

var builder1 = Host.CreateDefaultBuilder(args);
builder1.ConfigureLogging((_, logging) =>
{
    logging.ClearProviders();
    logging.AddConsole();
    logging.AddLogDB(options =>
    {
        options.ApiKey = apiKey;
        options.EnableBatching = false; // <-- immediate write
        options.MaxRetries = 3;
        options.EnableCircuitBreaker = true;
    });
});
builder1.ConfigureServices((_, services) =>
{
    services.AddLogDBReader(options =>
    {
        options.ApiKey = apiKey;
        if (!string.IsNullOrWhiteSpace(readerServiceUrl))
            options.ReaderServiceUrl = readerServiceUrl;
    });
});
var host1 = builder1.Build();
var logdb1 = host1.Services.GetRequiredService<ILogDBClient>();

try
{
    await logdb1.LogAsync(new Log
    {
        Application = "com.logdb.sample.sdk",
        Environment = "demo",
        Collection = "sdk-events-simple",
        Level = LogDB.Client.Models.LogLevel.Info,
        Message = "Immediate log â€” no batching, written instantly",
        AttributesS = new Dictionary<string, string> { ["mode"] = "immediate" }
    });

    await logdb1.LogCacheAsync(new LogCache
    {
        Key = "sdk:immediate:state",
        Value = "active",
        Collection = "sdk-states-simple",
        TtlSeconds = 300
    });

    await logdb1.FlushAsync();
    WriteSuccess("Scenario 1 completed â€” immediate logs sent.");
}
catch (Exception ex)
{
    WriteError($"Scenario 1 failed: {ex.Message}");
}

// ============================================================
// SCENARIO 2: High Volume / Batched
// Ideal for standard apps processing thousands of requests/sec.
// ============================================================
WriteStep("\nScenario 2: Batched writes (Batching = true)...");

var builder = Host.CreateDefaultBuilder(args);
builder.ConfigureLogging((_, logging) =>
{
    logging.ClearProviders();
    logging.AddConsole();
    logging.AddLogDB(options =>
    {
        options.ApiKey = apiKey;
        options.EnableBatching = true;  // <-- batched writes
        options.BatchSize = 200;
        options.FlushInterval = TimeSpan.FromSeconds(2);
        options.MaxRetries = 3;
        options.EnableCircuitBreaker = true;

        // Add a global enricher
        options.AddEnricher(log =>
        {
            log.AttributesS["team"] = "demo-team";
            log.AttributesS["service.version"] = "1.0.0";
        });
    });
});
builder.ConfigureServices((_, services) =>
{
    services.AddLogDBReader(options =>
    {
        options.ApiKey = apiKey;
        if (!string.IsNullOrWhiteSpace(readerServiceUrl))
            options.ReaderServiceUrl = readerServiceUrl;
    });
});
var host = builder.Build();
var logdb = host.Services.GetRequiredService<ILogDBClient>();
var logger = host.Services.GetRequiredService<ILogger<Program>>();

    logger.LogInformation("Starting LogDB Demo Application...");

try
{
    // ----- Standard Log -----
    WriteStep("Sending standard Logs (batched, visible in Log tab)...");

    var visibleLogCount = 24;
    var levels = new[]
    {
        LogDB.Client.Models.LogLevel.Info,
        LogDB.Client.Models.LogLevel.Warning,
        LogDB.Client.Models.LogLevel.Error,
        LogDB.Client.Models.LogLevel.Debug
    };

    for (var i = 1; i <= visibleLogCount; i++)
    {
        var level = levels[(i - 1) % levels.Length];
        await logdb.LogAsync(new Log
        {
            Application = "com.logdb.sample.sdk",
            Environment = "demo",
            Collection = "sdk-events-batched",
            Level = level,
            Message = $"Simulation event {i:00}/24",
            UserEmail = "user@demo.logdb.com",
            Source = "sample-runner",
            AttributesS = new Dictionary<string, string>
            {
                ["action"] = "simulation_event",
                ["source"] = "console",
                ["run_slot"] = i.ToString()
            },
            AttributesN = new Dictionary<string, double>
            {
                ["cpu_usage_start"] = 12.5 + i,
                ["sequence"] = i
            },
            AttributesB = new Dictionary<string, bool>
            {
                ["is_interactive"] = true,
                ["is_sample"] = true
            },
            Label = new List<string> { "simulation", "startup", $"event-{i:00}" }
        });
    }

    WriteSuccess($"Queued {visibleLogCount} standard Log records for the Log tab.");

    var cacheKey = $"simulation_state:{Guid.NewGuid()}";

    // ----- LogCache Entry -----
    WriteStep("Sending a LogCache entry (batched)...");
    await logdb.LogCacheAsync(new LogCache
    {
        Key = cacheKey,
        Value = "running",
        Collection = "sdk-states-batched",
        TtlSeconds = 600
    });

    // ----- LogBeat Heartbeat -----
    WriteStep("Sending a LogBeat heartbeat (batched)...");
    await logdb.LogBeatAsync(new LogBeat
    {
        Measurement = "worker_health",
        Application = "com.logdb.sample.sdk",
        Environment = "demo",
        Tag = new List<LogMeta>
        {
            new LogMeta { Key = "worker", Value = "demo-worker" },
            new LogMeta { Key = "health", Value = "healthy" }
        },
        Field = new List<LogMeta>
        {
            new LogMeta { Key = "status", Value = "okay" }
        }
    });

    // ----- Infrastructure Log Types -----
    // These typed models convert to Log entries with _sys_type routing.
    // Users building custom exporters use these same models.

    WriteStep("Sending a LogWindowsEvent...");
    await logdb.LogAsync(new LogWindowsEvent
    {
        EventId = 4625,
        ProviderName = "Microsoft-Windows-Security-Auditing",
        Channel = "Security",
        Level = "Warning",
        Computer = "DC-01.corp.local",
        Message = "An account failed to log on.",
        IpAddress = "10.0.0.55",
        Collection = "sdk-windows-events"
    }.ToLog());

    WriteStep("Sending a LogWindowsMetric...");
    await logdb.LogAsync(new LogWindowsMetric
    {
        Measurement = "cpu",
        ServerName = "WEB-01",
        CpuUsagePercent = 78.5,
        CpuIdlePercent = 21.5,
        CpuCoreCount = 8,
        Collection = "sdk-windows-metrics"
    }.ToLog());

    WriteStep("Sending a LogDockerEvent...");
    await logdb.LogAsync(new LogDockerEvent
    {
        ContainerId = "abc123def456",
        ContainerName = "api-gateway",
        Image = "nginx:latest",
        Stream = "stderr",
        Level = "Error",
        Message = "upstream timed out (110: Connection timed out)",
        HostName = "docker-host-01",
        ComposeProject = "myapp",
        ComposeService = "gateway",
        Collection = "sdk-docker-events"
    }.ToLog());

    WriteStep("Sending a LogDockerMetric...");
    await logdb.LogAsync(new LogDockerMetric
    {
        ContainerId = "abc123def456",
        ContainerName = "api-gateway",
        Image = "nginx",
        ImageTag = "latest",
        HostName = "docker-host-01",
        ContainerState = "running",
        CpuUsagePercent = 45.2,
        MemoryUsageBytes = 268_435_456,
        MemoryLimitBytes = 536_870_912,
        MemoryUsagePercent = 50.0,
        NetworkRxBytes = 1_048_576,
        NetworkTxBytes = 524_288,
        PidsCurrent = 12,
        Collection = "sdk-docker-metrics"
    }.ToLog());

    WriteStep("Sending a LogIISEvent...");
    await logdb.LogAsync(new LogIISEvent
    {
        Method = "GET",
        UriStem = "/api/products",
        UriQuery = "category=shoes",
        Status = 200,
        TimeTaken = 142,
        ClientIp = "203.0.113.42",
        ServerIp = "10.0.1.5",
        Port = 443,
        Host = "shop.example.com",
        UserAgent = "Mozilla/5.0",
        SiteName = "Default Web Site",
        ServerName = "IIS-WEB-01",
        BytesSent = 4096,
        Collection = "sdk-iis-events"
    }.ToLog());

    WriteStep("Sending a LogNginxEvent...");
    await logdb.LogAsync(new LogNginxEvent
    {
        LogType = "access",
        TargetName = "api-backend",
        HostName = "proxy-01",
        RemoteAddress = "198.51.100.23",
        Method = "POST",
        Path = "/api/orders",
        Protocol = "HTTP/2",
        StatusCode = 201,
        ResponseBytes = 1024,
        RequestTime = 0.045,
        ServerName = "api.example.com",
        UserAgent = "curl/7.88.1",
        Message = "POST /api/orders 201 0.045s",
        Collection = "sdk-nginx-events"
    }.ToLog());

    WriteSuccess("Infrastructure log types sent.");

    // ----- Encrypted infrastructure log types -----
    WriteStep("Sending encrypted infrastructure logs...");

    await logdb.LogAsync(new LogIISEvent
    {
        Method = "POST",
        UriStem = "/api/users/profile",
        UriQuery = EncryptionService.Encrypt("ssn=123-45-6789&dob=1990-01-01"),
        Status = 200,
        TimeTaken = 85,
        ClientIp = EncryptionService.Encrypt("203.0.113.42"),
        Username = EncryptionService.Encrypt("alice@example.com"),
        UserAgent = EncryptionService.Encrypt("Mozilla/5.0 (Windows NT 10.0)"),
        SiteName = "Secure API",
        ServerName = "IIS-PCI-01",
        Collection = "sdk-iis-encrypted"
    }.ToLog());

    await logdb.LogAsync(new LogNginxEvent
    {
        LogType = "access",
        Method = "GET",
        Path = "/api/account/balance",
        StatusCode = 200,
        RemoteAddress = EncryptionService.Encrypt("198.51.100.23"),
        UserAgent = EncryptionService.Encrypt("BankApp/2.1 (iOS 17)"),
        Referer = EncryptionService.Encrypt("https://bank.example.com/dashboard"),
        Message = EncryptionService.Encrypt("GET /api/account/balance 200"),
        ServerName = "api.bank.example.com",
        Collection = "sdk-nginx-encrypted"
    }.ToLog());

    await logdb.LogAsync(new LogWindowsEvent
    {
        EventId = 4625,
        ProviderName = "Microsoft-Windows-Security-Auditing",
        Channel = "Security",
        Level = "Warning",
        Computer = EncryptionService.Encrypt("DC-01.corp.local"),
        UserId = EncryptionService.Encrypt("CORP\\admin"),
        IpAddress = EncryptionService.Encrypt("10.0.0.55"),
        Message = EncryptionService.Encrypt("An account failed to log on. Target: admin@corp.local"),
        XmlData = EncryptionService.Encrypt("<Event><Data Name='TargetUserName'>admin</Data></Event>"),
        Collection = "sdk-windows-encrypted"
    }.ToLog());

    WriteSuccess("Encrypted infrastructure logs sent.");

    // ============================================================
    // SCENARIO 3: Encrypted logging
    // Requires LOGDB_SECRET_KEY environment variable to be set.
    // Uses EncryptionService.Encrypt() to encrypt individual fields,
    // then sends via the same logdb.LogAsync() that works for plain logs.
    // ============================================================
    WriteStep("\nScenario 3: Encrypted logging...");

    // ----- Per-field encryption: only sensitive fields are encrypted -----
    WriteStep("Sending an encrypted Log (per-field encryption)...");
    await logdb.LogAsync(new Log
    {
        Application = "com.logdb.sample.sdk",
        Environment = "demo",
        Collection = "sdk-events-encrypted",
        Level = LogDB.Client.Models.LogLevel.Info,
        Message = "User login from sensitive location",                           // plaintext
        UserEmail = EncryptionService.Encrypt("confidential@example.com"),        // encrypted
        IpAddress = EncryptionService.Encrypt("192.168.1.42"),                    // encrypted
        RequestPath = "/api/auth/login",                                           // plaintext
        Source = "auth-service",                                                   // plaintext
        AttributesS = new Dictionary<string, string>
        {
            ["region"] = "eu-west-1",                                              // plaintext
            [EncryptionService.Encrypt("session_token")] = EncryptionService.Encrypt("sk_live_abc123xyz")  // encrypted key+value
        },
        Label = new List<string> { "login", EncryptionService.Encrypt("pii") }    // mixed
    });

    // ----- Fully encrypted Log: all fields encrypted -----
    WriteStep("Sending a fully encrypted Log (all fields)...");
    await logdb.LogAsync(new Log
    {
        Application = EncryptionService.Encrypt("com.logdb.sample.sdk"),
        Environment = EncryptionService.Encrypt("demo"),
        Collection = "sdk-events-encrypted",
        Level = LogDB.Client.Models.LogLevel.Info,
        Message = EncryptionService.Encrypt("Full PII record: John Doe, SSN 123-45-6789"),
        UserEmail = EncryptionService.Encrypt("john.doe@example.com"),
        IpAddress = EncryptionService.Encrypt("10.0.0.1"),
        Source = EncryptionService.Encrypt("pii-service"),
        AttributesS = new Dictionary<string, string>
        {
            [EncryptionService.Encrypt("ssn")] = EncryptionService.Encrypt("123-45-6789")
        },
        Label = new List<string> { EncryptionService.Encrypt("pii"), EncryptionService.Encrypt("gdpr") }
    });

    // ============================================================
    // SCENARIO 4: V1 Encryption (AES-256-GCM + X25519 key wrapping)
    // ============================================================
    // V1 uses authenticated encryption with per-recipient key wrapping.
    // Only the holder of the matching private key can decrypt.
    //
    // KEY SHARING FLOW:
    //   1. The LogDB Desktop App generates an X25519 key pair (Settings > Profile > V1 Encryption)
    //   2. The App keeps the PRIVATE key and displays the PUBLIC key
    //   3. You copy that public key and set it here (env var or config)
    //   4. The SDK encrypts fields using the App's public key
    //   5. Any holder of the matching private key can decrypt â€” you control who gets it
    //
    // Set the recipient's public key (Base64, from LogDB App > Settings > Profile > V1 Encryption):
    //   Windows:   setx LOGDB_V1_PUBLIC_KEY "Base64PublicKeyFromApp"
    //   macOS/Linux: export LOGDB_V1_PUBLIC_KEY="Base64PublicKeyFromApp"

    var recipientPubKeyBase64 = Environment.GetEnvironmentVariable("LOGDB_V1_PUBLIC_KEY");

    if (!string.IsNullOrWhiteSpace(recipientPubKeyBase64))
    {
        WriteStep("V1 Encryption: Using recipient public key from LOGDB_V1_PUBLIC_KEY...");
        var recipientPublicKey = Convert.FromBase64String(recipientPubKeyBase64);
        WriteInfo($"  Recipient Public Key: {recipientPubKeyBase64[..12]}...");

        // Encrypt a sensitive field for the recipient
        var logId = Guid.NewGuid().ToString();
        var timestamp = DateTime.UtcNow.ToString("O");

        var envelope = EncryptionServiceV1.Encrypt(
            plaintext: "jane.doe@example.com",
            fieldName: "UserEmail",
            logId: logId,
            timestamp: timestamp,
            recipientPublicKeys: new[] { recipientPublicKey });

        // Serialize to the wire format (encrypted_v1:Base64...)
        var serialized = EncryptionServiceV1.Serialize(envelope);
        WriteInfo($"  Encrypted ({serialized.Length} chars): {serialized[..40]}...");

        // Send the serialized value as any string field in a Log:
        await logdb.LogAsync(new Log
        {
            Application = "com.logdb.sample.sdk",
            Environment = "demo",
            Collection = "sdk-events-v1-encrypted",
            Level = LogDB.Client.Models.LogLevel.Info,
            Message = "User login with V1-encrypted email",
            UserEmail = serialized,  // <-- V1 encrypted, only the private key holder can decrypt
            AttributesS = new Dictionary<string, string> { ["action"] = "v1_demo" }
        });

        WriteSuccess("  V1 encrypted log sent. The LogDB App will decrypt it with its private key.");
    }
    else
    {
        WriteWarn("V1 Encryption: Skipped â€” LOGDB_V1_PUBLIC_KEY not set.");
        WriteInfo("  To use V1 encryption:");
        WriteInfo("  1. Open LogDB App > Settings > Profile > V1 Encryption");
        WriteInfo("  2. Click 'Generate New Key Pair'");
        WriteInfo("  3. Copy the Public Key shown");
        WriteInfo("  4. Set it: setx LOGDB_V1_PUBLIC_KEY \"<paste-public-key>\"");
        WriteInfo("  5. Re-run this sample");
    }

    // Force flush so read demo is deterministic.
    WriteInfo("\nDemo write actions completed. Flushing buffered writes...");
    await logdb.FlushAsync();
    WriteSuccess("Flush completed.");
    
    var expectedLogTabCount = 1 + visibleLogCount + 2 + (!string.IsNullOrWhiteSpace(recipientPubKeyBase64) ? 1 : 0);
    const int expectedSpecializedLogCount = 9;
    const int expectedCacheCount = 2;
    const int expectedBeatCount = 1;
    
    WriteSection("Expected Results");
    WriteInfo($"Log tab records expected this run: {expectedLogTabCount}");
    WriteInfo("  Includes: 1 immediate log, 24 standard logs, 2 encrypted logs, plus 1 V1 encrypted log when LOGDB_V1_PUBLIC_KEY is set.");
    WriteInfo($"Specialized infrastructure records expected outside the main Log tab: {expectedSpecializedLogCount}");
    WriteInfo("  These go to Windows/IIS/Docker/Nginx specific tables or tabs.");
    WriteInfo($"Cache entries expected: {expectedCacheCount}");
    WriteInfo($"Beat records expected: {expectedBeatCount}");

    WriteSection("Reading Data from LogDB");

    var reader = host.Services.GetRequiredService<ILogDBReader>();
    try
    {
        // Query 1: Read Latest Logs
        WriteInfo($"\n--- 1. Querying Latest Logs for '{logdb.GetType().Name}' ---");
        var logsResponse = await reader.QueryLogs()
            .FromApplication("com.logdb.sample.sdk")
            .OrderByTimestamp(ascending: false)
            .Take(3)
            .ExecuteAsync();

        var latestLogs = logsResponse.Items.Take(3).ToList();

        if (latestLogs.Count == 0)
        {
            WriteWarn("  No logs found for com.logdb.sample.sdk.");
        }

        foreach (var l in latestLogs)
        {
            var levelText = l.Level?.ToString() ?? "Unknown";
            var levelColor = levelText.ToLowerInvariant() switch
            {
                "critical" or "error" or "exception" => ConsoleColor.Red,
                "warning" => ConsoleColor.Yellow,
                "info" => ConsoleColor.Green,
                "debug" or "trace" => ConsoleColor.DarkGray,
                _ => ConsoleColor.Gray
            };

            WriteWithColor($"  [{l.Timestamp}] {levelText}: {l.Message}", levelColor);
        }

        // Query 2: Read The Specific Cache Entry
        WriteInfo($"\n--- 2. Querying Cache (Key: {cacheKey}) ---");
        var sessionList = await reader.QueryCache()
            .WithKeyPattern(cacheKey)
            .ExecuteAsync();

        if (sessionList?.Items?.Count > 0)
        {
            var session = sessionList.Items[0];
            WriteSuccess($"  Cache Hit -> Value: {session.Value}, Collection: {session.Collection}");
        }
        else
        {
            WriteWarn("  Cache Miss -> Entry not yet available or key mismatch.");
        }

        // Query 3: Distinct Collections
        WriteInfo("\n--- 3. Distinct Log Collections ---");
        var collections = await reader.GetDistinctValuesAsync("collection");
        WriteInfo($"  Collections: {string.Join(", ", collections)}");
    }
    catch (RpcException rpcEx) when (rpcEx.StatusCode == StatusCode.Unknown &&
                                     rpcEx.Status.Detail.Contains("Customer 0 not found", StringComparison.OrdinalIgnoreCase))
    {
        WriteError("Reader query failed: grpc-server still reports Customer 0.");
        WriteWarn("Recommended fix: deploy grpc-server with API-key account fallback in GetUserAccounts.");
    }
    catch (RpcException rpcEx)
    {
        WriteError($"Reader query failed: {rpcEx.StatusCode} - {rpcEx.Status.Detail}");
        WriteWarn("Writes are still valid. Verify LOGDB_GRPC_SERVER_URL or reader endpoint availability.");
    }

    WriteSuccess("\nRun completed successfully.");
}
catch (Exception ex)
{
    logger.LogError(ex, "An error occurred during the demo execution. Ensure your API Key is valid.");
    WriteError("\nRun failed. See error details above.");
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
static void WriteWarn(string message) => WriteWithColor(message, ConsoleColor.Yellow);
static void WriteError(string message) => WriteWithColor(message, ConsoleColor.Red);

static void WriteWithColor(string message, ConsoleColor color)
{
    var originalColor = Console.ForegroundColor;
    Console.ForegroundColor = color;
    Console.WriteLine(message);
    Console.ForegroundColor = originalColor;
}

