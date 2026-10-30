# Third-party notices

DroidDeck is distributed under the MIT License in [LICENSE](../LICENSE). This file
lists the third-party components in the distribution and the notices that apply.

## Runtime components

| Component | Version | License | Project |
| --- | --- | --- | --- |
| .NET runtime | 10.0 | MIT | <https://github.com/dotnet/runtime> |
| Microsoft Windows App SDK (WinUI) | 2.5.1 | Microsoft Software License Terms; see `WINDOWSAPPSDK-LICENSE.txt` and `WINDOWSAPPSDK-NOTICES.txt` in this folder | <https://github.com/microsoft/WindowsAppSDK> |
| Microsoft.Extensions.DependencyInjection | 10.0.14 | MIT | <https://github.com/dotnet/runtime> |
| Microsoft.Extensions.Logging | 10.0.14 | MIT | <https://github.com/dotnet/runtime> |
| System.Security.Cryptography.ProtectedData | 10.0.14 | MIT | <https://github.com/dotnet/runtime> |
| QRCoder | 1.8.1 | MIT | <https://github.com/codebude/QRCoder> |

The Windows App SDK package also incorporates third-party material. Its complete
`NOTICE.txt` is copied next to the license terms by the build, and the source is
listed at <https://3rdpartysource.microsoft.com>.

### MIT License

The following notice applies to the MIT-licensed components listed above.

```text
The MIT License (MIT)

Copyright (c) .NET Foundation and Contributors
Copyright (c) 2013-2025 Raffael Herrmann
Copyright (c) 2024-2025 Shane Krueger

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

The full .NET runtime license is also included as `DOTNET-LICENSE.txt`.

## Optional external tools

DroidDeck invokes tools that you install. It does not distribute them, and each is
governed by its own license:

| Tool | License | Project |
| --- | --- | --- |
| Android SDK platform-tools (`adb`) | Android SDK terms | <https://developer.android.com/tools/releases/platform-tools> |
| scrcpy | Apache-2.0 | <https://github.com/Genymobile/scrcpy> |
| Android Studio / emulator | Android Studio terms | <https://developer.android.com/studio> |
| JDK, Gradle, Flutter, Dart, Node.js, Unity, .NET SDK | Their respective licenses | Vendor sites |

If a distribution bundles scrcpy, that distribution must also include scrcpy's
`LICENSE`, `NOTICE`, version, and source link as required by Apache-2.0. The current
build detects an installed scrcpy instead of bundling it, so no scrcpy binaries are
included.

Android Studio, the Android SDK, adb, and the emulator are not bundled. Android is a
trademark of Google LLC. DroidDeck is not affiliated with Google.

## Development and test dependencies

These packages are used to build and test the repository and are not part of the
application distribution:

| Component | Version | License |
| --- | --- | --- |
| Microsoft.NET.Test.Sdk | 18.11.0 | MIT |
| xunit | 2.9.3 | Apache-2.0 |
| xunit.runner.visualstudio | 3.1.5 | Apache-2.0 |
| FlaUI.Core | 5.0.0 | MIT |
| FlaUI.UIA3 | 5.0.0 | MIT |

xunit and xunit.runner.visualstudio are licensed under Apache-2.0; see
<https://github.com/xunit/xunit/blob/main/LICENSE> and
<https://github.com/xunit/visualstudio.xunit/blob/main/LICENSE>.

## Trademarks

Windows, Microsoft, WinUI, and Windows App SDK are trademarks of Microsoft
Corporation. Android is a trademark of Google LLC. All other trademarks are the
property of their respective owners.

## Reporting a notice problem

If a required notice is missing or inaccurate, open an issue at
<https://github.com/raulshma/droiddeck/issues> and it will be corrected in the next
release.
