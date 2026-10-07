# ADR 0002: Native macOS stack with no fallbacks

Status: accepted, 2026-10-07. Supersedes the parts of [ADR 0001](0001-platform-packages.md),
[platform-packages.md](../platform-packages.md) and [implementation-plan.md](../implementation-plan.md)
that keep a Vulkan comparison profile, SDL or other portable fallbacks on macOS.

## Decision

The macOS build uses only native macOS facilities:

| Concern | macOS | Windows / Linux |
| --- | --- | --- |
| GPU | Metal (`MetalGuestGpuBackend`) | Vulkan |
| Window, surface, keyboard | AppKit `NSWindow` + `CAMetalLayer` | SDL3 |
| Gamepads, haptics, lightbar, adaptive triggers | GameController + CoreHaptics | SDL3 |
| Audio output | CoreAudio | SDL3 |
| Display enumeration | CoreGraphics / `NSScreen` | SDL3 |
| Guest CPU | x86-64 direct execution under Rosetta 2 | x86-64 direct execution |

There is no MoltenVK, Vulkan loader or SDL in a macOS process or in macOS build
outputs, and there is no runtime fallback between backends on any OS. Asking for a
backend the host does not provide (for example `SHARPEMU_GPU_BACKEND=vulkan` on macOS)
stops startup with an error.

Windows and Linux keep Vulkan and SDL in their own packages. This keeps the fork
mergeable upstream: the macOS work arrives as separate packages and backend-neutral
contracts, not as edits that remove other platforms' code.

## Consequences

- The "Vulkan on Mac comparison profile" in the earlier plan is dropped. Cross-backend
  comparison happens across machines (Vulkan on Windows/Linux and Metal on macOS) or
  through independently expected synthetic outputs, which W16 already requires.
- W05 is complete: Metal is the only macOS backend, and MoltenVK staging and loader
  preloading are removed.
- W21's "preserve portable fallback" for audio does not apply to macOS; CoreAudio is
  the only macOS output.
- Shared GPU engine contracts must not use `Silk.NET.Vulkan` types, because the macOS
  closure cannot contain them. A source-level architecture test enforces this, and a
  publish-time guard rejects Vulkan, MoltenVK and SDL files in macOS outputs.
- Metal feature gaps fail clearly with a capability error. They are not hidden behind
  another backend.
