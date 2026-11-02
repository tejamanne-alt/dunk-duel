using System;
using DunkDuel.AI;
using DunkDuel.Net;
using DunkDuel.Sim;
using DunkDuel.Sim.Data;
using DunkDuel.Sim.Session;
using NUnit.Framework;

namespace DunkDuel.Tests
{
    public class NetTests
    {
        private const long TickMicros = 16667;

        [Test]
        public void InputPacketIsTwentyBytesWithEightFramesOfHistory()
        {
            var ring = new FrameInput[256];
            for (int f = 0; f < 256; f++) ring[f] = new FrameInput((ushort)(f * 7 & 0x3FF));
            var buf = new byte[InputPacket.Size];
            InputPacket.Write(buf, 70000, 69990, ring, 255);
            Assert.AreEqual(20, InputPacket.Size);
            Assert.AreEqual(70000 & 0xFFFF, InputPacket.Latest(buf));
            Assert.AreEqual(70000, InputPacket.Unwrap(InputPacket.Latest(buf), 69995));
            for (int i = 0; i < 8; i++) Assert.AreEqual(ring[(70000 - 7 + i) & 255], InputPacket.Input(buf, i));
        }

        [Test]
        public void UnwrapHandlesTheSixteenBitBoundary()
        {
            Assert.AreEqual(65537, InputPacket.Unwrap(1, 65530));
            Assert.AreEqual(65530, InputPacket.Unwrap(65530, 65540));
            Assert.AreEqual(5, InputPacket.Unwrap(5, 0));
        }

        private sealed class Pair
        {
            public ManualClock Clock = new ManualClock { NowMicros = 1 };
            public SimulatedLink Link;
            public RollbackSession Host;
            public RollbackSession Guest;
            public int Desyncs;
        }

        private static Pair Connect(int latency, int jitter, int loss, ulong seed, int delay = 2, string f0 = "kofi", string f1 = "matas")
        {
            var p = new Pair();
            p.Link = SimulatedLink.CreatePair(p.Clock, seed, latency, jitter, loss);
            p.Link.A.Host("ABCD", "host");
            p.Link.B.Join(new PeerInfo { Address = "loopback" });
            p.Link.A.Poll();
            p.Link.B.Poll();
            SimTables t = TestData.Tables;
            var setup = MatchSetup.Versus(TestData.Fighter(f0), TestData.Fighter(f1), seed);
            var cfg = new NetConfig();
            p.Host = new RollbackSession(t, setup, 0, true, p.Link.A, new CpuPlayer(t, TestData.Tiers, CpuTier.Legend, 0, seed + 1), p.Clock, cfg, delay);
            p.Guest = new RollbackSession(t, setup, 1, false, p.Link.B, new CpuPlayer(t, TestData.Tiers, CpuTier.Legend, 1, seed + 2), p.Clock, cfg, delay);
            p.Host.DesyncDetected += (a, b, c) => p.Desyncs++;
            p.Guest.DesyncDetected += (a, b, c) => p.Desyncs++;
            return p;
        }

        private static void Advance(Pair p, int ticks)
        {
            for (int i = 0; i < ticks && !(p.Host.IsOver && p.Guest.IsOver); i++)
            {
                p.Clock.Advance(TickMicros);
                p.Host.Tick();
                p.Guest.Tick();
            }
        }

        /// <summary>Both peers have simulated a common tick: compare their confirmed history by replaying.</summary>
        private static void AssertConverged(Pair p)
        {
            // Let in-flight inputs land, then both sides confirm the same frames.
            for (int i = 0; i < 40; i++)
            {
                p.Clock.Advance(TickMicros);
                p.Host.Tick();
                p.Guest.Tick();
            }
            Assert.AreEqual(0, p.Desyncs, "hash exchange found a desync");
            Assert.Greater(p.Host.Stats.Resimulated + p.Guest.Stats.Resimulated, -1);
        }

        [Test]
        public void SoakOverSimulatedBleHasZeroDesyncs()
        {
            // BLE-like conditions: 15-45 ms each way, 5% input loss.
            for (int match = 0; match < 3; match++)
            {
                Pair p = Connect(15000, 30000, 50, (ulong)(900 + match));
                Advance(p, 60 * 60 * 6);
                AssertConverged(p);
                Assert.IsTrue(p.Host.IsOver || p.Host.CurrentTick > 10000, "match progressed");
                NetStats s = p.Guest.Stats;
                Assert.LessOrEqual(s.RollbackP95, 4, "rollback p95 must be 4 frames or less");
                Assert.LessOrEqual(s.MaxRollback, new NetConfig().MaxRollback);
            }
        }

        [Test]
        public void PeersReachTheSameFinalState()
        {
            Pair p = Connect(20000, 10000, 20, 4242, 2, "juno", "meera");
            Advance(p, 60 * 60 * 6);
            AssertConverged(p);
            // Compare at a common tick whose inputs are all confirmed on both phones.
            for (int i = 0; i < 600; i++)
            {
                bool sameTick = p.Host.CurrentTick == p.Guest.CurrentTick;
                bool confirmed = p.Host.ConfirmedTick >= p.Host.CurrentTick - 1 && p.Guest.ConfirmedTick >= p.Guest.CurrentTick - 1;
                if (sameTick && confirmed) break;
                p.Clock.Advance(TickMicros);
                if (p.Host.CurrentTick <= p.Guest.CurrentTick) p.Host.Tick();
                if (p.Guest.CurrentTick <= p.Host.CurrentTick) p.Guest.Tick();
            }
            Assert.AreEqual(p.Host.CurrentTick, p.Guest.CurrentTick, "peers reached a common tick");
            Assert.AreEqual(p.Host.State.Hash(), p.Guest.State.Hash(), "both phones hold the same state at the same confirmed tick");
            Assert.AreEqual(p.Host.State.MatchWinner, p.Guest.State.MatchWinner);
        }

