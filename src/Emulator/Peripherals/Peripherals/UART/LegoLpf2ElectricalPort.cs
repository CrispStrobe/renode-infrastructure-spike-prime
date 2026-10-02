// SPDX-License-Identifier: BSD-3-Clause
// Copyright (c) 2026 Brickwright contributors
using System;
using System.Collections.Generic;
using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.GPIOPort;
using Antmicro.Renode.Peripherals.Timers;

namespace Antmicro.Renode.Peripherals.UART
{
    // Synthetic electrical attachment for our NuttX board pin contract. CPU
    // code still performs DCM, UART discovery, mode selection and PWM writes.
    public class LegoLpf2ElectricalPort : LegoLpf2Port
    {
        public LegoLpf2ElectricalPort(IMachine machine, STM32_GPIOPort id1Gpio, int id1Pin,
            STM32_GPIOPort id2Gpio, int id2Pin, STM32_GPIOPort txGpio, int txPin,
            STM32_GPIOPort rxGpio, int rxPin, string device = "none",
            STM32_GPIOPort bridgeGpio = null, int bridgePin1 = 0, int bridgePin2 = 0,
            STM32_Timer bridgeTimer = null, int channel1 = 1, int channel2 = 2,
            int bridgeAlternateFunction = 1, bool advancedTimer = true, STM32_GPIOPort bridgeGpio2 = null) : base(device)
        {
            if(machine == null || id1Gpio == null || id2Gpio == null || txGpio == null || rxGpio == null)
                throw new ArgumentNullException("Electrical attachment requires a machine and all ID/UART GPIO ports");
            ValidatePin(id1Pin); ValidatePin(id2Pin); ValidatePin(txPin); ValidatePin(rxPin);
            if(bridgeGpio != null)
            {
                ValidatePin(bridgePin1); ValidatePin(bridgePin2);
                if(bridgeTimer == null || channel1 < 1 || channel1 > 4 || channel2 < 1 || channel2 > 4)
                    throw new ArgumentException("Motor bridge needs a timer and channels 1..4");
                if(channel1 == channel2 || bridgeAlternateFunction < 0 || bridgeAlternateFunction > 15)
                    throw new ArgumentException("Motor bridge needs distinct channels and an alternate function 0..15");
            }
            this.id1Gpio=id1Gpio; this.id1Pin=id1Pin; this.id2Gpio=id2Gpio; this.id2Pin=id2Pin;
            this.txGpio=txGpio; this.txPin=txPin; this.rxGpio=rxGpio; this.rxPin=rxPin;
            this.bridgeGpio=bridgeGpio; this.bridgePin1=bridgePin1; this.bridgePin2=bridgePin2;
            this.bridgeGpio2=bridgeGpio2 ?? bridgeGpio;
            this.bridgeAlternateFunction=bridgeAlternateFunction; this.advancedTimer=advancedTimer;
            this.bridgeTimer=bridgeTimer; this.channel1=channel1; this.channel2=channel2;
            if(device == "motor" || device == "medium-motor") AttachDevice(new Lpf2ElectricalMotor());
            feeder=machine.ObtainManagedThread(Tick,1000,name:"LPF2 electrical attachment",owner:this);
            feeder.Start();
        }
        public void Tick()
        {
            if(Device == null) return;
            // UART devices present an open ID1 signature and grounded ID2.
            // The host TX probe controls ID1 only while TX is GPIO output.
            if(Mode(id1Gpio,id1Pin)==0)
                id1Gpio.OnGPIO(id1Pin,Mode(txGpio,txPin)==1 ? Level(txGpio,txPin) : true);
            if(Mode(id2Gpio,id2Pin)==0) id2Gpio.OnGPIO(id2Pin,false);
            if(Mode(rxGpio,rxPin)==0) rxGpio.OnGPIO(rxPin,false);
            var motor=Device as Lpf2ElectricalMotor;
            if(motor != null && bridgeGpio != null)
            {
                var mode1=Mode(bridgeGpio,bridgePin1); var mode2=Mode(bridgeGpio2,bridgePin2);
                var high1=Level(bridgeGpio,bridgePin1); var high2=Level(bridgeGpio2,bridgePin2);
                var running=(bridgeTimer.ReadDoubleWord(0)&1)!=0 && bridgeTimer.Enabled
                    && (!advancedTimer || (bridgeTimer.ReadDoubleWord(0x44)&0x8000)!=0);
                var period=(ulong)bridgeTimer.ReadDoubleWord(0x2c)+1;
                var drive=0;
                if(running && mode1==2 && mode2==1 && high2 && IsDriveChannel(bridgeGpio,bridgePin1,channel1))
                    drive=(int)Math.Min(10000UL,(ulong)bridgeTimer.ReadDoubleWord(0x34+4*(channel1-1))*10000/period);
                else if(running && mode2==2 && mode1==1 && high1 && IsDriveChannel(bridgeGpio2,bridgePin2,channel2))
                    drive=-(int)Math.Min(10000UL,(ulong)bridgeTimer.ReadDoubleWord(0x34+4*(channel2-1))*10000/period);
                motor.SetDrive(drive,mode1==1 && mode2==1 && high1 && high2);
            }
            // Discovery starts after the unchanged driver switches TX to UART.
            // Mechanics keep advancing while UART is unavailable; discovery does not.
            if(Mode(txGpio,txPin)==2) AdvanceEmulatedTime(1000);
            else motor?.Advance(1);
        }
        public new void Attach(string device)
        {
            if(device == "motor" || device == "medium-motor") AttachDevice(new Lpf2ElectricalMotor());
            else base.Attach(device);
        }
        // Contract taken from the local NuttX board stm32_legoport_pwm.c:
        // PWM mode 1 with inverted polarity, AF1/TIM1 or AF2/TIM3/TIM4.
        private bool IsDriveChannel(STM32_GPIOPort gpio, int pin, int channel)
        {
            var af=(gpio.ReadDoubleWord(pin < 8 ? 0x20 : 0x24) >> ((pin % 8)*4))&15;
            var ccer=(bridgeTimer.ReadDoubleWord(0x20) >> ((channel-1)*4))&15;
            var ccmr=(bridgeTimer.ReadDoubleWord(channel <= 2 ? 0x18 : 0x1c) >> (((channel-1)%2)*8))&255;
            return af==bridgeAlternateFunction && (ccer&3)==3 && (ccmr&3)==0 && ((ccmr>>4)&7)==6;
        }
        private static void ValidatePin(int pin) { if(pin<0 || pin>15)throw new ArgumentOutOfRangeException(nameof(pin)); }
        private static uint Mode(STM32_GPIOPort gpio,int pin) { return (gpio.ReadDoubleWord(0)>>(pin*2))&3; }
        private static bool Level(STM32_GPIOPort gpio,int pin) { return (gpio.ReadDoubleWord(0x10)&(1u<<pin))!=0; }
        private readonly STM32_GPIOPort id1Gpio,id2Gpio,txGpio,rxGpio,bridgeGpio,bridgeGpio2;
        private readonly STM32_Timer bridgeTimer;
        private readonly int id1Pin,id2Pin,txPin,rxPin,bridgePin1,bridgePin2,channel1,channel2,bridgeAlternateFunction;
        private readonly bool advancedTimer;
        private readonly IManagedThread feeder;
    }
    // Deterministic mechanical policy, not a calibrated model of a real motor.
    public sealed class Lpf2ElectricalMotor : ILpf2Device
    {
        public byte TypeId => 48;
        public IReadOnlyList<Lpf2Mode> Modes { get; } = new[] {
            new Lpf2Mode(0,"POWER",1,Lpf2DataType.Int8),
            new Lpf2Mode(1,"SPEED",1,Lpf2DataType.Int8),
            new Lpf2Mode(2,"POS",1,Lpf2DataType.Int32),
        };
        public string Name => "Brickwright synthetic electrical motor";
        public uint ReportIntervalMicroseconds => 10000;
        public int Power => duty/100;
        public int SpeedPercent => (int)(speed/11.1);
        public double PositionDegrees => position;
        public int EncoderDegrees => (int)Math.Round(position);
        public double AngularVelocityDegreesPerSecond => speed;
        public int LoadPercent { get; private set; }
        public bool Stalled => duty!=0 && LoadPercent==100;
        public void SetLoad(byte value) { if(value>100)throw new ArgumentOutOfRangeException(nameof(value));LoadPercent=value; }
        public void SetDrive(int value,bool braking) { if(value < -10000 || value>10000)throw new ArgumentOutOfRangeException(nameof(value));duty=value;brake=braking; }
        public void Advance(uint milliseconds)
        {
            var seconds=milliseconds/1000.0;
            var target=Stalled ? 0 : duty*1110.0/10000*(100-LoadPercent)/100;
            var old=speed;
            var acceleration=duty!=0 ? 2000.0 : brake ? 4000.0 : 200.0;
            speed=Stalled ? 0 : Math.Max(old-acceleration*seconds,Math.Min(old+acceleration*seconds,target));
            if(!Stalled) position+=(old+speed)*0.5*seconds;
        }
        public byte[] ReadMode(byte mode)
        {
            if(mode==0)return new[] {unchecked((byte)(sbyte)Power)};
            if(mode==1)return new[] {unchecked((byte)(sbyte)SpeedPercent)};
            if(mode==2) {
                var value=unchecked((uint)(int)Math.Round(position));
                return new[] {(byte)value,(byte)(value>>8),(byte)(value>>16),(byte)(value>>24)};
            }
            throw new ArgumentOutOfRangeException(nameof(mode));
        }
        public void AcceptOutput(byte mode,byte[] payload) { throw new NotSupportedException("Motor drive comes from the physical H-bridge pins"); }
        public void Reset() { duty=0;speed=position=0;LoadPercent=0;brake=false; }
        private int duty;
        private bool brake;
        private double speed,position;
    }
}
