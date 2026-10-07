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
