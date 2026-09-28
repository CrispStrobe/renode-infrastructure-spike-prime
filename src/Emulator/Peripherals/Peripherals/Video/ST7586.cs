//
// Copyright (c) 2026 CrispStrobe
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System;

using Antmicro.Renode.Backends.Display;
using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.SPI;

namespace Antmicro.Renode.Peripherals.Video
{
    // Sitronix ST7586 display controller configured as on LEGO EV3. The model
    // accepts the controller's three-pixels-per-byte grey DDRAM stream and
    // exposes the EV3's 178x128 visible area as an L8 video surface.
    public class ST7586 : AutoRepaintingVideo, ISPIPeripheral, IGPIOReceiver
    {
        public ST7586(IMachine machine) : base(machine)
        {
            displayRam = new byte[RamColumns * RamRows];
            visibleFrame = new byte[VisibleWidth * VisibleHeight];
            parameters = new byte[4];
            Reconfigure(VisibleWidth, VisibleHeight, PixelFormat.RGB888, autoRepaint: false);
            Reset();
        }

        public byte Transmit(byte data)
        {
            if(chipSelect || reset)
            {
                return 0;
            }

            if(!dataCommand)
            {
                StartCommand(data);
                return 0;
            }

            if(currentCommand == MemoryWrite)
            {
                WriteMemory(data);
            }
            else
            {
                AcceptParameter(data);
            }
            return 0;
        }

        public void FinishTransmission()
        {
            // Command and parameter state intentionally survives CS edges. The
            // Linux MIPI-DBI path may split command and data into transactions.
        }

        public void OnGPIO(int number, bool value)
        {
            switch(number)
            {
            case ChipSelectGPIO:
                chipSelect = value;
                break;
            case DataCommandGPIO:
                dataCommand = value;
                break;
            case ResetGPIO:
                if(value && !reset) ResetController();
                reset = value;
                break;
            }
        }

        public override void Reset()
        {
            chipSelect = false;
            dataCommand = false;
            reset = false;
            ResetController();
        }

        public byte GetPixel(int x, int y)
        {
            if(x < 0 || x >= VisibleWidth || y < 0 || y >= VisibleHeight)
            {
                throw new ArgumentOutOfRangeException();
            }
            Repaint();
            return visibleFrame[y * VisibleWidth + x];
        }

        public uint FrameChecksum
        {
            get
            {
                Repaint();
                var hash = 2166136261u;
                foreach(var value in visibleFrame)
                {
                    hash ^= value;
                    hash *= 16777619u;
                }
                return hash;
            }
        }

        public ulong AcceptedDataBytes { get; private set; }

        public bool DisplayEnabled => displayOn && !sleeping && ddramEnabled && !reset;

        public const int ChipSelectGPIO = 0;
        public const int DataCommandGPIO = 1;
        public const int ResetGPIO = 2;
        public const int VisibleWidth = 178;
        public const int VisibleHeight = 128;

        protected override void Repaint()
        {
            var enabled = DisplayEnabled;
            for(var y = 0; y < VisibleHeight; ++y)
            {
                var sourceY = mirrorY ? VisibleHeight - 1 - y : y;
                for(var x = 0; x < VisibleWidth; ++x)
                {
                    var sourceX = mirrorX ? VisibleWidth - 1 - x : x;
                    byte luminance = 0xFF;
                    if(enabled)
                    {
                        var packed = displayRam[sourceY * RamColumns + sourceX / 3];
                        var level = sourceX % 3 == 0 ? (packed >> 5) & 0x7
                            : sourceX % 3 == 1 ? (packed >> 2) & 0x7
                            : (packed & 0x3) * 2 + (packed & 0x3) / 2;
                        luminance = DecodeLuminance(level);
                        if(inverted) luminance = (byte)(0xFF - luminance);
                    }
                    SetVisiblePixel(x, y, luminance);
                }
            }
        }

        private void StartCommand(byte command)
        {
            currentCommand = command;
            parameterIndex = 0;
            switch(command)
            {
            case ExitSleep: sleeping = false; break;
            case EnterSleep: sleeping = true; break;
            case DisplayOff: displayOn = false; break;
            case DisplayOn: displayOn = true; break;
            case ExitInvert: inverted = false; break;
            case EnterInvert: inverted = true; break;
            case MemoryWrite:
                currentColumn = startColumn;
                currentRow = startRow;
                break;
            }
            Repaint();
        }

