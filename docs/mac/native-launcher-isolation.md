# Native launcher and application services (W25/W26)

Base: d231b6d90319ef4c4fc244f9c1d75fcc63a91374. This isolated implementation has not replaced the shipping launcher or qualified an AppKit window yet.

## Boundaries

`SharpEmu.Application` owns settings, effective launch overrides, custom environment normalization, catalogue metadata, launch plans, localization and update identity. It references only the BCL and Logging for the existing portable/bundle data root. It must not reference GUI, Core, guest libraries, platform SDK handles or a font/raster backend. Windows/Linux Avalonia consumes the same services. `SharpEmu.Launcher.MacOS` owns AppKit windows, controls, menus, panels and its owned child process. CLI composes exactly one launcher by target RID; Mac must not restore GUI/Avalonia/Skia to satisfy a launch path.

Settings preserve `gui-settings.json`, `user/custom_configs/<titleId>.json`, `user/custom_envs.json`, `user/library_cache.json`, property names and null-versus-empty inheritance. The historical GuiSettings name is a source alias inside the portable frontend, not a runtime compatibility shim or a second settings authority. Resolution parsing belongs to application settings; native display enumeration belongs to host composition.

## Capability inventory and acceptance

| Existing capability | Native implementation requirement |
| --- | --- |
| Recursive eboot catalogue | Same depth8, inaccessible-directory tolerance, OS path comparer, exclusions, param.json localized title/version, icon0/pic0/pic1 selection |
| Cache/watch/search/layout | Same version1 cache schema, startup reconciliation, debounced filesystem watcher, searchable catalogue, retained Grid/Carousel choice |
| Open executable and game folder | Owned NSOpenPanel with eboot selection and folder panel; reveal/open/copy path/title actions |
| Global and per-game settings | All JSON fields remain editable; null inheritance and empty override distinguishable; custom environment global/game/one-shot precedence preserved |
| Session launch/stop | Same CLI arguments and environment precedence; one owned child; no process-name killing; no implicit start/stop from informational events |
| Console/logs | Bounded visible log history, clear/copy, separate console window, same custom/default log paths and override filename policy |
| Localization | Embedded translations and loose overrides, English fallback; native UI strings use existing keys where applicable |
| Updates/source links | Same fork build identity; check/download actions; signed Mac bundle remains notification/link only until W28, never flat in-place overwrite |
| Discord | Preserve option/client ID and presence transitions; existing pipe transport limitations explicit |
| Title preview music | Existing implementation is Windows-only; native Mac audio is a separate adapter, not a removed working Mac capability |
| Game launch recents | Existing settings contain no persisted recent-game field; use native document recents without rewriting schemas |

## Main-thread and lifetime rules

CLI Main is synchronous and STA. No-argument AppKit entry stays on the OS main thread and verifies `pthread_main_np`; its event loop never runs inside Task.Run. Scans/process readers marshal scalar/owned managed state to the main loop. No native UI access from timer or process callbacks. The launcher retains its window/delegate/action target and reverse-PInvoke delegates for the complete event-loop lifetime, then disconnects callbacks before releasing native objects. Panels are modal, locally owned and disposed after extracting copied paths. An autorelease pool bounds startup and each dispatch/event cycle; Cocoa run owns native event delivery. Window close confirms/stops only its owned emulator according to current launcher shutdown behavior. Queued UI callbacks carry a closed-generation check to avoid stale native handle access.

Settings parsing, catalogue scanning and launch construction are testable without AppKit. Actual window/menu/panel ownership, parent/child launch and shutdown need a separately granted native UI qualification; compile/tests do not establish UI usability. Full Mac publish closure is checked only after actual conditional launcher replacement, and the output guard must then reject Avalonia/Skia. Existing guest font CoreText proof remains separate from launcher acceptance.

## Initial isolated AppKit candidate

The native package and RID composition are implemented locally. No-argument Mac CLI selects AppKit; Windows/Linux retain Avalonia. No native window has been qualified yet. Application regression selection is 73/73 on ARM64 and x64/Rosetta, portable GUI seam selection 42/42 on ARM64. Native package tests include actual synthetic process stream draining and isolated owner stop; these do not launch an emulator/game or qualify Cocoa UI.

Implemented source: native catalogue table/search/cover selection, folder/open/reveal/copy/hide actions, debounced scans/cache, menu/responder-chain edit actions, full-field global/per-game settings editor with explicit list inheritance, global/game/one-shot custom environments, bundle-safe update notifications/links, isolated child launch/stop and bounded console/log file handling. Mac CLI's build graph excludes GUI/Avalonia; an actual publish artifact and window behavior are still required before runtime closure or usability claims.

Outstanding native parity work: Grid/Carousel visual presentation, gamepad navigation, friendly constrained settings/display choices, console geometry/maximized persistence and console filtering, Discord IPC transport/presence transitions, locale refresh of existing control labels, and accessibility/manual resize behavior. Title preview was already a Windows-only no-op on Mac. No existing Mac audio behavior is lost, but native preview remains a separate enhancement. Recent-game note uses NSDocumentController; an actual recent-menu route still needs qualification. These are explicitly unfinished capabilities; do not activate this candidate as a feature-complete root replacement.

