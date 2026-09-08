//
// Copyright (c) 2026 CrispStrobe
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.UART;
using Antmicro.Renode.Time;

using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    [TestFixture]
    public class STM32_UARTTests
    {
        [SetUp]
        public void SetUp()
        {
            machine = new Machine();
            EmulationManager.Instance.CurrentEmulation.AddMachine(machine);
            uart = new STM32_UART(machine, Frequency);
            uart.WriteDoubleWord((long)Registers.BaudRate, BaudRateDivider);
        }

        [TearDown]
        public void TearDown()
        {
            machine.Dispose();
        }

        [Test]
        public void ShouldRequirePeripheralAndReceiverToReceive()
        {
            uart.WriteDoubleWord((long)Registers.Control1, ReceiverEnable);
            uart.WriteChar(TestByte);
            Assert.AreEqual(0u, uart.ReadDoubleWord((long)Registers.Data));

            uart.WriteDoubleWord((long)Registers.Control1, UsartEnable);
            uart.WriteChar(TestByte);
            Assert.AreEqual(0u, uart.ReadDoubleWord((long)Registers.Data));

            uart.WriteDoubleWord((long)Registers.Control1, UsartEnable | ReceiverEnable);
            uart.WriteChar(TestByte);
            AdvanceMicroseconds(10);
            Assert.AreEqual(TestByte, uart.ReadDoubleWord((long)Registers.Data));
        }

        [Test]
        public void ShouldRequirePeripheralAndTransmitterToTransmit()
        {
            var transmittedBytes = 0;
            uart.CharReceived += _ => transmittedBytes++;

            uart.WriteDoubleWord((long)Registers.Control1, TransmitterEnable);
            uart.WriteDoubleWord((long)Registers.Data, TestByte);
            uart.WriteDoubleWord((long)Registers.Control1, UsartEnable);
            uart.WriteDoubleWord((long)Registers.Data, TestByte);
            Assert.AreEqual(0, transmittedBytes);

            uart.WriteDoubleWord((long)Registers.Control1, UsartEnable | TransmitterEnable);
            uart.WriteDoubleWord((long)Registers.Data, TestByte);
            Assert.AreEqual(1, transmittedBytes);
        }

        [Test]
        public void ShouldRequestDmaForEachReceivedByte()
        {
            var receiver = new CountingGPIOReceiver();
            uart.DMARequest.Connect(receiver, 0);
            uart.WriteDoubleWord((long)Registers.Control1, UsartEnable | ReceiverEnable);
            uart.WriteDoubleWord((long)Registers.Control3, DmaReceptionEnable);

            uart.WriteChar(TestByte);
            uart.WriteChar(TestByte);
            AdvanceMicroseconds(20);

            Assert.AreEqual(2, receiver.RisingEdges);
        }

        [Test]
        public void ShouldRescheduleIdleDetectionAfterEachByte()
        {
            uart.WriteDoubleWord((long)Registers.Control1, UsartEnable | ReceiverEnable | IdleInterruptEnable);
            uart.WriteChar(TestByte);
            AdvanceMicroseconds(10);
            AdvanceMicroseconds(5);
            uart.WriteChar(TestByte);

            AdvanceMicroseconds(10);
            Assert.False(uart.IRQ.IsSet, "The canceled timeout of the first byte must not raise IDLE");

            AdvanceMicroseconds(10);
            Assert.True(uart.IRQ.IsSet, "8N1 at 1 Mbaud should become idle after 10 microseconds");
        }

        [Test]
        public void ShouldClearIdleOnlyAfterStatusThenDataRead()
        {
            uart.WriteDoubleWord((long)Registers.Control1, UsartEnable | ReceiverEnable | IdleInterruptEnable);
            uart.WriteChar(TestByte);
            AdvanceMicroseconds(20);
            Assert.True(uart.IRQ.IsSet);

            uart.ReadDoubleWord((long)Registers.Data);
            Assert.True(uart.IRQ.IsSet, "Reading DR without first reading SR must not clear IDLE");

            Assert.AreNotEqual(0u, uart.ReadDoubleWord((long)Registers.Status) & IdleStatus);
            uart.ReadDoubleWord((long)Registers.Data);
            Assert.False(uart.IRQ.IsSet);
        }

        [Test]
        public void ShouldAccountForConfiguredFrameLength()
        {
            // 9-bit word with two stop bits is 12 bit-times including the start bit.
            uart.WriteDoubleWord((long)Registers.Control2, TwoStopBits);
            uart.WriteDoubleWord((long)Registers.Control1,
                UsartEnable | ReceiverEnable | NineBitWordLength | ParityEnable);
            uart.AutoUpdateDelay = true;

            Assert.AreEqual(TimeInterval.FromMicroseconds(12), uart.CharacterTransmissionDelay);
        }

        private void AdvanceMicroseconds(ulong microseconds)
        {
            ((BaseClockSource)machine.ClockSource).Advance(TimeInterval.FromMicroseconds(microseconds), true);
        }

        private Machine machine;
        private STM32_UART uart;

        private const uint Frequency = 16000000;
        private const uint BaudRateDivider = 0x10; // 1 Mbaud with 16x oversampling
        private const byte TestByte = 0x5A;
        private const uint ReceiverEnable = 1u << 2;
        private const uint TransmitterEnable = 1u << 3;
        private const uint IdleInterruptEnable = 1u << 4;
        private const uint ParityEnable = 1u << 10;
        private const uint NineBitWordLength = 1u << 12;
        private const uint UsartEnable = 1u << 13;
        private const uint TwoStopBits = 2u << 12;
        private const uint DmaReceptionEnable = 1u << 6;
        private const uint IdleStatus = 1u << 4;

        private enum Registers
        {
            Status = 0x00,
            Data = 0x04,
            BaudRate = 0x08,
            Control1 = 0x0C,
            Control2 = 0x10,
            Control3 = 0x14,
        }

        private class CountingGPIOReceiver : IGPIOReceiver
        {
            public void Reset()
            {
                RisingEdges = 0;
            }

            public void OnGPIO(int number, bool value)
            {
                if(value)
                {
                    RisingEdges++;
                }
            }

            public int RisingEdges { get; private set; }
        }
    }
}
