using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace YakUI.Platform.Windows;

/// <summary>
/// Один экземпляр <c>YakUI.App</c> на папку данных (NFR-05a). Повторный запуск сигналит первому экземпляру
/// «активируй окно» и завершается. <c>YakUI.Cli</c> проверяет <see cref="IsAppRunning"/>, чтобы не выполнять
/// монопольные операции при открытом окне.
/// </summary>
public sealed partial class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activate;
    private readonly RegisteredWaitHandle _registration;

    private SingleInstance(Mutex mutex, EventWaitHandle activate)
    {
        _mutex = mutex;
        _activate = activate;
        _registration = ThreadPool.RegisterWaitForSingleObject(_activate, (_, _) => ActivationRequested?.Invoke(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    /// <summary>Другой запуск попросил показать окно (вызывается в потоке пула).</summary>
    public event Action? ActivationRequested;

    /// <summary>Захватить право быть единственным окном; null — окно уже открыто.</summary>
    public static SingleInstance? TryAcquire(string dataDirectory)
    {
        var (mutexName, eventName) = Names(dataDirectory);
        var mutex = new Mutex(initiallyOwned: true, mutexName, out var createdNew);
        if (!createdNew)
        {
            // Mutex мог остаться от аварийно завершённого процесса: тогда он «брошен» и захватывается.
            try
            {
                if (!mutex.WaitOne(0))
                {
                    mutex.Dispose();
                    return null;
                }
            }
            catch (AbandonedMutexException)
            {
                // захвачен
            }
        }

        var activate = new EventWaitHandle(false, EventResetMode.AutoReset, eventName);
        return new SingleInstance(mutex, activate);
    }

    /// <summary>Открыто ли окно YakUI для этой папки данных.</summary>
    public static bool IsAppRunning(string dataDirectory)
    {
        if (!Mutex.TryOpenExisting(Names(dataDirectory).Mutex, out var m))
            return false;
        using (m)
        {
            try
            {
                // Брошенный mutex (процесс упал) окном не считается.
                if (m.WaitOne(0))
                {
                    m.ReleaseMutex();
                    return false;
                }

                return true;
            }
            catch (AbandonedMutexException)
            {
                m.ReleaseMutex();
                return false;
            }
        }
    }

    /// <summary>Попросить открытое окно выйти на передний план.</summary>
    public static bool SignalExisting(string dataDirectory)
    {
        if (!EventWaitHandle.TryOpenExisting(Names(dataDirectory).Event, out var e))
            return false;
        using (e)
        {
            // Мы — процесс, запущенный пользователем, поэтому можем разрешить другому процессу забрать фокус.
            AllowSetForegroundWindow(AsfwAny);
            return e.Set();
        }
    }

    internal static (string Mutex, string Event) Names(string dataDirectory)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(dataDirectory).TrimEnd('\\', '/').ToUpperInvariant())))[..16];
        return ($@"Local\YakUI.App.{key}", $@"Local\YakUI.App.Activate.{key}");
    }

    public void Dispose()
    {
        _registration.Unregister(null);
        _activate.Dispose();
        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // освобождается не из потока-владельца — mutex закроется вместе с процессом
        }

        _mutex.Dispose();
    }

    private const int AsfwAny = -1;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(int processId);
}
