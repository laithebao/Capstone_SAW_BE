using System.Diagnostics;
using System.Text;

namespace SAW.Testing;

internal static class Processes
{
    internal static readonly List<string> Secrets = [];
    internal static string Redact(string value)
    {
        foreach (var secret in Secrets.Where(s => s.Length > 3)) value = value.Replace(secret, "[redacted]", StringComparison.Ordinal);
        return value;
    }

    internal static async Task<(int Code, string Output)> Run(string command, IEnumerable<string> args,
        string directory, IDictionary<string, string?> environment, bool quiet = false)
    {
        var start = new ProcessStartInfo(command) { WorkingDirectory = directory, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true };
        if (OperatingSystem.IsWindows() && command == "npm")
        {
            start.FileName = "cmd.exe";
            start.ArgumentList.Add("/d"); start.ArgumentList.Add("/c"); start.ArgumentList.Add("npm.cmd");
        }
        foreach (var arg in args) start.ArgumentList.Add(arg);
        foreach (var entry in environment)
            if (entry.Value is null) start.Environment.Remove(entry.Key); else start.Environment[entry.Key] = entry.Value;
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Cannot start {command}.");
        var output = new StringBuilder();
        async Task Drain(StreamReader reader)
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                var safe = Redact(line);
                lock (output) output.AppendLine(safe);
                if (!quiet) Console.WriteLine(safe);
            }
        }
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; if (!process.HasExited) process.Kill(entireProcessTree: true); };
        Console.CancelKeyPress += cancel;
        try
        {
            await Task.WhenAll(Drain(process.StandardOutput), Drain(process.StandardError), process.WaitForExitAsync());
            return (process.ExitCode, output.ToString());
        }
        finally { Console.CancelKeyPress -= cancel; }
    }

    internal static async Task Checked(string command, IEnumerable<string> args, string directory, IDictionary<string, string?> env)
    {
        var result = await Run(command, args, directory, env);
        if (result.Code != 0) throw new InvalidOperationException($"{command} exited with code {result.Code}. See output above.");
    }
}
