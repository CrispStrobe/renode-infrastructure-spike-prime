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
using Antmicro.Renode.Peripherals.SPI;

using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    [TestFixture]
    public class STM32SPITests
    {
        [SetUp]
        public void SetUp()
        {
            machine = new Machine();
            EmulationManager.Instance.CurrentEmulation.AddMachine(machine);
            spi = new STM32SPI(machine);
            target = new RecordingSPIPeripheral();
            requests = new RisingEdgeCounter();
            spi.Register(target, NullRegistrationPoint.Instance);
            spi.DMATransmit.Connect(requests, 0);
        }

        [TearDown]
        public void TearDown()
        {
            machine.Dispose();
        }

        [Test]
        public void ShouldExposeBackwardCompatibleReceiveDMAName()
        {
            Assert.AreSame(spi.DMAReceive, spi.DMARecieve);
        }

        [Test]
        public void ShouldRequestTransmitDMAOnlyWhenEnabled()
        {
            spi.WriteDoubleWord(Control2, TransmitDMAEnable);
            Assert.AreEqual(0, requests.Count);

            spi.WriteDoubleWord(Control1, SPIEnable);
            Assert.AreEqual(1, requests.Count);

            spi.WriteByte(Data, 0x5A);
            Assert.AreEqual(2, requests.Count);
            CollectionAssert.AreEqual(new byte[] { 0x5A }, target.TransmittedBytes);

            spi.WriteDoubleWord(Control2, 0);
            spi.WriteByte(Data, 0xA5);
            Assert.AreEqual(2, requests.Count);
            CollectionAssert.AreEqual(new byte[] { 0x5A, 0xA5 }, target.TransmittedBytes);
        }

        [Test]
        public void ShouldRequestTransmitDMAWhenEnabledOnRunningSPI()
        {
            spi.WriteDoubleWord(Control1, SPIEnable);
            Assert.AreEqual(0, requests.Count);

            spi.WriteDoubleWord(Control2, TransmitDMAEnable);
            Assert.AreEqual(1, requests.Count);
        }

        private Machine machine;
        private STM32SPI spi;
        private RecordingSPIPeripheral target;
        private RisingEdgeCounter requests;

        private const long Control1 = 0x0;
        private const long Control2 = 0x4;
        private const long Data = 0xC;
        private const uint SPIEnable = 1 << 6;
        private const uint TransmitDMAEnable = 1 << 1;

        private sealed class RecordingSPIPeripheral : ISPIPeripheral
        {
            public byte Transmit(byte data)
            {
                TransmittedBytes.Add(data);
                return 0;
            }

            public void FinishTransmission()
            {
            }

            public void Reset()
            {
                TransmittedBytes.Clear();
            }

            public List<byte> TransmittedBytes { get; } = new List<byte>();
        }

        private sealed class RisingEdgeCounter : IGPIOReceiver
        {
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
