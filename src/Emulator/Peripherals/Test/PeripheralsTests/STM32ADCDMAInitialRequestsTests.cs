// SPDX-License-Identifier: BSD-3-Clause
// Copyright (c) 2026 Brickwright contributors
using System;
using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure;
using Antmicro.Renode.Peripherals.Analog;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.DMA;
using Antmicro.Renode.Peripherals.Memory;
using Antmicro.Renode.Time;
using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    [TestFixture]
    [NonParallelizable]
    public class STM32ADCDMAInitialRequestsTests
    {
        // Deliberately uses only the original public APIs: this regression
        // can execute against the original model, without a completion seam.
        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void ShouldTransferInitialDDSZeroSample(bool eachConversion, bool enableInterrupt)
        {
            using(var rig = new STM32ADCDMATestRig())
            {
                rig.ConfigureDMA(2, enableInterrupt);
                rig.StartADC(false, eachConversion);
                rig.Step();
                Assert.AreEqual(0x111, rig.ReadSample(0));
                Assert.AreEqual(1u, rig.DMA.ReadDoubleWord(0x14));
                Assert.AreEqual(0xaaaa, rig.ReadSample(1));
            }
        }
    }

    internal sealed class STM32ADCDMATestRig : IDisposable
    {
        public STM32ADCDMATestRig()
        {
            EmulationManager.Instance.Clear();
            Machine = new Machine();
            EmulationManager.Instance.CurrentEmulation.AddMachine(Machine);
            ADC = new STM32_ADC(Machine);
            DMA = new STM32DMA(Machine);
            var memory = new MappedMemory(Machine, 0x1000);
            Machine.SystemBus.Register(ADC, new BusRangeRegistration(ADCAddress, 0x50));
            Machine.SystemBus.Register(DMA, new BusRangeRegistration(0x40026400, 0x400));
            Machine.SystemBus.Register(memory, new BusRangeRegistration(MemoryAddress, 0x1000));
            for(var i = 0; i < 0x200; i += 2) Machine.SystemBus.WriteWord(MemoryAddress + (ulong)i, 0xaaaa);
            ADC.SetChannelValue(0, 0x111);
            ADC.SetChannelValue(1, 0x222);
            ADC.SetChannelValue(2, 0x333);
            ADC.WriteDoubleWord(0x2c, 2u << 20);
            ADC.WriteDoubleWord(0x34, (1u << 5) | (2u << 10));
            ADC.WriteDoubleWord(4, 1u << 8);
            ADC.DMARequest.Connect(DMA, 0);
            ADC.DMARequest.AddStateChangedHook(value => { if(value) Requests++; });
        }

        public void ConfigureDMA(uint count, bool interrupt = false, bool circular = false, ulong memoryOffset = 0, int stream = 0, bool memoryIncrement = true)
        {
            var offset = stream * 0x18;
            DMA.WriteDoubleWord(0x10 + offset, 0);
            DMA.WriteDoubleWord(0x18 + offset, (uint)(ADCAddress + 0x4c));
            DMA.WriteDoubleWord(0x1c + offset, (uint)(MemoryAddress + memoryOffset));
            DMA.WriteDoubleWord(0x14 + offset, count);
            DMA.WriteDoubleWord(0x10 + offset, 1u | (memoryIncrement ? 1u << 10 : 0) | (1u << 11) | (1u << 13)
                | (interrupt ? 1u << 4 : 0) | (circular ? 1u << 8 : 0));
        }

        public void StartADC(bool dds, bool eachConversion)
        {
            Control = 1u | (1u << 1) | (1u << 8) | (dds ? 1u << 9 : 0) | (eachConversion ? 1u << 10 : 0);
            ADC.WriteDoubleWord(8, Control | (1u << 30));
        }

        public void RearmADC()
        {
            ADC.WriteDoubleWord(8, Control & ~(1u << 8));
            ADC.WriteDoubleWord(8, Control);
        }

        public void Step(int count = 1)
        {
            for(var i = 0; i < count; i++)
                ((BaseClockSource)Machine.ClockSource).Advance(TimeInterval.FromNanoseconds(100000), true);
        }

        public ushort ReadSample(int index, ulong offset = 0)
        {
            return Machine.SystemBus.ReadWord(MemoryAddress + offset + (ulong)(index * 2));
        }

        public void Dispose()
        {
            EmulationManager.Instance.CurrentEmulation.RemoveMachine(Machine);
            Machine.Dispose();
        }

        public Machine Machine { get; }
        public STM32_ADC ADC { get; }
        public STM32DMA DMA { get; }
        public int Requests { get; private set; }
        public uint Control { get; private set; }
        public const ulong ADCAddress = 0x40012000;
        public const ulong MemoryAddress = 0x20000000;
    }
}
