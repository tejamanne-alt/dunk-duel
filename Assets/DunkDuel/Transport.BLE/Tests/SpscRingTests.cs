using System;
using System.Threading;
using NUnit.Framework;

namespace DunkDuel.Transport.BLE.Tests
{
    public sealed class SpscRingTests
    {
        [Test]
        public void FifoAndFull()
        {
            var ring = new SpscRing(4, 8);
            var buf = new byte[8];
            for (byte i = 0; i < 4; i++) Assert.IsTrue(ring.TryWrite(new[] { i, (byte)(i + 1) }, 0, 2));
            Assert.IsFalse(ring.TryWrite(new byte[] { 9 }, 0, 1), "full ring refuses");
            Assert.AreEqual(4, ring.Count);
            for (byte i = 0; i < 4; i++)
            {
                Assert.AreEqual(2, ring.TryRead(buf));
                Assert.AreEqual(i, buf[0]);
            }
            Assert.AreEqual(-1, ring.TryRead(buf));
        }

        [Test]
        public void WrapsAroundManyTimes()
        {
            var ring = new SpscRing(8, 4);
            var buf = new byte[4];
            for (int i = 0; i < 10000; i++)
            {
                Assert.IsTrue(ring.TryWrite(BitConverter.GetBytes(i), 0, 4));
                Assert.AreEqual(4, ring.TryRead(buf));
                Assert.AreEqual(i, BitConverter.ToInt32(buf, 0));
            }
        }

        [Test]
        public void RejectsBadConfiguration()
        {
            Assert.Throws<ArgumentException>(() => new SpscRing(6, 8));
            var ring = new SpscRing(2, 4);
            Assert.Throws<ArgumentOutOfRangeException>(() => ring.TryWrite(new byte[8], 0, 8));
        }

        [Test]
        public void ProducerAndConsumerThreadsKeepOrder()
        {
            const int count = 200000;
            var ring = new SpscRing(16, 4);
            bool done = false;
            Exception failure = null;
            var producer = new Thread(() =>
            {
                try
                {
                    var b = new byte[4];
                    for (int i = 0; i < count; i++)
                    {
                        b[0] = (byte)i;
                        b[1] = (byte)(i >> 8);
                        b[2] = (byte)(i >> 16);
                        b[3] = (byte)(i >> 24);
                        while (!ring.TryWrite(b, 0, 4)) Thread.Yield();
                    }
                }
                catch (Exception e)
                {
                    failure = e;
                }
                finally
                {
                    Volatile.Write(ref done, true);
                }
            });
            producer.Start();
            var buf = new byte[4];
            int expected = 0;
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (expected < count && DateTime.UtcNow < deadline)
            {
                if (ring.TryRead(buf) < 0)
                {
                    if (Volatile.Read(ref done) && ring.Count == 0) break;
                    Thread.Yield();
                    continue;
                }
                int v = buf[0] | (buf[1] << 8) | (buf[2] << 16) | (buf[3] << 24);
                Assert.AreEqual(expected, v);
                expected++;
            }
            producer.Join();
            Assert.IsNull(failure);
            Assert.AreEqual(count, expected);
        }
    }
}
