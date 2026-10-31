# Documentation

- [Public project overview](../README.md) / [繁體中文](../README.zh-TW.md)
- [GitLab CI setup](ci.md): runner prerequisites, test scope and package artifacts

| Folder | What it holds | Who updates it |
|---|---|---|
| [design/spec.md](design/spec.md) | The product spec and source of truth: goals, energy rules, architecture, milestones and their definitions of done, open questions | The human; agents only fix references |
| [design/principles.md](design/principles.md) | Engineering priorities set by the human: performance and energy first, then memory, then binary size; aggressively opt in to new ISA and APIs above an x86-64-v3 floor | The human |
| [decisions/](decisions/README.md) | One numbered record per choice the spec leaves open, or departure the human approved | Whoever makes the choice, in the same change |
| [verification/](verification/README.md) | Dated records of live tests against real guests: what ran, on what, and the measured result | Whoever runs the test |
| [reviews/](reviews/README.md) | Review rounds (Codex): findings, verdicts and the fixes that answered them | The reviewer; fixes are appended by the implementer |

Documentation that belongs to one component stays next to it:

- [native/README.md](../native/README.md) and [native/VENDORED.md](../native/VENDORED.md): codec DLL build and the LGPL source record
- [tests/fixtures/README.md](../tests/fixtures/README.md): replay fixtures and their approved goldens
- [tests/Spice.Viewer.Tests/README.md](../tests/Spice.Viewer.Tests/README.md): what the viewer helper tests cover
- [tools/spicerec/README.md](../tools/spicerec/README.md): recording and scrubbing new fixtures
- [tools/package/README.txt](../tools/package/README.txt): the end-user README shipped in the release zip

## Rules for these documents

- The spec is not edited to match the code. When the code must differ, write a decision record and get the human's
  acceptance if the spec says MUST or the item is an open question.
- Records name guests by OS (`win7`, `win81`), never by VM ID, hostname, IP or account. Those stay out of the repository.
- Verification and review records are history: add a new dated file or section instead of rewriting old results.
