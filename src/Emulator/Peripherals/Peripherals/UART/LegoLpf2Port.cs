//
// Copyright (c) 2026 CrispStrobe
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System;
using System.Collections.Generic;
using System.Linq;

using Antmicro.Migrant;
using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;

namespace Antmicro.Renode.Peripherals.UART
{
    public static class LegoLpf2PortExtensions
    {
        public static void CreateLegoLpf2Port(this Emulation emulation, string name, string device = "none")
        {
            emulation.ExternalsManager.AddExternal(new LegoLpf2Port(device), name);
        }
    }

    // A byte-transport adapter for deterministic LEGO LPF2 devices. The device
    // contract below deliberately has no dependency on UART or Renode timing.
    public class LegoLpf2Port : IUART, IExternal
    {
        public LegoLpf2Port(string device = "none")
        {
            receiveBuffer = new List<byte>();
            Attach(device);
        }

        public void Attach(string device)
        {
            switch((device ?? "none").Trim().ToLowerInvariant())
            {
                case "none":
                    Device = null;
                    break;
                case "ultrasonic":
                    Device = new Lpf2UltrasonicSensor();
                    break;
                case "medium-motor":
                case "motor":
                    Device = new Lpf2MediumMotor();
                    break;
                default:
                    throw new ArgumentException($"Unsupported LPF2 device '{device}'", nameof(device));
            }
            ResetProtocol();
        }

        public void AttachDevice(ILpf2Device device)
        {
            Device = device ?? throw new ArgumentNullException(nameof(device));
            ResetProtocol();
        }

        public void Detach()
        {
            Device = null;
            ResetProtocol();
        }

        public void StartNegotiation()
        {
            ResetProtocol();
            if(Device == null)
            {
                return;
            }

            Transmit(0x00); // SYNC
            SendMessage(0x40, Device.TypeId);
            SendMessage(0x49, (byte)(Device.Modes.Count - 1), (byte)(Device.Modes.Count - 1));
            SendMessage(0x52, EncodeUInt32LittleEndian(BaudRate));
            foreach(var mode in Device.Modes.Reverse())
            {
                var name = new byte[8];
                var source = System.Text.Encoding.ASCII.GetBytes(mode.Name);
                Array.Copy(source, name, Math.Min(source.Length, name.Length));
                SendMessage((byte)(0x98 | (mode.Number & 0x7)), new[] { (byte)0x00 }.Concat(name).ToArray());
                SendMessage((byte)(0xa0 | (mode.Number & 0x7)), 0x80,
                    mode.Values, (byte)mode.DataType, 3, 0);
            }
            Transmit(0x04); // ACK: device information is complete.
            State = Lpf2PortState.WaitingForAck;
        }

        public void WriteChar(byte value)
        {
            if(Device == null)
            {
                return;
            }
            if(value == 0x04 && receiveBuffer.Count == 0)
            {
                State = Lpf2PortState.Streaming;
                SendCurrentData();
                return;
            }
            if(value == 0x02 && receiveBuffer.Count == 0)
            {
                if(State == Lpf2PortState.Streaming)
                {
                    SendCurrentData();
                }
                return;
            }

            receiveBuffer.Add(value);
            if(receiveBuffer.Count > MaximumFrameLength)
            {
                RejectFrame();
                return;
            }
            if(receiveBuffer.Count == 1)
            {
                expectedLength = FrameLength(value);
                if(expectedLength > MaximumFrameLength)
                {
                    RejectFrame();
                    return;
                }
            }
            if(receiveBuffer.Count < expectedLength)
            {
                return;
            }

            var frame = receiveBuffer.ToArray();
            receiveBuffer.Clear();
            expectedLength = 0;
            if(!HasValidChecksum(frame))
            {
                RejectFrame();
                return;
            }
            HandleFrame(frame);
        }

        public void Advance(uint milliseconds)
        {
            Device?.Advance(milliseconds);
            if(State == Lpf2PortState.Streaming)
            {
                SendCurrentData();
            }
        }

        public void Reset()
        {
            Device?.Reset();
            ResetProtocol();
        }

