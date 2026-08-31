# OpenVisionLab Integration Contracts

The public Release 2 development branch consumes the following exact local
packages:

- `OpenVisionLab.Integration.Contracts` `0.2.0-alpha.2`
  - SHA-256: `9F94B300D8DEBC4B69DC242B5E47018605CAA725A5DA265F32718DBDDF8A1BD5`
- `OpenVisionLab.Integration.Transport.Tcp` `0.1.0-alpha.2`
  - SHA-256: `67E0270C67DCE287E3DCA3D4A01557C526185719E87248FC662FBCF99BBA13A2`

Both packages are development snapshots sourced from the shared integration
workspace with repository state recorded as `working-tree-uncommitted`. They
are suitable for the `0.2.0-dev` feature branch only, not a clean-source
release candidate. The older `OpenVisionLab.Integration.Contracts` `0.1.0`
package remains for historical traceability and is no longer referenced by the
application.

The packages own the shared Handoff, Acknowledgement, Result, validation,
correlation, error, JSON fixture, and authenticated bounded TCP transport
contracts. Do not replace either package without updating both OpenVisionLab
consumers to the same exact package hashes.
