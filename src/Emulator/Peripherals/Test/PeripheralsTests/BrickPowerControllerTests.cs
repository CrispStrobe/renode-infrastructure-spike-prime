// Copyright (c) 2026 CrispStrobe
// SPDX-License-Identifier: MIT
using Antmicro.Renode.Peripherals.Miscellaneous;
using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    [TestFixture]
    public class BrickPowerControllerTests
    {
        [Test]
        public void ShouldExposeBatteryAndChargerState()
        {
            var power = new BrickPowerController();
            Assert.IsTrue(power.Connections[0].IsSet);
            power.SetBatteryMillivolts(5900);
            Assert.IsTrue(power.BatteryLow);
            Assert.IsFalse(power.Connections[0].IsSet);
            power.SetChargerConnected(true);
            Assert.IsTrue(power.Connections[0].IsSet);
            Assert.AreEqual(BrickPowerController.ChargeStates.Suspended, power.ChargeState);
            power.OnGPIO(BrickPowerController.ChargerModeInput, true);
            Assert.IsTrue(power.Connections[1].IsSet);
            Assert.AreEqual(BrickPowerController.ChargeStates.Charging, power.ChargeState);
            power.SetChargeComplete(true);
            Assert.IsFalse(power.Connections[1].IsSet);
            Assert.AreEqual(BrickPowerController.ChargeStates.Complete, power.ChargeState);
            Assert.Throws<System.ArgumentOutOfRangeException>(() => power.SetBatteryMillivolts(20001));
        }

        [Test]
        public void ShouldLatchShutdownOnPowerHoldFallingEdge()
        {
            var power = new BrickPowerController();
            power.OnGPIO(BrickPowerController.PowerHoldInput, true);
            power.OnGPIO(BrickPowerController.PowerHoldInput, false);
            Assert.IsTrue(power.ShutdownRequested);
            power.ClearShutdownRequest();
            Assert.IsFalse(power.ShutdownRequested);
        }
    }
}
