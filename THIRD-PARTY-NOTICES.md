# Third-Party Notices

OpenVisionLab Machine Studio's Windows packages distribute the following
third-party assemblies.

## WPF-UI 4.3.0 and WPF-UI.Abstractions 4.3.0

- Project: https://github.com/lepoco/wpfui
- License: MIT
- Copyright (c) 2021-2025 Leszek Pomianowski and WPF UI Contributors

The publish output includes the package's complete `LICENSE.md` and
`ThirdPartyNotices.txt` under `THIRD-PARTY-NOTICES`.

## Microsoft.Xaml.Behaviors.Wpf 1.1.122

- Project: https://github.com/microsoft/XamlBehaviorsWpf
- License: MIT
- Copyright (c) Microsoft Corporation. All rights reserved.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies
of the Software, and to permit persons to whom the Software is furnished to do
so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

## Microsoft .NET runtime

The public Windows x64 self-contained package bundles Microsoft.NETCore.App and
Microsoft.WindowsDesktop.App 8.x. The exact runtime versions are recorded in
the package manifest. The matching runtime license and third-party notices are
included under `THIRD-PARTY-NOTICES` in the package.

A framework-dependent development publish instead requires a compatible
Microsoft .NET 8 Windows Desktop Runtime installed on the target computer.

## OpenVisionLab integration packages

- OpenVisionLab.Integration.Contracts 0.2.0-alpha.4
- OpenVisionLab.Integration.Transport.Tcp 0.1.0-alpha.4
- Source: https://github.com/Noah8218/OpenVisionLab-Integration-Contracts
- Source commit recorded in both packages: `f4743f3307d20a963b2197f2019713320b9859b9`
- License: MIT; Copyright (c) 2026 Noah Choi.

The pinned packages under `third_party/OpenVisionLabIntegrationContracts`
include their complete LICENSE and NOTICE. SHA-256 files accompany the exact
package bytes. These prerelease dependencies provide contracts and transport;
they are not a production-control or safety system.
