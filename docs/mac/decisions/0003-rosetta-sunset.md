# ADR 0003: Rosetta sunset makes native ARM64 guest execution required

Status: accepted (problem statement); approach open. 2026-10-07.

## Facts (Apple primary sources)

- "Rosetta ... will be available through macOS 27 — as a general-purpose tool for Intel
  apps ... Beyond this timeframe, we will keep a subset of Rosetta functionality aimed at
  supporting older unmaintained gaming titles, that rely on Intel-based frameworks."
  ([About the Rosetta translation environment](https://developer.apple.com/documentation/apple-silicon/about-the-rosetta-translation-environment))
- "All Intel-based software will no longer be compatible with macOS 28.0, excluding legacy
  games." ([macOS 27 release notes](https://developer.apple.com/documentation/macos-release-notes/macos-27-release-notes), 176042635)
- Rosetta is not reinstalled automatically after upgrading to macOS 27 (163213094).
- The legacy-games mechanism (`game-test-tool` in macOS 27 betas) disables general Rosetta
  and is scoped to games. Apple does not say which titles or frameworks qualify.
- Rosetta for Linux VMs (Virtualization framework) is built into macOS 27 and is not part
  of the sunset.
  ([Running Intel binaries in Linux VMs](https://developer.apple.com/documentation/virtualization/running-intel-binaries-in-linux-vms))
- Rosetta translates AVX and AVX2, not AVX-512.

## Consequence

SharpEmu executes guest x86-64 code directly, so its macOS build is an x86_64 process
that depends on general-purpose Rosetta. Reading Apple's wording, an emulator is not a
"legacy gaming title", so the osx-x64 build should be expected to stop working on
macOS 28. This is an inference from Apple's text, not an Apple statement about emulators.

Native ARM64 guest execution (W30) therefore changes from "later" to a release-blocking
milestone before macOS 28. The osx-x64/Rosetta build remains the shipping path until then.
Users on macOS 27 must install Rosetta themselves (`softwareupdate --install-rosetta`).

## Candidate approaches (to be decided)

1. **Own x86-64 → ARM64 translator (JIT) in the emulator.** Full control, and the HLE,
   GPU and audio code stays in one native arm64 process. Largest effort: decoder, register
   allocation, flags, SSE/AVX semantics, x86 memory ordering (TSO) on ARM's weaker
   model, self-modifying code, fault and signal mapping.
2. **Embed an existing translator.** Licence, macOS support and performance need checking;
   most mature x86-64 → ARM64 translators (FEX, Box64) target Linux only.
3. **Linux VM with Apple's built-in Rosetta for Linux.** Run the x86-64 guest-execution
   part in an ARM Linux VM through the Virtualization framework, with graphics, audio and
   input on the macOS host over IPC. It reuses Apple's translator, which has no announced
   end date, but needs a process split and high-bandwidth guest memory sharing.
