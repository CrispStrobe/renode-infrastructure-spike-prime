// SPDX-License-Identifier: BSD-3-Clause
// Copyright (c) 2026 Brickwright contributors
using System;
using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure;
using Antmicro.Renode.Peripherals;
using Antmicro.Renode.Peripherals.Analog;
using Antmicro.Renode.Peripherals.Bus;
using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    [TestFixture]
    [NonParallelizable]
    public class STM32ADCDMATests
    {
        // NDTR deliberately differs from the three-rank ADC sequence: the
        // terminal condition belongs to DMA, including across ADC sequences.
        [TestCase(1, false, false)]
        [TestCase(1, false, true)]
        [TestCase(1, true, false)]
        [TestCase(1, true, true)]
        [TestCase(2, false, false)]
        [TestCase(2, false, true)]
        [TestCase(2, true, false)]
        [TestCase(2, true, true)]
        [TestCase(4, false, false)]
        [TestCase(4, false, true)]
        [TestCase(4, true, false)]
        [TestCase(4, true, true)]
        public void ShouldSuppressDDSZeroOnlyAfterActualBufferCompletion(int count, bool eachConversion, bool interrupt)
        {
            using(var rig = new STM32ADCDMATestRig())
            {
                var completions = ConnectCompletion(rig);
                rig.ConfigureDMA((uint)count, interrupt);
                rig.StartADC(false, eachConversion);
                rig.Step(count);
                for(var i = 0; i < count; i++) Assert.AreEqual(((i % 3) + 1) * 0x111, rig.ReadSample(i));
                Assert.AreEqual(count, rig.Requests);
                Assert.AreEqual(1, completions());
                Assert.AreEqual(0u, rig.DMA.ReadDoubleWord(0x14));
                Assert.AreEqual(0u, rig.DMA.ReadDoubleWord(0x10) & 1u);
                Assert.AreEqual(interrupt, rig.DMA.Connections[0].IsSet);
                rig.Step(6);
                Assert.AreEqual(count, rig.Requests, "Subsequent conversions must not issue requests");
                Assert.AreEqual(1, completions());
                Assert.IsFalse(rig.DMA.TransferComplete0.IsSet);
                Assert.AreEqual(8, rig.DMA.Connections.Count, "Existing IRQ dictionary remains unchanged");
            }
        }

        [Test]
        public void ShouldRequireADCDMARisingEnableToRearm()
        {
            using(var rig = new STM32ADCDMATestRig())
            {
                var completions = ConnectCompletion(rig);
                rig.ConfigureDMA(1);
                rig.StartADC(false, false);
                rig.Step();
                rig.ConfigureDMA(2, memoryOffset: 0x100);
                // Stream enable, a repeated ADC DMA=1 write, DDS toggles,
                // ADON toggles and EOC/DR reads must not rearm that latch.
                rig.ADC.WriteDoubleWord(8, rig.Control);
                rig.ADC.WriteDoubleWord(8, rig.Control | (1u << 9));
                rig.Step();
                rig.ADC.WriteDoubleWord(8, rig.Control);
                rig.ADC.WriteDoubleWord(8, rig.Control & ~1u);
                rig.ADC.WriteDoubleWord(8, rig.Control);
                rig.ADC.ReadDoubleWord(0x4c);
                rig.Step();
                Assert.AreEqual(1, rig.Requests);
                Assert.AreEqual(0xaaaa, rig.ReadSample(0, 0x100));
                Assert.AreEqual(2u, rig.DMA.ReadDoubleWord(0x14));
                rig.RearmADC();
                rig.Step(2);
                Assert.AreEqual(3, rig.Requests);
                Assert.AreEqual(2, completions());
                Assert.AreEqual(0x111, rig.ReadSample(0, 0x100));
                Assert.AreEqual(0x222, rig.ReadSample(1, 0x100));
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ShouldContinueDDSOneCircularTransfersDespiteLatchedTCIF(bool interrupt)
        {
            using(var rig = new STM32ADCDMATestRig())
            {
                var completions = ConnectCompletion(rig);
                rig.ConfigureDMA(2, interrupt, circular: true);
                rig.StartADC(true, false);
                rig.Step(7);
                Assert.AreEqual(7, rig.Requests);
                Assert.AreEqual(3, completions());
                Assert.AreEqual(1u, rig.DMA.ReadDoubleWord(0x14));
                Assert.AreEqual(1u, rig.DMA.ReadDoubleWord(0x10) & 1u);
                Assert.AreEqual(1u << 5, rig.DMA.ReadDoubleWord(0) & (1u << 5));
                Assert.AreEqual(interrupt, rig.DMA.Connections[0].IsSet);
                Assert.AreEqual(0x111, rig.ReadSample(0));
                Assert.AreEqual(0x333, rig.ReadSample(1));
            }
        }

        [Test]
        public void ShouldSuppressDDSZeroAtCircularBufferBoundary()
        {
            using(var rig = new STM32ADCDMATestRig())
            {
                var completions = ConnectCompletion(rig);
                rig.ConfigureDMA(2, circular: true);
                rig.StartADC(false, true);
                rig.Step(5);
                Assert.AreEqual(2, rig.Requests);
                Assert.AreEqual(1, completions());
                Assert.AreEqual(2u, rig.DMA.ReadDoubleWord(0x14));
                Assert.AreEqual(1u, rig.DMA.ReadDoubleWord(0x10) & 1u);
                Assert.AreEqual(0x111, rig.ReadSample(0));
                Assert.AreEqual(0x222, rig.ReadSample(1));
            }
        }

        [Test]
        public void ShouldIgnoreUnrelatedStreamCompletion()
        {
            using(var rig = new STM32ADCDMATestRig())
            {
                var completions = ConnectCompletion(rig);
                var other = 0;
                rig.DMA.TransferComplete1.AddStateChangedHook(value => { if(value) other++; });
                rig.ConfigureDMA(2);
                rig.StartADC(false, false);
                // Stream1 really copies data from ADC_DR, but its completion
                // signal is not connected to this ADC's completion input.
                rig.ConfigureDMA(1, memoryOffset: 0x100, stream: 1);
                rig.DMA.OnGPIO(1, true);
                Assert.AreEqual(1, other);
                rig.Step(2);
                Assert.AreEqual(2, rig.Requests);
                Assert.AreEqual(1, completions());
                Assert.AreEqual(0x111, rig.ReadSample(0));
                Assert.AreEqual(0x222, rig.ReadSample(1));
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ShouldIgnoreReusedStreamZeroCompletionOutsideADCRequest(bool memoryToMemory)
        {
            using(var rig = new STM32ADCDMATestRig())
            {
                var completions = ConnectCompletion(rig);
                rig.StartADC(false, false);
                var source = new InterruptingDestination(() => {});
                source.WriteWord(0, 0x444);
                rig.Machine.SystemBus.Register(source, new BusRangeRegistration(0x20002000, 2));
                rig.DMA.WriteDoubleWord(0x18, memoryToMemory ? (uint)STM32ADCDMATestRig.MemoryAddress : 0x20002000);
                rig.DMA.WriteDoubleWord(0x1c, (uint)STM32ADCDMATestRig.MemoryAddress + 0x100);
                rig.DMA.WriteDoubleWord(0x14, 1);
                rig.DMA.WriteDoubleWord(0x10, 1u | (1u << 11) | (1u << 13) | (memoryToMemory ? 2u << 6 : 0));
                if(!memoryToMemory) rig.DMA.OnGPIO(0, true);
                Assert.AreEqual(1, completions(), "Same stream completes for an unrelated producer");
                Assert.AreEqual(memoryToMemory ? 0xaaaa : 0x444, rig.ReadSample(0, 0x100));
                Assert.AreEqual(0, rig.Requests);
                rig.ConfigureDMA(2);
                rig.Step(2);
                Assert.AreEqual(2, rig.Requests, "An out-of-band completion must not suppress ADC");
                Assert.AreEqual(2, completions());
                Assert.AreEqual(0x111, rig.ReadSample(0));
                Assert.AreEqual(0x222, rig.ReadSample(1));
            }
        }

        [Test]
        public void ShouldNotAcknowledgePrematureDisableOrInvalidConfiguration()
        {
            using(var rig = new STM32ADCDMATestRig())
            {
                var completions = ConnectCompletion(rig);
                rig.ConfigureDMA(3);
                rig.StartADC(false, false);
                rig.Step();
                rig.DMA.WriteDoubleWord(0x10, 0);
                rig.Step();
                Assert.AreEqual(0, completions());
                Assert.AreEqual(2, rig.Requests);
                // Reserved DIR must not produce a terminal acknowledgement.
                rig.DMA.WriteDoubleWord(0x14, 1);
                rig.DMA.WriteDoubleWord(0x10, 1u | (3u << 6));
                rig.Step();
                Assert.AreEqual(0, completions());
                Assert.AreEqual(3, rig.Requests);
                // Fix the recovery destination address, independently of the
                // existing model's retained pointer after manual abort.
                rig.ConfigureDMA(1, memoryOffset: 0x100, memoryIncrement: false);
                rig.Step();
                Assert.AreEqual(1, completions());
                Assert.AreEqual(0x111, rig.ReadSample(0, 0x100));
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ShouldNotAcknowledgeDisableOrResetDuringFinalMemoryWrite(bool reset)
        {
            using(var rig = new STM32ADCDMATestRig())
            {
                var completions = ConnectCompletion(rig);
                var destination = new InterruptingDestination(() =>
                {
                    if(reset) rig.DMA.Reset();
                    else rig.DMA.WriteDoubleWord(0x10, 0);
                });
                rig.Machine.SystemBus.Register(destination, new BusRangeRegistration(0x20001000, 2));
                rig.ConfigureDMA(1);
                rig.DMA.WriteDoubleWord(0x1c, 0x20001000);
                rig.StartADC(false, false);
                rig.Step();
                Assert.AreEqual(0x111, destination.Value);
                Assert.AreEqual(0, completions());
                Assert.IsFalse(rig.DMA.TransferComplete0.IsSet);
                Assert.IsFalse(rig.DMA.Connections[0].IsSet);
                // Fix the recovery destination address, independently of the
                // existing model's retained pointer after manual abort.
                rig.ConfigureDMA(1, memoryOffset: 0x100, memoryIncrement: false);
                rig.Step();
                Assert.AreEqual(1, completions());
                Assert.AreEqual(0x222, rig.ReadSample(0, 0x100));
            }
        }

        [Test]
        public void ShouldNotifyPeripheralBeforeSynchronousIRQRearm()
        {
            using(var rig = new STM32ADCDMATestRig())
            {
                var completions = ConnectCompletion(rig);
                var irqs = 0;
                ((GPIO)rig.DMA.Connections[0]).AddStateChangedHook(value =>
                {
                    if(!value) return;
                    irqs++;
                    if(irqs != 1) return;
                    rig.DMA.WriteDoubleWord(8, 1u << 5);
                    rig.ConfigureDMA(1, interrupt: true, memoryOffset: 0x100);
                    rig.RearmADC();
                });
                rig.ConfigureDMA(1, interrupt: true);
                rig.StartADC(false, true);
                rig.Step(3);
                Assert.AreEqual(2, irqs);
                Assert.AreEqual(2, completions());
                Assert.AreEqual(2, rig.Requests);
                Assert.AreEqual(0x111, rig.ReadSample(0));
                Assert.AreEqual(0x222, rig.ReadSample(0, 0x100));
            }
        }

        [Test]
        public void ShouldReleaseADCRequestWhenListenerThrows()
        {
            using(var rig = new STM32ADCDMATestRig())
            {
                rig.ConfigureDMA(2);
                rig.ADC.DMARequest.AddStateChangedHook(value =>
                {
                    if(value) throw new InvalidOperationException("synthetic request listener failure");
                });
                rig.StartADC(false, false);
                var threw = false;
                try { rig.Step(); }
                catch(InvalidOperationException) { threw = true; }
                Assert.AreEqual(true, threw);
                Assert.AreEqual(false, rig.ADC.DMARequest.IsSet, "Request ownership must end on exceptional dispatch");
                Assert.AreEqual(0x111, rig.ReadSample(0));
            }
        }

        [Test]
        public void ShouldRearmOnADCResetAndDeassertDMACompletionOnDMAReset()
        {
            using(var rig = new STM32ADCDMATestRig())
            {
                var completions = ConnectCompletion(rig);
                rig.ConfigureDMA(1);
                rig.StartADC(false, false);
                rig.Step();
                rig.DMA.Reset();
                // Fix the recovery destination address, independently of the
                // existing model's retained pointer after manual abort.
                rig.ConfigureDMA(1, memoryOffset: 0x100, memoryIncrement: false);
                rig.Step();
                Assert.AreEqual(1, rig.Requests, "DMA reset alone must not rearm ADC");
                rig.ADC.Reset();
                rig.StartADC(false, false);
                rig.Step();
                Assert.AreEqual(2, rig.Requests);
                Assert.AreEqual(2, completions());
                Assert.AreEqual(0x111, rig.ReadSample(0, 0x100));
                var outputs = new[] { rig.DMA.TransferComplete0, rig.DMA.TransferComplete1,
                    rig.DMA.TransferComplete2, rig.DMA.TransferComplete3, rig.DMA.TransferComplete4,
                    rig.DMA.TransferComplete5, rig.DMA.TransferComplete6, rig.DMA.TransferComplete7 };
                foreach(var output in outputs) output.Set();
                rig.DMA.Reset();
                foreach(var output in outputs) Assert.IsFalse(output.IsSet);
            }
        }

        private static Func<int> ConnectCompletion(STM32ADCDMATestRig rig)
        {
            var count = 0;
            rig.DMA.TransferComplete0.Connect(rig.ADC, STM32_ADC.DmaTransferCompleteInput);
            rig.DMA.TransferComplete0.AddStateChangedHook(value => { if(value) count++; });
            return () => count;
        }

        private sealed class InterruptingDestination : IWordPeripheral, IKnownSize
        {
            public InterruptingDestination(Action interrupt) { this.interrupt = interrupt; }
            public ushort ReadWord(long offset) { return Value; }
            public void WriteWord(long offset, ushort value)
            {
                Value = value;
                interrupt();
            }
            public void Reset() { Value = 0; }
            public long Size => 2;
            public ushort Value { get; private set; }
            private readonly Action interrupt;
        }
    }
}
