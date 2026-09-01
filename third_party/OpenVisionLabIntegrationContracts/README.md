# OpenVisionLab Integration Contracts

The public Release 2 development branch consumes the following exact local
packages:

- `OpenVisionLab.Integration.Contracts` `0.2.0-alpha.3`
  - SHA-256: `25CADF8BD6EDBC7E9C089BE6CE2286A7ADA5A335A3DEA5FBDBCCEF63343E4A24`
- `OpenVisionLab.Integration.Transport.Tcp` `0.1.0-alpha.3`
  - SHA-256: `5FBFE95358554D47A047305D589614832EAF775A05100B2B8E626D8DDDEC424F`

Both packages are immutable development candidates sourced from Shared commit
`f4743f3307d20a963b2197f2019713320b9859b9` with clean source state. They are
used by the `v0.2.0-dev.1` Release 2 development candidate and are not a
published release. The previous alpha.2 bytes remain beside this alpha.3 pair
for historical traceability and are not overwritten.

The packages own the shared Handoff, Acknowledgement, Result, validation,
correlation, error, JSON fixture, and authenticated bounded TCP transport
contracts. Do not replace either package without updating every OpenVisionLab
consumer to the same exact package versions and hashes.
