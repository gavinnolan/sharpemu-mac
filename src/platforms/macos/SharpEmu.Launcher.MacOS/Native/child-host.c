// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
#include <errno.h>
#include <stdio.h>
#include <unistd.h>

// The owned PID becomes the private session/process-group identity before any
// target code can create children. No shell parsing or environment rewriting.
int main(int argc, char **argv)
{
    if (argc < 2) { fputs("SharpEmu child host: missing executable\n", stderr); return 127; }
    if (setsid() == -1) { perror("SharpEmu child host setsid"); return 127; }
    if (dprintf(STDERR_FILENO, "SHARPEMU_CHILD_GROUP_READY:%d\n", (int)getpid()) < 0) return 127;
    execv(argv[1], argv + 1);
    int error = errno;
    perror("SharpEmu child host execv");
    return error == ENOENT ? 127 : 126;
}
