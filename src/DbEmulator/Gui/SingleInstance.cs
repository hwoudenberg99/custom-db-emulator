using System.Security.Cryptography;
using System.Text;

namespace DbEmulator.Gui;

/// <summary>Starting the exe again while the app runs brings the existing window to the front.</summary>
internal static class SingleInstance
{
    public static IDisposable Listen(string dataDirectory, Action show)
    {
        var signal = new EventWaitHandle(false, EventResetMode.AutoReset, Name(dataDirectory));
        var stop = new ManualResetEvent(false);
        var thread = new Thread(() =>
        {
            var handles = new WaitHandle[] { signal, stop };
            while (WaitHandle.WaitAny(handles) == 0)
            {
                show();
            }
        })
        {
            IsBackground = true,
            Name = "DbEmulator show signal",
        };
        thread.Start();
        return new Listener(signal, stop);
    }

    /// <summary>Asks a running instance to show its window. False when none is listening.</summary>
    public static bool SignalShow(string dataDirectory)
    {
        if (!EventWaitHandle.TryOpenExisting(Name(dataDirectory), out var signal))
        {
            return false;
        }

        using (signal)
        {
            signal.Set();
        }

        return true;
    }

    private static string Name(string dataDirectory) =>
        "Local\\DbEmulator-show-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dataDirectory.ToUpperInvariant())))[..16];

    private sealed class Listener(EventWaitHandle signal, ManualResetEvent stop) : IDisposable
    {
        public void Dispose()
        {
            stop.Set();
            signal.Dispose();
        }
    }
}
