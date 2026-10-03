// SPDX-License-Identifier: BSD-3-Clause
// Copyright (c) 2026 Brickwright contributors
using System;
using System.Collections.Generic;
using NUnit.Framework;
using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.GPIOPort;
using Antmicro.Renode.Peripherals.Timers;
using Antmicro.Renode.Peripherals.UART;
public static class ElectricalTests
{
    private static int count;
    private static void Check(bool ok, string message) { if(!ok) throw new Exception(message); count++; }
    private static void Mode(STM32_GPIOPort gpio, int pin, uint mode) { gpio.WriteDoubleWord(0, (gpio.ReadDoubleWord(0)&~(3u<<(pin*2)))|(mode<<(pin*2))); }
    public static string RunElectricalTests(this Emulation emulation)
    {
        count=0;
        using(var m = new Machine())
        {
            var gpio=new STM32_GPIOPort(m);
            var timer=new STM32_Timer(m, 96000000, 1000);
            var p=new LegoLpf2ElectricalPort(m,gpio,0,gpio,1,gpio,2,gpio,3,"motor",gpio,9,11,timer);
            var motor=(Lpf2ElectricalMotor)p.Device;
            var discovery=new List<byte>();p.CharReceived+=discovery.Add;p.StartNegotiation();
            var cursor=1;var formats=0;
            if(discovery.Count==0 || discovery[0]!=0)throw new Exception("discovery SYNC missing");
            while(cursor<discovery.Count-1)
            {
                var header=discovery[cursor];
                var length=(1<<((header>>3)&7))+2+((header&0xc0)==0x80 ? 1 : 0);
                if(cursor+length>discovery.Count-1)throw new Exception("discovery frame declared length exceeds payload");
                byte checksum=0;for(var j=0;j<length;j++)checksum^=discovery[cursor+j];
                if(checksum!=0xff)throw new Exception("discovery frame checksum/length mismatch");
                if((header&0xc0)==0x80 && discovery[cursor+1]==0x80)
                { if(length!=7)throw new Exception("FORMAT must declare four data bytes");formats++; }
                cursor+=length;
            }
            Check(cursor==discovery.Count-1 && discovery[cursor]==4 && formats==3,"complete framed mode discovery");
            p.Tick(); Check((gpio.ReadDoubleWord(0x10)&3)==1,"ID signature");
            Mode(gpio,2,1); gpio.WriteDoubleWord(0x14,0); p.Tick(); Check((gpio.ReadDoubleWord(0x10)&1)==0,"TX low signature");
            gpio.WriteDoubleWord(0x14,1u<<2); p.Tick(); Check((gpio.ReadDoubleWord(0x10)&1)!=0,"TX high signature");
            Mode(gpio,9,2); Mode(gpio,11,1); gpio.WriteDoubleWord(0x14,1u<<11);
            gpio.WriteDoubleWord(0x24,1u<<4); timer.WriteDoubleWord(0x2c,999); timer.WriteDoubleWord(0x34,500);
            timer.WriteDoubleWord(0x18,0x6060); timer.WriteDoubleWord(0x20,0x33); timer.WriteDoubleWord(0x44,0x8000); timer.WriteDoubleWord(0,1);
            p.Tick(); Check(motor.Power==50,"forward electrical duty"); Check(motor.SpeedPercent==0 && motor.PositionDegrees>0,"mechanics advance without UART");
            timer.WriteDoubleWord(0x20,0x32);p.Tick();Check(motor.Power==0,"channel enable enforced");
            timer.WriteDoubleWord(0x20,0x31);p.Tick();Check(motor.Power==0,"inverted polarity enforced");
            timer.WriteDoubleWord(0x18,0x6070);p.Tick();Check(motor.Power==50,"PWM2 normal polarity is equivalent active-low drive");
            timer.WriteDoubleWord(0x34,250);p.Tick();Check(motor.Power==25,"PWM2 driven fraction follows CCR");
            timer.WriteDoubleWord(0x20,0x33);p.Tick();Check(motor.Power==0,"PWM2 inverted polarity rejected");
            timer.WriteDoubleWord(0x34,500);
            timer.WriteDoubleWord(0x20,0x33);timer.WriteDoubleWord(0x18,0x6050);p.Tick();Check(motor.Power==0,"PWM mode enforced");
            timer.WriteDoubleWord(0x18,0x6060);gpio.WriteDoubleWord(0x24,2u<<4);p.Tick();Check(motor.Power==0,"AF enforced");
            gpio.WriteDoubleWord(0x24,1u<<4);timer.WriteDoubleWord(0,0);p.Tick();Check(motor.Power==0,"CEN enforced");
            timer.WriteDoubleWord(0,1);timer.WriteDoubleWord(0x44,0);p.Tick();Check(motor.Power==0,"MOE enforced on advanced timer");
            timer.WriteDoubleWord(0x44,0x8000);Mode(gpio,9,1);Mode(gpio,11,2);gpio.WriteDoubleWord(0x14,1u<<9);gpio.WriteDoubleWord(0x24,1u<<12);timer.WriteDoubleWord(0x38,250);p.Tick();Check(motor.Power==-25,"reverse electrical duty");
            timer.WriteDoubleWord(0x18,0x7060);timer.WriteDoubleWord(0x20,0x13);p.Tick();Check(motor.Power==-25,"reverse PWM2 active-low drive");
            timer.WriteDoubleWord(0x38,1000);p.Tick();Check(motor.Power==-100,"PWM2 full duty boundary");
            timer.WriteDoubleWord(0x20,0x03);p.Tick();Check(motor.Power==0,"PWM2 channel enable enforced");
            Mode(gpio,11,1); gpio.WriteDoubleWord(0x14,(1u<<9)|(1u<<11));p.Tick();Check(motor.Power==0,"GPIO brake");
            var gpio2=new STM32_GPIOPort(m);
            var general=new LegoLpf2ElectricalPort(m,gpio,0,gpio,1,gpio,2,gpio,3,"motor",gpio,6,1,timer,3,4,2,false,gpio2);
            Mode(gpio,6,2);Mode(gpio2,1,1);gpio2.WriteDoubleWord(0x14,2);gpio.WriteDoubleWord(0x20,2u<<24);timer.WriteDoubleWord(0x1c,0x6060);timer.WriteDoubleWord(0x20,0x3300);timer.WriteDoubleWord(0x3c,750);timer.WriteDoubleWord(0x44,0);general.Tick();
            Check(((Lpf2ElectricalMotor)general.Device).Power==75,"general timer and cross-bank bridge");
        }
        var probe=new LegoLpf2Port("force");var responses=new List<byte>();probe.CharReceived+=responses.Add;
        foreach(var value in new byte[]{0x52,0x00,0xc2,0x01,0x00,0x6e})probe.WriteChar(value);
        Check(responses.Count>2 && responses[0]==4 && responses[1]==0 && probe.State==Lpf2PortState.WaitingForAck,"speed ACK precedes discovery");
        probe.WriteChar(4);Check(probe.State==Lpf2PortState.Streaming,"host ACK starts sensor reporting");
        var a=new Lpf2ElectricalMotor();a.SetDrive(10000,false);a.Advance(100);Check(a.SpeedPercent==18,"bounded acceleration");Check(Math.Abs(a.PositionDegrees-10)<0.001,"accelerated position");
        a.Advance(1000);Check(a.SpeedPercent==100,"maximum speed");
        a.SetDrive(0,false);a.Advance(100);var coast=a.SpeedPercent;Check(coast>90,"coast inertia");
        var b=new Lpf2ElectricalMotor();b.SetDrive(10000,false);b.Advance(1000);b.SetDrive(0,true);b.Advance(100);Check(b.SpeedPercent<coast,"brake stronger than coast");b.Advance(1000);Check(b.SpeedPercent==0,"brake stops");
        b.Reset();b.SetDrive(-10000,false);b.Advance(1000);Check(b.SpeedPercent==-100,"signed speed");Check(BitConverter.ToInt32(b.ReadMode(2),0)<0,"signed encoder little-endian");Check((sbyte)b.ReadMode(1)[0]==-100 && (sbyte)b.ReadMode(0)[0]==-100,"signed protocol values");
        b.SetLoad(100);var before=b.PositionDegrees;b.Advance(100);Check(b.Stalled && b.SpeedPercent==0 && b.PositionDegrees==before,"stalled encoder remains fixed");
        b.SetLoad(50);b.Advance(1000);Check(!b.Stalled && b.SpeedPercent==-50,"load reduces speed");
        b.Reset();Check(b.Power==0 && b.SpeedPercent==0 && b.PositionDegrees==0 && b.LoadPercent==0,"reset");
        return "PASS "+count+" electrical and mechanical checks";
    }
}

namespace Antmicro.Renode.PeripheralsTests
{
    [TestFixture]
    [NonParallelizable]
    public class LegoLpf2ElectricalPortTests
    {
        [Test]
        public void ShouldApplyElectricalGatesAndMechanicalPolicy()
        {
            Assert.AreEqual("PASS 35 electrical and mechanical checks",
                ElectricalTests.RunElectricalTests(EmulationManager.Instance.CurrentEmulation));
        }
    }
}
