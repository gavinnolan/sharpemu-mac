# First original-currentness refusal during ordinary acquisition

Source-only successor of qualified `1893dc25`; compilation and execution are pending.

The Silent Hill `20261009-221248` replay stops with exit 139 after 36 seconds. Before the guest SIGSEGV at `0x8049A8381` writing to `0x28`, several exact ordinary-write wait receipts return `OriginalChanged`. The saved failure reports `original_currentness_reason=not-classified`, because acquisition uses the Boolean property while the attributed query exists only after successful admission. This does not prove which mapping predicate failed or establish crash causality.

An explicit attributed `TryAcquire` overload substitutes the same ordered original-currentness query before and after admission, returning its first detached refusal. The existing overload keeps the ordinary Boolean path. PhysicalVirtualMemory selects attribution only when its existing default-off ordinary-write diagnostic flag is enabled. No second query explains a previous decision, and no mapping, admission, timeout, write, retry or cleanup rule changes. A timeout/refusal with current identity carries no invented currentness failure.

Five source-ready controls cover real shared-snapshot replacement, raw provider revocation, unknown provider, admitted acquisition and timeout. They retain zero destination bytes, one-use acquisition, exact first-reason stability and original failure behavior. Native syscall outcomes in the fake provider remain synthetic. No boot success follows from this diagnostic change.
