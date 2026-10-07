// SPDX-License-Identifier: BSD-3-Clause
// Copyright (c) 2026 Brickwright contributors
using System;
using System.Collections.Generic;
using NUnit.Framework;
using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.GPIOPort;
using Antmicro.Renode.Peripherals.Timers;
using Antmicro.Renode.Peripherals.UART;

public static class ElectricalAttachmentChecks
{
    private static void Require(bool condition, string message)
    {
        if(!condition) throw new Exception(message);
    }
    private static void Mode(STM32_GPIOPort gpio, int pin, uint mode)
    {
        gpio.WriteDoubleWord(0,(gpio.ReadDoubleWord(0)&~(3u<<(pin*2)))|(mode<<(pin*2)));
    }
    private static bool Level(STM32_GPIOPort gpio, int pin)
    {
        return (gpio.ReadDoubleWord(0x10)&(1u<<pin))!=0;
    }
    public static string RunElectricalAttachmentChecks(this Emulation emulation)
    {
        using(var machine=new Machine())
        {
            var gpio=new STM32_GPIOPort(machine);
            var timer=new STM32_Timer(machine,96000000,1000);
            var port=new LegoLpf2ElectricalPort(machine,gpio,0,gpio,1,gpio,2,gpio,3,
                "motor",gpio,9,11,timer);
            var original=(Lpf2ElectricalMotor)port.Device;
            port.Tick();
            Require(Level(gpio,0) && !Level(gpio,1) && !Level(gpio,3),"attached input signature");
            var generation=port.TopologyGeneration;
            port.Detach();port.Tick();
            Require(port.Device==null && !port.Gpio1Attached && port.TopologyGeneration==generation+1,"logical detach");
            Require(Level(gpio,0) && Level(gpio,1) && Level(gpio,3),"detached virtual idle levels");
            Mode(gpio,2,1);gpio.WriteDoubleWord(0x14,0);port.Tick();
            Require(!Level(gpio,0) && Level(gpio,1) && Level(gpio,3),"TX low without cross-coupling");
            gpio.WriteDoubleWord(0x14,4);port.Tick();Require(Level(gpio,0),"TX high while detached");
            Mode(gpio,1,1);gpio.WriteDoubleWord(0x14,4);port.Tick();
            Require(!Level(gpio,3) && !Level(gpio,1),"ID2 output low reaches RX without overriding output");
            gpio.WriteDoubleWord(0x14,6);port.Tick();Require(Level(gpio,3),"ID2 output high reaches RX");
            Mode(gpio,1,0);Mode(gpio,3,1);gpio.WriteDoubleWord(0x14,0);port.Tick();
            Require(!Level(gpio,1) && !Level(gpio,3),"RX output low reaches ID2");
            gpio.WriteDoubleWord(0x14,8);port.Tick();Require(Level(gpio,1),"RX output high reaches ID2");
            Mode(gpio,3,0);Mode(gpio,2,2);
            var bytes=new List<byte>();port.CharReceived+=bytes.Add;
            var before=port.EmulatedTimeMicroseconds;port.Tick();
            Require(port.EmulatedTimeMicroseconds==before+1000 && bytes.Count==0,"detached UART time advances silently");

            Mode(gpio,9,2);Mode(gpio,11,1);gpio.WriteDoubleWord(0x14,1u<<11);
            gpio.WriteDoubleWord(0x24,1u<<4);timer.WriteDoubleWord(0x2c,999);
            timer.WriteDoubleWord(0x34,500);timer.WriteDoubleWord(0x18,0x6060);
            timer.WriteDoubleWord(0x20,0x33);timer.WriteDoubleWord(0x44,0x8000);timer.WriteDoubleWord(0,1);
            Require(port.Device==null && port.BridgeDrive==5000 && !port.BridgeBraking,"detached bridge demand remains observable");
            var modeBefore=gpio.ReadDoubleWord(0);var demandBefore=timer.ReadDoubleWord(0x34);
            port.Tick();Require(port.BridgeDrive==5000 && gpio.ReadDoubleWord(0)==modeBefore
                && timer.ReadDoubleWord(0x34)==demandBefore,"tick does not silently release guest PWM");
            port.Attach("motor");var replacement=(Lpf2ElectricalMotor)port.Device;
            Require(!ReferenceEquals(original,replacement) && port.TopologyGeneration==generation+2,"same-type fresh attachment");
            Require(replacement.PositionDegrees==0 && replacement.Power==0 && replacement.LoadPercent==0,"replacement has fresh mechanics");
            port.Tick();Require(replacement.Power==50,"replacement sees actual unreleased bridge demand");
            Require(!Level(gpio,1) && !Level(gpio,3),"reattached input signature");
            Mode(gpio,9,1);gpio.WriteDoubleWord(0x14,0);port.Tick();
            Require(port.BridgeDrive==0 && !port.BridgeBraking && replacement.Power==0,"guest coast releases bridge");
            gpio.WriteDoubleWord(0x14,(1u<<9)|(1u<<11));port.Tick();
            Require(port.BridgeDrive==0 && port.BridgeBraking,"brake observation");
            port.Detach();Require(port.BridgeBraking,"detached brake remains observable");
        }
        return "PASS electrical attachment contract";
    }
}

namespace Antmicro.Renode.PeripheralsTests
{
    [TestFixture]
    [NonParallelizable]
    public class LegoLpf2ElectricalAttachmentTests
    {
        [Test]
        public void ShouldResolveDetachedInputsAndExposeUnmaskedBridgeDemand()
        {
            Assert.AreEqual("PASS electrical attachment contract",
                ElectricalAttachmentChecks.RunElectricalAttachmentChecks(EmulationManager.Instance.CurrentEmulation));
        }
    }
}
