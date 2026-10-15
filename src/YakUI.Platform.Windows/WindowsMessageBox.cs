using System.Runtime.InteropServices;

namespace YakUI.Platform.Windows;

/// <summary>Системное окно сообщения — для ошибок до запуска Avalonia (16.1).</summary>
public static partial class WindowsMessageBox
{
    private const uint MbIconWarning = 0x30;

    public static void Warning(string title, string text) => MessageBoxW(IntPtr.Zero, text, title, MbIconWarning);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
