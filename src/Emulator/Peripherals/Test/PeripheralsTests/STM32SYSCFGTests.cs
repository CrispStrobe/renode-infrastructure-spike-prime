// SPDX-License-Identifier: BSD-3-Clause
// Copyright (c) 2026 Brickwright contributors

using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.IRQControllers;
using Antmicro.Renode.Peripherals.Miscellaneous;
using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    [TestFixture]
    public class STM32SYSCFGTests
    {
        [Test]
        public void ShouldRestorePortASelectionAndHeldInputOnReset(
            [Range(0, 15)] int pin, [Values(false, true)] bool portAHigh,
            [Values(false, true)] bool portBHigh)
        {
            var mux = new STM32_SYSCFG();
            var portA = mux.GetLocalReceiver(0);
            var portB = mux.GetLocalReceiver(1);
            portA.OnGPIO(pin, portAHigh);
            portB.OnGPIO(pin, portBHigh);
            Assert.AreEqual(portAHigh, mux.Connections[pin].IsSet);
            mux.WriteDoubleWord(8 + 4 * (pin / 4), 1u << (4 * (pin % 4)));
            Assert.AreEqual(portBHigh, mux.Connections[pin].IsSet);

            mux.Reset();

            for(var offset = 8; offset <= 20; offset += 4)
            {
                Assert.AreEqual(0u, mux.ReadDoubleWord(offset));
            }
            Assert.AreEqual(portAHigh, mux.Connections[pin].IsSet,
                "Reset must publish the held level of the reset-selected port A");
            portB.OnGPIO(pin, !portBHigh);
            Assert.AreEqual(portAHigh, mux.Connections[pin].IsSet,
                "The previously selected bank must be isolated after reset");
            portA.OnGPIO(pin, !portAHigh);
            Assert.AreEqual(!portAHigh, mux.Connections[pin].IsSet);
        }

        [Test]
        public void ShouldResetToLowWhenPortAHasNeverBeenConnected()
        {
            var mux = new STM32_SYSCFG();
            mux.GetLocalReceiver(1).OnGPIO(9, true);
            mux.WriteDoubleWord(0x10, 1u << 4);
            Assert.IsTrue(mux.Connections[9].IsSet);
            mux.Reset();
            Assert.IsFalse(mux.Connections[9].IsSet);
        }

        [Test]
        public void ShouldRouteOnlySelectedEdgesToExtiAndClearPendingWithW1C()
        {
            using(var machine = new Machine())
            {
                var mux = new STM32_SYSCFG();
                var exti = new STM32F4_EXTI(machine, numberOfOutputLines: 16);
                var portA = mux.GetLocalReceiver(0);
                var portB = mux.GetLocalReceiver(1);
                const uint bit = 1u << 9;
                mux.Connections[9].Connect(exti, 9);
                exti.WriteDoubleWord(0, bit);
                exti.WriteDoubleWord(8, bit);
                exti.WriteDoubleWord(12, bit);
                portB.OnGPIO(9, true);
                Assert.AreEqual(0u, exti.ReadDoubleWord(20));
                portA.OnGPIO(9, true);
                Assert.AreEqual(bit, exti.ReadDoubleWord(20));
                Assert.IsTrue(exti.Connections[9].IsSet);
                exti.WriteDoubleWord(20, bit);
                Assert.AreEqual(0u, exti.ReadDoubleWord(20));
                Assert.IsFalse(exti.Connections[9].IsSet);
                mux.WriteDoubleWord(0x10, 1u << 4);
                exti.WriteDoubleWord(20, bit);
                portA.OnGPIO(9, false);
                Assert.AreEqual(0u, exti.ReadDoubleWord(20));
                portB.OnGPIO(9, false);
                Assert.AreEqual(bit, exti.ReadDoubleWord(20));
                exti.WriteDoubleWord(20, bit);
                Assert.IsFalse(exti.Connections[9].IsSet);
            }
        }
    }
}
