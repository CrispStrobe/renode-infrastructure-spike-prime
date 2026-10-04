//
// Copyright (c) 2026 CrispStrobe
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

using Antmicro.Renode.UI;

using NUnit.Framework;

namespace Antmicro.Renode.UnitTests
{
    [TestFixture]
    public class ConsoleIOSourceTests
    {
        [Test]
        public void ShouldReturnAtEmptyEofWithoutEmittingIt()
        {
            var received = new List<int>();
            var reader = new BoundedReader("");

            ReadRedirectedInput(reader, () => received.Add, UnexpectedWait);

            Assert.AreEqual(1, reader.ReadCount);
            Assert.IsEmpty(received);
        }

        [Test]
        public void ShouldDeliverFiniteCharactersOnceAndStopAtEof()
        {
            var received = new List<int>();
            var reader = new BoundedReader("A\0B");

            ReadRedirectedInput(reader, () => received.Add, UnexpectedWait);

            CollectionAssert.AreEqual(new[] { 65, 0, 66 }, received);
            Assert.AreEqual(4, reader.ReadCount);
        }

        [Test]
        public void ShouldWaitWithoutConsumingInputWithNoSubscriber()
        {
            var reader = new BoundedReader("AB");

            var waits = 0;
            var error = Assert.Throws<TargetInvocationException>(() =>
                ReadRedirectedInput(reader, () => null, () =>
                {
                    ++waits;
                    throw new FixtureStopException();
                }));

            Assert.IsInstanceOf<FixtureStopException>(error.InnerException);
            Assert.AreEqual(1, waits);
            Assert.AreEqual(0, reader.ReadCount);
        }

        [Test]
        public void ShouldObserveALateLiveSubscriber()
        {
            var received = new List<int>();
            Action<int> subscriber = null;
            var reader = new BoundedReader("AB");
            var waits = 0;

            ReadRedirectedInput(reader, () => subscriber, () =>
            {
                Assert.AreEqual(0, reader.ReadCount);
                ++waits;
                subscriber = received.Add;
            });

            CollectionAssert.AreEqual(new[] { 65, 66 }, received);
            Assert.AreEqual(1, waits);
            Assert.AreEqual(3, reader.ReadCount);
        }

        [Test]
        public void ShouldObserveSubscriberChangesBetweenReads()
        {
            var first = new List<int>();
            var later = new List<int>();
            Action<int> subscriber = null;
            subscriber = value =>
            {
                first.Add(value);
                subscriber = later.Add;
            };
            var reader = new BoundedReader("AB");

            ReadRedirectedInput(reader, () => subscriber, UnexpectedWait);

            CollectionAssert.AreEqual(new[] { 65 }, first);
            CollectionAssert.AreEqual(new[] { 66 }, later);
            Assert.AreEqual(3, reader.ReadCount);
        }

        private static void UnexpectedWait()
        {
            Assert.Fail("An attached subscriber should not require waiting");
        }

        [Test]
        public void ShouldDeliverReadToTheSubscriberCapturedBeforeReading()
        {
            var first = new List<int>();
            var later = new List<int>();
            Action<int> subscriber = first.Add;
            var reader = new BoundedReader("AB");
            reader.BeforeRead = () =>
            {
                subscriber = later.Add;
                reader.BeforeRead = null;
            };

            ReadRedirectedInput(reader, () => subscriber, UnexpectedWait);

            CollectionAssert.AreEqual(new[] { 65 }, first);
            CollectionAssert.AreEqual(new[] { 66 }, later);
        }

        private static void ReadRedirectedInput(TextReader reader,
            Func<Action<int>> getConsumer, Action waitForSubscriber)
        {
            var method = typeof(ConsoleIOSource).GetMethod("ReadRedirectedInput",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(method);
            method.Invoke(null, new object[] { reader, getConsumer, waitForSubscriber });
        }

        private sealed class FixtureStopException : Exception
        {
        }

        private sealed class BoundedReader : TextReader
        {
            public BoundedReader(string input)
            {
                this.input = input;
            }

            public override int Read()
            {
                ++ReadCount;
                if(ReadCount > input.Length + 1)
                {
                    throw new InvalidOperationException("Input was read again after EOF");
                }
                BeforeRead?.Invoke();
                return ReadCount <= input.Length ? input[ReadCount - 1] : -1;
            }

            public int ReadCount { get; private set; }

            public Action BeforeRead { get; set; }

            private readonly string input;
        }
    }
}
