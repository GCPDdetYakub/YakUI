using System.Diagnostics;
using System.Text;
using YakUI.Core.Platform;

namespace YakUI.Platform.Windows;

/// <summary>
/// Запуск процессов. Вывод читается как UTF-8; если в нём окажутся искажённые символы (например, в имени
/// пользователя), ядро на это не опирается — из путей берётся только окончание <c>packages\&lt;N.0&gt;</c>.
/// </summary>
public sealed class WindowsProcessRunner : IProcessRunner
{
    public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken = default) =>
        RunAsync(fileName, arguments, timeout, null, cancellationToken);

    public async Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, IReadOnlyDictionary<string, string?>? environment, CancellationToken cancellationToken = default)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(fileName) ?? Environment.CurrentDirectory,
        };
        foreach (var a in arguments)
            psi.ArgumentList.Add(a);
        foreach (var (name, value) in environment ?? new Dictionary<string, string?>())
        {
            if (value is null)
                psi.Environment.Remove(name);
            else
                psi.Environment[name] = value;
        }

        var sw = Stopwatch.StartNew();
        using var process = new Process { StartInfo = psi };
        process.Start();
        process.StandardInput.Close(); // yak не должен ждать ввода

        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = !cancellationToken.IsCancellationRequested;
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // процесс уже завершился
            }

            if (!timedOut)
                throw;
        }

        var outText = await stdout.ConfigureAwait(false);
        var errText = await stderr.ConfigureAwait(false);
        sw.Stop();

        return new ProcessResult(timedOut ? -1 : process.ExitCode, outText, errText, sw.Elapsed, timedOut);
    }
}
