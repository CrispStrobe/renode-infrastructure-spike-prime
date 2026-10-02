// SPDX-License-Identifier: BSD-3-Clause
// Copyright (c) 2026 Brickwright contributors
using System;
using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.DMA;
using Antmicro.Renode.Peripherals.Memory;
using Antmicro.Renode.Peripherals.SPI;
using Antmicro.Renode.Time;
using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    [TestFixture]
    [NonParallelizable]
    public class STM32SPIDmaReadTests
    {
        [Test]
        public void ShouldReadRepeatedDmaBlocksAfterCpuConsumesCommandBytes()
        {
            EmulationManager.Instance.Clear();
            using(var machine = new Machine())
            {
                EmulationManager.Instance.CurrentEmulation.AddMachine(machine);
                var spi = new STM32SPI(machine);
                var dma = new STM32DMA(machine);
                var memory = new MappedMemory(machine, 4096);
                machine.SystemBus.Register(spi, new BusPointRegistration(0x40013000));
                machine.SystemBus.Register(dma, new BusPointRegistration(0x40026000));
                machine.SystemBus.Register(memory, new BusRangeRegistration(0x20000000, 4096));
                spi.Register(new PatternSource(), NullRegistrationPoint.Instance);
                spi.DMAReceive.Connect(dma, 0);
                spi.DMATransmit.Connect(dma, 1);
                spi.WriteDoubleWord(0, 1 << 6);
                spi.WriteDoubleWord(4, 3);
                memory.WriteByte(0x100, 0xFF);
                for(var block = 0; block < 6; block++)
                {
                    spi.WriteByte(0xC, 0xFF);
                    Equal(block * 33, spi.ReadByte(0xC), "CPU command response");
                    dma.WriteDoubleWord(0x18, 0x4001300C);
                    dma.WriteDoubleWord(0x1C, 0x20000000);
                    var fifo = block % 2 == 0 ? 0u : 7u;
                    dma.WriteDoubleWord(0x24, fifo); // alternate direct and NuttX full FIFO
                    dma.WriteDoubleWord(0x14, 32);
                    dma.WriteDoubleWord(0x10, 1 | (1 << 10));
                    Equal(32, dma.ReadDoubleWord(0x14), "consumed CPU byte must not trigger DMA");
                    dma.WriteDoubleWord(0x30, 0x4001300C);
                    dma.WriteDoubleWord(0x34, 0x20000100);
                    dma.WriteDoubleWord(0x3C, fifo);
                    dma.WriteDoubleWord(0x2C, 32);
                    dma.WriteDoubleWord(0x28, 1 | (1 << 6));
                    Equal(0, dma.ReadDoubleWord(0x14), "RX completion");
                    Equal(0, dma.ReadDoubleWord(0x2C), "TX completion");
                    for(var i = 0; i < 32; i++)
                    {
                        Equal(block * 33 + i + 1, memory.ReadByte(i), "ordered DMA response");
                    }
                    Equal(0, spi.ReadDoubleWord(8) & 1, "receive buffer drained");
                }
            }
            EmulationManager.Instance.Clear();
        }

        [TestCase(4096)]
        [TestCase(65535)]
        public void ShouldDrainLargePairedTransfersWithoutRecursiveRequests(int length)
        {
            EmulationManager.Instance.Clear();
            using(var machine = new Machine())
            {
                EmulationManager.Instance.CurrentEmulation.AddMachine(machine);
                var spi = new STM32SPI(machine);
                var dma = new STM32DMA(machine);
                var memory = new MappedMemory(machine, 0x20000);
                var source = new PatternSource { ExpectedData = 0xFF };
                machine.SystemBus.Register(spi, new BusPointRegistration(0x40013000));
                machine.SystemBus.Register(dma, new BusPointRegistration(0x40026000));
                machine.SystemBus.Register(memory, new BusRangeRegistration(0x20000000, 0x20000));
                spi.Register(source, NullRegistrationPoint.Instance);
                spi.DMAReceive.Connect(dma, 0);
                spi.DMATransmit.Connect(dma, 1);
                var rxAssertions = 0;
                var txAssertions = 0;
                ((GPIO)dma.Connections[0]).AddStateChangedHook(value => { if(value) rxAssertions++; });
                ((GPIO)dma.Connections[1]).AddStateChangedHook(value => { if(value) txAssertions++; });
                spi.WriteDoubleWord(0, 1 << 6);
                spi.WriteDoubleWord(4, 3);
                memory.WriteByte(0x11000, 0xFF);
                var total = 0;
                foreach(var count in new[] { length, 17, 257 })
                {
                    memory.WriteByte(0, 0xA5);
                    memory.WriteByte(count + 1, 0x5A);
                    dma.WriteDoubleWord(0x18, 0x4001300C);
                    dma.WriteDoubleWord(0x1C, 0x20000001);
                    dma.WriteDoubleWord(0x24, 7);
                    dma.WriteDoubleWord(0x14, (uint)count);
                    dma.WriteDoubleWord(0x10, 1 | (1 << 4) | (1 << 10));
                    dma.WriteDoubleWord(0x30, 0x4001300C);
                    dma.WriteDoubleWord(0x34, 0x20011000);
                    dma.WriteDoubleWord(0x3C, 7);
                    dma.WriteDoubleWord(0x2C, (uint)count);
                    dma.WriteDoubleWord(0x28, 1 | (1 << 4) | (1 << 6));
                    Equal(total + count, source.Count, "one SPI transaction per unit");
                    for(var i = 0; i < count; i++)
                    {
                        Equal((byte)(total + i), memory.ReadByte(i + 1), "ordered RX byte");
                    }
                    Equal(0xA5, memory.ReadByte(0), "leading guard");
                    Equal(0x5A, memory.ReadByte(count + 1), "trailing guard");
                    Equal(0, dma.ReadDoubleWord(0x14), "RX NDTR");
                    Equal(0, dma.ReadDoubleWord(0x2C), "TX NDTR");
                    Equal(0, dma.ReadDoubleWord(0x10) & 1, "RX EN clears");
                    Equal(0, dma.ReadDoubleWord(0x28) & 1, "TX EN clears");
                    Equal(0, spi.ReadDoubleWord(8) & 1, "RX FIFO drained");
                    Equal(1, dma.Connections[0].IsSet ? 1 : 0, "RX TC IRQ");
                    Equal(1, dma.Connections[1].IsSet ? 1 : 0, "TX TC IRQ");
                    dma.WriteDoubleWord(8, 1 << 5);
                    Equal(0, dma.Connections[0].IsSet ? 1 : 0, "RX independent W1C");
                    Equal(1, dma.Connections[1].IsSet ? 1 : 0, "TX retained until own W1C");
                    dma.WriteDoubleWord(8, 1 << 11);
                    Equal(0, dma.Connections[1].IsSet ? 1 : 0, "TX W1C");
                    total += count;
                }
                Equal(3, rxAssertions, "one RX completion edge per descriptor");
                Equal(3, txAssertions, "one TX completion edge per descriptor");
            }
            EmulationManager.Instance.Clear();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ShouldYieldCircularTransfersAndCancelStaleContinuation(bool reset)
        {
            EmulationManager.Instance.Clear();
            using(var machine = new Machine())
            {
                EmulationManager.Instance.CurrentEmulation.AddMachine(machine);
                var spi = new STM32SPI(machine);
                var dma = new STM32DMA(machine);
                var memory = new MappedMemory(machine, 4096);
                var source = new PatternSource();
                machine.SystemBus.Register(spi, new BusPointRegistration(0x40013000));
                machine.SystemBus.Register(dma, new BusPointRegistration(0x40026000));
                machine.SystemBus.Register(memory, new BusRangeRegistration(0x20000000, 4096));
                spi.Register(source, NullRegistrationPoint.Instance);
                spi.DMAReceive.Connect(dma, 0);
                spi.DMATransmit.Connect(dma, 1);
                spi.WriteDoubleWord(0, 1 << 6);
                spi.WriteDoubleWord(4, 3);
                dma.WriteDoubleWord(0x18, 0x4001300C);
                dma.WriteDoubleWord(0x1C, 0x20000000);
                dma.WriteDoubleWord(0x14, 4);
                dma.WriteDoubleWord(0x10, 1 | (1 << 8) | (1 << 10));
                dma.WriteDoubleWord(0x30, 0x4001300C);
                dma.WriteDoubleWord(0x34, 0x20000100);
                dma.WriteDoubleWord(0x2C, 4);
                dma.WriteDoubleWord(0x28, 1 | (1 << 6) | (1 << 8));
                Equal(4, source.Count, "circular immediate work is bounded");
                ((BaseClockSource)machine.ClockSource).Advance(TimeInterval.FromMicroseconds(1), true);
                Equal(8, source.Count, "deferred circular buffer executes");
                if(reset)
                {
                    dma.Reset();
                }
                else
                {
                    dma.WriteDoubleWord(0x28, (1 << 6) | (1 << 8));
                    // A new finite descriptor must not inherit the deferred one.
                    dma.WriteDoubleWord(0x2C, 3);
                    dma.WriteDoubleWord(0x28, 1 | (1 << 6));
                    Equal(11, source.Count, "re-enabled descriptor");
                }
                var before = source.Count;
                ((BaseClockSource)machine.ClockSource).Advance(TimeInterval.FromMicroseconds(2), true);
                Equal(before, source.Count, "stale continuation cancelled");
            }
            EmulationManager.Instance.Clear();
        }

        private static void Equal(long expected, long actual, string context)
        {
            if(expected != actual) throw new Exception(context + ": " + expected + " / " + actual);
        }

        private sealed class PatternSource : ISPIPeripheral
        {
            public byte Transmit(byte data)
            {
                if(ExpectedData.HasValue) Equal(ExpectedData.Value, data, "TX byte");
                return (byte)Count++;
            }
            public void FinishTransmission() { }
            public void Reset() { Count = 0; }
            public int Count { get; private set; }
            public byte? ExpectedData { get; set; }
        }
    }
}
