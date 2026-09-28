//
// Copyright (c) 2026 CrispStrobe
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    public class TI_DA8xx_SYSCFG0 : IDoubleWordPeripheral, IKnownSize
    {
        public TI_DA8xx_SYSCFG0(uint bootConfiguration = 0, bool postBootloaderState = false)
        {
            this.bootConfiguration = bootConfiguration;
            this.postBootloaderState = postBootloaderState;
            pinMultiplexing = new uint[20];
            configuration = new uint[5];
            Reset();
        }

        public uint ReadDoubleWord(long offset)
        {
            if(IsIndexed(offset, PinMultiplexing0, pinMultiplexing.Length, out var index)) return pinMultiplexing[index];
            if(IsIndexed(offset, ConfigurationChip0, configuration.Length - 1, out index)) return configuration[index];
            switch(offset)
            {
            case Revision: return RevisionValue;
            case DeviceId: return DeviceIdValue;
            case BootConfiguration: return bootConfiguration;
            case ChipRevision: return ChipRevisionValue;
            case Kick0: case Kick1: return 0;
            case SuspendSource: return suspendSource;
            case ChipSignal: return chipSignal;
            case ChipSignalClear: return 0;
            // AMUTECLR is an action-only register; it has no retained readback.
            case ConfigurationChip4: return 0;
            default: return 0;
            }
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            if(IsIndexed(offset, PinMultiplexing0, pinMultiplexing.Length, out var index))
            {
                pinMultiplexing[index] = value; return;
            }
            if(IsIndexed(offset, ConfigurationChip0, configuration.Length - 1, out index))
            {
                configuration[index] = value; return;
            }
            switch(offset)
            {
            case SuspendSource: suspendSource = value; break;
            case ChipSignal: chipSignal |= value; break;
            case ChipSignalClear: chipSignal &= ~value; break;
            case ConfigurationChip4: break;
            }
        }

        public void Reset()
        {
            for(var i = 0; i < pinMultiplexing.Length; i++) pinMultiplexing[i] = 0;
            for(var i = 0; i < configuration.Length; i++) configuration[i] = 0;
            suspendSource = uint.MaxValue;
            chipSignal = 0;
            configuration[3] = 0x0000FF00;
            if(postBootloaderState)
            {
                pinMultiplexing[3] = 0x00001101;
                pinMultiplexing[4] = 0x22002210;
                pinMultiplexing[10] = 0x00222222;
                suspendSource = 0xF7D6FFFF;
            }
        }

        public long Size => 0x1000;

        private static bool IsIndexed(long offset, long start, int count, out int index)
        {
            index = (int)((offset - start) / 4);
            return offset >= start && offset < start + count * 4 && (offset & 3) == 0;
        }

        private readonly uint bootConfiguration;
        private readonly bool postBootloaderState;
        private readonly uint[] pinMultiplexing, configuration;
        private uint suspendSource, chipSignal;

        private const long Revision=0x0, DeviceId=0x18, BootConfiguration=0x20, ChipRevision=0x24;
        private const long Kick0=0x38, Kick1=0x3C, PinMultiplexing0=0x120, SuspendSource=0x170;
        private const long ChipSignal=0x174, ChipSignalClear=0x178, ConfigurationChip0=0x17C, ConfigurationChip4=0x18C;
        private const uint RevisionValue=0x4E840102, DeviceIdValue=0x1B7D102F, ChipRevisionValue=4;
    }
}
