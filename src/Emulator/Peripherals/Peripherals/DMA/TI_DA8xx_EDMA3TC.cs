//
// Copyright (c) 2026 CrispStrobe
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.DMA
{
    // EDMA3 transfer-controller register and error endpoint. Data movement is
    // coordinated by TI_DA8xx_EDMA3CC in the deterministic v1 model.
    public class TI_DA8xx_EDMA3TC : IDoubleWordPeripheral, IKnownSize
    {
        public TI_DA8xx_EDMA3TC(uint configuration = 0x212)
        {
            this.configuration = configuration;
            IRQ = new GPIO();
            Reset();
        }

        public uint ReadDoubleWord(long offset)
        {
            switch(offset)
            {
            case 0x0: return 0x40003B00;
            case 0x4: return configuration;
            case 0x100: return 0;
            case 0x120: return errorStatus;
            case 0x124: return errorEnable;
            case 0x12C: return errorDetails;
            case 0x140: return readRate;
            default: return 0;
            }
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            switch(offset)
            {
            case 0x124: errorEnable = value & ErrorMask; Update(); break;
            case 0x128: errorStatus &= ~(value & ErrorMask); Update(); break;
            case 0x130: if((value & 1) != 0) Update(); break;
            case 0x140: readRate = value & 0x7; break;
            }
        }

        public void ReportError(uint value, uint details = 0)
        {
            errorStatus |= value & ErrorMask;
            errorDetails = details;
            Update();
        }

        public void Reset()
        {
            errorStatus = errorDetails = readRate = 0;
            errorEnable = 0;
            IRQ.Set(false);
        }

        public GPIO IRQ { get; }
        public long Size => 0x400;

        private void Update() => IRQ.Set((errorStatus & errorEnable) != 0);

        private readonly uint configuration;
        private uint errorStatus, errorDetails, errorEnable, readRate;
        private const uint ErrorMask = 0xD;
    }
}
