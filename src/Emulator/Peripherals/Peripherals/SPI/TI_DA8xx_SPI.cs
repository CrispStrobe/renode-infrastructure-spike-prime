//
// Copyright (c) 2026 CrispStrobe
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.SPI
{
    // A functional model of the AM1808 SPI master data path used by the EV3.
    // 8/16-bit MSB-first transfers are synchronous; timing, slave mode and multi-buffer mode are
    // deliberately outside this model's scope.
    public class TI_DA8xx_SPI : SimpleContainer<ISPIPeripheral>, IBytePeripheral,
        IWordPeripheral, IDoubleWordPeripheral, IKnownSize
    {
        public TI_DA8xx_SPI(IMachine machine, bool externalChipSelect = true) : base(machine)
        {
            this.externalChipSelect = externalChipSelect;
            IRQ = new GPIO();
            Reset();
        }

        public uint ReadDoubleWord(long offset)
        {
            switch(offset)
            {
            case GlobalControl0: return globalControl0;
            case GlobalControl1: return globalControl1;
            case InterruptEnable: return interruptEnable;
            case InterruptLevel: return interruptLevel;
            case Flags: return CurrentFlags;
            case PinControl0: return pinControl0;
            case PinControl1: return pinControl1;
            case PinControl2: return pinControl2;
            case PinControl3: return pinControl3;
            case Data0: return data0;
            case Data1: return data1;
            case Buffer: return ReadBuffer(true);
            case EmulationBuffer: return ReadBuffer(false);
            case Delay: return delay;
            case DefaultChipSelect: return defaultChipSelect;
            case Format0: return formats[0];
            case Format1: return formats[1];
            case Format2: return formats[2];
            case Format3: return formats[3];
            case InterruptVector: return GetInterruptVector();
            default: return 0;
            }
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            switch(offset)
            {
            case GlobalControl0:
                globalControl0 = value & 1;
                if(globalControl0 == 0) ResetModule();
                break;
            case GlobalControl1:
                var wasEnabled = IsEnabled;
                globalControl1 = value & GlobalControl1Mask;
                if(wasEnabled && !IsEnabled) ClearTransferState();
                break;
            case InterruptEnable: interruptEnable = value & InterruptMask; break;
            case InterruptLevel: interruptLevel = value & InterruptEventMask; break;
            case Flags:
                stickyFlags &= ~(value & StickyFlagMask);
                if((value & ReceiveInterrupt) != 0) ConsumeReceiveBuffer();
                break;
            case PinControl0: pinControl0 = value; break;
            case PinControl1: pinControl1 = value; break;
            case PinControl2: pinControl2 = value; break;
            case PinControl3: pinControl3 = value; break;
            case PinControlSet: pinControl3 |= value; break;
            case PinControlClear: pinControl3 &= ~value; break;
            case Data0:
                data0 = value & 0xFFFF;
                Transfer((ushort)data0, (int)((data1 >> 24) & 0x3));
                break;
            case Data1:
                data1 = value & Data1Mask;
                Transfer((ushort)data1, (int)((data1 >> 24) & 0x3));
                break;
            case Delay: delay = value; break;
            case DefaultChipSelect: defaultChipSelect = value & 0xFF; break;
            case Format0: formats[0] = value; break;
            case Format1: formats[1] = value; break;
            case Format2: formats[2] = value; break;
            case Format3: formats[3] = value; break;
            }
            UpdateInterrupt();
        }

        public ushort ReadWord(long offset)
        {
            var aligned = offset & ~3L;
            return (ushort)(ReadDoubleWord(aligned) >> (int)((offset & 2) * 8));
        }

        public void WriteWord(long offset, ushort value)
        {
            var aligned = offset & ~3L;
            if(aligned == Data1 && (offset & 2) != 0)
            {
                data1 = ((data1 & 0xFFFF) | ((uint)value << 16)) & Data1Mask;
                UpdateInterrupt();
                return;
            }
            WriteLane(offset, value, 2);
        }

        public byte ReadByte(long offset)
        {
            var aligned = offset & ~3L;
            return (byte)(ReadDoubleWord(aligned) >> (int)((offset & 3) * 8));
        }

        public void WriteByte(long offset, byte value)
        {
            if((offset & ~3L) == Data1 && (offset & 3) >= 2)
            {
                var shift = (int)((offset & 3) * 8);
                data1 = ((data1 & ~(0xFFu << shift)) | ((uint)value << shift)) & Data1Mask;
                UpdateInterrupt();
                return;
            }
            WriteLane(offset, value, 1);
        }

        public override void Reset()
        {
            FinishHeldTransmission();
            globalControl0 = globalControl1 = interruptEnable = interruptLevel = stickyFlags = 0;
            pinControl0 = pinControl1 = pinControl2 = pinControl3 = 0;
            data0 = data1 = delay = 0;
            defaultChipSelect = 0xFF;
            for(var i = 0; i < formats.Length; ++i) formats[i] = 0;
            receiveData = 0;
            receiveFull = false;
            pendingReceiveFull = false;
            transmitEmpty = false;
            IRQ.Set(false);
        }

        public GPIO IRQ { get; }

        public long Size => 0x1000;

        private void WriteLane(long offset, uint value, int width)
        {
            var aligned = offset & ~3L;
            var shift = (int)((offset & 3) * 8);
            var mask = width == 1 ? 0xFFu : 0xFFFFu;
            if(aligned == Flags || aligned == PinControlSet || aligned == PinControlClear)
            {
                WriteDoubleWord(aligned, (value & mask) << shift);
                return;
            }
            var current = ReadDoubleWordWithoutSideEffects(aligned);
            var merged = (current & ~(mask << shift)) | ((value & mask) << shift);
            WriteDoubleWord(aligned, merged);
        }

        private uint ReadDoubleWordWithoutSideEffects(long offset)
        {
            if(offset == Buffer || offset == EmulationBuffer)
            {
                return ComposeBuffer();
            }
            return ReadDoubleWord(offset);
        }

        private void Transfer(ushort value, int formatIndex)
        {
            if(!IsEnabled || (globalControl1 & MasterAndClockMode) != MasterAndClockMode)
            {
                return;
            }
            var characterLength = (int)(formats[formatIndex] & 0x1F);
            if((characterLength != 8 && characterLength != 16) || (formats[formatIndex] & (1u << 20)) != 0)
            {
                stickyFlags |= DataLengthError;
                UpdateInterrupt();
                return;
            }

            ushort received;
            if((globalControl1 & Loopback) != 0)
            {
                received = characterLength == 8 ? (ushort)(value & 0xFF) : value;
            }
            else
            {
                ISPIPeripheral peripheral = null;
                if(externalChipSelect)
                {
                    TryGetByAddress(0, out peripheral);
                }
                else
                {
                    var selected = (~data1 >> 16) & 0xFF;
                    if(selected != 0 && (selected & (selected - 1)) == 0)
                    {
                        var index = 0;
                        while((selected >>= 1) != 0) index++;
                        TryGetByAddress(index, out peripheral);
                    }
                    if(heldPeripheral != peripheral) FinishHeldTransmission();
                }
                received = 0;
                if(peripheral != null)
                {
                    if(characterLength == 16) received = (ushort)(peripheral.Transmit((byte)(value >> 8)) << 8);
                    received |= peripheral.Transmit((byte)value);
                    if(!externalChipSelect)
                    {
                        if((data1 & (1u << 28)) != 0) heldPeripheral = peripheral;
                        else
                        {
                            peripheral.FinishTransmission();
                            heldPeripheral = null;
                        }
                    }
                }
            }
            transmitEmpty = true;

            if(receiveFull)
            {
                if(pendingReceiveFull)
                {
                    stickyFlags |= ReceiveOverrun;
                }
                else
                {
                    pendingReceiveData = received;
                    pendingReceiveFull = true;
                }
            }
            else
            {
                receiveData = received;
                receiveFull = true;
            }
            UpdateInterrupt();
        }

        private uint ReadBuffer(bool consume)
        {
            var result = ComposeBuffer();
            if(consume && receiveFull)
            {
                ConsumeReceiveBuffer();
                UpdateInterrupt();
            }
            return result;
        }

        private void ConsumeReceiveBuffer()
        {
            receiveFull = pendingReceiveFull;
            if(pendingReceiveFull) receiveData = pendingReceiveData;
            pendingReceiveFull = false;
        }

        private uint ComposeBuffer()
        {
            var result = (uint)receiveData;
            if(!receiveFull) result |= ReceiveEmpty;
            if((stickyFlags & ReceiveOverrun) != 0) result |= BufferReceiveOverrun;
            if((stickyFlags & DataLengthError) != 0) result |= BufferDataLengthError;
            return result;
        }

        private uint CurrentFlags
        {
            get
            {
                var result = stickyFlags | 0x01000000u;
                if(transmitEmpty) result |= TransmitInterrupt;
                if(receiveFull) result |= ReceiveInterrupt;
                return result;
            }
        }

        private uint GetInterruptVector()
        {
            var pending = CurrentFlags & interruptEnable & interruptLevel;
            uint vector;
            if((pending & 0x1F) != 0)
            {
                vector = 0x11;
            }
            else if((pending & ReceiveOverrun) != 0)
            {
                vector = 0x13;
                stickyFlags &= ~ReceiveOverrun;
            }
            else if((pending & ReceiveInterrupt) != 0)
            {
                vector = 0x12;
                ConsumeReceiveBuffer();
            }
            else if((pending & TransmitInterrupt) != 0)
            {
                vector = 0x14;
            }
            else
            {
                vector = 0;
            }
            UpdateInterrupt();
            return vector << 1;
        }

        private void UpdateInterrupt()
        {
            IRQ.Set((CurrentFlags & interruptEnable & interruptLevel) != 0);
        }

        private void ResetModule()
        {
            globalControl1 = interruptEnable = interruptLevel = stickyFlags = 0;
            pinControl0 = pinControl1 = pinControl2 = pinControl3 = 0;
            data0 = data1 = delay = 0;
            defaultChipSelect = 0xFF;
            for(var i = 0; i < formats.Length; ++i) formats[i] = 0;
            ClearTransferState();
        }

        private void ClearTransferState()
        {
            FinishHeldTransmission();
            receiveData = 0;
            receiveFull = false;
            pendingReceiveFull = false;
            transmitEmpty = false;
            stickyFlags = 0;
            UpdateInterrupt();
        }

        private bool IsEnabled => globalControl0 != 0 && (globalControl1 & Enable) != 0;

        private void FinishHeldTransmission()
        {
            heldPeripheral?.FinishTransmission();
            heldPeripheral = null;
        }

        private uint globalControl0;
        private uint globalControl1;
        private uint interruptEnable;
        private uint interruptLevel;
        private uint stickyFlags;
        private uint pinControl0;
        private uint pinControl1;
        private uint pinControl2;
        private uint pinControl3;
        private uint data0;
        private uint data1;
        private ushort receiveData;
        private bool receiveFull;
        private ushort pendingReceiveData;
        private bool pendingReceiveFull;
        private bool transmitEmpty;
        private ISPIPeripheral heldPeripheral;
        private readonly bool externalChipSelect;
        private uint delay;
        private uint defaultChipSelect;
        private readonly uint[] formats = new uint[4];

        private const long GlobalControl0 = 0x00;
        private const long GlobalControl1 = 0x04;
        private const long InterruptEnable = 0x08;
        private const long InterruptLevel = 0x0C;
        private const long Flags = 0x10;
        private const long PinControl0 = 0x14;
        private const long PinControl1 = 0x18;
        private const long PinControl2 = 0x1C;
        private const long PinControl3 = 0x20;
        private const long PinControlSet = 0x24;
        private const long PinControlClear = 0x28;
        private const long Data0 = 0x38;
        private const long Data1 = 0x3C;
        private const long Buffer = 0x40;
        private const long EmulationBuffer = 0x44;
        private const long Delay = 0x48;
        private const long DefaultChipSelect = 0x4C;
        private const long Format0 = 0x50;
        private const long Format1 = 0x54;
        private const long Format2 = 0x58;
        private const long Format3 = 0x5C;
        private const long InterruptVector = 0x64;

        private const uint DataLengthError = 1 << 0;
        private const uint ReceiveOverrun = 1 << 6;
        private const uint ReceiveInterrupt = 1 << 8;
        private const uint TransmitInterrupt = 1 << 9;
        private const uint StickyFlagMask = 0x5F;
        private const uint InterruptEventMask = 0x35F;
        private const uint InterruptMask = 0x0101035F;
        private const uint ReceiveEmpty = 1u << 31;
        private const uint BufferReceiveOverrun = 1u << 30;
        private const uint BufferDataLengthError = 1u << 24;
        private const uint MasterAndClockMode = 0x3;
        private const uint Loopback = 1u << 16;
        private const uint Enable = 1u << 24;
        private const uint GlobalControl1Mask = 0x81010103;
        private const uint Data1Mask = 0x17FFFFFF;
    }
}
