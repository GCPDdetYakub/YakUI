using YakUI.Core.Platform;

namespace YakUI.Platform.Windows;

/// <summary>Mark-of-the-Web — альтернативный поток NTFS <c>Zone.Identifier</c> (FR-15).</summary>
public sealed class WindowsMarkOfTheWeb : IMarkOfTheWeb
{
    private static string Stream(string path) => path + ":Zone.Identifier";

    public bool Has(string path)
    {
        try
        {
            return File.Exists(Stream(path));
        }
        catch (Exception e) when (e is IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public string? Read(string path)
    {
        try
        {
            return File.Exists(Stream(path)) ? File.ReadAllText(Stream(path)) : null;
        }
        catch (Exception e) when (e is IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Remove(string path)
    {
        try
        {
            if (File.Exists(Stream(path)))
                File.Delete(Stream(path));
        }
        catch (Exception e) when (e is IOException or NotSupportedException or UnauthorizedAccessException)
        {
            // не NTFS или нет потока — метки нет
        }
    }
}
