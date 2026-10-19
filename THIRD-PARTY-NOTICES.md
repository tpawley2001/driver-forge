# Third-party notices

Driver Forge ships these components. Every dependency must be permissively
licensed (MIT, BSD, Apache-2.0, ISC); check before adding one
(see docs/CLEAN-ROOM.md).

| Component | Use | License |
|---|---|---|
| .NET runtime and WPF (self-contained publish) | runtime | MIT, © .NET Foundation and Contributors |
| SharpCompress 0.50 (adamhathcock/sharpcompress) | .7z driver archives in local folders | MIT, © Adam Hathcock; its LZMA code derives from the public-domain LZMA SDK |
| Windows Package Manager in-process engine (`Microsoft.WindowsPackageManager.InProcCom` 1.29.380) | Apps page: installs and updates programs | MIT, © Microsoft Corporation |
| Windows Package Manager COM projection (`Microsoft.WindowsPackageManager.ComInterop` 1.29.380) | Apps page | MIT, © Microsoft Corporation |
| C#/WinRT runtime (`WinRT.Runtime`) | WinRT interop for the above | MIT, © Microsoft Corporation |
| Windows SDK projection (`Microsoft.Windows.SDK.NET.dll`, from `Microsoft.Windows.SDK.NET.Ref` 10.0.26100) | Windows.Foundation types the winget projection uses | ⚠️ **Windows SDK license** (redistributable as part of an app, NOT MIT) — open decision, see docs/APPS.md §3.1 |
| System.Management | WMI (restore points, system info) | MIT, © .NET Foundation and Contributors |
| WPF-UI 4.3 (lepoco/wpfui) | Fluent controls, icons | MIT, © Leszek Pomianowski and WPF UI Contributors |
| Fluent UI System Icons (inside WPF-UI) | icons | MIT, © Microsoft Corporation |
| CommunityToolkit.Mvvm 8.4 | MVVM | MIT, © .NET Foundation and Contributors |
| xunit.v3 (tests only, not shipped) | tests | Apache-2.0, © .NET Foundation and Contributors |
