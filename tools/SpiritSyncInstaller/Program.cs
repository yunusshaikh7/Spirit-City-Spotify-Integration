using System.Diagnostics;

return RunScript("install-spirit-sync-launcher.ps1", args);

static int RunScript(string scriptName, IReadOnlyList<string> scriptArgs)
{
    var packageRoot = AppContext.BaseDirectory;
    var scriptPath = Path.Combine(packageRoot, "scripts", scriptName);

    if (!File.Exists(scriptPath))
    {
        Console.Error.WriteLine($"Install script was not found: {scriptPath}");
        Console.Error.WriteLine("Run this installer from the extracted Spirit Sync release folder.");
        return 1;
    }

    var startInfo = new ProcessStartInfo
    {
        FileName = "powershell.exe",
        WorkingDirectory = packageRoot,
        UseShellExecute = false,
    };
    startInfo.ArgumentList.Add("-NoProfile");
    startInfo.ArgumentList.Add("-ExecutionPolicy");
    startInfo.ArgumentList.Add("Bypass");
    startInfo.ArgumentList.Add("-File");
    startInfo.ArgumentList.Add(scriptPath);
    foreach (var arg in scriptArgs)
    {
        startInfo.ArgumentList.Add(arg);
    }

    using var process = Process.Start(startInfo);

    if (process is null)
    {
        Console.Error.WriteLine("Could not start PowerShell.");
        return 1;
    }

    process.WaitForExit();
    return process.ExitCode;
}
