using XCL2.App.Services;
using System.Text.Json;

static void Require(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
var queue = DownloadQueueService.Instance;
var folder = Path.GetFullPath("queue-regression-minecraft");

// Awaiting a prerequisite returns only the actual result and does not grant background retry.
var successGate = Signal();
var success = queue.RunAndWaitAsync("Java prerequisite", async (_, _) => { await successGate.Task; return 42; });
var successItem = queue.Items.Last();
Require(!success.IsCompleted && successItem.IsRunning, "Prerequisite finished too early");
Require(!successItem.CanPause && !successItem.CanResume, "Awaited operation offered pause/retry");
successItem.RegisterInstallationTarget("dynamic-target");
try
{
    queue.StartNew("conflicting dynamic target", (_, _) => Task.CompletedTask, "dynamic-target", folder);
    throw new Exception("Dynamically registered version allowed a concurrent writer");
}
catch (InvalidOperationException) { }
successGate.SetResult(true);
Require(await success == 42 && successItem.Status == DownloadQueueItemStatus.Completed, "Wrong successful result");

// Failed prerequisite must throw and remain visibly failed, with no background replay.
try
{
    await queue.RunAndWaitAsync<int>("broken prerequisite", (_, _) => Task.FromException<int>(new IOException("fixture failure")));
    throw new Exception("Failure was returned as success");
}
catch (IOException ex) when (ex.Message == "fixture failure") { }
var failed = queue.Items.Last();
Require(failed.Status == DownloadQueueItemStatus.Failed && !failed.CanResume, "Failed prerequisite was retryable");

// Canceling a version task keeps its reservation until cleanup actually finishes.
var cleanupStarted = Signal();
var allowCleanup = Signal();
using var callerCts = new CancellationTokenSource();
var canceled = queue.RunAndWaitAsync("repair 1.20.1", async (_, ct) =>
{
    try { await Task.Delay(Timeout.Infinite, ct); return true; }
    finally { cleanupStarted.TrySetResult(true); await allowCleanup.Task; }
}, callerCts.Token, "1.20.1", folder);
var canceledItem = queue.Items.Last();
callerCts.Cancel();
await cleanupStarted.Task;
Require(!canceled.IsCompleted && canceledItem.IsRunning, "Cancellation returned before cleanup");
Require(queue.FindActiveForVersion(folder + Path.DirectorySeparatorChar, "1.20.1") == canceledItem,
    "Canceled-but-writing task lost its reservation");
try
{
    queue.StartNew("conflicting install", (_, _) => Task.CompletedTask, "1.20.1", folder);
    throw new Exception("Concurrent repair/install was accepted");
}
catch (InvalidOperationException) { }
allowCleanup.SetResult(true);
try { await canceled; throw new Exception("Canceled prerequisite was successful"); }
catch (OperationCanceledException) { }
Require(canceledItem.Status == DownloadQueueItemStatus.Canceled && !canceledItem.IsRunning, "Wrong canceled state");
Require(queue.FindActiveForVersion(folder, "1.20.1") == null, "Finished task retained reservation");

// A running installer cannot be paused, canceled, or replayed by download controls.
var installGate = Signal();
var installer = queue.StartNew("driver installer", async (_, _) => await installGate.Task,
    supportsPause: false, supportsRetry: false, supportsCancel: false);
Require(!installer.CanPauseOrResume && !installer.CanCancel, "Unsafe installer controls were enabled");
queue.Cancel(installer);
Require(installer.IsRunning && installer.Status == DownloadQueueItemStatus.Downloading, "Installer was falsely canceled");
installGate.SetResult(true);
Require(await installer.Completion == DownloadQueueItemStatus.Completed, "Installer did not complete");
Require(installer.CanCancel && installer.CancelLabel == "移除", "Terminal installer cannot be dismissed");

// Real pause waits for cleanup, and resume starts a distinct attempt with a fresh completion.
var pauseCleanup = Signal();
var pauseObserved = Signal();
var attempts = 0;
var resumable = queue.StartNew("pause fixture", async (_, ct) =>
{
    if (++attempts > 1) return;
    try { await Task.Delay(Timeout.Infinite, ct); }
    finally { pauseObserved.SetResult(true); await pauseCleanup.Task; }
}, "pause-version", folder);
queue.Pause(resumable);
await pauseObserved.Task;
Require(!resumable.CanResume, "Resume allowed before old writer stopped");
pauseCleanup.SetResult(true);
Require(await resumable.Completion == DownloadQueueItemStatus.Paused && resumable.CanResume, "Pause state incorrect");
queue.Resume(resumable);
Require(await resumable.Completion == DownloadQueueItemStatus.Completed && attempts == 2, "Resume did not start one fresh attempt");
// ZIP and MSI come from distinct official metadata. Never apply a ZIP checksum to an invented MSI URL.
using var metadata = JsonDocument.Parse(JsonSerializer.Serialize(new
{
    package = new { link = "https://github.com/adoptium/fixture/releases/download/v1/java.zip", name = "java.zip", checksum = new string('a', 64), size = 123L },
    installer = new { link = "https://github.com/adoptium/fixture/releases/download/v1/setup.msi", name = "setup.msi", checksum = new string('b', 64), size = 456L }
}));
var zip = JavaDownloadSource.FromAdoptiumBinary(metadata.RootElement, false);
var msi = JavaDownloadSource.FromAdoptiumBinary(metadata.RootElement, true);
Require(zip.FileName == "java.zip" && zip.Sha256 == new string('a', 64) && zip.Size == 123, "ZIP metadata lost");
Require(msi.FileName == "setup.msi" && msi.Sha256 == new string('b', 64) && msi.Size == 456, "MSI used ZIP metadata");
using var noInstaller = JsonDocument.Parse("{\"package\":{}}");
try { JavaDownloadSource.FromAdoptiumBinary(noInstaller.RootElement, true); throw new Exception("MSI URL was guessed"); }
catch (InvalidOperationException) { }
using var noHash = JsonDocument.Parse("{\"installer\":{\"link\":\"https://example.com/setup.msi\",\"name\":\"setup.msi\"}}");
try { JavaDownloadSource.FromAdoptiumBinary(noHash.RootElement, true); throw new Exception("Unverifiable MSI was accepted"); }
catch (InvalidOperationException) { }
Console.WriteLine("Download queue and Java metadata regression checks passed.");
