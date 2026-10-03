//
// Copyright (c) 2010-2024 Antmicro
// Copyright (c) 2011-2015 Realtime Embedded
// Copyright (c) 2026 Brickwright contributors
//
// SPDX-License-Identifier: MIT AND BSD-3-Clause
// Retained Renode code is licensed under MIT ('licenses/MIT.txt').
// Brickwright receive-readiness and optional timing additions are licensed
// under BSD-3-Clause ('licenses/BSD-3-Clause.txt').
//
using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Time;
using Antmicro.Renode.Utilities.Collections;

namespace Antmicro.Renode.Peripherals.SPI
{
    public sealed class STM32SPI : NullRegistrationPointPeripheralContainer<ISPIPeripheral>, IWordPeripheral, IDoubleWordPeripheral, IBytePeripheral, IKnownSize
    {
        // A zero input clock retains instantaneous byte transfers. A positive
        // clock opts into simulated wire pacing, rounded up to nanoseconds.
        // This remains a byte-only model with a receive queue defaulting to
        // four bytes; it does not emulate all silicon timing or overflow flags.
        public STM32SPI(IMachine machine, int bufferCapacity = DefaultBufferCapacity, ulong frequency = 0) : base(machine)
        {
            this.machine = machine;
            this.frequency = frequency;
            receiveBuffer = new CircularBuffer<byte>(bufferCapacity);
            IRQ = new GPIO();
            DMAReceive = new GPIO();
            DMATransmit = new GPIO();
            registers = new DoubleWordRegisterCollection(this);
            SetupRegisters();
            Reset();
        }

        // We can't use AllowedTranslations because then WriteByte/WriteWord will trigger
        // an additional read (see ReadWriteExtensions:WriteByteUsingDoubleWord).
        // We can't have this happen for the data register.
        public byte ReadByte(long offset)
        {
            // byte interface is there for DMA
            if(offset % 4 == 0)
            {
                return (byte)ReadDoubleWord(offset);
            }
            this.LogUnhandledRead(offset);
            return 0;
        }

        public void WriteByte(long offset, byte value)
        {
            if(offset % 4 == 0)
            {
                WriteDoubleWord(offset, (uint)value);
            }
            else
            {
                this.LogUnhandledWrite(offset, value);
            }
        }

        public ushort ReadWord(long offset)
        {
            return (ushort)ReadDoubleWord(offset);
        }

        public void WriteWord(long offset, ushort value)
        {
            WriteDoubleWord(offset, (uint)value);
        }

        public uint ReadDoubleWord(long offset)
        {
            return registers.Read(offset);
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            registers.Write(offset, value);
        }

        public override void Reset()
        {
            CancelTransmission();
            IRQ.Unset();
            DMAReceive.Unset();
            DMATransmit.Unset();
            lock(receiveBuffer)
            {
                receiveBuffer.Clear();
            }
            registers.Reset();
        }

        public long Size
        {
            get
            {
                return 0x400;
            }
        }

        public GPIO IRQ { get; }

        public GPIO DMAReceive { get; }

        // Keep the original misspelled name for compatibility with existing
        // platform descriptions.
        public GPIO DMARecieve => DMAReceive;

        public GPIO DMATransmit { get; }

        private uint HandleDataRead()
        {
            IRQ.Unset();
            lock(receiveBuffer)
            {
                if(receiveBuffer.TryDequeue(out var value))
                {
                    // Consuming a data unit acknowledges its DMA request. If
                    // more bytes are buffered, Update publishes readiness for
                    // the next unit, including during a synchronous DMA read.
                    DMAReceive.Unset();
                    Update();
                    return value;
                }
                // We don't warn when the data register is read while it's empty because the HAL
                // (for example L0, F4) does this intentionally.
                // See https://github.com/STMicroelectronics/STM32CubeL0/blob/bec4e499a74de98ab60784bf2ef1912bee9c1a22/Drivers/STM32L0xx_HAL_Driver/Src/stm32l0xx_hal_spi.c#L1368-L1372
                return 0;
            }
        }

        private void HandleDataWrite(uint value)
        {
            IRQ.Unset();
            if(frequency != 0)
            {
                if(!spiEnable.Value || holdingByte.HasValue)
                {
                    this.Log(LogLevel.Warning, "SPI data write while disabled or transmit holding register occupied.");
                    return;
                }
                if(isShifting)
                {
                    holdingByte = (byte)value;
                }
                else
                {
                    StartTransmission((byte)value);
                }
                Update();
                RequestTransmitDMA();
                return;
            }
            lock(receiveBuffer)
            {
                var peripheral = RegisteredPeripheral;
                if(peripheral == null)
                {
                    this.Log(LogLevel.Warning, "SPI transmission while no SPI peripheral is connected.");
                    receiveBuffer.Enqueue(0x0);
                    return;
                }
                var response = peripheral.Transmit((byte)value); // currently byte mode is the only one we support
                receiveBuffer.Enqueue(response);
                this.NoisyLog("Transmitted 0x{0:X}, received 0x{1:X}.", value, response);
            }
            Update();
            RequestTransmitDMA();
        }