        private void AcceptParameter(byte value)
        {
            switch(currentCommand)
            {
            case ColumnAddress:
            case PageAddress:
                parameters[parameterIndex++] = value;
                if(parameterIndex == 4)
                {
                    var start = (parameters[0] << 8) | parameters[1];
                    var end = (parameters[2] << 8) | parameters[3];
                    if(currentCommand == ColumnAddress)
                    {
                        startColumn = Math.Min(start, RamColumns - 1);
                        endColumn = Math.Min(Math.Max(end, startColumn), RamColumns - 1);
                    }
                    else
                    {
                        startRow = Math.Min(start, RamRows - 1);
                        endRow = Math.Min(Math.Max(end, startRow), RamRows - 1);
                    }
                    parameterIndex = 0;
                }
                break;
            case EnableDDRAM:
                ddramEnabled = (value & 0x2) != 0;
                break;
            case AddressMode:
                mirrorX = (value & 0x40) != 0;
                mirrorY = (value & 0x80) != 0;
                break;
            }
            Repaint();
        }

        private void WriteMemory(byte value)
        {
            if(currentColumn < RamColumns && currentRow < RamRows)
            {
                displayRam[currentRow * RamColumns + currentColumn] = value;
                AcceptedDataBytes++;
                RenderRamByte(currentColumn, currentRow, value);
            }
            if(currentColumn < endColumn)
            {
                currentColumn++;
            }
            else
            {
                currentColumn = startColumn;
                currentRow = currentRow < endRow ? currentRow + 1 : startRow;
            }
        }

        private void RenderRamByte(int column, int row, byte value)
        {
            if(!DisplayEnabled || row >= VisibleHeight)
            {
                return;
            }
            for(var subPixel = 0; subPixel < 3; ++subPixel)
            {
                var sourceX = column * 3 + subPixel;
                if(sourceX >= VisibleWidth) continue;
                var level = subPixel == 0 ? (value >> 5) & 0x7
                    : subPixel == 1 ? (value >> 2) & 0x7
                    : (value & 0x3) * 2 + (value & 0x3) / 2;
                var luminance = DecodeLuminance(level);
                if(inverted) luminance = (byte)(0xFF - luminance);
                var x = mirrorX ? VisibleWidth - 1 - sourceX : sourceX;
                var y = mirrorY ? VisibleHeight - 1 - row : row;
                SetVisiblePixel(x, y, luminance);
            }
        }

        private void SetVisiblePixel(int x, int y, byte luminance)
        {
            var pixel = y * VisibleWidth + x;
            visibleFrame[pixel] = luminance;
            var bufferOffset = pixel * 3;
            buffer[bufferOffset] = luminance;
            buffer[bufferOffset + 1] = luminance;
            buffer[bufferOffset + 2] = luminance;
        }

        private void ResetController()
        {
            Array.Clear(displayRam, 0, displayRam.Length);
            Array.Clear(parameters, 0, parameters.Length);
            sleeping = true;
            displayOn = ddramEnabled = inverted = mirrorX = mirrorY = false;
            currentCommand = 0;
            parameterIndex = 0;
            startColumn = startRow = currentColumn = currentRow = 0;
            endColumn = RamColumns - 1;
            endRow = RamRows - 1;
            AcceptedDataBytes = 0;
            Repaint();
        }

        private static byte DecodeLuminance(int level)
        {
            if(level >= 7) return 0x00;
            if(level >= 4) return 0x55;
            if(level >= 2) return 0xAA;
            return 0xFF;
        }

        private const int RamColumns = 128;
        private const int RamRows = 160;
        private const byte EnterSleep = 0x10;
        private const byte ExitSleep = 0x11;
        private const byte ExitInvert = 0x20;
        private const byte EnterInvert = 0x21;
        private const byte DisplayOff = 0x28;
        private const byte DisplayOn = 0x29;
        private const byte ColumnAddress = 0x2A;
        private const byte PageAddress = 0x2B;
        private const byte MemoryWrite = 0x2C;
        private const byte AddressMode = 0x36;
        private const byte EnableDDRAM = 0x3A;

        private readonly byte[] displayRam;
        private readonly byte[] visibleFrame;
        private readonly byte[] parameters;
        private bool chipSelect;
        private bool dataCommand;
        private bool reset;
        private bool sleeping;
        private bool displayOn;
        private bool ddramEnabled;
        private bool inverted;
        private bool mirrorX;
        private bool mirrorY;
        private byte currentCommand;
        private int parameterIndex;
        private int startColumn;
        private int endColumn;
        private int startRow;
        private int endRow;
        private int currentColumn;
        private int currentRow;
    }
}
