// SPDX-License-Identifier: BSD-3-Clause
// Copyright (c) 2026 Brickwright contributors
using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure;
using Antmicro.Renode.Peripherals.I2C;
using Antmicro.Renode.Peripherals.Bus;
using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    [TestFixture]
    [NonParallelizable]
    public class STM32F4I2CStreamTests
    {
        [TestCase(1)] [TestCase(2)] [TestCase(6)] [TestCase(32)]
        public void ShouldStreamUntilFinalNack(int count)
        {
            // Other peripheral fixtures may leave disposed machines in the global emulation.
            EmulationManager.Instance.Clear();
            using(var machine=new Machine())
            {
                var emulation=EmulationManager.Instance.CurrentEmulation;
                emulation.AddMachine(machine);
                try
                {
                var controller=new STM32F4_I2C(machine);
                machine.SystemBus.Register(controller,new BusRangeRegistration(0x40005800,0x400));
                var device=new RegisterDevice();
                controller.Register(device,new NumberRegistrationPoint<int>(0x6a));
                controller.WriteDoubleWord(0,0x101);
                controller.WriteDoubleWord(0x10,0xd4);
                controller.ReadDoubleWord(0x18);
                controller.WriteDoubleWord(0x10,0x20);
                controller.WriteDoubleWord(0,0x101);
                controller.WriteDoubleWord(0x10,0xd5);
                controller.WriteDoubleWord(0,count>1 ? 0x401u : 1u);
                controller.ReadDoubleWord(0x18);
                for(var i=0;i<count;i++)
                {
                    Assert.AreNotEqual(0u,controller.ReadDoubleWord(0x14)&0x40u);
                    Assert.AreEqual(0x20+i,controller.ReadByte(0x10));
                    if(i==count-2)controller.WriteDoubleWord(0,1);
                }
                Assert.AreEqual(count,device.BytesRead);
                Assert.AreEqual(0u,controller.ReadDoubleWord(0x14)&0x40u);
                controller.WriteDoubleWord(0,0x201);
                controller.Reset();
                Assert.AreEqual(0u,controller.ReadDoubleWord(0x14)&0x40u);
                }
                finally { emulation.RemoveMachine(machine); }
            }
        }
        private class RegisterDevice : II2CPeripheral
        {
            public void Write(byte[] bytes) { if(bytes.Length>0)next=bytes[0]; }
            public byte[] Read(int count=1)
            { var result=new byte[count];for(var i=0;i<count;i++){ result[i]=next++;BytesRead++; }return result; }
            public void FinishTransmission() {}
            public void Reset() { next=0;BytesRead=0; }
            public int BytesRead {get;private set;}
            private byte next;
        }
    }
}
