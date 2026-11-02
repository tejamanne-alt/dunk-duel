using System;
using System.Numerics;
using DunkDuel.AI;
using DunkDuel.Sim;
using DunkDuel.Sim.Data;
using DunkDuel.Sim.Session;
using NUnit.Framework;

namespace DunkDuel.Tests
{
    public class FixTests
    {
        private static long RefMul(long a, long b)
        {
            BigInteger p = (BigInteger)a * b;
            BigInteger q = BigInteger.Abs(p) + (BigInteger.One << 31);
            q >>= 32;
            return (long)(p.Sign < 0 ? -q : q);
        }

        private static long RefDiv(long a, long b)
        {
            BigInteger n = BigInteger.Abs((BigInteger)a) << 32;
            BigInteger d = BigInteger.Abs((BigInteger)b);
            BigInteger q = BigInteger.DivRem(n, d, out BigInteger r);
            if (r * 2 >= d) q += 1;
            bool neg = (a < 0) != (b < 0);
            return (long)(neg ? -q : q);
        }

        [Test]
        public void MultiplyAndDivideMatchExactReference()
        {
            ulong rng = 42;
            for (int i = 0; i < 20000; i++)
            {
                long a = ((long)Rng.NextU32(ref rng) << 16 | Rng.NextU32(ref rng) & 0xFFFF) - (1L << 46);
                long b = ((long)Rng.NextU32(ref rng) << 10 | Rng.NextU32(ref rng) & 0x3FF) - (1L << 40);
                if (i % 3 == 0) b >>= 12;
                Assert.AreEqual(RefMul(a, b), Fix.MulRaw(a, b), "mul " + a + " " + b);
                if (b != 0 && Math.Abs((double)a / b) < 1e9) Assert.AreEqual(RefDiv(a, b), Fix.DivRaw(a, b), "div " + a + " " + b);
            }
        }

        [Test]
        public void ParseAndPrintUseIntegerMath()
        {
            Assert.AreEqual(Fix.FromInt(3) + Fix.Ratio(1, 20), Fix.Parse("3.05"));
            Assert.AreEqual(-Fix.Parse("6.75"), Fix.Parse("-6.75"));
            Assert.AreEqual("-6.7500", Fix.Parse("-6.75").ToString());
            Assert.AreEqual("0.0300", Fix.Parse("0.03").ToString());
            Assert.IsFalse(Fix.TryParse("1e3", out _));
            Assert.IsFalse(Fix.TryParse("1.0000000001", out _), "more than 9 decimals is rejected");
        }

        [Test]
        public void SqrtAndHelpers()
        {
            Assert.AreEqual(Fix.FromInt(3), Fix.Sqrt(Fix.FromInt(9)));
            Fix two = Fix.Sqrt(Fix.Two);
            Assert.LessOrEqual(Fix.Abs(two * two - Fix.Two), Fix.FromRaw(16));
            Assert.AreEqual(Fix.Half, Fix.Ramp(Fix.FromInt(2), Fix.FromInt(1), Fix.FromInt(3)));
            Assert.AreEqual(Fix.One, Fix.Ramp(Fix.FromInt(0), Fix.FromInt(3), Fix.FromInt(1)), "reversed ramp clamps");
            Assert.AreEqual(Fix.FromInt(2), Fix.FromInt(7) / 3 - Fix.Ratio(1, 3), "int division rounds to nearest");
        }

        [Test]
        public void RngIsReproducibleAndRoughlyUniform()
        {
            ulong a = Rng.Seed(7), b = Rng.Seed(7);
            int[] buckets = new int[10];
            for (int i = 0; i < 10000; i++)
            {
                uint x = Rng.NextU32(ref a);
                Assert.AreEqual(x, Rng.NextU32(ref b));
                buckets[Rng.Range(ref a, 0, 10)]++;
                Rng.Range(ref b, 0, 10);
            }
            foreach (int c in buckets) Assert.That(c, Is.InRange(850, 1150));
        }
    }

    public class DataTests
    {
        [Test]
        public void EveryFighterSpendsThirtySixPointsWithOneMastery()
        {
            Assert.AreEqual(4, TestData.Tables.FighterCount);
            foreach (FighterStats f in TestData.Tables.Fighters)
            {
                AttributeBlock a = f.Def.Attributes;
                Assert.AreEqual(36, a.Total, f.Def.Id);
                int nines = 0;
                foreach (int v in new[] { a.Speed, a.Handles, a.Range, a.Finishing, a.Defense, a.Strength }) if (v == 9) nines++;
                Assert.AreEqual(1, nines, f.Def.Id + " single 9 marks the mastery");
            }
        }

        [Test]
        public void HeightsFollowTheRoster()
        {
            string[] ids = { "kofi", "meera", "juno", "matas" };
            string[] heights = { "1.96", "1.76", "1.68", "2.16" };
            for (int i = 0; i < ids.Length; i++)
                Assert.AreEqual(Fix.Parse(heights[i]), TestData.Tables.Fighters[TestData.Fighter(ids[i])].Height);
        }

