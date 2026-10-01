// SPDX-License-Identifier: BSD-3-Clause
// Copyright (c) 2026 Brickwright contributors
using System;
using Antmicro.Renode.Peripherals.UART;
using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    [TestFixture]
    public class Lpf2ArenaSensorsTests
    {
        [Test]
        public void ColorModesReadInjectedValuesAndReset()
        {
            var sensor = new Lpf2ArenaColorSensor();
            sensor.SetReading(3, 80, 10);
            CollectionAssert.AreEqual(new byte[] {3}, sensor.ReadMode(0));
            CollectionAssert.AreEqual(new byte[] {80}, sensor.ReadMode(1));
            CollectionAssert.AreEqual(new byte[] {10}, sensor.ReadMode(2));
            Assert.Throws<ArgumentOutOfRangeException>(() => sensor.SetReading(9, 101, 0));
            Assert.AreEqual(3, sensor.ColorId, "invalid input must preserve the complete reading");
            sensor.Reset();
            Assert.AreEqual(255, sensor.ColorId);
        }
        [Test]
        public void ForceModesReadInjectedValuesAndReset()
        {
            var sensor = new Lpf2ArenaForceSensor();
            sensor.SetReading(60, true);
            CollectionAssert.AreEqual(new byte[] {60}, sensor.ReadMode(0));
            CollectionAssert.AreEqual(new byte[] {1}, sensor.ReadMode(1));
            Assert.Throws<ArgumentOutOfRangeException>(() => sensor.SetReading(101, false));
            Assert.AreEqual(60, sensor.ForcePercent);
            Assert.IsTrue(sensor.Pressed);
            sensor.Reset();
            Assert.AreEqual(0, sensor.ForcePercent);
            Assert.IsFalse(sensor.Pressed);
        }
        [TestCase("color", typeof(Lpf2ArenaColorSensor))]
        [TestCase("force", typeof(Lpf2ArenaForceSensor))]
        public void PortAttachesArenaSensor(string kind, Type expected)
        {
            var port = new LegoLpf2Port();
            port.Attach(kind);
            Assert.AreEqual(expected, port.Device.GetType());
            port.StartNegotiation();
            Assert.AreEqual(Lpf2PortState.WaitingForAck, port.State);
            port.WriteChar(0x04);
            Assert.AreEqual(Lpf2PortState.Streaming, port.State);
        }
    }
}
