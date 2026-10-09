# Current native Mac game-entry checkpoint

Priority: responsive, controllable gameplay; performance and visual polish follow entry.

Local main was fast-forwarded to `188b9c2843289cdd4da5b709f49973eef3084f52`. No remote push or upstream merge was performed. This combines the storage phase/readback original continuations, first-status preservation, ordered native color borrowing, and bounded log-anchored input owner.

Exact qualification passed three builds, all 838 managed cases on each actual ARM64 and x64 CLR, and 43 fresh native workers per CLR with no skipped cases. The native inventory contains 38 Metal and five ordinary Mac-provider workers. The 104 compiled files remained stable; the signed native-only macOS payload contains 239 unchanged files and passes deep strict signature verification. ROOT independently verified all 506 qualification artifacts against the immutable manifest.

Evidence: `artifacts/validation/all-games-20261009/game-entry-readback-color-188b9c28-prepared/qualification-manifest.json`, SHA-256 `629ea214dce4cc64a223c91bc983d589543a154795ef4fa5a2d4e13736415033`. That manifest closes the prerequisite workers before games; it is not an all-games acceptance report.

## Fresh entry failures on the qualified payload

Runs are under `.claude/worktrees/agent-package-ordered-queue-scope/artifacts/runs/`.

| Title / run | First observed failure | Reached level |
| --- | --- | --- |
| Octopath Traveler II / `20261009-053910-octo-game-entry-readback-color-188b9c28` | Compute sequence 57, graphics submission 3: singleton original-content validation in `WithFiniteStorageValidation` / `FinalizeFiniteStorageWrites`. Two producer completions and applied publications, one canonical completion. Adjacent admission diagnostics do not identify the exact failing predicate. | No accepted gameplay. |
| The Oregon Trail / `20261009-054029-oregon-game-entry-readback-color-188b9c28` | Singleton original-content validation during canonical dirty acknowledgment. Two canonical completions precede the fatal. Adjacent watch samples do not establish its exact origin or predicate. | No accepted gameplay. |
| Silent Hill: The Short Message / `20261009-054140-sh-game-entry-readback-color-188b9c28` | Guest `APR setBuffer failed 80020101`; the import writes the command-buffer header at `0x7060EBB648`, not the target buffer at `0x75A0E34000`. Earlier constructor header writes also return memory fault. The inner provider/permission cause is unresolved. | No accepted gameplay. Transient unrelated Kairo overlap censors timing; no causal attribution to Kairo. |

Superliminal's next launch was rejected before creating a game worker because another Kairo game was active. Remaining runs are queued through the shared single-game harness. Foreign processes are left untouched; CPU-only build/test work continues independently.

## Acceptance and remaining work

Dreaming Sarah remains the only title with verified right/left directional control, most recently on the preceding qualified `1d4c8cb1` payload. Its fresh `188b9c28` replay remains outstanding at this snapshot. No other title is accepted, and no sustained 60 FPS or fast-loading claim follows from fixture results or loading frames.

Separate source work records the first singleton original refusal and first APR header-write failure without relaxing validation or retrying effects. Demon's Souls still needs x64 native prerequisite closure for its GPU-produced private R32 path. Poppy's ordinary protection-metadata controls and executor-cell foundation do not yet implement the real fault/store/trace recovery path. Void Terrarium requires captured real shader-launch inputs before its workgroup primitive-generation path can be admitted. Quake requires an input replay anchored after its actual initialization marker to distinguish player control from the automatic demo.

This document is a snapshot after the first three new runs. Earlier failed source, native workers and game evidence remain immutable.
