//
// Copyright (c) 2010-2026 Antmicro
// Copyright (c) 2011-2015 Realtime Embedded
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System;
using System.Linq;
using System.Threading;

using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.Memory;
using Antmicro.Renode.Utilities;

using NUnit.Framework;

namespace Antmicro.Renode.UnitTests
{
    [TestFixture]
    public class MemoryTests
    {
        [Test]
        public void ShouldReadWriteMemoryBiggerThan2GB()
        {
            const uint memorySize = 3u * 1024 * 1024 * 1024;
            var machine = new Machine();
            var memory = new MappedMemory(machine, memorySize);
            var start = (ulong)100.MB();
            machine.SystemBus.Register(memory, start);
            var offset1 = start + 16;
            var offset2 = start + memorySize - 16;
            machine.SystemBus.WriteByte(offset1, 0x1);
            machine.SystemBus.WriteByte(offset2, 0x2);

            Assert.AreEqual(0x1, machine.SystemBus.ReadByte(offset1));
            Assert.AreEqual(0x2, machine.SystemBus.ReadByte(offset2));
        }

        [Test]
        public void ShouldReturnOnePointerWhenSegmentIsTouchedConcurrently()
        {
            const int threadCount = 64;
            const int attemptCount = 20;
            for(var attempt = 0; attempt < attemptCount; attempt++)
            {
                using(var memory = new MappedMemory(null, 64.KB(), 64.KB()))
                using(var barrier = new Barrier(threadCount + 1))
                {
                    var segment = memory.MappedSegments.Single();
                    var pointers = new IntPtr[threadCount];
                    var segmentTouchedCount = 0;
                    memory.SegmentTouched += _ => Interlocked.Increment(ref segmentTouchedCount);
                    var threads = Enumerable.Range(0, threadCount).Select(index => new Thread(() =>
                    {
                        barrier.SignalAndWait();
                        segment.Touch();
                        pointers[index] = segment.Pointer;
                    })).ToArray();

                    foreach(var thread in threads)
                    {
                        thread.Start();
                    }
                    barrier.SignalAndWait();
                    foreach(var thread in threads)
                    {
                        thread.Join();
                    }

                    Assert.That(segmentTouchedCount, Is.EqualTo(1));
                    Assert.That(pointers.Distinct().Count(), Is.EqualTo(1));
                }
            }
        }
    }
}