using System;
using System.Threading;

namespace DunkDuel.Transport.BLE
{
    /// <summary>
    /// Lock-free single-producer / single-consumer ring of small fixed-size packets. Used for outgoing INPUT
    /// packets so the netcode may run its tick on another thread while the main thread owns the radio bridge.
    /// Exactly one thread may write and exactly one thread may read.
    /// </summary>
    public sealed class SpscRing
    {
        private readonly byte[] _data;
        private readonly int[] _lengths;
        private readonly int _slot;
        private readonly int _mask;
        private int _head; // next slot to write; written only by the producer
        private int _tail; // next slot to read; written only by the consumer

        public SpscRing(int capacityPowerOfTwo, int slotBytes)
        {
            if (capacityPowerOfTwo < 2 || (capacityPowerOfTwo & (capacityPowerOfTwo - 1)) != 0)
                throw new ArgumentException("capacity must be a power of two >= 2", nameof(capacityPowerOfTwo));
            if (slotBytes < 1) throw new ArgumentOutOfRangeException(nameof(slotBytes));
            Capacity = capacityPowerOfTwo;
            _slot = slotBytes;
            _mask = capacityPowerOfTwo - 1;
            _data = new byte[capacityPowerOfTwo * slotBytes];
            _lengths = new int[capacityPowerOfTwo];
        }

        public int Capacity { get; }
        public int SlotBytes => _slot;

        /// <summary>Approximate number of queued packets (exact when called from either endpoint thread).</summary>
        public int Count => unchecked(Volatile.Read(ref _head) - Volatile.Read(ref _tail));

        /// <summary>Producer: copies a packet in. Returns false (and drops it) when the ring is full.</summary>
        public bool TryWrite(byte[] src, int offset, int count)
        {
            if (count < 0 || count > _slot) throw new ArgumentOutOfRangeException(nameof(count));
            int head = _head;
            int tail = Volatile.Read(ref _tail);
            if (unchecked(head - tail) >= Capacity) return false;
            int slot = head & _mask;
            Buffer.BlockCopy(src, offset, _data, slot * _slot, count);
            _lengths[slot] = count;
            Volatile.Write(ref _head, unchecked(head + 1));
            return true;
        }

        /// <summary>Consumer: copies the oldest packet into <paramref name="dst"/>. Returns its length, or -1 when empty.</summary>
        public int TryRead(byte[] dst)
        {
            int tail = _tail;
            int head = Volatile.Read(ref _head);
            if (tail == head) return -1;
            int slot = tail & _mask;
            int len = _lengths[slot];
            Buffer.BlockCopy(_data, slot * _slot, dst, 0, len);
            Volatile.Write(ref _tail, unchecked(tail + 1));
            return len;
        }
    }
}
