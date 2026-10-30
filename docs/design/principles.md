# Engineering principles

Set by the human on 2026-10-30 for the first release line. They rank the trade-offs the spec leaves open; when a
choice is between two of these goals, the higher one wins. The spec's MUST rules still apply on top of them.

## Priorities, in order

1. **Performance and energy efficiency.** Fast when the guest is busy and silent when it isn't. The energy rules in
   [spec.md](spec.md) (present only on change, no polling, no timers while idle, no hot-path allocation) are part of
   this goal, not a separate one. A faster path that wakes the CPU more often is not faster.
2. **Low memory use.** Keep the working set small: pool and reuse buffers, size caches to what is measurably needed,
   release what an idle session doesn't use. Memory may be spent only where it buys measured speed or energy.
3. **Small binaries.** Shrink the exe and DLLs (trimmed exports, dead-code elimination, no unused dependencies), but
   never at the cost of the two goals above: no size optimisation that makes a hot path slower.

## Transparency and flexibility

The human's philosophy for building software (2026-10-30). The priorities above rank trade-offs; these values
shape every choice.

- **Transparency.** Prefer components whose source we can read, build ourselves and debug. A component should have
  documented behaviour that we can pin to a version. Avoid opaque binaries, and avoid OS or third-party components
  whose behaviour we can't inspect when an open option does the job. The same goes for our own work:
  - choices are written down in [decisions/](../decisions/README.md);
  - measurements are recorded;
  - logs say what happened;
  - limitations are stated, not hidden.
- **Flexibility.** Put clear seams between layers (transport, protocol, codec, renderer, UI) so that any one of them
  can be replaced. Don't lock users or the code into one host application, UI framework or vendor. The viewer
  works standalone and can also be hosted by another program.
  - Where users already have a tool they like, let them choose it. For example, the v1 terminal will have a built-in
    VT, and users will also be able to use their own terminal program.

## Opt in to the newest hardware and APIs, aggressively

- Actively adopt new ISA extensions and Windows/graphics APIs wherever they measurably help, for example newer SIMD
  (AVX-512/AVX10 paths behind run-time detection), hardware composition and overlays, and hardware video decoding.
  Treat "the newest thing the platform offers" as the first option to evaluate, not a later optimisation.
- The baseline is a floor, raised when it pays. The first release requires **x86-64-v3** (AVX2, FMA, BMI1/2, LZCNT,
  MOVBE): the C# code through Native AOT's instruction set, the native codecs through clang's `-march`, with every
  SIMD path up to that level enabled unconditionally ([decisions/0026](../decisions/0026-x64-builds-target-x86-64-v3.md)).
  A CPU below the floor is not supported; the user README says so.
- Don't spend effort on paths that exist only for hardware below the floor. The Windows versions named by the spec
  and [decisions/0022](../decisions/0022-windows-10-22h2-best-effort-human-2026-10-29.md), and the WARP/RDP software
  profile, stay supported: those are environments the viewer runs in, not old hardware.
- Raising or lowering the floor, adding a second build flavour or another rendering backend is the human's decision.

## How to apply them

- **Measure before and after.** A performance, memory or size claim needs a number: a benchmark, an allocation
  test, an ETW trace or a file size. Record notable results in [verification/](../verification/README.md) or in the
  decision record that relied on them.
- **No speculative fallbacks.** Don't add code for hardware below the floor or OS versions outside the target
  "just in case".
- **Regressions block.** A change that makes a hot path slower, wakes the CPU more, or grows memory or binary size
  without a matching gain in a higher priority needs a decision record explaining why.
- New decisions cite this page when they trade one priority against another.
