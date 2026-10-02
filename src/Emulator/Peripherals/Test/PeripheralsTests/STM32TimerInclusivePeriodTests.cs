// SPDX-License-Identifier: BSD-3-Clause
// Copyright (c) 2026 Brickwright contributors
using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.Timers;
using Antmicro.Renode.Time;
using NUnit.Framework;
namespace Antmicro.Renode.PeripheralsTests
{
    [TestFixture]
    [NonParallelizable]
    public class STM32TimerInclusivePeriodTests
    {
        [SetUp] public void SetUp() { EmulationManager.Instance.Clear(); machine=new Machine(); EmulationManager.Instance.CurrentEmulation.AddMachine(machine); }
        [TearDown] public void TearDown() { EmulationManager.Instance.Clear(); }
        [TestCase(65535u)] [TestCase(uint.MaxValue)]
        public void ShouldCountThroughArrBeforeWrapping(uint arr)
        {
            var timer=new STM32_Timer(machine,1000,arr);
            timer.WriteDoubleWord(0x2c,arr);timer.WriteDoubleWord(0x24,arr-1);timer.WriteDoubleWord(0x00,1);
            Advance(1);Assert.AreEqual(arr,timer.ReadDoubleWord(0x24),"CNT exposes ARR for one tick");
            Advance(1);Assert.AreEqual(0u,timer.ReadDoubleWord(0x24),"CNT wraps after ARR");
            Advance(1);Assert.AreEqual(1u,timer.ReadDoubleWord(0x24));
            timer.Reset();timer.WriteDoubleWord(0x24,arr-1);timer.WriteDoubleWord(0x00,1);
            Advance(1);Assert.AreEqual(arr,timer.ReadDoubleWord(0x24),"Reset restores inclusive period");
        }
        [TestCase(65535u,false,false)] [TestCase(65535u,false,true)]
        [TestCase(65535u,true,false)] [TestCase(65535u,true,true)]
        [TestCase(uint.MaxValue,false,false)] [TestCase(uint.MaxValue,false,true)]
        [TestCase(uint.MaxValue,true,false)] [TestCase(uint.MaxValue,true,true)]
        public void ShouldDeliverBoundaryCompare(uint arr,bool compareZero,bool updateEnabled)
        {
            var timer=new STM32_Timer(machine,1000,arr);uint compare=compareZero?0:arr;
            var observer=new FlagObserver(timer);timer.IRQ.Connect(observer,0);
            timer.WriteDoubleWord(0x2c,arr);timer.WriteDoubleWord(0x34,compare);
            timer.WriteDoubleWord(0x0c,updateEnabled?3u:2u);timer.WriteDoubleWord(0x24,arr-1);timer.WriteDoubleWord(0x00,1);
            Advance(1);
            Assert.AreEqual(arr,timer.ReadDoubleWord(0x24));
            Assert.AreEqual(compareZero?0u:2u,timer.ReadDoubleWord(0x10)&2u,"ARR compare precedes overflow");
            Assert.AreEqual(0u,timer.ReadDoubleWord(0x10)&1u);
            if(!compareZero)Assert.AreEqual(2u,observer.FirstRiseFlags&3u,"Observer sees CC1 without UIF at ARR");
            Advance(1);Assert.AreEqual(0u,timer.ReadDoubleWord(0x24));
            Assert.AreEqual(2u,timer.ReadDoubleWord(0x10)&2u);Assert.IsTrue(timer.IRQ.IsSet);
            if(updateEnabled)Assert.AreEqual(1u,timer.ReadDoubleWord(0x10)&1u);
            if(compareZero)Assert.AreEqual(updateEnabled?3u:2u,observer.FirstRiseFlags&3u);
        }
        [Test]
        public void ShouldKeepPreloadedArrUntilRolloverAcrossControlWrites()
        {
            var timer=new STM32_Timer(machine,1000,65535);
            timer.WriteDoubleWord(0x2c,9);timer.WriteDoubleWord(0x24,8);timer.WriteDoubleWord(0x00,129);
            timer.WriteDoubleWord(0x2c,19);timer.WriteDoubleWord(0x00,129);
            Assert.AreEqual(10UL,timer.Limit,"CR1 write must not apply shadow ARR");
            Advance(1);Assert.AreEqual(9u,timer.ReadDoubleWord(0x24));
            Advance(1);Assert.AreEqual(0u,timer.ReadDoubleWord(0x24));
            Assert.AreEqual(20UL,timer.Limit,"Rollover transfers preloaded ARR");
        }
        [Test]
        public void ShouldRetainLegacyDescendingAndCenterAlignedPeriods()
        {
            var timer=new STM32_Timer(machine,1000,65535);
            timer.WriteDoubleWord(0x2c,9);Assert.AreEqual(10UL,timer.Limit);
            timer.WriteDoubleWord(0x00,16);Assert.AreEqual(9UL,timer.Limit);
            timer.WriteDoubleWord(0x00,32);Assert.AreEqual(9UL,timer.Limit);
            timer.WriteDoubleWord(0x00,0);Assert.AreEqual(10UL,timer.Limit);
        }
        private sealed class FlagObserver : IGPIOReceiver
        {
            public FlagObserver(STM32_Timer timer) { this.timer=timer; }
            public void Reset() { Seen=false;FirstRiseFlags=0; }
            public void OnGPIO(int number,bool value) { if(value&&!Seen) { Seen=true;FirstRiseFlags=timer.ReadDoubleWord(0x10); } }
            public uint FirstRiseFlags { get; private set; }
            private bool Seen;private readonly STM32_Timer timer;
        }
        private void Advance(int ms) { ((BaseClockSource)machine.ClockSource).Advance(TimeInterval.FromMilliseconds(ms),true); }
        private Machine machine;
    }
}
