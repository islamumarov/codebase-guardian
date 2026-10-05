using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace CodebaseGuardian.Processes;

/// <summary>Runs external processes with an argument list (no shell), concurrent output capture, a timeout and tree kill.</summary>
public sealed class ProcessRunner : IProcessRunner
{
    private static readonly TimeSpan StreamDrainGrace = TimeSpan.FromSeconds(2);

    public async Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        cancellationToken.ThrowIfCancellationRequested();

        if (!Directory.Exists(spec.WorkingDirectory))
        {
            // Starting a process in a missing directory fails like a missing executable; report it as what it is.
            throw new DirectoryNotFoundException($"Working directory '{spec.WorkingDirectory}' does not exist.");
        }

        var startInfo = new ProcessStartInfo(spec.FileName)
        {
            WorkingDirectory = spec.WorkingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in spec.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (spec.Environment is not null)
        {
            foreach (var (key, value) in spec.Environment)
            {
                startInfo.Environment[key] = value;
            }
        }

        var stopwatch = Stopwatch.StartNew();
        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Win32Exception exception)
        {
            throw new ExecutableNotFoundException(spec.FileName, exception);
        }

        process.StandardInput.Close();
        var standardOutput = ReadCappedAsync(process.StandardOutput.BaseStream, spec.MaxOutputBytes);
        var standardError = ReadCappedAsync(process.StandardError.BaseStream, spec.MaxOutputBytes);

        using var timeout = new CancellationTokenSource(spec.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        var timedOut = false;
        var abandonedDrain = false;
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            KillTree(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            timedOut = true;
        }

        // After a kill, a surviving grandchild can keep a pipe open; do not wait for it forever.
        var drained = Task.WhenAll(standardOutput, standardError);
        if (timedOut)
        {
            await Task.WhenAny(drained, Task.Delay(StreamDrainGrace, CancellationToken.None)).ConfigureAwait(false);
        }
        else
        {
            // The process exited, but a descendant (a backgrounded server) can still hold a pipe open. The timeout and
            // the caller's token bound this wait too.
            try
            {
                await drained.WaitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                // Keep the exit code; the output captured so far is dropped with the unfinished readers.
                abandonedDrain = true;
            }
        }

        var (output, outputTruncated) = standardOutput.IsCompletedSuccessfully ? standardOutput.Result : (string.Empty, false);
        var (error, errorTruncated) = standardError.IsCompletedSuccessfully ? standardError.Result : (string.Empty, false);
        stopwatch.Stop();

        return new ProcessResult(
            timedOut ? -1 : process.ExitCode,
            output,
            error,
            timedOut,
            outputTruncated || errorTruncated || abandonedDrain,
            stopwatch.Elapsed);
    }

    private static void KillTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            // Already exited.
        }
    }

    private static async Task<(string Text, bool Truncated)> ReadCappedAsync(Stream stream, int maxBytes)
    {
        using var captured = new MemoryStream();
        var buffer = new byte[8192];
        var truncated = false;
        int read;
        while ((read = await stream.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            var room = maxBytes - (int)captured.Length;
            if (room > 0)
            {
                captured.Write(buffer, 0, Math.Min(read, room));
            }

            // Keep draining so the child never blocks on a full pipe.
            truncated |= read > room;
        }

        return (new UTF8Encoding(false).GetString(captured.GetBuffer(), 0, (int)captured.Length), truncated);
    }
}
