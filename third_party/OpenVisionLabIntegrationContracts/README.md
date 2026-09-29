# Pinned integration dependencies

`NuGet.Config` includes this local package feed so the source build resolves the
exact contracts used by the application and infrastructure adapters.

| Package | Version |
| --- | --- |
| OpenVisionLab.Integration.Contracts | 0.2.0-alpha.4 |
| OpenVisionLab.Integration.Transport.Tcp | 0.1.0-alpha.4 |

Both packages record source commit `f4743f3307d20a963b2197f2019713320b9859b9`
from https://github.com/Noah8218/OpenVisionLab-Integration-Contracts and carry
the MIT license. Each package contains LICENSE, NOTICE and its protocol
documentation. The adjacent `.sha256` file identifies the exact package bytes.

Run `./scripts/verify-integration-contracts-package.ps1` from the repository root
to check hashes, package identity, source commit and required contents. The older
0.1.0 package is retained unchanged for source-history compatibility; the current
project references select the versions above.

These are prerelease dependencies. A successful source build does not prove
interoperability with an arbitrary installed peer. Match the declared protocol,
artifact and authentication contracts before connecting external software.