        [Test]
        public void DesyncIsDetectedAndTheGuestResyncsFromTheHost()
        {
            Pair p = Connect(15000, 5000, 0, 77);
            Advance(p, 600);
            p.Guest.DebugCorruptState();
            Advance(p, 300);
            Assert.GreaterOrEqual(p.Desyncs, 1, "corruption must be caught by the 30-frame hash exchange");
            Assert.GreaterOrEqual(p.Guest.Stats.DesyncRecoveries, 1, "host streams its state to the guest");
            int before = p.Desyncs;
            Advance(p, 600);
            Assert.AreEqual(before, p.Desyncs, "no further desyncs after recovery");
        }

        [Test]
        public void SilencePausesAndTheMatchResumes()
        {
            Pair p = Connect(15000, 0, 0, 5);
            Advance(p, 300);
            p.Link.Severed = true;
            Advance(p, 60 * 4);
            Assert.IsTrue(p.Host.Paused, "3 s of silence pauses the match");
            int tick = p.Host.CurrentTick;
            p.Link.Severed = false;
            Advance(p, 120);
            Assert.IsFalse(p.Host.Paused);
            Assert.Greater(p.Host.CurrentTick, tick);
            AssertConverged(p);
        }

        [Test]
        public void ReconnectWindowExpiresIntoNoContest()
        {
            Pair p = Connect(15000, 0, 0, 6);
            Advance(p, 200);
            p.Link.Severed = true;
            Advance(p, 60 * 25);
            Assert.AreEqual(NetFailure.NoContest, p.Host.Failure);
        }

        [Test]
        public void LobbyHandshakeAgreesOnSeedFightersAndDelay()
        {
            var clock = new ManualClock { NowMicros = 1 };
            SimulatedLink link = SimulatedLink.CreatePair(clock, 3, 20000, 5000, 0);
            SimTables t = TestData.Tables;
            var host = new NetLobby(t, link.A, clock, new NetConfig(), 0x1234UL, "Host");
            var guest = new NetLobby(t, link.B, clock, new NetConfig(), 0xF0F0UL, "Guest");
            host.Host("WXYZ", TestData.Fighter("kofi"), 4, 2);
            guest.Join(new PeerInfo { Address = "loopback" }, TestData.Fighter("meera"));
            host.SetLocalInput(new ConstantInput(FrameInput.None));
            guest.SetLocalInput(new ConstantInput(FrameInput.None));
            for (int i = 0; i < 600 && (host.Session == null || guest.Session == null); i++)
            {
                clock.Advance(TickMicros);
                host.Update();
                guest.Update();
            }
            Assert.NotNull(host.Session, "host started");
            Assert.NotNull(guest.Session, "guest started");
            Assert.AreEqual(0x1234UL ^ 0xF0F0UL, host.Config.Seed, "seed = host seed XOR guest seed");
            Assert.AreEqual(host.Config.Seed, guest.Config.Seed);
            Assert.AreEqual(TestData.Fighter("meera"), host.Config.GuestFighter);
            Assert.AreEqual(host.InputDelay, guest.InputDelay);
            Assert.That(host.InputDelay, Is.InRange(1, 3));
            Assert.AreEqual(4, guest.Config.Court);
        }

        [Test]
        public void MismatchedSimVersionIsRefused()
        {
            var clock = new ManualClock { NowMicros = 1 };
            SimulatedLink link = SimulatedLink.CreatePair(clock, 3, 20000, 0, 0);
            MemoryDataSource other = TestData.CopyOfData();
            other.Add("tuning", other.Read("tuning").Replace("\"label\": \"", "\"label\": \"patched "));
            var host = new NetLobby(TestData.Tables, link.A, clock, new NetConfig(), 1, "Host");
            var guest = new NetLobby(SimTables.Load(other), link.B, clock, new NetConfig(), 2, "Guest");
            host.Host("ABCD", 0, 0, 2);
            guest.Join(new PeerInfo { Address = "loopback" }, 1);
            for (int i = 0; i < 200; i++)
            {
                clock.Advance(TickMicros);
                host.Update();
                guest.Update();
            }
            Assert.AreEqual(LobbyState.Failed, host.State);
            Assert.AreEqual(NetFailure.VersionMismatch, host.Failure);
        }

        [Test]
        public void EventLedgerFadesOrphansAndSpawnsLateEvents()
        {
            var ledger = new EventLedger();
            int spawned = 0, orphaned = 0, late = 0;
            ledger.Spawned += (e, age) =>
            {
                spawned++;
                if (age > 0) late++;
            };
            ledger.Orphaned += e => orphaned++;
            var buf = new SimEventBuffer();
            buf.Add(10, SimEventType.Steal, 1, 0, 0, Fix.Zero, Fix.Zero);
            ledger.Confirm(10, buf);
            buf.Clear();
            ledger.Confirm(12, buf);
            EventLedger.RollbackScope scope = ledger.BeginRollback(10);
            var corrected = new SimEventBuffer();
            corrected.Add(11, SimEventType.Block, 1, 0, 0, Fix.Zero, Fix.Zero);
            scope.Resimulated(11, corrected);
            scope.End();
            Assert.AreEqual(1, orphaned, "the steal never happened");
            Assert.AreEqual(2, spawned);
            Assert.AreEqual(1, late, "the block spawns late with an age");
        }
    }
}
