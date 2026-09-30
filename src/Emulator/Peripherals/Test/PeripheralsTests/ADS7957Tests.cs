// Copyright (c) 2026 CrispStrobe
// SPDX-License-Identifier: MIT
using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure;
using Antmicro.Renode.Exceptions;
using Antmicro.Renode.Peripherals.Analog;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.SPI;
using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    [TestFixture]
    public class ADS7957Tests
    {
        [TestCase(0)]
        [TestCase(341)]
        [TestCase(1023)]
        public void ShouldReturnInput1SampleThroughRealSpiOnThirdFrame(int sample)
        {
            using(var machine = new Machine())
            {
                var spi = new TI_DA8xx_SPI(machine, externalChipSelect: false);
                machine.SystemBus.Register(spi, new BusPointRegistration(0x01C41000));
                var adc = new ADS7957();
                adc.SetChannelValue(6, (ushort)sample);
                spi.Register(adc, new NumberRegistrationPoint<int>(3));
                spi.WriteDoubleWord(0x00, 1);
                spi.WriteDoubleWord(0x04, 0x01000003);
                spi.WriteDoubleWord(0x50, 16);
                for(var frame = 0; frame < 3; frame++)
                {
                    spi.WriteDoubleWord(0x3C, 0x00F71B40);
                    var response = spi.ReadDoubleWord(0x40) & 0xFFFF;
                    Assert.AreEqual(frame < 2 ? 0u : (uint)(0x6000 | (sample << 2)), response);
                }
                Assert.IsTrue(adc.TwoTimesReference);
                Assert.AreEqual(3, adc.CompletedFrames);
            }
        }

        [Test]
        public void ShouldBoundSamplesAndPreserveInputsAcrossProtocolReset()
        {
            var adc = new ADS7957();
            Assert.Throws<RecoverableException>(() => adc.SetChannelValue(-1, 0));
            Assert.Throws<RecoverableException>(() => adc.SetChannelValue(16, 0));
            Assert.Throws<RecoverableException>(() => adc.SetChannelValue(0, 1024));
            adc.SetChannelValue(15, 1023);
            Frame(adc, 0x1FC0); Frame(adc, 0); // request last channel, then wait
            Assert.AreEqual(0xFFFC, Frame(adc, 0));
            adc.Reset();
            Assert.AreEqual(1023, adc.GetChannelValue(15));
            Assert.AreEqual(0, adc.CompletedFrames);
            Assert.IsFalse(adc.TwoTimesReference);
        }

        [Test]
        public void ShouldRequireCsFrameBoundaryAndDiscardPartialFrame()
        {
            var adc = new ADS7957();
            adc.Transmit(0x1B);
            adc.FinishTransmission();
            Assert.AreEqual(0, adc.CompletedFrames);
            adc.Transmit(0x1B); adc.Transmit(0x40);
            Assert.AreEqual(0, adc.Transmit(0x12));
            Assert.AreEqual(0, adc.CompletedFrames);
            adc.FinishTransmission();
            Assert.AreEqual(1, adc.CompletedFrames);
            Assert.IsTrue(adc.TwoTimesReference);
            Frame(adc, 0x1300); // DI11 clear leaves range unchanged
            Assert.IsTrue(adc.TwoTimesReference);
            Frame(adc, 0x2000);
            Assert.AreEqual(1, adc.UnsupportedCommands);
        }

        private static ushort Frame(ADS7957 adc, ushort command)
        {
            var response = (ushort)(adc.Transmit((byte)(command >> 8)) << 8);
            response |= adc.Transmit((byte)command);
            adc.FinishTransmission();
            return response;
        }
    }
}
