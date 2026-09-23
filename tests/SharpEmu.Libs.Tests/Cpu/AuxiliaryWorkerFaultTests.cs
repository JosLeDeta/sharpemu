// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Native;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

public sealed class AuxiliaryWorkerFaultTests
{
    [Theory]
    [InlineData(0xC0000005u, 2u, 0UL, false)]
    [InlineData(0xC0000005u, 2u, 1UL, false)]
    [InlineData(0xC0000005u, 2u, 8UL, true)]
    [InlineData(0xC0000005u, 0u, 8UL, false)]
    [InlineData(0xC0000005u, 1u, 8UL, false)]
    [InlineData(0xC000001Du, 2u, 8UL, false)]
    public void WorkerAbortOnlyHandlesExecuteFaults(uint code, uint parameterCount, ulong accessType, bool expected)
    {
        Assert.Equal(expected, DirectExecutionBackend.IsAuxiliaryExecuteAccessViolation(code, parameterCount, accessType));
    }
}
