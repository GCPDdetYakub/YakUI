# YakUI 0.1.0

Windows app to browse, install and manage Rhino Yak packages (Rhino 7, 8, 9).

## Build

Requirements: .NET 8 SDK, Inno Setup 6 (for the EXE installer).

```
powershell -ExecutionPolicy Bypass -File build\publish.ps1
```

Output in `artifacts\`: `YakUI-<version>-win-<arch>-setup.exe`, `YakUI-<version>-win-<arch>-portable.zip`, `SHA256SUMS.txt`.
