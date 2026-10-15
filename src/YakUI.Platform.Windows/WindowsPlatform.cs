using YakUI.Core.Platform;

namespace YakUI.Platform.Windows;

/// <summary>Набор Windows-реализаций платформенных интерфейсов YakUI.Core (AD-08).</summary>
public sealed class WindowsPlatform
{
    public WindowsPlatform(string? appDirectory = null)
    {
        Registry = new WindowsRegistryReader();
        FileSystem = new WindowsFileSystem();
        Paths = new WindowsPlatformPaths(appDirectory);
        Processes = new WindowsProcessRunner();
        Signatures = new WindowsSignatureVerifier();
        DotNet = new WindowsDotNetRuntimeProbe(Registry, FileSystem, Paths);
        Urls = new WindowsUrlLauncher();
        Images = new SkiaImageScaler();
        RhinoProcesses = new WindowsRhinoProcessProbe();
        Folders = new WindowsFolderOpener();
        Watcher = new WindowsDirectoryWatcher();
        MarkOfTheWeb = new WindowsMarkOfTheWeb();
        RhinoInstaller = new WindowsRhinoInstallerEngine();
        Hosts = new WindowsHostsPlatform(Registry);
    }

    public IHostsPlatform Hosts { get; }

    public IRhinoInstallerEngine RhinoInstaller { get; }

    public IMarkOfTheWeb MarkOfTheWeb { get; }

    public IDirectoryWatcher Watcher { get; }

    public IRhinoProcessProbe RhinoProcesses { get; }

    public IFolderOpener Folders { get; }

    public IImageScaler Images { get; }

    public IUrlLauncher Urls { get; }

    public IRegistryReader Registry { get; }

    public IFileSystem FileSystem { get; }

    public IPlatformPaths Paths { get; }

    public IProcessRunner Processes { get; }

    public ISignatureVerifier Signatures { get; }

    public IDotNetRuntimeProbe DotNet { get; }
}
