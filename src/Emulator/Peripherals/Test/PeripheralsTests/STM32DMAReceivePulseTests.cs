// SPDX-License-Identifier: BSD-3-Clause
// Copyright (c) 2026 Brickwright contributors
using System;
using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure;
using Antmicro.Renode.Peripherals;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.DMA;
using Antmicro.Renode.Peripherals.Memory;
using Antmicro.Renode.Time;
using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    [TestFixture]
    [NonParallelizable]
    public class STM32DMAReceivePulseTests
    {
        [Test]
        public void ShouldRetainReceivePulseAfterRearmInsideSourceRead(
            [Values(false, true)] bool reset, [Values(0, 1, 2)] int width)
        {
            using(var rig = new Rig())
            {
                rig.OldSource.FirstRead = () =>
                {
                    rig.Abort(reset);
                    rig.Program(NewSourceAddress, 0x500, 1, width);
                    rig.Pulse();
                };
                rig.Program(OldSourceAddress, 0x400, 2, width);
                rig.Dma.OnGPIO(0, true);
                Assert.AreEqual(1, rig.OldSource.Reads);
                Assert.AreEqual(0, rig.NewSource.Reads, "new request waits until old copy returns");
                Assert.AreEqual(0, rig.Completions, "aborted generation cannot acknowledge completion");
                Assert.AreEqual(1, rig.Dma.ReadDoubleWord(0x14));
                Assert.AreEqual(0x11, rig.Read(0x400, width));
                rig.Advance();
                Assert.AreEqual(1, rig.NewSource.Reads);
                Assert.AreEqual(0x21, rig.Read(0x500, width));
                Assert.AreEqual(0, rig.Dma.ReadDoubleWord(0x14));
                Assert.AreEqual(1, rig.Completions);
                rig.AssertGuards(width);
                rig.Advance();
                Assert.AreEqual(1, rig.NewSource.Reads);
                Assert.AreEqual(1, rig.Completions);
            }
        }

        [Test]
        public void ShouldRetainNestedReceivePulseWithoutRearm([Values(0, 1, 2)] int width)
        {
            using(var rig = new Rig())
            {
                rig.OldSource.FirstRead = rig.Pulse;
                rig.Program(OldSourceAddress, 0x400, 2, width);
                rig.Dma.OnGPIO(0, true);
                Assert.AreEqual(2, rig.OldSource.Reads);
                Assert.AreEqual(0x11, rig.Read(0x400, width));
                Assert.AreEqual(0x12, rig.Read(0x400 + (1 << width), width));
                Assert.AreEqual(0, rig.Dma.ReadDoubleWord(0x14));
                Assert.AreEqual(1, rig.Completions);
                Assert.AreEqual(0xA5, rig.Read(0x400 - (1 << width), width));
                Assert.AreEqual(0xA5, rig.Read(0x400 + 2 * (1 << width), width));
                rig.Advance();
                Assert.AreEqual(2, rig.OldSource.Reads);
                Assert.AreEqual(1, rig.Completions);
            }
        }

        [Test]
        public void ShouldCancelQueuedReceiveGenerationOnAbortOrReset([Values(false, true)] bool reset)
        {
            using(var rig = new Rig())
            {
                rig.OldSource.FirstRead = () =>
                {
                    rig.Abort(false);
                    rig.Program(NewSourceAddress, 0x500, 1, 0);
                    rig.Pulse();
                    rig.Abort(reset);
                };
                rig.Program(OldSourceAddress, 0x400, 2, 0);
                rig.Dma.OnGPIO(0, true);
                rig.Advance();
                Assert.AreEqual(0, rig.NewSource.Reads);
                Assert.AreEqual(0xA5, rig.Read(0x500, 0));
                Assert.AreEqual(0, rig.Completions);
                rig.Program(NewSourceAddress, 0x500, 1, 0);
                Assert.AreEqual(0, rig.NewSource.Reads, "cancelled pulse must not leak into another descriptor");
                rig.Pulse();
                Assert.AreEqual(1, rig.NewSource.Reads);
                Assert.AreEqual(0x21, rig.Read(0x500, 0));
                Assert.AreEqual(1, rig.Completions);
                rig.AssertGuards(0);
            }
        }

        [Test]
        public void ShouldNotCarryOldGenerationPulseAcrossManualRearm()
        {
            using(var rig = new Rig())
            {
                rig.OldSource.FirstRead = () =>
                {
                    rig.Pulse(); // This request belongs to the old descriptor.
                    rig.Abort(false);
                    rig.Program(NewSourceAddress, 0x500, 1, 0);
                };
                rig.Program(OldSourceAddress, 0x400, 2, 0);
                rig.Dma.OnGPIO(0, true);
                rig.Advance();
                Assert.AreEqual(0, rig.NewSource.Reads);
                Assert.AreEqual(0xA5, rig.Read(0x500, 0));
                Assert.AreEqual(0, rig.Completions);
                rig.Pulse();
                Assert.AreEqual(1, rig.NewSource.Reads);
                Assert.AreEqual(0x21, rig.Read(0x500, 0));
                Assert.AreEqual(1, rig.Completions);
                rig.AssertGuards(0);
            }
        }

        [Test]
        public void ShouldCancelDisabledReadinessWhenLowAtRest()
        {
            using(var rig = new Rig())
            {
                rig.Pulse();
                rig.Program(NewSourceAddress, 0x500, 1, 0);
                Assert.AreEqual(0, rig.NewSource.Reads);
                Assert.AreEqual(1, rig.Dma.ReadDoubleWord(0x14));
                rig.Advance();
                Assert.AreEqual(0, rig.NewSource.Reads);
                rig.Pulse();
                Assert.AreEqual(1, rig.NewSource.Reads);
                Assert.AreEqual(1, rig.Completions);
                rig.AssertGuards(0, false);
            }
        }

        [Test]
        public void ShouldPreserveDisabledReadinessUntilEnable()
        {
            using(var rig = new Rig())
            {
                rig.Dma.OnGPIO(0, true);
                rig.Program(NewSourceAddress, 0x500, 1, 0);
                Assert.AreEqual(1, rig.NewSource.Reads);
                Assert.AreEqual(0x21, rig.Read(0x500, 0));
                Assert.AreEqual(1, rig.Completions);
                rig.Advance();
                Assert.AreEqual(1, rig.NewSource.Reads);
                rig.AssertGuards(0, false);
            }
        }

        [Test]
        public void ShouldPreserveHeldReceiveReadinessAcrossManualRearm()
        {
            using(var rig = new Rig())
            {
                rig.OldSource.FirstRead = () =>
                {
                    rig.Abort(false);
                    rig.Program(OldSourceAddress, 0x500, 1, 0);
                    // No second edge: the original request line is still high.
                };
                rig.Program(OldSourceAddress, 0x400, 2, 0);
                rig.Dma.OnGPIO(0, true);
                rig.Advance();
                Assert.AreEqual(2, rig.OldSource.Reads);
                Assert.AreEqual(0x12, rig.Read(0x500, 0));
                Assert.AreEqual(1, rig.Completions);
                rig.AssertGuards(0);
                rig.Advance();
                Assert.AreEqual(2, rig.OldSource.Reads);
                Assert.AreEqual(1, rig.Completions);
            }
        }

        [Test]
        public void ShouldCancelHeldReceiveReadinessOnReset()
        {
            using(var rig = new Rig())
            {
                rig.OldSource.FirstRead = () =>
                {
                    rig.Abort(true);
                    rig.Program(OldSourceAddress, 0x500, 2, 0);
                    // No new event after reset. A subsequent disable/re-enable
                    // must not recover the pre-reset high line as readiness.
                    rig.Abort(false);
                    rig.Program(OldSourceAddress, 0x500, 1, 0);
                };
                rig.Program(OldSourceAddress, 0x400, 2, 0);
                rig.Dma.OnGPIO(0, true);
                rig.Advance();
                Assert.AreEqual(1, rig.OldSource.Reads);
                Assert.AreEqual(0xA5, rig.Read(0x500, 0));
                Assert.AreEqual(0, rig.Completions);
                rig.Pulse();
                Assert.AreEqual(2, rig.OldSource.Reads);
                Assert.AreEqual(0x12, rig.Read(0x500, 0));
                Assert.AreEqual(1, rig.Completions);
                rig.AssertGuards(0);
            }
        }

        [Test]
        public void ShouldCancelQueuedReceiveReadinessWhenLowAtRest()
        {
            using(var rig = new Rig())
            {
                rig.OldSource.FirstRead = () =>
                {
                    rig.Abort(false);
                    rig.Program(NewSourceAddress, 0x500, 1, 0);
                    rig.Pulse();
                };
                rig.Program(OldSourceAddress, 0x400, 2, 0);
                rig.Dma.OnGPIO(0, true);
                rig.Dma.OnGPIO(0, false); // Outside IssueCopy this cancels readiness.
                rig.Advance();
                Assert.AreEqual(0, rig.NewSource.Reads);
                Assert.AreEqual(1, rig.Dma.ReadDoubleWord(0x14));
                Assert.AreEqual(0, rig.Completions);
                rig.Pulse();
                Assert.AreEqual(1, rig.NewSource.Reads);
                Assert.AreEqual(1, rig.Completions);
                rig.AssertGuards(0);
            }
        }

        private sealed class Rig : IDisposable
        {
            public Rig()
            {
                EmulationManager.Instance.Clear();
                machine = new Machine();
                EmulationManager.Instance.CurrentEmulation.AddMachine(machine);
                Dma = new STM32DMA(machine);
                OldSource = new Source(0x11);
                NewSource = new Source(0x21);
                memory = new MappedMemory(machine, 4096);
                machine.SystemBus.Register(Dma, new BusPointRegistration(0x40026400));
                machine.SystemBus.Register(OldSource, new BusRangeRegistration(OldSourceAddress, 16));
                machine.SystemBus.Register(NewSource, new BusRangeRegistration(NewSourceAddress, 16));
                machine.SystemBus.Register(memory, new BusRangeRegistration(0x20000000, 4096));
                Dma.TransferComplete0.AddStateChangedHook(value => { if(value) Completions++; });
                for(var i = 0; i < 4096; i++) memory.WriteByte(i, 0xA5);
            }
            public void Program(uint source, uint destination, uint count, int width)
            {
                Dma.WriteDoubleWord(0x18, source);
                Dma.WriteDoubleWord(0x1c, 0x20000000 + destination);
                Dma.WriteDoubleWord(0x14, count);
                Dma.WriteDoubleWord(0x10, 1u | (1u << 10) | ((uint)width << 11) | ((uint)width << 13));
            }
            public void Abort(bool reset)
            {
                if(reset) Dma.Reset();
                else Dma.WriteDoubleWord(0x10, Dma.ReadDoubleWord(0x10) & ~1u);
            }
            public void Pulse() { Dma.OnGPIO(0, true); Dma.OnGPIO(0, false); }
            public void Advance() { ((BaseClockSource)machine.ClockSource).Advance(TimeInterval.FromMicroseconds(2), true); }
            public uint Read(int offset, int width)
            {
                var value = width == 0 ? memory.ReadByte(offset) : width == 1 ? memory.ReadWord(offset) : memory.ReadDoubleWord(offset);
                return value == 0xA5A5 || value == 0xA5A5A5A5 ? 0xA5u : value;
            }
            public void AssertGuards(int width, bool oldCopied = true)
            {
                var size = 1 << width;
                Assert.AreEqual(oldCopied ? 0x11 : 0xA5, Read(0x400, width));
                Assert.AreEqual(0xA5, Read(0x400 - size, width));
                Assert.AreEqual(0xA5, Read(0x400 + size, width));
                Assert.AreEqual(0xA5, Read(0x500 - size, width));
                Assert.AreEqual(0xA5, Read(0x500 + size, width));
            }
            public void Dispose()
            {
                EmulationManager.Instance.CurrentEmulation.RemoveMachine(machine);
                machine.Dispose();
                EmulationManager.Instance.Clear();
            }
            public STM32DMA Dma { get; private set; }
            public Source OldSource { get; private set; }
            public Source NewSource { get; private set; }
            public int Completions { get; private set; }
            private readonly Machine machine;
            private readonly MappedMemory memory;
        }
        private sealed class Source : IBytePeripheral, IWordPeripheral, IDoubleWordPeripheral, IKnownSize
        {
            public Source(uint firstValue) { this.firstValue = firstValue; }
            public byte ReadByte(long offset) { return (byte)Read(); }
            public ushort ReadWord(long offset) { return (ushort)Read(); }
            public uint ReadDoubleWord(long offset) { return Read(); }
            public void WriteByte(long offset, byte value) { }
            public void WriteWord(long offset, ushort value) { }
            public void WriteDoubleWord(long offset, uint value) { }
            public void Reset() { Reads = 0; }
            public long Size => 16;
            public Action FirstRead { get; set; }
            public int Reads { get; private set; }
            private uint Read() { var value = firstValue + (uint)Reads++; if(Reads == 1 && FirstRead != null) FirstRead(); return value; }
            private readonly uint firstValue;
        }
        private const uint OldSourceAddress = 0x40000000;
        private const uint NewSourceAddress = 0x40000100;
    }
}