        private void RequestTransmitDMA()
        {
            if(spiEnable.Value && txDmaEnable.Value && !holdingByte.HasValue)
            {
                // Signal that the holding register can accept a DMA data unit.
                // In instantaneous mode it is always available. A
                // DMA write can synchronously reach this method while the prior
                // pulse is still asserted, so release it before retriggering.
                DMATransmit.Unset();
                DMATransmit.Set();
            }
            else
            {
                DMATransmit.Unset();
            }
        }

        private void StartTransmission(byte value)
        {
            isShifting = true;
            var generation = transmissionGeneration;
            // Eight bits at frequency / (2 << BR). The largest numerator
            // fits in ulong; quotient/remainder rounding avoids overflow for
            // input clocks near ulong.MaxValue and never schedules zero delay.
            var numerator = 8UL * (2UL << (int)baudRate.Value) * 1000000000UL;
            var delay = numerator / frequency + (numerator % frequency != 0 ? 1UL : 0UL);
            machine.ScheduleAction(TimeInterval.FromNanoseconds(delay), _ =>
            {
                lock(receiveBuffer)
                {
                    if(generation != transmissionGeneration || !spiEnable.Value)
                    {
                        return;
                    }
                    var peripheral = RegisteredPeripheral;
                    var response = peripheral == null ? (byte)0 : peripheral.Transmit(value);
                    if(generation != transmissionGeneration || !spiEnable.Value)
                    {
                        return;
                    }
                    receiveBuffer.Enqueue(response);
                    isShifting = false;
                    Update();
                    if(generation != transmissionGeneration || !spiEnable.Value)
                    {
                        return;
                    }
                    if(holdingByte.HasValue)
                    {
                        var next = holdingByte.Value;
                        holdingByte = null;
                        StartTransmission(next);
                    }
                    Update();
                    RequestTransmitDMA();
                }
            });
        }

        private void CancelTransmission()
        {
            transmissionGeneration++;
            isShifting = false;
            holdingByte = null;
        }

        private void Update()
        {
            var rxBufferNotEmpty = receiveBuffer.Count != 0;
            var rxBufferNotEmptyInterruptFlag = rxBufferNotEmpty && rxBufferNotEmptyInterruptEnable.Value;

            IRQ.Set((txBufferEmptyInterruptEnable.Value && !holdingByte.HasValue) || rxBufferNotEmptyInterruptFlag);
            // A CPU read can consume RXNE while DMA is disabled. Withdraw the
            // request when the buffer drains, rather than remembering a pulse
            // for a byte that no longer exists.
            RequestReceiveDMA();
        }

        private void RequestReceiveDMA()
        {
            DMAReceive.Set(spiEnable.Value && rxDmaEnable.Value && receiveBuffer.Count != 0);
        }

