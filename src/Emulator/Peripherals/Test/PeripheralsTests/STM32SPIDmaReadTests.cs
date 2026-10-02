// SPDX-License-Identifier: BSD-3-Clause
// Copyright (c) 2026 Brickwright contributors
using System;
using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.DMA;
using Antmicro.Renode.Peripherals.Memory;
using Antmicro.Renode.Peripherals.SPI;
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

        private static void Equal(long expected, long actual, string context)
        {
            if(expected != actual) throw new Exception(context + ": " + expected + " / " + actual);
        }

        private sealed class PatternSource : ISPIPeripheral
        {
            public byte Transmit(byte data) => next++;
            public void FinishTransmission() { }
            public void Reset() { next = 0; }
            private byte next;
        }
    }
}
