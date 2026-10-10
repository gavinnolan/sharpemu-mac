# Input scheduling after game initialization

The retained Quake replay `20261008-200941-quake-game-entry-decd4910` sent scheduled menu inputs at launch-relative seconds 10 through 100 and later logged automatic `demo1.dem` / The Necropolis loading. Its screenshots are corrupted, and neither delivered input nor a loaded demo proves player control. The old harness did not record when initialization was observed relative to its input plan, so this evidence does not establish whether those menu inputs reached a ready game.

`SHARPEMU_INPUT_START_AFTER_LOG='Quake Initialized'` makes key-plan times relative to the first observed literal marker in the current run's own game log. A missing marker produces no input and retains the existing bounded run limit. The run environment records the marker, and `keys.log` records its actual observed time, held-key transitions and final clearing. Launch-relative plans remain the default.

One bounded Python child now owns the key-state file. Atomic replacements prevent partial snapshots; overlapping holds retain all active keys, including a newer hold of the same key. Previously, an older background sleep could clear a newer key press. The harness stops and waits for its exact input child during cleanup, and a failed input scheduler stops the game with an explicit result instead of implying that the plan was delivered.

The six tests passed in 1.056 seconds, covering overlapping holds/chords, malformed and unbounded plans, markers split across writes, actual no-input-before-marker behavior, exact owned-child TERM clearing, a missing-marker deadline and existing launch-relative behavior. Shell syntax validation also passed. They launch no emulator and supply no gameplay or graphics proof. No app payload or game/save/config file changes in this slice.

After qualification, a Quake entry replay should use the initialization marker and send Options/menu actions followed by movement only after observation. Keep the same signed app provenance and machine game lock; record reached scene and visible control separately from log progression.
