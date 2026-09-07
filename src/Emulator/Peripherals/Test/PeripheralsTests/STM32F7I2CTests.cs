//
// Copyright (c) 2026 CrispStrobe
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure;
using Antmicro.Renode.Peripherals;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.I2C;

using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    [TestFixture]
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