AppKit main-thread and modal event-loop behavior follows Apple's [run modal window documentation](https://developer.apple.com/documentation/appkit/nsapplication/runmodal%28for%3A%29) and [main application loop documentation](https://developer.apple.com/documentation/appkit/nsapplication/run%28%29). Native selectors, Objective-C handles, class IMP delegates and constant pointers remain private SDK ABI inside the Mac package.

### Pre-window independent review corrections

The `2b856d8` candidate remains preserved as a pre-window build, not a native UI acceptance result. Independent review found stale child notifications, nested-modal quit sequencing, and two exact Cocoa callback return-type mismatches. The follow-up binds every output/exit notification to its monotonic owned-child session and checks that identity when the main loop consumes the deferred job. A real synthetic child regression leaves old output and exit jobs queued, starts a newer owned child, and verifies those jobs cannot mutate its console/log/status.

Quit now uses modern `stopModal` to unwind active Cocoa modal loops first and closes the main window only after the modal run has returned. The quit request is consumed before the main close, so canceling its child-stop confirmation cannot retrigger quit later. Main-window close always attempts the outer `stop:` plus an application-defined wake event in a `finally`, independently of child-stop failure. `windowShouldClose:` returns a byte with SDK BOOL encoding `B` on ARM64 and `c` on x64; the application termination reply uses unsigned native integer encoding `Q`. These are source and pure/synthetic controls; actual window/modal/menu cleanup still requires the granted isolated UI run.

The displayed console is bounded to 5,000 lines, but the deferred managed Action queue is currently unbounded. Sustained child output can therefore grow a backlog while the UI is blocked. This remains an explicit workload/resource follow-up before claiming complete launcher parity or unrestricted long-running readiness.

### Cooperative owned-child shutdown

Stop now sends SIGTERM to the retained owned child, allowing the emulator's cooperative host shutdown, then awaits exit and redirected-reader completion off the AppKit main thread. The default grace deadline is ten seconds; only expiration triggers `Process.Kill(entireProcessTree: true)` for that still-owned process. Quit leaves the Cocoa loop running with a stopping status until the selected child session has completed, then closes the main window. Repeated Stop is idempotent and launching a new session while this close is pending is rejected.

The final synthetic selection passes 14/14 on ARM64 and 14/14 on Rosetta with no declared skips. A real shell handling TERM emits its final output and completes promptly. A real TERM-ignoring shell plus its sleep descendant reaches the same deadline implementation (a private 250 ms fixture deadline), both exit, and the inherited readers complete before notification. This qualifies those controlled direct/alive-parent topologies, not every descendant topology: a parent that exits before a descendant while the descendant retains redirected pipes can orphan that process before `Kill(root)` can discover it. The owner currently does not establish a private POSIX process group or retain a complete independent descendant set. That topology remains unsupported, and no unrestricted process-tree cleanup claim is made.

### Exact first visible native qualification: 3b54f79

The ad-hoc signed source-frozen `3b54f7943dc014cfb865071e7cbef693df8649cd` bundle ran under Rosetta with an absolute ephemeral `SHARPEMU_DATA_ROOT`, updates/Discord disabled, and only two synthetic catalog entries plus a synthetic shell child. It exited zero without the 600-second watchdog firing (278.59 seconds), released the shared lock, and all three retained synthetic child PIDs were gone. The complete signed payload hashes were unchanged. Evidence is archived in `artifacts/validation/integrated-20261007/package-agent/native-launcher-3b54f79`.

Actual native interactions qualified catalog rendering and search filtering, per-game `LogLevel=Debug` and global `LogLevel=Warn` persistence in the existing schemas, native folder-picker cancellation, settings-window close unwinding its modal, game custom environment overriding the saved global value, stdout/stderr console delivery, cooperative TERM final output, and owned Stop-and-Quit confirmation followed by main-loop exit. The displayed `-2` is the owner's stop-requested status tag, not a signal number or guest CPU result; the synthetic TERM handler exited zero. No game or emulator graphics fixture ran.

Cocoa disabled all current application menu actions, including Cmd-Q, while settings was modal. Thus that menu route did not execute the corrected quit sequence and is not accepted. A modeless native settings design is a follow-up. Minimum resize bounds were not exercised. The first Python helper failed under Rosetta because the system xcrun stub's CLT library lacked an x64 slice; its error/exit-one console is preserved, and only the ephemeral helper was replaced by a shell. A harness manifest-import typo is separately preserved. Neither failure is counted as synthetic launch success.

### Bounded output notification retention

The successor mailbox retains at most 1,024 queued output jobs and truncates retained lines to 4,096 characters plus a visible marker. Overflow discards the oldest output jobs, preserves FIFO control jobs (exit/scan/resource transitions), and reports the dropped line count. Closing the mailbox rejects late callbacks. Two actual mailbox tests exercise 3,000 output notifications around ordered control jobs, exact retained tail and overflow accounting, oversized lines, and close rejection. Final related selection: 16/16 ARM64 and 16/16 Rosetta, no declared skips; no new window run. This bounds queued output retention, not the old `Process.BeginOutputReadLine` decoder's temporary accumulation of a single unterminated line; the bounded stream reader is the next child-host increment. Non-output control jobs originate only from bounded scan/update/exit sources and are preserved rather than silently dropped.
