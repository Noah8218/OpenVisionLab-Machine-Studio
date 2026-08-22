# Vendor Integration and Built-in Asset Policy

This policy applies to vendor adapters, SDKs, protocols, equipment images,
icons, CAD-derived geometry, sample recipes, and other material proposed for
the Machine Studio repository or distributed application.

## Default rule

Keep the generic project, simulation, and UI contracts vendor-neutral. A
vendor-specific integration belongs in an optional adapter at the
Infrastructure boundary and must not change generic equipment semantics to
match one vendor. Do not imply affiliation, certification, or endorsement.

## Allowed inputs

- Original OpenVisionLab work with a retained author or generation record.
- Material under a license that explicitly allows the intended modification,
  embedding, and redistribution, with its notice retained.
- Official public APIs or published protocols used within their terms.
- User-installed vendor SDKs referenced by an optional adapter when the SDK
  license does not allow redistribution.

## Prohibited inputs

- Vendor logos, model badges, distinctive product appearance, screenshots, UI,
  recipes, manuals, private protocols, or internal mechanisms copied as product
  content.
- CAD or geometry derived from a vendor file without explicit redistribution
  rights.
- SDK binaries, headers, examples, or documentation copied into the repository
  when their license does not permit it.
- An asset whose source, license, or transformation history is unknown in a
  release candidate.

## Required adapter record

Before adding a vendor adapter, record:

1. vendor and public integration surface;
2. source URL and document/SDK version;
3. license and redistribution decision;
4. repository module and generic contract used;
5. user installation or credential prerequisite;
6. commands/data transmitted to real equipment and the safety boundary;
7. test method that does not require committing licensed vendor material; and
8. reviewer and approval date.

Real-equipment control requires a separate product and safety decision. This
policy does not authorize it.

## Required asset record

Every repository-owned visual or geometry asset must have one entry in
`ASSET-PROVENANCE.json` containing its repository path, SHA-256 hash, media
type, purpose, origin summary, first recorded commit, vendor-reference status,
distribution status, and review note.

Distribution statuses are:

- `approved`: source and redistribution rights are documented;
- `blocked-pending-source-record`: development use may continue, but release
  promotion is blocked until the record is completed or the asset is replaced;
- `excluded`: the asset is not embedded or distributed.

Run the normal inventory check after any asset change:

```powershell
./scripts/verify-asset-provenance.ps1
```

Use the release gate only when preparing a distributable candidate:

```powershell
./scripts/verify-asset-provenance.ps1 -RequireDistributionApproved
```

The normal check must find every declared file, reject undeclared files, and
match every SHA-256 value. The release gate additionally rejects any status
other than `approved`.

## Intake checklist

- [ ] The source and author/generator are identified.
- [ ] Reference inputs and transformation history are retained.
- [ ] License permits the intended repository and distribution use.
- [ ] Required attribution is added to `NOTICE` or
      `THIRD-PARTY-NOTICES.md`.
- [ ] No vendor trademark or distinctive copied appearance remains.
- [ ] `ASSET-PROVENANCE.json` is added or updated with the final hash.
- [ ] The normal inventory check passes.
- [ ] The release gate passes before release promotion.

If any answer is unknown, use `blocked-pending-source-record`; do not infer
permission from the absence of a logo or from public availability.
