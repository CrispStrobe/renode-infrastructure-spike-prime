// SPDX-License-Identifier: BSD-3-Clause
// Copyright (c) 2026 Brickwright contributors
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
        public void ShouldSelectEdgesAndScanIdleButtonThroughHalfwordDataReads()
        {
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
        private static void Advance(Machine machine,ulong ns)
        { ((BaseClockSource)machine.ClockSource).Advance(TimeInterval.FromNanoseconds(ns),true); }
    }
}
