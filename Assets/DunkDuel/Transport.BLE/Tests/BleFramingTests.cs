using System;
using NUnit.Framework;

namespace DunkDuel.Transport.BLE.Tests
{
    public sealed class BleFramingTests
    {
        private static byte[] Message(int length, int salt)
        {
            var m = new byte[length];
            for (int i = 0; i < length; i++) m[i] = (byte)(i * 31 + salt);
            if (m[0] >= BleProtocol.HeartbeatPing) m[0] = 1;
            return m;
        }

        [TestCase(20)]
        [TestCase(23)]
        [TestCase(182)]
        [TestCase(512)]
        public void EveryLengthRoundTrips(int payload)
        {
            var reassembler = new BleReassembler();
            var frag = new byte[payload];
            byte seq = 0;
            for (int len = 1; len <= BleProtocol.MaxControlMessage; len += len < 80 ? 1 : 29)
            {
                byte[] msg = Message(len, len);
                int n = BleFraming.FragmentCount(len, payload);
                int completed = -2;
                for (int i = 0; i < n; i++)
                {
                    int fl = BleFraming.WriteFragment(msg, 0, len, payload, i, seq, frag);
                    Assert.LessOrEqual(fl, payload, "fragment exceeds the packet");
                    seq = BleFraming.NextSeq(seq);
                    int r = reassembler.Accept(frag, 0, fl);
                    if (i < n - 1) Assert.AreEqual(0, r, "completed early at fragment " + i);
                    else completed = r;
                }
                Assert.AreEqual(len, completed, "length " + len);
                for (int k = 0; k < len; k++) Assert.AreEqual(msg[k], reassembler.Message[k], "byte " + k + " of " + len);
            }
            Assert.AreEqual(0, reassembler.Errors);
        }

        [Test]
        public void OffsetIntoSourceBuffer()
        {
            var big = new byte[300];
            for (int i = 0; i < big.Length; i++) big[i] = (byte)i;
            var r = new BleReassembler();
            var frag = new byte[20];
            int n = BleFraming.FragmentCount(100, 20);
            int done = 0;
            for (int i = 0; i < n; i++)
            {
                int fl = BleFraming.WriteFragment(big, 50, 100, 20, i, (byte)i, frag);
                done = r.Accept(frag, 0, fl);
            }
            Assert.AreEqual(100, done);
            for (int k = 0; k < 100; k++) Assert.AreEqual((byte)(50 + k), r.Message[k]);
        }

        [Test]
        public void SequenceGapIsRejectedAndNextMessageResyncs()
        {
            var r = new BleReassembler();
            var frag = new byte[20];
            byte[] msg = Message(60, 3);
            Assert.AreEqual(0, r.Accept(frag, 0, BleFraming.WriteFragment(msg, 0, 60, 20, 0, 0, frag)));
            // Skip fragment 1 (sequence 1) and deliver fragment 2 with sequence 2.
            Assert.AreEqual(-1, r.Accept(frag, 0, BleFraming.WriteFragment(msg, 0, 60, 20, 2, 2, frag)));
            Assert.AreEqual(1, r.Errors);
            byte[] next = Message(10, 9);
            Assert.AreEqual(10, r.Accept(frag, 0, BleFraming.WriteFragment(next, 0, 10, 20, 0, 3, frag)));
        }

        [Test]
        public void OrphanContinuationIsRejected()
        {
            var r = new BleReassembler();
            var frag = new byte[20];
            byte[] msg = Message(60, 1);
            int fl = BleFraming.WriteFragment(msg, 0, 60, 20, 1, 5, frag);
            Assert.AreEqual(-1, r.Accept(frag, 0, fl));
            Assert.AreEqual(1, r.Errors);
        }

        [Test]
        public void AbandonedMessageIsCountedAndReplaced()
        {
            var r = new BleReassembler();
            var frag = new byte[20];
            byte[] first = Message(60, 1);
            byte[] second = Message(12, 2);
            r.Accept(frag, 0, BleFraming.WriteFragment(first, 0, 60, 20, 0, 0, frag));
            int done = r.Accept(frag, 0, BleFraming.WriteFragment(second, 0, 12, 20, 0, 1, frag));
            Assert.AreEqual(12, done);
            Assert.AreEqual(1, r.Errors);
        }

        [Test]
        public void SequenceWrapsModulo64()
        {
            var r = new BleReassembler();
            var frag = new byte[20];
            byte seq = 60;
            byte[] msg = Message(200, 7);
            int n = BleFraming.FragmentCount(200, 20);
            int done = 0;
            for (int i = 0; i < n; i++)
            {
                done = r.Accept(frag, 0, BleFraming.WriteFragment(msg, 0, 200, 20, i, seq, frag));
                seq = BleFraming.NextSeq(seq);
            }
            Assert.AreEqual(200, done);
            Assert.AreEqual(0, r.Errors);
        }

        [Test]
        public void LimitsAreEnforced()
        {
            var frag = new byte[512];
            Assert.Throws<ArgumentOutOfRangeException>(() => BleFraming.FragmentCount(BleProtocol.MaxControlMessage + 1, 20));
            Assert.Throws<ArgumentOutOfRangeException>(() => BleFraming.FragmentCount(0, 20));
            Assert.Throws<ArgumentOutOfRangeException>(() => BleFraming.FragmentCount(10, 3));
            Assert.Throws<ArgumentException>(() => BleFraming.WriteFragment(new byte[10], 0, 10, 20, 0, 0, new byte[5]));
            var r = new BleReassembler();
            // A START fragment that claims more than the maximum message size.
            frag[0] = BleFraming.FlagStart | BleFraming.FlagEnd;
            frag[1] = 0xFF;
            frag[2] = 0xFF;
            Assert.AreEqual(-1, r.Accept(frag, 0, 10));
        }

        [Test]
        public void SingleFragmentOverheadIsThreeBytes()
        {
            Assert.AreEqual(1, BleFraming.FragmentCount(17, 20));
            Assert.AreEqual(2, BleFraming.FragmentCount(18, 20));
            Assert.AreEqual(1, BleFraming.FragmentCount(BleHeartbeat.PingBytes, 20), "a heartbeat fits one default packet");
        }
    }
}