        public ILpf2Device Device { get; private set; }
        public Lpf2PortState State { get; private set; }
        public byte SelectedMode { get; private set; }
        public ulong TransmittedFrames { get; private set; }
        public ulong ReceivedFrames { get; private set; }
        public ulong InvalidFrames { get; private set; }

        public Bits StopBits => Bits.One;
        public Parity ParityBit => Parity.None;
        public uint BaudRate => 115200;

        [field: Transient]
        public event Action<byte> CharReceived;

        private void HandleFrame(byte[] frame)
        {
            ReceivedFrames++;
            var kind = frame[0] & 0xc0;
            var mode = (byte)(frame[0] & 0x7);
            if(kind == 0x40 && (frame[0] & 0x7) == 0x3 && frame.Length >= 3)
            {
                if(Device.Modes.Any(x => x.Number == frame[1]))
                {
                    SelectedMode = frame[1];
                    SendCurrentData();
                    return;
                }
                RejectFrame();
                return;
            }
            if(kind == 0x40 && (frame[0] & 0x7) == 0x4)
            {
                Device.AcceptOutput(SelectedMode, frame.Skip(1).Take(frame.Length - 2).ToArray());
                return;
            }
            if(kind == 0xc0)
            {
                if(!Device.Modes.Any(x => x.Number == mode))
                {
                    RejectFrame();
                    return;
                }
                Device.AcceptOutput(mode, frame.Skip(1).Take(frame.Length - 2).ToArray());
            }
        }

        private void SendCurrentData()
        {
            if(Device == null)
            {
                return;
            }
            var payload = Device.ReadMode(SelectedMode);
            var paddedLength = NextPowerOfTwo(payload.Length);
            if(paddedLength > 32)
            {
                throw new InvalidOperationException("LPF2 payload exceeds the supported 32-byte frame");
            }
            var padded = new byte[paddedLength];
            Array.Copy(payload, padded, payload.Length);
            var sizeCode = (byte)Math.Log(paddedLength, 2);
            SendMessage((byte)(0xc0 | (sizeCode << 3) | (SelectedMode & 0x7)), padded);
        }

        private void SendMessage(byte header, params byte[] payload)
        {
            var checksum = (byte)(0xff ^ header);
            Transmit(header);
            foreach(var value in payload)
            {
                checksum ^= value;
                Transmit(value);
            }
            Transmit(checksum);
            TransmittedFrames++;
        }

        private void Transmit(byte value)
        {
            CharReceived?.Invoke(value);
        }

        private void ResetProtocol()
        {
            receiveBuffer.Clear();
            State = Device == null ? Lpf2PortState.Detached : Lpf2PortState.Attached;
            SelectedMode = 0;
            TransmittedFrames = 0;
            ReceivedFrames = 0;
            InvalidFrames = 0;
        }

        private static int FrameLength(byte header)
        {
            if(header < 0x40)
            {
                return 1;
            }
            var payloadLength = 1 << ((header & 0x38) >> 3);
            return payloadLength + 2 + ((header & 0xc0) == 0x80 ? 1 : 0);
        }

        private static bool HasValidChecksum(byte[] frame)
        {
            return frame.Aggregate((byte)0, (checksum, value) => (byte)(checksum ^ value)) == 0xff;
        }

        private static int NextPowerOfTwo(int value)
        {
            var result = 1;
            while(result < Math.Max(value, 1))
            {
                result <<= 1;
            }
            return result;
        }

        private static byte[] EncodeUInt32LittleEndian(uint value)
        {
            return new[]
            {
                (byte)value,
                (byte)(value >> 8),
                (byte)(value >> 16),
                (byte)(value >> 24),
            };
        }

        private void RejectFrame()
        {
            receiveBuffer.Clear();
            expectedLength = 0;
            InvalidFrames++;
            Transmit(0x02);
        }

        private const int MaximumFrameLength = 35;
        private readonly List<byte> receiveBuffer;
        private int expectedLength;
    }

    public enum Lpf2PortState
    {
        Detached,
        Attached,
        WaitingForAck,
        Streaming,
    }
}
