# Superliminal first original publication refusal

Source-only successor of `1893dc2527dcb0eeb5e4442b1b7bd62729acfeb7`.

The qualified Superliminal run `20261009-221110-superl-boot-cold-hook-successor-1893dc25-entry` stopped at graphics submission 18, ordered memory operation 375. Its first lower writer-reservation CAS refused with `admission-busy`, sampled admission 2, original revision 1264075 and sampled revision 1264085. Provider mapping/currentness and write geometry guards had passed. Disjoint history/read-watch coverage checks after reservation were not reached. The revision difference does not establish stale or overlapping content, and admission 2 does not establish foreign writers' ranges or eventual release.

The earlier bounded per-command trace had exhausted its budget. This successor adds operation kind, debug name, address, owned byte count, original watch flag, preparation result and ticket presence to the exception at the actual failed `ExecuteOriginalWrite` preparation. Each field comes from the same original operation or the one returned preparation. No later watch, mapping or status query is performed. Existing default-off lower publication diagnostics retain their first CAS attribution.

Eight ordinary managed controls cover Write/Fill, plain/watched actual ledger original, and real managed admission refusal/mapping replacement. They use the existing pinned-byte provider fixture and two real disjoint managed pre-byte tokens for the admission control. The forwarding source forbids any copy, validation, status or release query after the first preparation; assertions check one attempt, no owner callback, unchanged full bytes, one-use refusal and zero backing accesses. These are managed ledger controls, not native Metal or game success evidence.

No publication wait, fresh mapping, original recapture, guard relaxation or rendering policy change is included. The actual failing operation cannot yet be called Fill or Write from the retained run alone. No build, test or replay has been run for this successor.
