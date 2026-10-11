using System.Diagnostics;
using XCL2.App.Models;
using XCL2.App.Services;

var checkedCount = 0;
void Check(bool passed, string message)
{
    if (!passed) throw new InvalidOperationException(message);
    checkedCount++;
}

var raw = "--accessToken command-token\nAuthorization: Bearer bearer-token\n" +
          "{\"refresh_token\":\"json-token\",'api_key':'single-quoted-key'}\n" +
          "minecraftAccessToken=cached-token\nopaque-secret-from-settings\nCaused by: example.ModFailure";
var scrubbed = CrashAiLogService.PrepareForAi(raw, new[] { "opaque-secret-from-settings" }, out var truncated);
foreach (var secret in new[] { "command-token", "bearer-token", "json-token", "single-quoted-key", "cached-token", "opaque-secret-from-settings" })
    Check(!scrubbed.Contains(secret, StringComparison.Ordinal), "Credential leaked: " + secret);
Check(scrubbed.Contains("example.ModFailure"), "Diagnostic evidence was removed");
Check(!truncated, "Small log was marked as truncated");

var longLog = "ENVIRONMENT_HEAD\n" + new string('x', 100000) + "\nROOT_CAUSE_TAIL";
var bounded = CrashAiLogService.PrepareForAi(longLog, Array.Empty<string>(), out truncated);
Check(truncated && bounded.Length < 32100, "Large submission was not bounded");
Check(bounded.Contains("ENVIRONMENT_HEAD") && bounded.Contains("ROOT_CAUSE_TAIL"), "Environment or root-cause tail missing");

var directory = Path.Combine(Path.GetTempPath(), "XCL2_CrashAiRegression_" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(Path.Combine(directory, "crash-reports"));
Directory.CreateDirectory(Path.Combine(directory, "logs"));
try
{
    using var nativeProcess = new Process();
    var process = new GameProcessInfo(nativeProcess, "test-instance", "offline-account", directory);
    process.OutputBuffer.AppendLine("CURRENT_PROCESS_ERROR command-token");
    var incident = DateTime.Now;
    void Log(string relativePath, string text, DateTime timestamp)
    {
        var path = Path.Combine(directory, relativePath);
        File.WriteAllText(path, text);
        File.SetLastWriteTime(path, timestamp);
    }

    Log("crash-reports/old.txt", "OLD_INCIDENT_MUST_NOT_APPEAR", process.StartedAt.AddHours(-1));
    Log("crash-reports/current.txt", "CURRENT_CRASH_REPORT", incident);
    Log("logs/latest.log", "LOG_HEAD\n" + new string('z', 200000) + "\nLAST_ERROR_TARGET", incident);
    Log("logs/debug.log", "NEXT_GAME_MUST_NOT_APPEAR", incident.AddMinutes(10));
    var files = CrashAiLogService.GetCurrentIncidentFiles(process, incident);
    Check(files.Count == 2, "Stale or later-game logs were included");
    Check(Path.GetFileName(files[0]) == "current.txt", "Crash report was not preferred");
    var prompt = CrashAiLogService.BuildPrompt(process, incident, "game exited", new[] { "command-token" });
    Check(prompt.Contains("CURRENT_CRASH_REPORT") && prompt.Contains("CURRENT_PROCESS_ERROR"), "Current incident evidence missing");
    Check(prompt.Contains("LAST_ERROR_TARGET"), "Large-file root-cause tail missing");
    Check(!prompt.Contains("OLD_INCIDENT_MUST_NOT_APPEAR") && !prompt.Contains("NEXT_GAME_MUST_NOT_APPEAR"), "Wrong incident submitted");
    Check(!prompt.Contains("command-token"), "Known process credential leaked");

    using var cancelled = new CancellationTokenSource();
    cancelled.Cancel();
    bool cancellationObserved = false;
    try { CrashAiLogService.BuildPrompt(process, incident, "game exited", Array.Empty<string>(), cancelled.Token); }
    catch (OperationCanceledException) { cancellationObserved = true; }
    Check(cancellationObserved, "Log preparation ignored cancellation");
}
finally { Directory.Delete(directory, recursive: true); }

Console.WriteLine($"Crash AI regression passed: {checkedCount} assertions.");
