using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace SupplierFeed.Tests.Support;

/// <summary>
/// A real, separate server process running the built API from the test output folder. Several of these
/// sharing one database file are real "multiple instances": separate OS processes, separate connection
/// pools, only the file's locks in common.
/// </summary>
public sealed class ServerProcess : IDisposable
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(30);
    private static readonly Regex Listening = new(@"Now listening on:\s*(http://\S+)", RegexOptions.Compiled);

    private readonly Process _process;

    private ServerProcess(Process process, Uri baseAddress)
    {
        _process = process;
        BaseAddress = baseAddress;
    }

    public Uri BaseAddress { get; }

    public static async Task<ServerProcess> StartAsync(string connectionString, params (string Key, string Value)[] settings)
    {
        var startInfo = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "SupplierFeed.Api.dll"));
        startInfo.ArgumentList.Add("--urls");
        startInfo.ArgumentList.Add("http://127.0.0.1:0"); // ephemeral port, read back from the startup log
        startInfo.Environment["ConnectionStrings__Default"] = connectionString;
        startInfo.Environment["Logging__LogLevel__Microsoft.Hosting.Lifetime"] = "Information";
        foreach (var (key, value) in settings)
            startInfo.Environment[key.Replace(":", "__")] = value;

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var output = new StringBuilder();
        var listening = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Capture(string? line)
        {
            if (line is null) return;
            lock (output) output.AppendLine(line);
            var match = Listening.Match(line);
            if (match.Success) listening.TrySetResult(new Uri(match.Groups[1].Value));
        }

        process.OutputDataReceived += (_, e) => Capture(e.Data);
        process.ErrorDataReceived += (_, e) => Capture(e.Data);
        process.Exited += (_, _) =>
        {
            string log;
            lock (output) log = output.ToString();
            listening.TrySetException(new InvalidOperationException($"Server exited before it was listening:{Environment.NewLine}{log}"));
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (await Task.WhenAny(listening.Task, Task.Delay(StartupTimeout)) != listening.Task)
        {
            Kill(process);
            string log;
            lock (output) log = output.ToString();
            throw new TimeoutException($"Server did not start within {StartupTimeout.TotalSeconds}s:{Environment.NewLine}{log}");
        }

        try
        {
            var address = await listening.Task;
            return new ServerProcess(process, address);
        }
        catch
        {
            Kill(process);
            throw;
        }
    }

    public HttpClient CreateClient() => new() { BaseAddress = BaseAddress };

    public void Dispose() => Kill(_process);

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            process.WaitForExit(5_000);
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }
        finally
        {
            process.Dispose();
        }
    }
}
