//
// Copyright (c) 2026 CrispStrobe
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System;

using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.DMA
{
    // Functional subset of the TI DA8xx EDMA3 channel controller. Transfers
    // complete synchronously, which keeps firmware-visible ordering deterministic.
    // QDMA configuration is visible, but QDMA execution is intentionally absent.
    public class TI_DA8xx_EDMA3CC : IDoubleWordPeripheral, IKnownSize, IGPIOReceiver
    {
        public TI_DA8xx_EDMA3CC(IMachine machine, int controllerIndex = 0)
        {
            if(controllerIndex < 0 || controllerIndex > 1)
            {
                throw new ArgumentOutOfRangeException(nameof(controllerIndex));
            }
            sysbus = machine.GetSystemBus(this);
            this.controllerIndex = controllerIndex;
            parameter = new uint[ParameterCount, ParameterWords];
            qdmaChannelMap = new uint[QdmaChannelCount];
            transferControllers = new TI_DA8xx_EDMA3TC[2];
            regionAccessEnable = new uint[RegionCount];
            interruptEnable = new uint[RegionCount];
            IRQ = new GPIO();
            ErrorIRQ = new GPIO();
            Reset();
        }

        public uint ReadDoubleWord(long offset)
        {
            if(TryGetParameter(offset, out var parameterIndex, out var word))
            {
                return parameter[parameterIndex, word];
            }
            if(TryGetRegionAccess(offset, out var accessRegion)) return regionAccessEnable[accessRegion];
            if(TryGetShadow(offset, out var shadowRegion, out var shadowOffset)) return ReadShadow(shadowRegion, shadowOffset);
            if(offset >= GlobalBase && offset < GlobalBase + 0x80) return ReadShadow(-1, offset - GlobalBase);
            // DA8xx has fixed channel-to-PaRAM mapping (CHMAP_EXIST=0).
            if(offset >= DmaChannelMap && offset < DmaChannelMap + ChannelCount * 4) return 0;
            if(offset >= QdmaChannelMap && offset < QdmaChannelMap + QdmaChannelCount * 4 && (offset & 3) == 0)
            {
                return qdmaChannelMap[(offset - QdmaChannelMap) / 4];
            }
            switch(offset)
            {
            case Revision: return RevisionValue;
            case Configuration: return controllerIndex == 0 ? Controller0Configuration : Controller1Configuration;
            case DmaQueueNumber0: return dmaQueueNumber[0];
            case DmaQueueNumber1: return dmaQueueNumber[1];
            case DmaQueueNumber2: return dmaQueueNumber[2];
            case DmaQueueNumber3: return dmaQueueNumber[3];
            case QdmaQueueNumber: return qdmaQueueNumber;
            case QueuePriority: return queuePriority;
            case EventMissed: return eventMissed;
            case QdmaEventMissed: return qdmaEventMissed;
            case ControllerError: return controllerError;
            default: return 0;
            }
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            if(TryGetParameter(offset, out var parameterIndex, out var word))
            {
                parameter[parameterIndex, word] = value;
                return;
            }
            if(TryGetRegionAccess(offset, out var accessRegion))
            {
                regionAccessEnable[accessRegion] = value;
                return;
            }
            if(TryGetShadow(offset, out var shadowRegion, out var shadowOffset))
            {
                WriteShadow(shadowRegion, shadowOffset, value);
                return;
            }
            if(offset >= GlobalBase && offset < GlobalBase + 0x80)
            {
                WriteShadow(-1, offset - GlobalBase, value);
                return;
            }
            if(offset >= QdmaChannelMap && offset < QdmaChannelMap + QdmaChannelCount * 4 && (offset & 3) == 0)
            {
                qdmaChannelMap[(offset - QdmaChannelMap) / 4] = value & 0x3FFC;
                return;
            }
            switch(offset)
            {
            case DmaQueueNumber0: dmaQueueNumber[0] = MaskQueueAssignments(value); break;
            case DmaQueueNumber1: dmaQueueNumber[1] = MaskQueueAssignments(value); break;
            case DmaQueueNumber2: dmaQueueNumber[2] = MaskQueueAssignments(value); break;
            case DmaQueueNumber3: dmaQueueNumber[3] = MaskQueueAssignments(value); break;
            case QdmaQueueNumber: qdmaQueueNumber = MaskQueueAssignments(value); break;
            case QueuePriority: queuePriority = value & (controllerIndex == 0 ? 0x77u : 0x7u); break;
            case EventMissedClear: eventMissed &= ~value; UpdateError(); break;
            case QdmaEventMissedClear: qdmaEventMissed &= ~(value & 0xFF); UpdateError(); break;
            case ControllerErrorClear: controllerError &= ~(value & 0x10003); UpdateError(); break;
            }
        }

        public void OnGPIO(int number, bool value)
        {
            if(number < 0 || number >= ChannelCount || !value)
            {
                return;
            }
            var bit = 1u << number;
            if((pendingEvents & bit) != 0)
            {
                eventMissed |= bit;
                secondaryEvents |= bit;
                UpdateError();
                return;
            }
            pendingEvents |= bit;
            if((eventEnable & bit) != 0)
            {
                ServiceChannel(number);
            }
        }

        public void Reset()
        {
            Array.Clear(parameter, 0, parameter.Length);
            Array.Clear(qdmaChannelMap, 0, qdmaChannelMap.Length);
            dmaQueueNumber = new uint[4];
            qdmaQueueNumber = queuePriority = 0;
            pendingEvents = eventEnable = secondaryEvents = 0;
            Array.Clear(regionAccessEnable, 0, regionAccessEnable.Length);
            Array.Clear(interruptEnable, 0, interruptEnable.Length);
            interruptPending = 0;
            eventMissed = qdmaEventMissed = controllerError = 0;
            IRQ.Set(false);
            ErrorIRQ.Set(false);
        }

        public void AttachTransferController(TI_DA8xx_EDMA3TC transferController, int queue)
        {
            if(queue < 0 || queue >= transferControllers.Length) throw new ArgumentOutOfRangeException(nameof(queue));
            transferControllers[queue] = transferController ?? throw new ArgumentNullException(nameof(transferController));
        }

        public GPIO IRQ { get; }
        public GPIO ErrorIRQ { get; }
        public long Size => 0x8000;

        private void SoftwareTrigger(uint channels)
        {
            for(var channel = 0; channel < ChannelCount; channel++)
            {
                if((channels & (1u << channel)) != 0) ServiceChannel(channel);
            }
        }

        private void ServicePendingEvents(uint mask)
        {
            var ready = pendingEvents & eventEnable & mask;
            for(var channel = 0; channel < ChannelCount; channel++)
            {
                if((ready & (1u << channel)) != 0) ServiceChannel(channel);
            }
        }

        private void ServiceChannel(int channel)
        {
            currentChannel = channel;
            var bit = 1u << channel;
            pendingEvents &= ~bit;
            var option = parameter[channel, Opt];
            var counts = parameter[channel, Counts];
            var aCount = (int)(counts & 0xFFFF);
            var bCount = (int)(counts >> 16);
            var cCount = (int)(parameter[channel, CCount] & 0xFFFF);
            if(aCount == 0 && bCount == 0 && cCount == 0)
            {
                eventMissed |= bit;
                secondaryEvents |= bit;
                UpdateError();
                return;
            }

            if(aCount == 0 || bCount == 0 || cCount == 0)
            {
                CompleteTransfer(channel, option);
                return;
            }
            try
            {
                var source = parameter[channel, Source];
                var destination = parameter[channel, Destination];
                var indexes = parameter[channel, BIndexes];
                var sourceBIndex = (short)(indexes & 0xFFFF);
                var destinationBIndex = (short)(indexes >> 16);
                var synchronizedByFrame = (option & SynchronizationDimension) != 0;
                var arrays = synchronizedByFrame ? bCount : 1;
                var frameSource = source;
                var frameDestination = destination;
                for(var array = 0; array < arrays; array++)
                {
                    CopyArray(source, destination, aCount, option);
                    if(array + 1 < arrays)
                    {
                        source = AddSigned(source, sourceBIndex);
                        destination = AddSigned(destination, destinationBIndex);
                    }
                }

                if(synchronizedByFrame)
                {
                    cCount--;
                    source = AddSigned(frameSource, (short)(parameter[channel, CIndexes] & 0xFFFF));
                    destination = AddSigned(frameDestination, (short)(parameter[channel, CIndexes] >> 16));
                }
                else
                {
                    bCount--;
                    if(bCount == 0)
                    {
                        cCount--;
                        if(cCount > 0)
                        {
                            bCount = (int)(parameter[channel, LinkAndReload] >> 16);
                            source = AddSigned(frameSource, (short)(parameter[channel, CIndexes] & 0xFFFF));
                            destination = AddSigned(frameDestination, (short)(parameter[channel, CIndexes] >> 16));
                        }
                    }
                    else
                    {
                        source = AddSigned(frameSource, sourceBIndex);
                        destination = AddSigned(frameDestination, destinationBIndex);
                    }
                }
                if((option & StaticParameter) == 0)
                {
                    parameter[channel, Source] = source;
                    parameter[channel, Destination] = destination;
                    parameter[channel, Counts] = ((uint)bCount << 16) | (uint)aCount;
                    parameter[channel, CCount] = (uint)cCount;
                }

                if(cCount != 0 && (option & IntermediateTransferInterruptEnable) != 0) RaiseCompletion(option);

                if(cCount == 0)
                {
                    CompleteTransfer(channel, option);
                }
            }
            catch(BusTransferException)
            {
                // The selected TC owns bus error reporting.
            }
            catch(Exception)
            {
                ReportTransferControllerError(1u << 2, parameter[channel, Source], parameter[channel, Destination]);
            }
        }

        private void CopyArray(uint source, uint destination, int count, uint option)
        {
            var widthCode = (int)((option >> 8) & 0x7);
            if(widthCode > 2)
            {
                ReportTransferControllerError(1u << 2, source, destination);
                throw new BusTransferException();
            }
            var width = 1 << widthCode;
            var sourceFixed = (option & SourceAddressMode) != 0;
            var destinationFixed = (option & DestinationAddressMode) != 0;
            for(var offset = 0; offset < count; offset += width)
            {
                var currentWidth = Math.Min(width, count - offset);
                var sourceAddress = sourceFixed ? source : source + (uint)offset;
                var destinationAddress = destinationFixed ? destination : destination + (uint)offset;
                var mapped = true;
                for(var byteIndex = 0; byteIndex < currentWidth; byteIndex++)
                {
                    mapped &= sysbus.WhatIsAt(sourceFixed ? sourceAddress : sourceAddress + (uint)byteIndex, this) != null;
                    mapped &= sysbus.WhatIsAt(destinationFixed ? destinationAddress : destinationAddress + (uint)byteIndex, this) != null;
                }
                if(!mapped)
                {
                    ReportTransferControllerError(1, sourceAddress, destinationAddress);
                    throw new BusTransferException();
                }
                switch(currentWidth)
                {
                case 1: sysbus.WriteByte(destinationAddress, sysbus.ReadByte(sourceAddress), this); break;
                case 2: sysbus.WriteWord(destinationAddress, sysbus.ReadWord(sourceAddress), this); break;
                case 4: sysbus.WriteDoubleWord(destinationAddress, sysbus.ReadDoubleWord(sourceAddress), this); break;
                default:
                    var bytes = sysbus.ReadBytes(sourceAddress, currentWidth, context: this);
                    sysbus.WriteBytes(bytes, destinationAddress, context: this);
                    break;
                }
            }
        }

        private void CompleteTransfer(int channel, uint option)
        {
            if((option & TransferCompleteInterruptEnable) != 0) RaiseCompletion(option);
            var link = parameter[channel, LinkAndReload] & 0xFFFF;
            if((option & StaticParameter) != 0 || link == NullLink) return;
            if(link >= ParameterBase) link -= (uint)ParameterBase;
            if((link & (ParameterSize - 1)) != 0 || link >= ParameterCount * ParameterSize)
            {
                eventMissed |= 1u << channel;
                UpdateError();
                return;
            }
            var linked = (int)(link / ParameterSize);
            for(var word = 0; word < ParameterWords; word++) parameter[channel, word] = parameter[linked, word];
        }

        private void RaiseCompletion(uint option)
        {
            var tcc = (int)((option >> 12) & 0x1F);
            interruptPending |= 1u << tcc;
            UpdateInterrupt();
        }

        private void UpdateInterrupt() => IRQ.Set((interruptPending & interruptEnable[0]) != 0);
        private void UpdateError() => ErrorIRQ.Set(eventMissed != 0 || qdmaEventMissed != 0 || controllerError != 0);
        private uint MaskQueueAssignments(uint value) => value & (controllerIndex == 0 ? 0x11111111u : 0u);
        private static uint AddSigned(uint value, short increment) => unchecked((uint)(value + increment));

        private void ReportTransferControllerError(uint error, uint source, uint destination)
        {
            var queueWord = dmaQueueNumber[currentChannel / 8];
            var queue = (int)((queueWord >> ((currentChannel & 7) * 4)) & 0x7);
            if(queue >= transferControllers.Length) queue = 0;
            transferControllers[queue]?.ReportError(error, source ^ destination);
        }

        private static bool TryGetParameter(long offset, out int index, out int word)
        {
            var relative = offset - ParameterBase;
            index = (int)(relative / ParameterSize);
            word = (int)((relative % ParameterSize) / 4);
            return relative >= 0 && relative < ParameterCount * ParameterSize && (offset & 3) == 0;
        }

        private uint ReadShadow(int region, long offset)
        {
            var mask = region < 0 ? uint.MaxValue : regionAccessEnable[region];
            switch(offset)
            {
            case 0x0: return pendingEvents & mask;
            case 0x20: return eventEnable & mask;
            case 0x38: return secondaryEvents & mask;
            case 0x50: return (region < 0 ? interruptEnable[0] : interruptEnable[region]) & mask;
            case 0x68: return interruptPending & mask;
            default: return 0;
            }
        }

        private void WriteShadow(int region, long offset, uint value)
        {
            var mask = region < 0 ? uint.MaxValue : regionAccessEnable[region];
            value &= mask;
            switch(offset)
            {
            case 0x8: pendingEvents &= ~value; break;
            case 0x10: SoftwareTrigger(value); break;
            case 0x28: eventEnable &= ~value; break;
            case 0x30: eventEnable |= value; ServicePendingEvents(value); break;
            case 0x40: secondaryEvents &= ~value; break;
            case 0x58: if(region < 0) interruptEnable[0] &= ~value; else interruptEnable[region] &= ~value; UpdateInterrupt(); break;
            case 0x60: if(region < 0) interruptEnable[0] |= value; else interruptEnable[region] |= value; UpdateInterrupt(); break;
            case 0x70: interruptPending &= ~value; UpdateInterrupt(); break;
            case 0x78: UpdateInterrupt(); break;
            }
        }

        private static bool TryGetShadow(long offset, out int region, out long localOffset)
        {
            var relative = offset - ShadowBase;
            region = (int)(relative / ShadowStride);
            localOffset = relative % ShadowStride;
            return relative >= 0 && region < RegionCount;
        }

        private static bool TryGetRegionAccess(long offset, out int region)
        {
            var relative = offset - RegionAccessEnableBase;
            region = (int)(relative / 8);
            return relative >= 0 && region < RegionCount && relative % 8 == 0;
        }

        private readonly IBusController sysbus;
        private readonly int controllerIndex;
        private readonly uint[,] parameter;
        private readonly uint[] qdmaChannelMap;
        private readonly TI_DA8xx_EDMA3TC[] transferControllers;
        private uint[] dmaQueueNumber;
        private uint qdmaQueueNumber, queuePriority, pendingEvents, eventEnable, secondaryEvents;
        private readonly uint[] regionAccessEnable, interruptEnable;
        private uint interruptPending, eventMissed, qdmaEventMissed, controllerError;
        private int currentChannel;

        private const int ChannelCount=32, QdmaChannelCount=8, ParameterCount=128, ParameterWords=8, ParameterSize=0x20, RegionCount=4;
        private const int Opt=0, Source=1, Counts=2, Destination=3, BIndexes=4, LinkAndReload=5, CIndexes=6, CCount=7;
        private const long Revision=0x0, Configuration=0x4, DmaChannelMap=0x100, QdmaChannelMap=0x200;
        private const long DmaQueueNumber0=0x240, DmaQueueNumber1=0x244, DmaQueueNumber2=0x248, DmaQueueNumber3=0x24C;
        private const long QdmaQueueNumber=0x260, QueuePriority=0x284, EventMissed=0x300, EventMissedClear=0x308;
        private const long QdmaEventMissed=0x310, QdmaEventMissedClear=0x314, ControllerError=0x318, ControllerErrorClear=0x31C;
        private const long RegionAccessEnableBase=0x340, GlobalBase=0x1000, ShadowBase=0x2000, ShadowStride=0x200;
        private const long ParameterBase=0x4000;
        private const uint RevisionValue=0x40015300, Controller0Configuration=0x213344, Controller1Configuration=0x203344;
        private const uint SourceAddressMode=1u << 0, DestinationAddressMode=1u << 1, SynchronizationDimension=1u << 2;
        private const uint StaticParameter=1u << 3, TransferCompleteInterruptEnable=1u << 20;
        private const uint IntermediateTransferInterruptEnable=1u << 21, NullLink=0xFFFF;

        private sealed class BusTransferException : Exception
        {
        }
    }
}
