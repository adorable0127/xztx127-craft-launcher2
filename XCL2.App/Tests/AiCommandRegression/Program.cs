using System.Diagnostics;
using System.Text;
using System.Text.Json;
using XCL2.App.Models;
using XCL2.App.Services;

var checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + name);
    checks++;
}
void Converts(string? shell, string input, string output, string expectedShell = "powershell")
{
    var result = WindowsCommandAdapter.Prepare(shell, input);
    Check(result.Error == null && result.Converted && result.Command == output && result.Shell == expectedShell,
        $"conversion of {input}: {result}");
}
void Preserves(string input, string? shell = null)
{
    var result = WindowsCommandAdapter.Prepare(shell, input);
    Check(result.Error == null && !result.Converted && result.Command == input, "preserves " + input);
}
void Rejects(string input, string? shell = null)
{
    var result = WindowsCommandAdapter.Prepare(shell, input);
    Check(result.Error != null && !result.Converted && result.Command == input, "rejects without partial conversion: " + input);
}

Converts(null, "dotnet --info > /dev/null 2>&1", "dotnet --info > $null 2>&1");
Converts(" CMD ", "dotnet --info 2>/dev/null", "dotnet --info 2>NUL", "cmd");
Converts(null, "Write-Output x >> '/dev/null'", "Write-Output x >> $null");
Converts(null, "dotnet --info &>/dev/null", "dotnet --info *>$null");
Converts(null, "dotnet --info &>> \"out log.txt\"", "dotnet --info *>> \"out log.txt\"");
Converts(null, "Write-Output '/dev/null' > /dev/null", "Write-Output '/dev/null' > $null");
Converts(null, "ls -la", "Get-ChildItem -Force");
Converts("bash", "pwd", "Get-Location");
Converts(null, "\nls -al\r\n", "Get-ChildItem -Force");
Converts(null, "mkdir -p 'a b'", "New-Item -ItemType Directory -Path 'a b' -Force");
Converts(null, "cp -r 'a b' 'c d'", "Copy-Item -LiteralPath 'a b' -Destination 'c d' -Recurse");
Converts(null, "mv a.txt b.txt", "Move-Item -LiteralPath 'a.txt' -Destination 'b.txt'");
Converts(null, "rm -rf temp", "Remove-Item -LiteralPath 'temp' -Recurse -Force");
Converts(null, "cat 'a b.txt'", "Get-Content -LiteralPath 'a b.txt'");
Converts("bash", "export XCL_TEST=hello", "$env:XCL_TEST = 'hello'");

Preserves("Write-Output '> /dev/null && cat <<EOF'");
Preserves("Write-Output \"a `\" > /dev/null\"");
Preserves("Write-Output https://example.com/dev/null");
Preserves("Write-Output 1 # > /dev/null");
Preserves("<# outer <# > /dev/null #> && <<EOF #> Write-Output 1");
Preserves("$text = @'\n> /dev/null && cat <<EOF\n'@ | Out-String\nWrite-Output $text");
Preserves("'pwd'");
Preserves("ls -Force $env:TEMP");
Preserves("cat $path | Out-String");
Preserves("mkdir -Path 'C:\\test' -Force");
Preserves("mkdir \"C:\\a b\"", "cmd");
Preserves("echo ok && echo done", "cmd");
Preserves("type < input.txt", "cmd");
Preserves("Write-Output `> /dev/null");
Rejects("echo ok && echo done");
Rejects("echo ok || echo failed");
Rejects("python - <<'PY'\nprint('hello')\nPY");
Rejects("cat <<EOF > output.txt\n$HOME\nEOF");
Rejects("cat <<EOF > output.txt\nhello\\n\nEOF");
Rejects("cat <<'EOF' > output.txt\nmissing terminator");
Rejects("cat <<'EOF' > output.txt\ntext\nEOF\nWrite-Output unsafe");
Rejects("cat <<'EOF' > /tmp/output.txt\ntext\nEOF");
Rejects("ls -la | grep x");
Rejects("ls --color=never");
Rejects("cp -z a b");
Rejects("rm -rf $target");
Rejects("rm -rf /tmp/data");
Rejects("rm -rf *.tmp");
Rejects("mkdir -p my\\ folder");
Rejects("echo hi", "bash");
Rejects("echo hi", "fish");
Rejects("sudo apt update");
Rejects("python < input.txt");
Rejects("Write-Output 'unterminated");
Rejects("dotnet --info &>/dev/null", "cmd");
Rejects("");

var body = "你好 'quoted' $HOME `literal` > /dev/null\n";
var heredoc = WindowsCommandAdapter.Prepare("bash", "cat > '中文 文件.txt' <<'EOF'\n" + body + "EOF");
Check(heredoc.Error == null && heredoc.Converted && heredoc.Shell == "powershell"
    && heredoc.Command.Contains("WriteAllText") && heredoc.Command.Contains("''quoted''")
    && heredoc.Command.Contains("$HOME `literal` > /dev/null") && heredoc.Command.Contains("UTF8Encoding"),
    "quoted heredoc retains body literally with UTF8 encoding");
