// SPDX-License-Identifier: BSD-3-Clause
// Copyright (c) 2026 Brickwright contributors
using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure;
using Antmicro.Renode.Peripherals;
using Antmicro.Renode.Peripherals.I2C;
using Antmicro.Renode.Peripherals.Bus;
using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    [TestFixture]
    [NonParallelizable]
    public class STM32F4I2CStreamTests
    {
        [Test]
        public void ShouldStreamUntilFinalNack([Values(1, 2, 3, 4, 12, 32)] int count,
                                               [Values(false, true)] bool halfword,
                                               [Values(false, true)] bool restart)
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
                var edges=new RisingEdgeCounter();
                controller.EventInterrupt.Connect(edges, 0);
                System.Action<long, uint> write = (offset, value) =>
                {
                    if(halfword) machine.SystemBus.WriteWord(0x40005800 + (ulong)offset, (ushort)value);
                    else controller.WriteDoubleWord(offset, value);
                };
                System.Func<long, uint> read = offset => halfword
                    ? machine.SystemBus.ReadWord(0x40005800 + (ulong)offset)
                    : controller.ReadDoubleWord(offset);
                var device=new RegisterDevice();
                controller.Register(device,new NumberRegistrationPoint<int>(0x6a));
                write(4,0x600); // Event and buffer IRQs, as the guest uses.
                write(0,0x101);
                write(0x10,0xd4);
                read(0x18);
                write(0x10,0x20);
                write(0,0x101);
                write(0x10,0xd5);
                // Match stm32f40xxx_i2c.c: single-byte STOP, two-byte POS,
                // or the BTF-only three-byte tail with ACK cleared early.
                write(0,count==1 ? 1u : count==2 ? 0x801u : 0x401u);
                read(0x18); // Clear ADDR and start receive shift register.
                if(count==1) write(0,restart ? 0x101u : 0x201u);
                for(var i=0;i<count;i++)
                {
                    var remaining=count-i;
                    if(count>2 && remaining<5) write(4,0x200); // Disable RXNE IRQ.
                    var status=read(0x14);
                    Assert.AreNotEqual(0u,status&0x40u);
                    if(remaining>=2) Assert.AreNotEqual(0u,status&4u, "Two buffered bytes must assert BTF");
                    if(count>2 && remaining==3) write(0,1);
                    if(remaining==2) write(0,(restart ? 0x101u : 0x201u) | (count==2 ? 0x800u : 0u));
                    int before=edges.Count;
                    Assert.AreEqual(0x20+i, halfword ? read(0x10) : controller.ReadByte(0x10));
                    if(remaining>2 && remaining<5)
                        Assert.Greater(edges.Count, before, "Refill did not reassert the receive IRQ");
                }
                Assert.AreEqual(count,device.BytesRead);
                Assert.AreEqual(0u,read(0x14)&0x40u);
                write(0,0x201);
                controller.Reset();
                Assert.AreEqual(0u,read(0x14)&0x40u);
                }
                finally { emulation.RemoveMachine(machine); }
            }
        }
        private sealed class RisingEdgeCounter : IGPIOReceiver
        {
            public void OnGPIO(int number, bool value) { if(value) Count++; }
            public void Reset() { Count=0; }
            public int Count { get; private set; }
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
