// SPDX-License-Identifier: BSD-3-Clause
// Copyright (c) 2026 Brickwright contributors
using System;
using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals;
using Antmicro.Renode.Core.Structure;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.DMA;
using Antmicro.Renode.Peripherals.Memory;
using Antmicro.Renode.Time;
using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    [TestFixture]
    [NonParallelizable]
    public class STM32DMAAbortRearmTests
    {
        [Test]
        public void ShouldReloadProgrammedBasesAfterPartialAbort(
            [Values(false, true)] bool transmit,
            [Values(0, 1, 2)] int width,
            [Values(false, true)] bool memoryIncrement,
            [Values(false, true)] bool peripheralIncrement)
        {
            using(var rig = new Rig())
            {
                var size = 1 << width;
                var control = Control(transmit, width, memoryIncrement, peripheralIncrement);
                rig.Fill(0x100, size, 0x11);
                rig.Fill(0x200, size, 0x22);
                rig.Program(transmit, 0x100, 0x500, 4, control);
                rig.Request();
                Assert.AreEqual(3, rig.Dma.ReadDoubleWord(0x14));
                Assert.AreEqual(0x11, rig.Read(0x500, size));
                rig.Dma.WriteDoubleWord(0x10, control & ~1u);
                Assert.AreEqual(3, rig.Dma.ReadDoubleWord(0x14), "abort exposes the residual count");
                Assert.IsFalse(rig.Dma.TransferComplete0.IsSet);
                Assert.AreEqual(0, rig.Completions, "abort must not acknowledge a successful buffer");
                rig.Program(transmit, 0x200, 0x600, 2, control);
                rig.Request();
                Assert.AreEqual(1, rig.Dma.ReadDoubleWord(0x14));
                Assert.AreEqual(0x22, rig.Read(0x600, size), "reprogrammed descriptor starts at its base");
                rig.Request();
                var sourceIncrement = transmit ? memoryIncrement : peripheralIncrement;
                var destinationIncrement = transmit ? peripheralIncrement : memoryIncrement;
                Assert.AreEqual(sourceIncrement ? 0x23 : 0x22, rig.Read(0x600 + (destinationIncrement ? size : 0), size));
                if(destinationIncrement) Assert.AreEqual(0x22, rig.Read(0x600, size));
                Assert.AreEqual(0xA5, rig.Read(0x600 - size, size));
                Assert.AreEqual(0xA5, rig.Read(0x600 + 2 * size, size));
                Assert.AreEqual(0x11, rig.Read(0x500, size), "abandoned buffer is unchanged");
                Assert.AreEqual(0, rig.Dma.ReadDoubleWord(0x14));
                Assert.AreEqual(0, rig.Dma.ReadDoubleWord(0x10) & 1);
                Assert.AreEqual(1u << 5, rig.Dma.ReadDoubleWord(0) & (1u << 5));
            }
        }

        [Test]
        public void ShouldPreserveProgressWhenEnabledControlIsWrittenAgain(
            [Values(false, true)] bool transmit, [Values(0, 1, 2)] int width)
        {
            using(var rig = new Rig())
            {
                var size = 1 << width;
                var control = Control(transmit, width, true, true);
                rig.Fill(0x100, size, 0x31);
                rig.Program(transmit, 0x100, 0x500, 3, control);
                rig.Request();
                rig.Dma.WriteDoubleWord(0x10, control | (1u << 4));
                Assert.AreEqual(2, rig.Dma.ReadDoubleWord(0x14));
                Assert.AreEqual(0xA5, rig.Read(0x500 + size, size), "control write must not request data");
                rig.Request();
                Assert.AreEqual(0x31, rig.Read(0x500, size));
                Assert.AreEqual(0x32, rig.Read(0x500 + size, size));
                Assert.AreEqual(1, rig.Dma.ReadDoubleWord(0x14));
                rig.Request();
                Assert.AreEqual(0x33, rig.Read(0x500 + 2 * size, size));
                Assert.AreEqual(0, rig.Dma.ReadDoubleWord(0x14));
            }
        }

        [Test]
        public void ShouldUseWholeControlWriteForUnpacedMemoryCopy([Values(0, 1, 2)] int width)
        {
            using(var rig = new Rig())
            {
                var size = 1 << width;
                rig.Fill(0x100, size, 0x41);
                rig.Dma.WriteDoubleWord(0x18, RamAddress + 0x100);
                rig.Dma.WriteDoubleWord(0x1c, RamAddress + 0x500);
                rig.Dma.WriteDoubleWord(0x14, 3);
                var control = 1u | (2u << 6) | (1u << 9) | (1u << 10) | ((uint)width << 11) | ((uint)width << 13);
                rig.Dma.WriteDoubleWord(0x10, control);
                Assert.AreEqual(0, rig.Dma.ReadDoubleWord(0x14));
                Assert.AreEqual(0, rig.Dma.ReadDoubleWord(0x10) & 1);
                for(var i = 0; i < 3; i++) Assert.AreEqual(0x41 + i, rig.Read(0x500 + i * size, size));
                Assert.AreEqual(0xA5, rig.Read(0x500 - size, size));
                Assert.AreEqual(0xA5, rig.Read(0x500 + 3 * size, size));
            }
        }

        [Test]
        public void ShouldReloadProgrammedCountUnlessResidualIsWritten(
            [Values(false, true)] bool transmit, [Values(0, 1, 2)] int width,
            [Values(false, true)] bool writeResidual)
        {
            using(var rig = new Rig())
            {
                var size = 1 << width;
                var control = Control(transmit, width, true, true);
                rig.Fill(0x100, size, 0x51);
                rig.Program(transmit, 0x100, 0x500, 4, control);
                rig.Request();
                rig.Dma.WriteDoubleWord(0x10, control & ~1u);
                Assert.AreEqual(3, rig.Dma.ReadDoubleWord(0x14));
                if(writeResidual)
                {
                    rig.Dma.WriteDoubleWord(0x18, RamAddress + (uint)(transmit ? 0x500 + size : 0x100 + size));
                    rig.Dma.WriteDoubleWord(0x1c, RamAddress + (uint)(transmit ? 0x100 + size : 0x500 + size));
                    rig.Dma.WriteDoubleWord(0x14, 3);
                }
                rig.Dma.WriteDoubleWord(0x10, control);
                Assert.AreEqual(writeResidual ? 3 : 4, rig.Dma.ReadDoubleWord(0x14), "real enable reloads the last software-programmed count");
                var count = writeResidual ? 3 : 4;
                for(var i = 0; i < count; i++) rig.Request();
                for(var i = 0; i < 4; i++) Assert.AreEqual(0x51 + i, rig.Read(0x500 + i * size, size));
                Assert.AreEqual(0xA5, rig.Read(0x500 + 4 * size, size));
                Assert.AreEqual(0, rig.Dma.ReadDoubleWord(0x14));
            }
        }

        [Test]
        public void ShouldDeferRearmedMemoryCopyUntilOldGenerationReturns([Values(false, true)] bool reset)
        {
            using(var rig = new Rig())
            {
                rig.Fill(0x100, 1, 0x61);
                rig.Fill(0x200, 1, 0x71);
                var control = 1u | (2u << 6) | (1u << 9) | (1u << 10);
                var sink = rig.AddSink(() =>
                {
                    if(reset) rig.Dma.Reset();
                    else rig.Dma.WriteDoubleWord(0x10, control & ~1u);
                    rig.Dma.WriteDoubleWord(0x18, RamAddress + 0x200);
                    rig.Dma.WriteDoubleWord(0x1c, RamAddress + 0x700);
                    rig.Dma.WriteDoubleWord(0x14, 2);
                    rig.Dma.WriteDoubleWord(0x10, control);
                });
                rig.Dma.WriteDoubleWord(0x18, RamAddress + 0x100);
                rig.Dma.WriteDoubleWord(0x1c, 0x40000000);
                rig.Dma.WriteDoubleWord(0x14, 2);
                rig.Dma.WriteDoubleWord(0x10, control);
                Assert.AreEqual(2, sink.Writes, "already issued copy finishes synchronously");
                Assert.AreEqual(0, rig.Completions, "old generation must not acknowledge the new descriptor");
                Assert.AreEqual(2, rig.Dma.ReadDoubleWord(0x14));
                Assert.AreEqual(0xA5, rig.Read(0x700, 1));
                rig.Advance();
                Assert.AreEqual(0x71, rig.Read(0x700, 1));
                Assert.AreEqual(0x72, rig.Read(0x701, 1));
                Assert.AreEqual(0xA5, rig.Read(0x702, 1));
                Assert.AreEqual(1, rig.Completions);
                Assert.AreEqual(0, rig.Dma.ReadDoubleWord(0x14));
                rig.Advance();
                Assert.AreEqual(1, rig.Completions, "no stale or duplicate continuation");
            }
        }

        [Test]
        public void ShouldCancelQueuedRearmWhenDisabledOrReset([Values(false, true)] bool reset)
        {
            using(var rig = new Rig())
            {
                rig.Fill(0x100, 1, 0x91);
                rig.Fill(0x200, 1, 0xA1);
                var control = 1u | (2u << 6) | (1u << 9) | (1u << 10);
                rig.AddSink(() =>
                {
                    rig.Dma.WriteDoubleWord(0x10, control & ~1u);
                    rig.Dma.WriteDoubleWord(0x18, RamAddress + 0x200);
                    rig.Dma.WriteDoubleWord(0x1c, RamAddress + 0x700);
                    rig.Dma.WriteDoubleWord(0x14, 2);
                    rig.Dma.WriteDoubleWord(0x10, control);
                });
                rig.Dma.WriteDoubleWord(0x18, RamAddress + 0x100);
                rig.Dma.WriteDoubleWord(0x1c, 0x40000000);
                rig.Dma.WriteDoubleWord(0x14, 2);
                rig.Dma.WriteDoubleWord(0x10, control);
                if(reset) rig.Dma.Reset();
                else rig.Dma.WriteDoubleWord(0x10, control & ~1u);
                rig.Advance();
                Assert.AreEqual(0xA5, rig.Read(0x700, 1), "invalidated scheduled generation must not copy");
                Assert.AreEqual(0, rig.Completions);
                rig.Program(false, 0x200, 0x700, 2, Control(false, 0, true, true));
                rig.Request();
                rig.Request();
                Assert.AreEqual(0xA1, rig.Read(0x700, 1));
                Assert.AreEqual(0xA2, rig.Read(0x701, 1));
                Assert.AreEqual(1, rig.Completions);
                rig.Advance();
                Assert.AreEqual(1, rig.Completions);
            }
        }

        [Test]
        public void ShouldServeNewPeripheralRequestReceivedDuringOldCopy()
        {
            using(var rig = new Rig())
            {
                rig.Fill(0x100, 1, 0xB1);
                rig.Fill(0x200, 1, 0xC1);
                var control = Control(true, 0, true, false);
                rig.AddSink(() =>
                {
                    rig.Dma.WriteDoubleWord(0x10, control & ~1u);
                    rig.Program(true, 0x200, 0x700, 1, control);
                    // The new descriptor has no request at its enable edge;
                    // its one pulse arrives before the old IssueCopy returns.
                    rig.Dma.OnGPIO(0, true);
                    rig.Dma.OnGPIO(0, false);
                });
                rig.Dma.WriteDoubleWord(0x18, 0x40000000);
                rig.Dma.WriteDoubleWord(0x1c, RamAddress + 0x100);
                rig.Dma.WriteDoubleWord(0x14, 2);
                rig.Dma.WriteDoubleWord(0x10, control);
                rig.Dma.OnGPIO(0, true);
                Assert.AreEqual(1, rig.Dma.ReadDoubleWord(0x14));
                Assert.AreEqual(0, rig.Completions);
                rig.Advance();
                Assert.AreEqual(0xC1, rig.Read(0x700, 1));
                Assert.AreEqual(0xA5, rig.Read(0x701, 1));
                Assert.AreEqual(0, rig.Dma.ReadDoubleWord(0x14));
                Assert.AreEqual(1, rig.Completions);
                rig.Advance();
                Assert.AreEqual(1, rig.Completions);
            }
        }

        [Test]
        public void ShouldNotRetriggerMemoryCopyOnEnabledControlWrite()
        {
            using(var rig = new Rig())
            {
                rig.Fill(0x100, 1, 0x81);
                var control = 1u | (2u << 6) | (1u << 9) | (1u << 10);
                var sink = rig.AddSink(() => rig.Dma.WriteDoubleWord(0x10, control | (1u << 4)));
                rig.Dma.WriteDoubleWord(0x18, RamAddress + 0x100);
                rig.Dma.WriteDoubleWord(0x1c, 0x40000000);
                rig.Dma.WriteDoubleWord(0x14, 2);
                rig.Dma.WriteDoubleWord(0x10, control);
                Assert.AreEqual(2, sink.Writes);
                Assert.AreEqual(1, rig.Completions);
                Assert.AreEqual(0, rig.Dma.ReadDoubleWord(0x14));
                rig.Advance();
                Assert.AreEqual(2, sink.Writes);
                Assert.AreEqual(1, rig.Completions);
            }
        }

        private static uint Control(bool transmit, int width, bool memoryIncrement, bool peripheralIncrement)
        {
            return 1u | (transmit ? 1u << 6 : 0) | (memoryIncrement ? 1u << 10 : 0)
                | (peripheralIncrement ? 1u << 9 : 0) | ((uint)width << 11) | ((uint)width << 13);
        }

        private sealed class Rig : IDisposable
        {
            public Rig()
            {
                EmulationManager.Instance.Clear();
                machine = new Machine();
                EmulationManager.Instance.CurrentEmulation.AddMachine(machine);
                Dma = new STM32DMA(machine);
                Dma.TransferComplete0.AddStateChangedHook(value => { if(value) Completions++; });
                memory = new MappedMemory(machine, 4096);
                machine.SystemBus.Register(Dma, new BusPointRegistration(0x40026400));
                machine.SystemBus.Register(memory, new BusRangeRegistration(RamAddress, 4096));
                for(var i = 0; i < 4096; i++) memory.WriteByte(i, 0xA5);
            }
            public void Program(bool transmit, int source, int destination, uint count, uint control)
            {
                Dma.WriteDoubleWord(0x18, RamAddress + (uint)(transmit ? destination : source));
                Dma.WriteDoubleWord(0x1c, RamAddress + (uint)(transmit ? source : destination));
                Dma.WriteDoubleWord(0x14, count);
                Dma.WriteDoubleWord(0x10, control);
            }
            public void Request() { Dma.OnGPIO(0, true); Dma.OnGPIO(0, false); }
            public Sink AddSink(Action firstWrite)
            {
                var sink = new Sink(firstWrite);
                machine.SystemBus.Register(sink, new BusRangeRegistration(0x40000000, 16));
                return sink;
            }
            public void Advance() { ((BaseClockSource)machine.ClockSource).Advance(TimeInterval.FromMicroseconds(2), true); }
            public void Fill(int offset, int size, byte value)
            {
                for(var i = 0; i < 4; i++)
                {
                    if(size == 1) memory.WriteByte(offset + i, (byte)(value + i));
                    else if(size == 2) memory.WriteWord(offset + 2 * i, (ushort)(value + i));
                    else memory.WriteDoubleWord(offset + 4 * i, (uint)(value + i));
                }
            }
            public uint Read(int offset, int size)
            {
                var value = size == 1 ? memory.ReadByte(offset) : size == 2 ? memory.ReadWord(offset) : memory.ReadDoubleWord(offset);
                // Guards are initialized by byte, so wider untouched words are A5 repeated.
                return value == 0xA5A5 || value == 0xA5A5A5A5 ? 0xA5u : value;
            }
            public void Dispose()
            {
                EmulationManager.Instance.CurrentEmulation.RemoveMachine(machine);
                machine.Dispose();
                EmulationManager.Instance.Clear();
            }
            public int Completions { get; private set; }
            public STM32DMA Dma { get; private set; }
            private readonly Machine machine;
            private readonly MappedMemory memory;
        }
        private sealed class Sink : IBytePeripheral, IKnownSize
        {
            public Sink(Action firstWrite) { this.firstWrite = firstWrite; }
            public void WriteByte(long offset, byte value) { if(Writes++ == 0) firstWrite(); }
            public byte ReadByte(long offset) { return 0; }
            public void Reset() { Writes = 0; }
            public long Size => 16;
            public int Writes { get; private set; }
            private readonly Action firstWrite;
        }
        private const uint RamAddress = 0x20000000;
    }
}
