// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.Scheduling;

/// <summary>
/// What a command buffer was recording when something went wrong, carried through
/// <c>IRenderHost.SetDebugInformation</c> so a backend can label its native submissions. It is
/// guest vocabulary, not an API concept, so both backends use the same list.
/// </summary>
public enum RecordedOperation : uint
{
    DispatchDirect,
    DrawIndex,
    DrawIndexAuto,
    EopWrite,
    EopInterrupt,
    EopWriteBack,
    EopFlip,
    EopWriteBackFlip,
    EopOnlyFlip,
    Unknown,
}
