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
            // UART devices present an open ID1 signature and grounded ID2.
            // The host TX probe controls ID1 only while TX is GPIO output.
            if(Mode(id1Gpio,id1Pin)==0)
                id1Gpio.OnGPIO(id1Pin,Mode(txGpio,txPin)==1 ? Level(txGpio,txPin) : true);
            // ID2 and RX share connector pin 6 in our board contract. Without
            // a device, a host GPIO drive is visible at the other input. The
            // undriven virtual line resolves to UART idle high; this explicit
            // synthetic policy does not model a physical pull resistor.
            if(Mode(id2Gpio,id2Pin)==0)
                id2Gpio.OnGPIO(id2Pin,Device == null && (Mode(rxGpio,rxPin)!=1 || Level(rxGpio,rxPin)));
            if(Mode(rxGpio,rxPin)==0)
                rxGpio.OnGPIO(rxPin,Device == null && (Mode(id2Gpio,id2Pin)!=1 || Level(id2Gpio,id2Pin)));
            var motor=Device as Lpf2ElectricalMotor;
            if(motor != null && bridgeGpio != null)
            {
                // One coherent sample per tick feeds both the motor and the
                // duty integrator; the integrator never changes the motor.
                var drive=BridgeDrive;
                var braking=BridgeBraking;
                motor.SetDrive(drive,braking);
                lock(driveLock)
                {
                    driveSum+=drive;
                    driveTicks++;
                    if(braking) brakingTicks++;
                    lastDrive=drive;
                }
            }
            // Discovery starts after the unchanged driver switches TX to UART.
            // Mechanics keep advancing while UART is unavailable; discovery does not.
            // A detached UART port still advances time but emits no device data.
            if(Mode(txGpio,txPin)==2) AdvanceEmulatedTime(1000);
            else motor?.Advance(1);
        }
        // Read the actual bridge registers even when Device is null. These
        // observers neither cache demand nor clear a guest's leftover PWM.
        // Sample on a paused machine for a coherent multi-register observation.
        public int BridgeDrive
        {
            get
            {
                if(bridgeGpio == null) return 0;
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
                return drive;
            }
        }
        public bool BridgeBraking => bridgeGpio != null && Mode(bridgeGpio,bridgePin1)==1
            && Mode(bridgeGpio2,bridgePin2)==1 && Level(bridgeGpio,bridgePin1) && Level(bridgeGpio2,bridgePin2);
        public new void Attach(string device)
        {
            if(device == "motor" || device == "medium-motor") AttachDevice(NewMotor());
            else base.Attach(device);
        }
        // Robot Lab lockstep (external shaft). Once enabled, the shaft of the
        // attached motor, and of any motor attached later on this port, comes
        // from SetShaft instead of the synthetic mechanics. Never switched back.
        // Call with the machine paused, like the other fixture controls.
        public void EnableExternalShaft()
        {
            externalShaft=true;
            (Device as Lpf2ElectricalMotor)?.EnableExternalShaft();
        }
        public bool ExternalShaft => externalShaft;
        public void SetShaft(double positionDegrees, double speedDegreesPerSecond)
        {
            var motor=Device as Lpf2ElectricalMotor;
            if(motor == null) throw new InvalidOperationException("No electrical motor is attached to this port");
            motor.SetShaft(positionDegrees,speedDegreesPerSecond);
        }
        // Mean, last and braking count of the H-bridge samples taken by Tick
        // (one per millisecond of virtual time) since the previous call; resets
        // the accumulator. Ticks counts only samples fed to an attached motor.
        public Lpf2DriveAverage TakeDriveAverage()
        {
            lock(driveLock)
            {
                var result=new Lpf2DriveAverage(driveTicks==0 ? 0.0 : (double)driveSum/driveTicks,lastDrive,brakingTicks,driveTicks);
                driveSum=0;driveTicks=0;brakingTicks=0;
                return result;
            }
        }
        private Lpf2ElectricalMotor NewMotor()
        {
            var motor=new Lpf2ElectricalMotor();
            if(externalShaft) motor.EnableExternalShaft();
            return motor;
        }
        // Contract taken from the local NuttX board stm32_legoport_pwm.c:
        // PWM mode 1 with inverted polarity, AF1/TIM1 or AF2/TIM3/TIM4.
        private bool IsDriveChannel(STM32_GPIOPort gpio, int pin, int channel)
        {
            var af=(gpio.ReadDoubleWord(pin < 8 ? 0x20 : 0x24) >> ((pin % 8)*4))&15;
            var ccer=(bridgeTimer.ReadDoubleWord(0x20) >> ((channel-1)*4))&15;
            var ccmr=(bridgeTimer.ReadDoubleWord(channel <= 2 ? 0x18 : 0x1c) >> (((channel-1)%2)*8))&255;
            // PWM1 with inverted polarity and PWM2 with normal polarity
            // produce the same active-low bridge waveform. Both use CCR/ARR
            // as the driven fraction; keep rejecting other mode/polarity pairs.
            var mode=(ccmr>>4)&7;
            var activeLow=((ccer&3)==3 && mode==6) || ((ccer&3)==1 && mode==7);
            return af==bridgeAlternateFunction && activeLow && (ccmr&3)==0;
        }
        private static void ValidatePin(int pin) { if(pin<0 || pin>15)throw new ArgumentOutOfRangeException(nameof(pin)); }
        private static uint Mode(STM32_GPIOPort gpio,int pin) { return (gpio.ReadDoubleWord(0)>>(pin*2))&3; }
        private static bool Level(STM32_GPIOPort gpio,int pin) { return (gpio.ReadDoubleWord(0x10)&(1u<<pin))!=0; }
        private readonly STM32_GPIOPort id1Gpio,id2Gpio,txGpio,rxGpio,bridgeGpio,bridgeGpio2;
        private readonly STM32_Timer bridgeTimer;
        private readonly int id1Pin,id2Pin,txPin,rxPin,bridgePin1,bridgePin2,channel1,channel2,bridgeAlternateFunction;
        private readonly bool advancedTimer;
        private readonly IManagedThread feeder;
        private readonly object driveLock=new object();
        private long driveSum;
        private int driveTicks,brakingTicks,lastDrive;
        private bool externalShaft;
    }
    public sealed class Lpf2DriveAverage
    {
        public Lpf2DriveAverage(double meanDrive, int lastDrive, int brakingTicks, int ticks)
        {
            MeanDrive=meanDrive;LastDrive=lastDrive;BrakingTicks=brakingTicks;Ticks=ticks;
        }
        // Signed H-bridge drive in 1/10000 of full duty, as BridgeDrive.
        public double MeanDrive { get; }
        public int LastDrive { get; }
        public int BrakingTicks { get; }
        public int Ticks { get; }
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
        public bool Stalled => !external && duty!=0 && LoadPercent==100;
        public bool ExternalShaft => external;
        public void SetLoad(byte value)
        {
            if(external)throw new InvalidOperationException("Load is a property of the external shaft's physics, not of this motor");
            if(value>100)throw new ArgumentOutOfRangeException(nameof(value));LoadPercent=value;
        }
        public void SetDrive(int value,bool braking) { if(value < -10000 || value>10000)throw new ArgumentOutOfRangeException(nameof(value));duty=value;brake=braking; }
        // External shaft: the position and speed come from an outside physics
        // engine. Between anchors the shaft extrapolates linearly, so encoder
        // reports inside one lockstep quantum see a continuous shaft.
        public void EnableExternalShaft()
        {
            if(external) return;
            external=true;
            LoadPercent=0;
            Anchor(position,speed);
        }
        public void SetShaft(double positionDegrees, double speedDegreesPerSecond)
        {
            if(!external)throw new InvalidOperationException("SetShaft requires EnableExternalShaft");
            if(double.IsNaN(positionDegrees) || double.IsInfinity(positionDegrees) || Math.Abs(positionDegrees)>1e12)
                throw new ArgumentOutOfRangeException(nameof(positionDegrees));
            if(double.IsNaN(speedDegreesPerSecond) || double.IsInfinity(speedDegreesPerSecond) || Math.Abs(speedDegreesPerSecond)>1e7)
                throw new ArgumentOutOfRangeException(nameof(speedDegreesPerSecond));
            Anchor(positionDegrees,speedDegreesPerSecond);
        }
        public void Advance(uint milliseconds)
        {
            if(external)
            {
                // Exact from the anchor, never accumulated: no drift between anchors.
                anchorMilliseconds+=milliseconds;
                position=anchorPosition+speed*anchorMilliseconds/1000.0;
                return;
            }
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
        public void Reset() { duty=0;speed=position=0;LoadPercent=0;brake=false;if(external)Anchor(0,0); }
        private void Anchor(double positionDegrees, double speedDegreesPerSecond)
        {
            position=anchorPosition=positionDegrees;
            speed=speedDegreesPerSecond;
            anchorMilliseconds=0;
        }
        private int duty;
        private bool brake,external;
        private double speed,position,anchorPosition;
        private ulong anchorMilliseconds;
    }
}
