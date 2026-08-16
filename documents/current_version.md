# Current version

What the next delivery contains, and where each piece of it stands. Updated while the work
happens rather than written up at the end. What was delivered before is in the status table of
[roadmap.md](roadmap.md); what is proposed and not scheduled is in
[suggestion.md](suggestion.md).

## Target

**0.21.0**, not yet cut. `Directory.Build.props` still reads `0.20.0`, which is what shipped and
what the archives in `dist/` were built from; the version is bumped when the delivery is
prepared, and [tools/publish-release.ps1](../tools/publish-release.ps1) reads it from there.

## Planned

| # | Deliverable | State |
| --- | --- | --- |
| - | Documentation rules, and the illustrations in the archives | in progress |

**Documentation rules, and the illustrations in the archives.** Public and dev material had no
written boundary, and the release archives carried a `README.md` whose images and links pointed
at files that were not in them. [documentation-rules.md](documentation-rules.md) writes the rules
down once, the backlog moved out of the roadmap into [suggestion.md](suggestion.md), this
document appeared, and `publish-release.ps1` now packages the public documents with their images
and fails the build if one of them is missing.

No iteration is scheduled after it yet. Candidates are in [suggestion.md](suggestion.md), and an
entry moves here when it is taken.

## Before the delivery

- [ ] `Directory.Build.props` bumped to the delivered version
- [ ] `powershell -File tools/build-manual.ps1 -Check` clean
- [ ] `dotnet test` clean
- [ ] [roadmap.md](roadmap.md) and [README.md](../README.md) updated for everything above
- [ ] `powershell -File tools/publish-release.ps1`, archives checked on at least one platform
- [ ] `dist/release-notes.md` reread before it is pasted into the release form
