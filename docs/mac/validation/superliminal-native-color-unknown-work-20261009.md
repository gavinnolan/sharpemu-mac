# Superliminal unknown accepted native-color sibling

Source-only diagnostic successor of 188b9c2843289cdd4da5b709f49973eef3084f52.

The retained 188 replay reached loading, then failed at OffscreenGuestDraw sequence 2469, dcb.graphics submission 82 after about 73 seconds. The first native-color cause was `Native color has an unknown accepted sibling writer.` in the cohort's Work fallback, reached by ValidateNativeColorEncoderPredecessors. That fallback means a scanned accepted DTO was neither a draw nor compute dispatch nor the producer's own work. The old log does not identify that DTO or whether it was pending or taken. No physical overlap, writer token failure, or harmless action has been established from this cause.

The same fallback still throws the same NotSupportedException message. With cached SHARPEMU_NATIVE_COLOR_DIAGNOSTICS=1, its exception snapshots only owned DTO/context values: pending/taken location, accepted sequence/queue/submission/payload (taken acceptance queue is not recorded), known kind, owned action debug name or image/blit/flip/GDS scalars, and producer/taken/executing context. Unknown classes remain unclassified. It does not inspect delegates, invoke callbacks, access pixel contents, look up provider/tracker/native state, or infer physical authority from DTO address fields.

The predecessor catch calls the existing best-effort formatter after the presenter gate unwinds and before unchanged attempt-all temporary retain cleanup. The existing shared 64-row limit precedes formatting; free-form strings are limited to 160 characters and line breaks removed. Default-off returns the original exception before metadata capture. Other refusal predicates and query order are unchanged. No DTO is exempted, no CPU upload fallback is added, and no currentness/ownership policy is changed.

Four prepared managed rows use actual public native-color projection/submission and ordered-action admission. Pending and taken actions remain rejected and unacknowledged with their callbacks uninvoked; frontend watch ownership is released exactly once by shutdown. Explicit optional metadata capture does not change provider query/copy counts. These controls use existing synthetic mapping/protected metadata only; they supply no native content authority. No compiler, tests, native worker, or game replay was executed for this successor.

Next runtime evidence must name the actual first unknown DTO. A transferable no-guest-effect classification or an original writer-span capture can only be designed after that identity and execution route are established.