var appended = WindowsCommandAdapter.Prepare("cmd", "cat <<'END' >> out.txt\r\nappend\r\nEND\r\n");
Check(appended.Error == null && appended.Command.Contains("AppendAllText") && appended.Command.Contains("append\r\n"),
    "append heredoc and CRLF preserve exact payload");
var literal = WindowsCommandAdapter.Prepare(null, "cat <<EOF > out.txt\nhello\nEOF");
Check(literal.Error == null && literal.Converted, "unquoted heredoc without expansion is supported");
var empty = WindowsCommandAdapter.Prepare(null, "cat > out.txt <<'EOF'\nEOF");
Check(empty.Error == null && empty.Command.Contains(", '', "), "empty heredoc creates empty file");

// All combinations, including stale/incomplete persisted permission chains.
for (var bits = 0; bits < 32; bits++)
{
    var cfg = new AiAssistantConfig
    {
        FullDiskAccess = (bits & 1) != 0, AbsoluteAccess = (bits & 2) != 0,
        UnrestrictedAccess = (bits & 4) != 0, SystemControlAccess = (bits & 8) != 0,
        AllowShellCommands = (bits & 16) != 0
    };
    var unrestricted = (bits & 7) == 7;
    var canRun = (bits & 16) != 0 || (bits & 15) == 15;
    Check(AiShellPolicy.CanRun(cfg) == canRun, "permission gate " + bits);
    Check(AiShellPolicy.RequiresConfirmation(cfg) == !unrestricted, "confirmation policy " + bits);
    var prompt = AiShellPolicy.BuildPrompt(cfg);
    Check(!canRun ? prompt.Contains("未开启") : unrestricted ? prompt.Contains("不再弹出") && !prompt.Contains("每次执行需")
        : prompt.Contains("每次执行需"), "prompt matches effective permission " + bits);
    Check(cfg.AllowShellCommands == ((bits & 16) != 0), "policy never enables permission " + bits);
    AiShellPolicy.NormalizeGrantedShellPermission(cfg);
    Check(cfg.AllowShellCommands == canRun, "saved Shell toggle matches existing highest-access capability " + bits);
    Check(AiShellPolicy.CanRun(cfg) == canRun && AiShellPolicy.RequiresConfirmation(cfg) == !unrestricted,
        "normalization preserves effective capability and confirmation policy " + bits);
}
Check(!AiShellPolicy.CanRun(null) && AiShellPolicy.RequiresConfirmation(null), "missing config is fail closed");
var highestAccess = new AiAssistantConfig
{
    FullDiskAccess = true, AbsoluteAccess = true, UnrestrictedAccess = true, SystemControlAccess = true,
    AllowShellCommands = false
};
AiShellPolicy.NormalizeGrantedShellPermission(highestAccess);
var highestReloaded = JsonSerializer.Deserialize<AiAssistantConfig>(JsonSerializer.Serialize(highestAccess))!;
Check(highestReloaded.AllowShellCommands && AiShellPolicy.CanRun(highestReloaded)
    && !AiShellPolicy.RequiresConfirmation(highestReloaded), "highest-access Shell default survives configuration reload");
highestReloaded.UnrestrictedAccess = false;
Check(AiShellPolicy.RequiresConfirmation(highestReloaded), "leaving unrestricted access restores per-command confirmation");
var activity = new AiAssistantActivity
{
    Command = heredoc.Command, OriginalCommand = "cat...", CommandConversion = WindowsCommandAdapter.ConversionWarning
};
var restored = JsonSerializer.Deserialize<AiAssistantActivity>(JsonSerializer.Serialize(activity))!;
Check(restored.OriginalCommand == activity.OriginalCommand && restored.Command == activity.Command
    && restored.Clone().CommandConversion == activity.CommandConversion, "conversion survives history and snapshots");

// On Windows, execute only harmless temporary-file operations using the real PowerShell 5.1 entry point.
if (OperatingSystem.IsWindows())
{
    var directory = Path.Combine(Path.GetTempPath(), "XCL2-command-regression-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        void Run(string command)
        {
            var psi = new ProcessStartInfo("powershell.exe") { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add("-NoProfile"); psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-Command"); psi.ArgumentList.Add(command);
            using var process = Process.Start(psi)!;
            if (!process.WaitForExit(15000)) { process.Kill(entireProcessTree: true); throw new Exception("PowerShell test timed out"); }
            Check(process.ExitCode == 0, "PowerShell execution: " + command);
        }
        Run(heredoc.Command);
        Check(File.ReadAllBytes(Path.Combine(directory, "中文 文件.txt")).SequenceEqual(new UTF8Encoding(false).GetBytes(body)),
            "PowerShell writes exact UTF8 bytes, without interpolation or BOM");
        Run(appended.Command); Run(appended.Command);
        Check(File.ReadAllText(Path.Combine(directory, "out.txt")) == "append\r\nappend\r\n", "append never truncates prior content");
        Run(WindowsCommandAdapter.Prepare(null, "Write-Output x > /dev/null").Command);
        Check(!Directory.Exists(Path.Combine(directory, "dev")), "null redirection creates no fake Linux path");
    }
    finally { Directory.Delete(directory, recursive: true); }
}
else Console.WriteLine("SKIP: actual PowerShell execution requires Windows.");
Console.WriteLine($"PASS: {checks} AI command regression checks");