        [Test]
        public void CourtGeometryFollowsTheSpec()
        {
            CourtTable c = TestData.Tables.T.Court;
            Assert.AreEqual(Fix.Zero, c.RimX);
            Assert.AreEqual(Fix.Parse("3.05"), c.RimY);
            Assert.AreEqual(Fix.Parse("0.38"), c.BackboardX);
            Assert.AreEqual(Fix.Parse("1.6"), c.BaselineX);
            Assert.AreEqual(Fix.Parse("-6.75"), c.ArcX);
            Assert.AreEqual(Fix.Parse("-7.6"), c.CheckX);
            Assert.AreEqual(Fix.Parse("-10.5"), c.LeftBoundX);
        }

        [Test]
        public void UnitsAreBakedPerFrame()
        {
            Tuning t = TestData.Tables.T;
            Assert.AreEqual(Fix.Parse("-9.81") / 3600, t.Physics.BallGravity);
            Assert.AreEqual(Fix.Parse("5.4") / 60, t.Movement.DriveBase);
        }

        [Test]
        public void TrickFrameDataIsInsideTheSpecRanges()
        {
            foreach (FighterStats f in TestData.Tables.Fighters)
            {
                MoveDefinition d = f.Def.TrickDefense;
                Assert.That(d.Startup, Is.InRange(6, 12), f.Def.Id + " defensive start-up");
                Assert.That(d.WhiffRecovery, Is.InRange(20, 32), f.Def.Id + " defensive whiff recovery");
                foreach (MoveDefinition m in new[] { f.Def.TrickForward, f.Def.TrickBack })
                {
                    Assert.That(m.Startup, Is.InRange(8, 16), f.Def.Id + " " + m.Id + " start-up");
                    Assert.That(m.Recovery, Is.InRange(14, 28), f.Def.Id + " " + m.Id + " recovery");
                }
            }
            Assert.AreEqual(-3, TestData.Tables.T.Hype.AmpedStartupDelta);
        }

        [Test]
        public void BinderRejectsUnknownAndMissingKeys()
        {
            MemoryDataSource bad = TestData.CopyOfData();
            bad.Add("moves", bad.Read("moves").Replace("\"power\": \"22\"", "\"powr\": \"22\""));
            Assert.Throws<DataException>(() => SimTables.Load(bad));
            MemoryDataSource budget = TestData.CopyOfData();
            budget.Add("fighters/kofi", budget.Read("fighters/kofi").Replace("\"speed\": 7", "\"speed\": 8"));
            Assert.Throws<DataException>(() => SimTables.Load(budget), "37-point budget must fail");
        }

        [Test]
        public void DataHashChangesWithAnyTable()
        {
            MemoryDataSource copy = TestData.CopyOfData();
            ulong a = SimTables.Load(copy).DataHash;
            copy.Add("tuning", copy.Read("tuning").Replace("\"label\": \"", "\"label\": \"x"));
            Assert.AreNotEqual(a, SimTables.Load(copy).DataHash);
        }
    }

    public class CpuTests
    {
        [Test]
        public void TiersFollowTheDesignTable()
        {
            CpuTiers t = TestData.Tiers;
            int[] reaction = { 30, 24, 18, 14, 12 };
            string[] reads = { "0.15", "0.30", "0.45", "0.60", "0.70" };
            bool[] learns = { false, false, true, true, true };
            for (int i = 0; i < 5; i++)
            {
                Assert.AreEqual(reaction[i], t[(CpuTier)i].ReactionFrames);
                Assert.AreEqual(Fix.Parse(reads[i]), t[(CpuTier)i].ReadChance);
                Assert.AreEqual(learns[i], t[(CpuTier)i].LearnsHabits);
            }
        }

        [Test]
        public void CpuMatchCompletesWithAWinner()
        {
            SimTables tb = TestData.Tables;
            var session = new LocalSession(tb, MatchSetup.Versus(0, 3, 11),
                new CpuPlayer(tb, TestData.Tiers, CpuTier.Pro, 0, 1), new CpuPlayer(tb, TestData.Tiers, CpuTier.Pro, 1, 2));
            int guard = 60 * 60 * 30;
            while (!session.IsOver && guard-- > 0) session.Tick();
            Assert.IsTrue(session.IsOver);
            Assert.Less(session.State.MatchWinner, 2);
        }

        [Test]
        public void LegendBeatsRookie()
        {
            SimTables tb = TestData.Tables;
            int wins = 0, n = 24;
            for (int i = 0; i < n; i++)
            {
                int f = i % 4;
                bool swap = (i & 1) == 1;
                var session = new LocalSession(tb, MatchSetup.Versus(f, f, (ulong)(100 + i)),
                    new CpuPlayer(tb, TestData.Tiers, swap ? CpuTier.Rookie : CpuTier.Legend, 0, (ulong)i),
                    new CpuPlayer(tb, TestData.Tiers, swap ? CpuTier.Legend : CpuTier.Rookie, 1, (ulong)(i + 50)));
                int guard = 60 * 60 * 30;
                while (!session.IsOver && guard-- > 0) session.Tick();
                if (session.State.MatchWinner == (swap ? 1 : 0)) wins++;
            }
            Assert.GreaterOrEqual(wins, n * 3 / 4, "Legend should beat Rookie convincingly");
        }

        [Test]
        public void RivalBlendStaysBetweenTiers()
        {
            TierParams mid = TestData.Tiers.Blend(Fix.Parse("2.5"));
            Assert.That(mid.ReactionFrames, Is.InRange(14, 18));
            Assert.AreEqual(12, TestData.Tiers.Blend(Fix.FromInt(9)).ReactionFrames, "clamped to Legend, never faster");
        }
    }
}
