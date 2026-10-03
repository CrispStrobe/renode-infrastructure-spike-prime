//
// Copyright (c) 2026 CrispStrobe
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System.Collections.Generic;

using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure;
using Antmicro.Renode.Peripherals;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.DMA;
using Antmicro.Renode.Peripherals.Memory;
using Antmicro.Renode.Peripherals.I2C;

using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    [TestFixture]
    [NonParallelizable]
    public class STM32F7I2CTests
    {
        [SetUp]
        public void SetUp()
        {
            machine = new Machine();
            EmulationManager.Instance.CurrentEmulation.AddMachine(machine);
            controller = new STM32F7_I2C(machine);
            target = new LP50XX();
            target.OnGPIO(LP50XX.EnableGPIO, true);
            txRequests = new RisingEdgeCounter();
            rxRequests = new RisingEdgeCounter();
            machine.SystemBus.Register(controller, new BusPointRegistration(ControllerAddress));
            controller.Register(target, new NumberRegistrationPoint<int>(TargetAddress));
            controller.DMATransmit.Connect(txRequests, 0);
            controller.DMAReceive.Connect(rxRequests, 0);
        }

        [TearDown]
        public void TearDown()
        {
            machine.Dispose();
        }

        [Test]
        public void ShouldGateTransmitRequestsWithDMAEnable()
        {
            BeginWrite(2);
            Assert.AreEqual(0, txRequests.Count);

            controller.WriteDoubleWord(Control1, PeripheralEnable | TransmitDMAEnable);
            Assert.AreEqual(1, txRequests.Count);
            controller.WriteDoubleWord(TransmitData, 0x00);
            Assert.AreEqual(2, txRequests.Count);
            controller.WriteDoubleWord(TransmitData, 0x40);
            Assert.IsTrue(target.ChipEnabled);
        }

        [Test]
        public void ShouldRequestEveryByteOfDMARead()
        {
            target.Write(new byte[] { 0x00, 0x40 });
            target.Write(new byte[] { 0x00 });
            controller.WriteDoubleWord(Control1, PeripheralEnable | ReceiveDMAEnable);
            BeginRead(1);

            Assert.AreEqual(1, rxRequests.Count);
            Assert.AreEqual(0x40, controller.ReadDoubleWord(ReceiveData));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void ShouldTransferToRealDmaInEitherSetupOrder(bool controllerFirst)
        {
            var dma = ConnectTransmitDma();
            foreach(var length in new[] { 2, 17, 255 })
            {
                var expected = new byte[length];
                for(var i = 0; i < length; i++) expected[i] = (byte)(length + i);
                dmaMemory.WriteBytes(1, expected);
                dmaMemory.WriteByte(0, 0xA5);
                dmaMemory.WriteByte(length + 1, 0x5A);
                dmaTarget.Writes.Clear();
                ConfigureTransmitDma(dma, length);
                if(controllerFirst)
                {
                    StartDmaWrite((uint)length);
                    Assert.True(controller.DMATransmit.IsSet);
                    Assert.AreEqual((uint)length, dma.ReadDoubleWord(0x14));
                    dma.WriteDoubleWord(0x10, 0x451);
                }
                else
                {
                    dma.WriteDoubleWord(0x10, 0x451);
                    Assert.AreEqual((uint)length, dma.ReadDoubleWord(0x14));
                    StartDmaWrite((uint)length);
                }
                Assert.AreEqual(1, dmaTarget.Writes.Count);
                CollectionAssert.AreEqual(expected, dmaTarget.Writes[0]);
                Assert.AreEqual(0u, dma.ReadDoubleWord(0x14));
                Assert.AreEqual(0u, dma.ReadDoubleWord(0x10) & 1);
                Assert.True(dma.Connections[0].IsSet);
                dma.WriteDoubleWord(0x08, 1u << 5);
                Assert.False(dma.Connections[0].IsSet);
                Assert.False(controller.DMATransmit.IsSet);
                Assert.AreEqual(0xA5, dmaMemory.ReadByte(0));
                Assert.AreEqual(0x5A, dmaMemory.ReadByte(length + 1));
            }
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void ShouldCancelReadyRequestBeforeLateDmaEnable(int disable)
        {
            var dma = ConnectTransmitDma();
            ConfigureTransmitDma(dma, 2);
            StartDmaWrite(2);
            Assert.True(controller.DMATransmit.IsSet);
            if(disable == 0) controller.WriteDoubleWord(Control1, PeripheralEnable);
            if(disable == 1) controller.WriteDoubleWord(Control1, 0);
            if(disable == 2) controller.Reset();
            Assert.False(controller.DMATransmit.IsSet);
            dma.WriteDoubleWord(0x10, 0x451);
            Assert.AreEqual(2u, dma.ReadDoubleWord(0x14));
            Assert.IsEmpty(dmaTarget.Writes);
            dmaMemory.WriteBytes(1, new byte[] { 0x12, 0x34 });
            if(disable == 0)
            {
                // DMA gating leaves the existing I2C transaction active.
                controller.WriteDoubleWord(Control1, PeripheralEnable | TransmitDMAEnable);
            }
            else
            {
                StartDmaWrite(2);
            }
            Assert.AreEqual(1, dmaTarget.Writes.Count);
            CollectionAssert.AreEqual(new byte[] { 0x12, 0x34 }, dmaTarget.Writes[0]);
            Assert.AreEqual(0u, dma.ReadDoubleWord(0x14));
        }

        private STM32DMA ConnectTransmitDma()
        {
            var dma = new STM32DMA(machine);
            dmaMemory = new MappedMemory(machine, 4096);
            dmaTarget = new RecordingTarget();
            controller.Unregister(target);
            controller.Register(dmaTarget, new NumberRegistrationPoint<int>(TargetAddress));
            machine.SystemBus.Register(dma, new BusPointRegistration(0x40026000));
            machine.SystemBus.Register(dmaMemory, new BusRangeRegistration(0x20000000, 4096));
            controller.DMATransmit.Connect(dma, 0);
            return dma;
        }

        private void ConfigureTransmitDma(STM32DMA dma, int length)
        {
            dma.WriteDoubleWord(0x10, 0x450); // M2P, MINC, TCIE, disabled
            dma.WriteDoubleWord(0x14, (uint)length);
            dma.WriteDoubleWord(0x18, ControllerAddress + (uint)TransmitData);
            dma.WriteDoubleWord(0x1C, 0x20000001);
        }

        private void StartDmaWrite(uint count)
        {
            controller.WriteDoubleWord(Control1, PeripheralEnable | TransmitDMAEnable);
            controller.WriteDoubleWord(Control2, ((uint)TargetAddress << 1) | (count << 16) | Start | AutoEnd);
        }

        private sealed class RecordingTarget : II2CPeripheral
        {
            public readonly List<byte[]> Writes = new List<byte[]>();
            public void Write(byte[] data) { Writes.Add((byte[])data.Clone()); }
            public byte[] Read(int count = 1) { return new byte[count]; }
            public void FinishTransmission() { }
            public void Reset() { Writes.Clear(); }
        }

        private MappedMemory dmaMemory;
        private RecordingTarget dmaTarget;

        private void BeginWrite(uint count)
        {
            controller.WriteDoubleWord(Control1, PeripheralEnable);
            controller.WriteDoubleWord(Control2, ((uint)TargetAddress << 1) | (count << 16) | Start | AutoEnd);
        }

        private void BeginRead(uint count)
        {
            controller.WriteDoubleWord(Control2, ((uint)TargetAddress << 1) | Read | (count << 16) | Start | AutoEnd);
        }

        private Machine machine;
        private STM32F7_I2C controller;
        private LP50XX target;
        private RisingEdgeCounter txRequests;
        private RisingEdgeCounter rxRequests;

        private const uint ControllerAddress = 0x40006000;
        private const int TargetAddress = 0x28;
        private const long Control1 = 0x00;
        private const long Control2 = 0x04;
        private const long ReceiveData = 0x24;
        private const long TransmitData = 0x28;
        private const uint PeripheralEnable = 1 << 0;
        private const uint TransmitDMAEnable = 1 << 14;
        private const uint ReceiveDMAEnable = 1 << 15;
        private const uint Read = 1 << 10;
        private const uint Start = 1 << 13;
        private const uint AutoEnd = 1 << 25;

        private sealed class RisingEdgeCounter : IGPIOReceiver
        {
            public void Reset()
            {
                Count = 0;
            }

            public void OnGPIO(int number, bool value)
            {
                if(value)
                {
                    Count++;
                }
            }

            public int Count { get; private set; }
        }
    }
}