        private void SetupRegisters()
        {
            // Some fields relate to the physical layer of the SPI protocol, these are not
            // taken into account in Renode and are defined as flags or value fields without
            // a corresponding RegisterField or read/write callbacks. They are marked with
            // comments.
            Registers.Control1.Define(registers)
                .WithFlag(0, name: "CPHA") // Physical
                .WithFlag(1, name: "CPOL") // Physical
                .WithFlag(2, writeCallback: (_, value) =>
                {
                    if(!value)
                    {
                        this.Log(LogLevel.Warning, "Slave mode is not supported");
                    }
                }, name: "MSTR")
                .WithValueField(3, 3, out baudRate, name: "Baud")
                .WithFlag(6, out spiEnable, changeCallback: (oldValue, newValue) =>
                {
                    if(!newValue)
                    {
                        CancelTransmission();
                        IRQ.Unset();
                    }
                    RequestTransmitDMA();
                    RequestReceiveDMA();
                }, name: "SpiEnable")
                .WithFlag(7, name: "LSBFIRST") // Physical
                                               // We keep these as flags to preserve written values. SSI flag is used by drivers to select/detect operation mode (Master or Slave)
                .WithFlag(8, name: "SSI") // Internal slave select
                .WithFlag(9, name: "SSM") // Software slave management
                .WithTaggedFlag("RXONLY", 10)
                .WithTaggedFlag("DFF", 11)
                .WithTaggedFlag("CRCNEXT", 12)
                .WithTaggedFlag("CRCEN", 13)
                .WithTaggedFlag("BIDIOE", 14)
                .WithTaggedFlag("BIDIMODE", 15);

            Registers.Control2.Define(registers)
                .WithFlag(0, out rxDmaEnable, name: "RXDMAEN")
                .WithFlag(1, out txDmaEnable, changeCallback: (_, value) =>
                {
                    RequestTransmitDMA();
                }, name: "TXDMAEN")
                .WithTaggedFlag("SSOE", 2)
                .WithReservedBits(3, 1)
                .WithTaggedFlag("FRF", 4)
                .WithTaggedFlag("ERRIE", 5)
                .WithFlag(6, out rxBufferNotEmptyInterruptEnable, name: "RXNEIE")
                .WithFlag(7, out txBufferEmptyInterruptEnable, name: "TXEIE")
                .WithReservedBits(8, 24)
                .WithWriteCallback((_, __) => Update());

            Registers.Status.Define(registers, 2)
                .WithFlag(0, FieldMode.Read, valueProviderCallback: _ => receiveBuffer.Count != 0, name: "RXNE")
                .WithFlag(1, FieldMode.Read, valueProviderCallback: _ => !holdingByte.HasValue, name: "TXE")
                .WithTaggedFlag("CHSIDE", 2) // r/o
                .WithTaggedFlag("UDR", 3) // r/o
                .WithTaggedFlag("CRCERR", 4) // rc_w0
                .WithTaggedFlag("MODF", 5) // r/o
                .WithTaggedFlag("OVR", 6) // r/o
                .WithFlag(7, FieldMode.Read, valueProviderCallback: _ => isShifting, name: "BSY")
                .WithTaggedFlag("FRE", 8) // r/o
                .WithReservedBits(9, 23);

            Registers.Data.Define(registers)
                .WithValueField(0, 16, valueProviderCallback: _ => HandleDataRead(),
                    writeCallback: (_, value) => HandleDataWrite((uint)value), name: "DR")
                .WithReservedBits(16, 16);

            Registers.CRCPolynomial.Define(registers, 7)
                .WithTag("CRCPOLY", 0, 16)
                .WithReservedBits(16, 16);

            Registers.ReceivedCRC.Define(registers)
                .WithTag("RxCRC", 0, 16) // r/o
                .WithReservedBits(16, 16);

            Registers.TransmittedCRC.Define(registers)
                .WithTag("TxCRC", 0, 16) // r/o
                .WithReservedBits(16, 16);

            Registers.I2SConfiguration.Define(registers)
                .WithTaggedFlag("CHLEN", 0)
                .WithTag("DATLEN", 1, 2)
                .WithTaggedFlag("CKPOL", 3)
                .WithTag("I2SSTD", 4, 2)
                .WithReservedBits(6, 1)
                .WithTaggedFlag("PCMSYNC", 7)
                .WithTag("I2SCFG", 8, 2)
                .WithFlag(10, FieldMode.Read | FieldMode.WriteOneToClear, writeCallback: (oldValue, newValue) =>
                {
                    // write one to clear to keep this bit 0
                    if(newValue)
                    {
                        this.Log(LogLevel.Warning, "Trying to enable not supported I2S mode.");
                    }
                }, name: "I2SE")
                .WithTaggedFlag("I2SMOD", 11)
                .WithReservedBits(12, 20);

            Registers.I2SPrescaler.Define(registers, 2)
                .WithTag("I2SDIV", 0, 8)
                .WithTaggedFlag("ODD", 8)
                .WithTaggedFlag("MCKOE", 9)
                .WithReservedBits(10, 22);
        }

        private IFlagRegisterField txBufferEmptyInterruptEnable, rxBufferNotEmptyInterruptEnable, rxDmaEnable;
        private IFlagRegisterField spiEnable, txDmaEnable;
        private IValueRegisterField baudRate;

        private readonly IMachine machine;
        private readonly ulong frequency;
        private ulong transmissionGeneration;
        private bool isShifting;
        private byte? holdingByte;

        private readonly DoubleWordRegisterCollection registers;

        private readonly CircularBuffer<byte> receiveBuffer;

        private const int DefaultBufferCapacity = 4;

        private enum Registers
        {
            Control1 = 0x0, // SPI_CR1,
            Control2 = 0x4, // SPI_CR2
            Status = 0x8, // SPI_SR
            Data = 0xC, // SPI_DR
            CRCPolynomial = 0x10, // SPI_CRCPR
            ReceivedCRC = 0x14, // SPI_RXCRCR
            TransmittedCRC = 0x18, // SPI_TXCRCR
            I2SConfiguration = 0x1C, // SPI_I2SCFGR
            I2SPrescaler = 0x20, // SPI_I2SPR
        }
    }
}
