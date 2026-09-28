//
// Copyright (c) 2026 CrispStrobe
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System;
using System.Collections.Generic;

using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Utilities;

namespace Antmicro.Renode.Peripherals.SD
{
    // TI AM1808 MMC/SD controller. The first functional slice supports card
    // commands and receive data; write commands are rejected without modifying
    // the attached image.
    public class TI_DA8xx_MMCSD : NullRegistrationPointPeripheralContainer<SDCard>,
        IDoubleWordPeripheral, IWordPeripheral, IBytePeripheral, IKnownSize
    {
        public TI_DA8xx_MMCSD(IMachine machine) : base(machine)
        {
            this.machine = machine;
            fifo = new Queue<byte>();
            remainingData = new Queue<byte>();
            response = new uint[4];
            IRQ = new GPIO();
            SDIOIRQ = new GPIO();
            RxDMARequest = new GPIO();
            TxDMARequest = new GPIO();
            Reset();
        }

        public uint ReadDoubleWord(long offset)
        {
            if(offset == DataReceive) return ReadFifo(4);
            switch(offset)
            {
            case Control: return control;
            case Clock: return clock;
            case Status0:
                var result = status0;
                status0 &= ReadyStatusMask;
                UpdateInterrupts();
                return result;
            case Status1: return GetStatus1();
            case InterruptMask: return interruptMask;
            case ResponseTimeout: return responseTimeout;
            case DataTimeout: return dataTimeout;
            case BlockLength: return blockLength;
            case NumberOfBlocks: return numberOfBlocks;
            case NumberOfBlocksCounter: return numberOfBlocksCounter;
            case DataTransmit: return 0;
            case Command: return command & ~DmaTrigger;
            case Argument: return argument;
            case Response01: return response[0];
            case Response23: return response[1];
            case Response45: return response[2];
            case Response67: return response[3];
            case DataResponse: return dataResponse;
            case CommandIndex: return commandIndex;
            case ClockCounter: return 0;
            case ResponseTimeoutCounter: return 0;
            case DataTimeoutCounter: return 0;
            case BlockLengthCounter: return bytesRemainingInBlock;
            case SDIOControl: return sdioControl;
            case SDIOStatus0: return 1;
            case SDIOInterruptEnable: return sdioInterruptEnable;
            case SDIOInterruptStatus: return sdioInterruptStatus;
            case FifoControl: return fifoControl;
            default: return 0;
            }
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            switch(offset)
            {
            case Control:
                control = value & ControlMask;
                if((control & DataReset) != 0) AbortDataTransfer();
                if((control & CommandReset) != 0) ResetCommandPath();
                break;
            case Clock: clock = value & ClockMask; break;
            case InterruptMask: interruptMask = value & StatusMask; UpdateInterrupts(); break;
            case ResponseTimeout: responseTimeout = value & ResponseTimeoutMask; break;
            case DataTimeout: dataTimeout = value & 0xFFFF; break;
            case BlockLength: blockLength = value & 0xFFF; break;
            case NumberOfBlocks: numberOfBlocks = value & 0xFFFF; break;
            case DataTransmit: RejectWriteTransfer(); break;
            case Command:
                command = value & CommandMask;
                ExecuteCommand(value & CommandMask);
                break;
            case Argument: argument = value; break;
            case SDIOControl: sdioControl = value & 0x3; break;
            case SDIOInterruptEnable: sdioInterruptEnable = value & 0x3; UpdateInterrupts(); break;
            case SDIOInterruptStatus: sdioInterruptStatus &= ~(value & 0x3); UpdateInterrupts(); break;
            case FifoControl:
                fifoControl = value & 0x1F;
                if((fifoControl & FifoReset) != 0) ClearFifo();
                break;
            }
        }

        public ushort ReadWord(long offset)
        {
            if(offset >= DataReceive && offset < DataReceive + 4) return (ushort)ReadFifo(2);
            var aligned = offset & ~3L;
            return (ushort)(ReadDoubleWord(aligned) >> (int)((offset & 2) * 8));
        }

        public void WriteWord(long offset, ushort value)
        {
            if(offset >= DataTransmit && offset < DataTransmit + 4) { RejectWriteTransfer(); return; }
            if(IsReadOnly(offset & ~3L)) return;
            WriteLane(offset, value, 2);
        }

        public byte ReadByte(long offset)
        {
            if(offset >= DataReceive && offset < DataReceive + 4) return (byte)ReadFifo(1);
            var aligned = offset & ~3L;
            return (byte)(ReadDoubleWord(aligned) >> (int)((offset & 3) * 8));
        }

        public void WriteByte(long offset, byte value)
        {
            if(offset >= DataTransmit && offset < DataTransmit + 4) { RejectWriteTransfer(); return; }
            if(IsReadOnly(offset & ~3L)) return;
            WriteLane(offset, value, 1);
        }

        public override void Reset()
        {
            control = 0;
            clock = 0xFF;
            status0 = DataTransmitReady;
            interruptMask = responseTimeout = dataTimeout = 0;
            blockLength = 0x200;
            numberOfBlocks = 0;
            numberOfBlocksCounter = 0xFFFF;
            command = argument = dataResponse = commandIndex = 0;
            Array.Clear(response, 0, response.Length);
            sdioControl = sdioInterruptEnable = sdioInterruptStatus = fifoControl = 0;
            transferGeneration++;
            refillScheduled = false;
            bytesRemainingInBlock = 0;
            fifo.Clear();
            remainingData.Clear();
            IRQ.Set(false);
            SDIOIRQ.Set(false);
            RxDMARequest.Set(false);
            TxDMARequest.Set(false);
        }

        public GPIO IRQ { get; }
        public GPIO SDIOIRQ { get; }
        public GPIO RxDMARequest { get; }
        public GPIO TxDMARequest { get; }
        public long Size => 0x1000;

        public void SetSDIOInterrupt(bool value)
        {
            if(value) sdioInterruptStatus |= 1;
            UpdateInterrupts();
        }

        private void ExecuteCommand(uint value)
        {
            if((value & DataClear) != 0)
            {
                status0 &= ~ReadyStatusMask;
                ClearFifo();
            }
            var index = value & 0x3F;
            var hasData = (value & DataTransfer) != 0;
            var isWrite = (value & DataWrite) != 0;
            var responseExpected = (value & ResponseFormatMask) != 0;
            dmaTriggered = (value & DmaTrigger) != 0;
            if(hasData && isWrite)
            {
                RejectWriteTransfer();
                if(responseExpected) SetStatus(ResponseDone);
                return;
            }
            var card = RegisteredPeripheral;
            if(card == null)
            {
                if(responseExpected) SetStatus(ResponseTimeoutStatus);
                return;
            }

            var wasApplicationCommand = card.TreatNextCommandAsAppCommand;
            var result = card.HandleCommand(index, argument);
            StoreResponse(index, value, result);
            if(responseExpected) SetStatus(ResponseDone | (((value & BusyExpected) != 0) ? BusyDone : 0));
            if(hasData && !isWrite)
            {
                StartReadTransfer(card, index, wasApplicationCommand);
            }
        }

        private void StoreResponse(uint index, uint commandValue, BitStream bits)
        {
            Array.Clear(response, 0, response.Length);
            commandIndex = index & 0x3F;
            var format = (commandValue >> 9) & 0x3;
            if(format == 0 || bits.Length == 0) return;
            if(format == 2)
            {
                response[0] = bits.AsUInt32(0);
                response[1] = bits.AsUInt32(32);
                response[2] = bits.AsUInt32(64);
                response[3] = bits.AsUInt32(96);
            }
            else
            {
                response[3] = bits.AsUInt32(0);
            }
        }

        private void StartReadTransfer(SDCard card, uint index, bool wasApplicationCommand)
        {
            AbortDataTransfer();
            dmaTriggered = (command & DmaTrigger) != 0;
            byte[] data;
            if(index == 6 && !wasApplicationCommand)
            {
                data = card.ReadSwitchFunctionStatusRegister();
            }
            else
            {
                var blocks = index == 17 ? 1u : Math.Max(1u, numberOfBlocks);
                var length = checked((uint)Math.Max(1u, blockLength) * blocks);
                if(wasApplicationCommand && index == 51) length = 8;
                if(wasApplicationCommand && index == 13) length = 64;
                data = card.ReadData(length);
            }
            remainingData.EnqueueRange(data);
            var hasFixedCommandLength = index == 6 || (wasApplicationCommand && (index == 13 || index == 51));
            numberOfBlocksCounter = hasFixedCommandLength || index == 17 ? 1u : Math.Max(1u, numberOfBlocks);
            bytesRemainingInBlock = hasFixedCommandLength ? (uint)data.Length : Math.Max(1u, blockLength);
            RefillFifoAndRequest();
        }

        private uint ReadFifo(int width)
        {
            uint result = 0;
            for(var i = 0; i < width && fifo.Count > 0; i++)
            {
                result |= (uint)fifo.Dequeue() << (8 * i);
                AccountReceivedByte();
            }
            if(fifo.Count == 0)
            {
                status0 &= ~DataReceiveReady;
                if(remainingData.Count > 0)
                {
                    if(dmaTriggered) ScheduleRefill();
                    else RefillFifoAndRequest();
                }
                else if(bytesRemainingInBlock == 0)
                {
                    SetStatus(TransferDone | DataDone);
                }
            }
            UpdateInterrupts();
            return result;
        }

        private void AccountReceivedByte()
        {
            if(bytesRemainingInBlock > 0) bytesRemainingInBlock--;
            if(bytesRemainingInBlock == 0 && numberOfBlocksCounter > 0)
            {
                numberOfBlocksCounter--;
                if(numberOfBlocksCounter > 0) bytesRemainingInBlock = Math.Max(1u, blockLength);
            }
        }

        private void ScheduleRefill()
        {
            if(refillScheduled) return;
            refillScheduled = true;
            var generation = transferGeneration;
            machine.LocalTimeSource.ExecuteInNearestSyncedState(_ =>
            {
                if(generation != transferGeneration) return;
                refillScheduled = false;
                RefillFifoAndRequest();
            });
        }

        private void RefillFifoAndRequest()
        {
            var threshold = (fifoControl & FifoLevel) != 0 ? 64 : 32;
            while(fifo.Count < threshold && remainingData.Count > 0) fifo.Enqueue(remainingData.Dequeue());
            if(fifo.Count == 0) return;
            status0 |= DataReceiveReady;
            UpdateInterrupts();
            if(dmaTriggered) RxDMARequest.Blink();
        }

        private void AbortDataTransfer()
        {
            transferGeneration++;
            refillScheduled = false;
            fifo.Clear();
            remainingData.Clear();
            numberOfBlocksCounter = bytesRemainingInBlock = 0;
            status0 &= ~(DataReceiveReady | DataDone | TransferDone);
            RxDMARequest.Set(false);
            TxDMARequest.Set(false);
        }

        private void ClearFifo()
        {
            fifo.Clear();
            status0 &= ~DataReceiveReady;
            RxDMARequest.Set(false);
            UpdateInterrupts();
        }

        private void ResetCommandPath()
        {
            status0 &= ReadyStatusMask;
            commandIndex = 0;
            Array.Clear(response, 0, response.Length);
            UpdateInterrupts();
        }

        private void RejectWriteTransfer()
        {
            AbortDataTransfer();
            SetStatus(WriteCrcError);
        }

        private uint GetStatus1()
        {
            uint value = DataTransmitEmpty;
            if((clock & ClockEnable) == 0 || fifo.Count == 0) value |= ClockStopped;
            if(fifo.Count == 0) value |= FifoEmpty;
            if(fifo.Count >= 64) value |= FifoFull;
            if(fifo.Count >= 4) value |= DataReceiveFull;
            return value;
        }

        private static bool IsReadOnly(long offset)
        {
            return offset == Status0 || offset == Status1 || offset == NumberOfBlocksCounter
                || offset == DataReceive || offset == DataResponse || offset == CommandIndex
                || offset == ClockCounter || offset == ResponseTimeoutCounter || offset == DataTimeoutCounter
                || offset == BlockLengthCounter || offset == SDIOStatus0;
        }

        private void SetStatus(uint value)
        {
            status0 |= value;
            UpdateInterrupts();
        }

        private void UpdateInterrupts()
        {
            IRQ.Set((status0 & interruptMask) != 0);
            SDIOIRQ.Set((sdioInterruptStatus & sdioInterruptEnable) != 0);
        }

        private void WriteLane(long offset, uint value, int width)
        {
            var aligned = offset & ~3L;
            var shift = (int)((offset & 3) * 8);
            var mask = (width == 1 ? 0xFFu : 0xFFFFu) << shift;
            var current = ReadDoubleWord(aligned);
            WriteDoubleWord(aligned, (current & ~mask) | ((value << shift) & mask));
        }

        private readonly IMachine machine;
        private readonly Queue<byte> fifo, remainingData;
        private readonly uint[] response;
        private uint control, clock, status0, interruptMask, responseTimeout, dataTimeout;
        private uint blockLength, numberOfBlocks, numberOfBlocksCounter, command, argument;
        private uint dataResponse, commandIndex, bytesRemainingInBlock, fifoControl;
        private uint sdioControl, sdioInterruptEnable, sdioInterruptStatus;
        private ulong transferGeneration;
        private bool dmaTriggered, refillScheduled;

        private const long Control=0x00, Clock=0x04, Status0=0x08, Status1=0x0C, InterruptMask=0x10;
        private const long ResponseTimeout=0x14, DataTimeout=0x18, BlockLength=0x1C, NumberOfBlocks=0x20;
        private const long NumberOfBlocksCounter=0x24, DataReceive=0x28, DataTransmit=0x2C, Command=0x30;
        private const long Argument=0x34, Response01=0x38, Response23=0x3C, Response45=0x40, Response67=0x44;
        private const long DataResponse=0x48, CommandIndex=0x50, ClockCounter=0x54, ResponseTimeoutCounter=0x58;
        private const long DataTimeoutCounter=0x5C, BlockLengthCounter=0x60, SDIOControl=0x64, SDIOStatus0=0x68;
        private const long SDIOInterruptEnable=0x6C, SDIOInterruptStatus=0x70, FifoControl=0x74;
        private const uint DataDone=1u << 0, BusyDone=1u << 1, ResponseDone=1u << 2;
        private const uint ResponseTimeoutStatus=1u << 4, WriteCrcError=1u << 5, DataTransmitReady=1u << 9;
        private const uint DataReceiveReady=1u << 10, TransferDone=1u << 12;
        private const uint StatusMask=0x3EFF, ReadyStatusMask=DataTransmitReady | DataReceiveReady;
        private const uint ClockStopped=1u << 1, DataTransmitEmpty=1u << 2, DataReceiveFull=1u << 3;
        private const uint FifoEmpty=1u << 5, FifoFull=1u << 6, ClockEnable=1u << 8;
        private const uint DataReset=1u << 0, CommandReset=1u << 1, ControlMask=0x7C7, ClockMask=0x3FF;
        private const uint ResponseTimeoutMask=0x3FFFF, BusyExpected=1u << 8, ResponseFormatMask=3u << 9, DataWrite=1u << 11;
        private const uint DataTransfer=1u << 13, DataClear=1u << 15, DmaTrigger=1u << 16, CommandMask=0x1FFBF;
        private const uint FifoReset=1u << 0, FifoLevel=1u << 2;
    }
}
