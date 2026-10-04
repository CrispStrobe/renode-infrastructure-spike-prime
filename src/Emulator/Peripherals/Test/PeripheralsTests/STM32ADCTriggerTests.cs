// SPDX-License-Identifier: BSD-3-Clause
// Copyright (c) 2026 Brickwright contributors
using System.Collections.Generic;
using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure;
using Antmicro.Renode.Peripherals.Analog;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Time;
using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    [TestFixture]
    [NonParallelizable]
    public class STM32ADCTriggerTests
    {
        [Test]
        public void ShouldCancelPendingConversionAndRestoreSingleRankOnReset()
        {
            EmulationManager.Instance.Clear();
            using(var machine=new Machine())
            {
                var emulation=EmulationManager.Instance.CurrentEmulation;
                emulation.AddMachine(machine);
                try
                {
                    var adc=new STM32_ADC(machine);
                    adc.SetChannelValue(0,17);adc.SetChannelValue(10,1234);
                    adc.WriteDoubleWord(0x2c,1u<<20);
                    adc.WriteDoubleWord(0x34,10u|(10u<<5));
                    adc.WriteDoubleWord(4,1u<<8);
                    adc.WriteDoubleWord(8,1u|(6u<<24)|(1u<<28));
                    adc.OnGPIO(6,true);
                    adc.Reset();
                    Advance(machine,1000000);
                    Assert.AreEqual(0u,adc.ReadDoubleWord(0x4c));
                    Assert.IsFalse(adc.IRQ.IsSet);
                    Assert.IsFalse(adc.DMARequest.IsSet);
                    // The reset SQR1 L=0 means one conversion, without writes
                    // to sequence registers. Persistent analog inputs remain.
                    adc.WriteDoubleWord(8,1u|(6u<<24)|(1u<<28));
                    adc.OnGPIO(6,true);
                    Advance(machine,100000);
                    Assert.AreEqual(17u,adc.ReadDoubleWord(0x4c));
                }
                finally {emulation.RemoveMachine(machine);}
            }
        }

        [Test]
        public void ShouldUseSequenceWrittenAfterEnablingADC()
        {
            EmulationManager.Instance.Clear();
            using(var machine=new Machine())
            {
                var emulation=EmulationManager.Instance.CurrentEmulation;
                emulation.AddMachine(machine);
                try
                {
                    var adc=new STM32_ADC(machine);
                    adc.SetChannelValue(0,17);adc.SetChannelValue(10,1234);
                    // Match the board driver: ADON precedes SQR programming.
                    adc.WriteDoubleWord(8,1u|(6u<<24)|(1u<<28));
                    adc.WriteDoubleWord(0x34,10u);
                    adc.OnGPIO(6,true);
                    Advance(machine,100000);
                    Assert.AreEqual(1234u,adc.ReadDoubleWord(0x4c));
                    adc.OnGPIO(6,false);
                    adc.WriteDoubleWord(0x34,0u);
                    adc.OnGPIO(6,true);
                    Advance(machine,100000);
                    Assert.AreEqual(17u,adc.ReadDoubleWord(0x4c));
                }
                finally {emulation.RemoveMachine(machine);}
            }
        }

        [Test]
        public void ShouldSelectEdgesAndScanIdleButtonThroughHalfwordDataReads()
        {
            // Other peripheral fixtures may leave disposed machines in the global emulation.
            EmulationManager.Instance.Clear();
            using(var machine=new Machine())
            {
                var emulation=EmulationManager.Instance.CurrentEmulation;
                emulation.AddMachine(machine);
                try
                {
                    var adc=new STM32_ADC(machine);
                    machine.SystemBus.Register(adc,new BusRangeRegistration(0x40012000,0x400));
                    adc.SetChannelValue(10,1234);adc.SetChannelValue(14,4095);
                    adc.WriteDoubleWord(0x2c,1u<<20);
                    adc.WriteDoubleWord(0x34,10u|(14u<<5));
                    adc.WriteDoubleWord(4,1u<<8);
                    adc.WriteDoubleWord(8,1u|(6u<<24)|(1u<<28));
                    adc.OnGPIO(5,true);
                    Advance(machine,100000);
                    Assert.AreEqual(0u,adc.ReadDoubleWord(0x4c));
                    adc.OnGPIO(6,true);
                    Advance(machine,100000);
                    Assert.AreEqual(1234u,machine.SystemBus.ReadWord(0x4001204c));
                    Advance(machine,100000);
                    Assert.AreEqual(4095u,machine.SystemBus.ReadWord(0x4001204c));
                    // Falling edges are ignored in rising-only mode.
                    adc.OnGPIO(6,false);Advance(machine,100000);
                    Assert.AreEqual(4095u,adc.ReadDoubleWord(0x4c));
                    adc.WriteDoubleWord(8,1u|(6u<<24)|(2u<<28));
                    adc.OnGPIO(6,true);Advance(machine,100000);
                    Assert.AreEqual(4095u,adc.ReadDoubleWord(0x4c));
                    adc.OnGPIO(6,false);Advance(machine,100000);
                    Assert.AreEqual(1234u,adc.ReadDoubleWord(0x4c));
                    Advance(machine,100000);
                    adc.WriteDoubleWord(8,0);
                    adc.OnGPIO(6,true);adc.OnGPIO(6,false);Advance(machine,100000);
                    Assert.AreEqual(4095u,adc.ReadDoubleWord(0x4c));
                }
                finally {emulation.RemoveMachine(machine);}
            }
        }
        // RM0430 sections 13.3.8 and 13.12.1: EOC is available with
        // the conversion result, and reading ADC_DR clears it. A synchronous
        // DMA consumer must not have that cleared flag restored on return.
        [TestCase(false, false, false)]
        [TestCase(false, true, false)]
        [TestCase(true, false, false)]
        [TestCase(true, true, false)]
        [TestCase(false, false, true)]
        [TestCase(false, true, true)]
        [TestCase(true, false, true)]
        [TestCase(true, true, true)]
        public void ShouldPublishEOCBeforeDMAAndPreserveDataReadAcknowledgement(bool eachConversion, bool consumeData, bool enableInterrupt)
        {
            WithMachine(machine =>
            {
                var adc = new STM32_ADC(machine);
                machine.SystemBus.Register(adc, new BusRangeRegistration(0x40012000, 0x400));
                adc.SetChannelValue(10, 1234);
                adc.SetChannelValue(14, 4095);
                adc.WriteDoubleWord(0x2c, 1u << 20);
                adc.WriteDoubleWord(0x34, 10u | (14u << 5));
                adc.WriteDoubleWord(4, (1u << 8) | (enableInterrupt ? 1u << 5 : 0));
                var observedEOC = new List<bool>();
                var samples = new List<uint>();
                adc.DMARequest.AddStateChangedHook(value =>
                {
                    if(!value) return;
                    observedEOC.Add((adc.ReadDoubleWord(0) & 2u) != 0);
                    if(consumeData) samples.Add(machine.SystemBus.ReadWord(0x4001204c));
                });
                var control = 1u | (1u << 8) | (1u << 9) | (eachConversion ? 1u << 10 : 0);
                adc.WriteDoubleWord(8, control | (1u << 30));
                Advance(machine, 100000);
                Assert.AreEqual(consumeData ? false : eachConversion, (adc.ReadDoubleWord(0) & 2u) != 0, "First-rank EOC");
                Assert.AreEqual(!consumeData && eachConversion && enableInterrupt, adc.IRQ.IsSet, "First-rank IRQ");
                Advance(machine, 100000);
                CollectionAssert.AreEqual(new[] { eachConversion, true }, observedEOC, "EOC visible at each DMA request");
                Assert.AreEqual(!consumeData, (adc.ReadDoubleWord(0) & 2u) != 0, "Last-rank EOC after DMA");
                Assert.AreEqual(!consumeData && enableInterrupt, adc.IRQ.IsSet, "Last-rank IRQ after DMA");
                if(consumeData) CollectionAssert.AreEqual(new uint[] { 1234, 4095 }, samples);
                else Assert.AreEqual(4095u, adc.ReadDoubleWord(0x4c));
                Assert.IsFalse(adc.IRQ.IsSet);
                Assert.AreEqual(0u, adc.ReadDoubleWord(0) & 2u);
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ShouldKeepUnreadEOCForSoftwarePolling(bool enableInterrupt)
        {
            WithMachine(machine =>
            {
                var adc = new STM32_ADC(machine);
                adc.SetChannelValue(0, 731);
                adc.WriteDoubleWord(4, enableInterrupt ? 1u << 5 : 0);
                adc.WriteDoubleWord(8, 1u | (1u << 30));
                Advance(machine, 100000);
                Assert.AreEqual(2u, adc.ReadDoubleWord(0) & 2u);
                Assert.AreEqual(enableInterrupt, adc.IRQ.IsSet);
                Assert.AreEqual(731u, adc.ReadDoubleWord(0x4c));
                Assert.AreEqual(0u, adc.ReadDoubleWord(0) & 2u);
                Assert.IsFalse(adc.IRQ.IsSet);
            });
        }

        private static void WithMachine(System.Action<Machine> test)
        {
            EmulationManager.Instance.Clear();
            using(var machine = new Machine())
            {
                var emulation = EmulationManager.Instance.CurrentEmulation;
                emulation.AddMachine(machine);
                try { test(machine); }
                finally { emulation.RemoveMachine(machine); }
            }
        }
        private static void Advance(Machine machine,ulong ns)
        { ((BaseClockSource)machine.ClockSource).Advance(TimeInterval.FromNanoseconds(ns),true); }
    }
}
