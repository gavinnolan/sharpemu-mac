# APR constructor and header-write first refusal

Source base: qualified `188b9c2843289cdd4da5b709f49973eef3084f52`. Source-only diagnostic; no execution or entry improvement is claimed.

Silent Hill run `20261009-054140-sh-game-entry-readback-color-188b9c28/game.log` first reports constructor NID `8aI7R7WaOlc` MEMORY_FAULT at command buffers `0x7060EB7F48` and `0x7060EB9E08`. The subsequent `N-FSPA4S3nI` setBuffer call has command buffer `0x7060EBB648`, supplied data buffer `0x75A0E34000`, size `0x4000`, and return PC `0x8011C0147`; the guest reports `APR setBuffer failed80020101`. This precedes the secondary guest abort and later GPU failure. Foreign-process timing contamination is not evidence of a GPU cause.

Constructor writes 24 bytes to the command-buffer header. setBuffer writes size at +0xC (4 bytes), data pointer at +0x10 (8 bytes), offset at +0x4 (4 bytes), then count at +0x8 (4 bytes). It does not read or write the supplied data buffer. The actual size is below the uint limit, but the current log does not identify the refused field or inner provider cause.

This slice uses the existing default-off `SHARPEMU_LOG_APR_FAILURES=1` diagnostics and shared 64-row bound. It records only the first actual refused constructor/header write, with its original address/count/result; no memory/provider reread, retry, new permission, or state repair occurs. Successful prior header fields remain applied as before. Host command-buffer state is updated only after all four writes succeed. Provider exceptions retain their existing propagation. Existing submit/read/state diagnostics remain unchanged.

Seven prepared controls exercise all four failed fields, a failed constructor, successful header-only writes, and the uint-size boundary. They check write order, short-circuiting, no guest reads, and prior host-state preservation. They do not prove actual provider permissions or PublicationBit classification. The next qualified replay must distinguish the exact failing header field before a provider-local fix is selected; the existing bounded ordinary publication wait is already present and must not be bypassed.
