namespace YakUI.Core.Platform;

/// <summary>Проверка права записи в папку (16.1: portable-версия в папке без права записи).</summary>
public static class DirectoryAccess
{
    /// <summary>Можно ли создать папку (если её нет) и записать в неё файл; пробный файл удаляется.</summary>
    public static bool CanWrite(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, $".yakui-write-test-{Guid.NewGuid():N}");
            using (File.Create(probe, 1, FileOptions.DeleteOnClose))
            {
            }

            return true;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }
}
