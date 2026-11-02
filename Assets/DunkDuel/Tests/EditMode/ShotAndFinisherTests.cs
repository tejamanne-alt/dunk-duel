using DunkDuel.Sim;
using DunkDuel.Sim.Data;
using NUnit.Framework;

namespace DunkDuel.Tests
{
    /// <summary>The shot model P(make) = clamp(B + R - C - K), release windows, Rhythm, Finishers and Denial.</summary>
    public class ShotModelTests
    {
        private static Scenario Release(string fighter, string x, int releaseOffset, string balance = "100")
        {
            var sc = new Scenario(fighter, "kofi").Place(x, "-0.6");
            sc.P0.Balance = Scenario.F(balance);
            int peak = sc.Stats(0).Def.JumperGatherFrames;
            int releaseTick = peak + releaseOffset;
            sc.Run(releaseTick + 1, t => t < releaseTick ? Scenario.In(Buttons.Shoot) : FrameInput.None);
            Assert.IsTrue(sc.S.Shot.Active || sc.Saw(SimEventType.ShotRelease), "shot released");
            return sc;
        }

        [Test]
        public void UncontestedPerfectMidRangeIsBasePlusPerfect()
        {
            var sc = Release("kofi", "-4.5", 0);
            FighterStats k = sc.Stats(0);
            Assert.AreEqual(ReleaseGrade.Perfect, sc.S.Shot.Grade);
            Assert.AreEqual(Fix.Zero, sc.S.Shot.Contest, "defender far away: no contest");
            Assert.AreEqual(k.BaseMid + sc.T.Shot.BonusPerfect, sc.S.Shot.MakeChance);
            // B for mid-range: 0.48 +/- 0.03 per Range point from 5 (Kofi Range 4 -> 0.45).
            Assert.LessOrEqual(Fix.Abs(Scenario.F("0.45") - k.BaseMid), Fix.FromRaw(8));
        }

        [Test]
        public void LowBalanceAddsUpToFifteenPercent()
        {
            var sc = Release("kofi", "-4.5", 0, "20");
            FighterStats k = sc.Stats(0);
            Fix kpen = sc.T.Shot.BalancePenaltyMax * (sc.T.Shot.BalancePenaltyBelow - Scenario.F("20")) / sc.T.Shot.BalancePenaltyBelow;
            Assert.AreEqual(k.BaseMid + sc.T.Shot.BonusPerfect - kpen, sc.S.Shot.MakeChance);
        }

        [Test]
        public void DeepBaseUsesRangeAndRimBaseUsesFinishing()
        {
            FighterStats meera = TestData.Tables.Fighters[TestData.Fighter("meera")];
            FighterStats kofi = TestData.Tables.Fighters[TestData.Fighter("kofi")];
            Assert.LessOrEqual(Fix.Abs(Scenario.F("0.48") - meera.BaseDeep), Fix.FromRaw(8), "0.36 + 4 x 0.03");
            Assert.LessOrEqual(Fix.Abs(Scenario.F("0.82") - kofi.BaseRim), Fix.FromRaw(8), "0.70 + 4 x 0.03");
        }

        [Test]
        public void ChanceIsClampedTo2And98Percent()
        {
            var sc = Release("matas", "-9.5", -20, "0");
            Assert.GreaterOrEqual(sc.S.Shot.MakeChance, sc.T.Shot.MinChance);
            var rim = new Scenario("kofi", "meera").Place("-1.2", "-9.0");
            int peak = rim.Stats(0).Def.FinishGatherFrames;
            rim.Run(peak + 1, t => t < peak ? Scenario.In(Buttons.Shoot) : FrameInput.None);
            Assert.AreEqual(rim.T.Shot.MaxChance, rim.S.Shot.MakeChance, "open perfect dunk caps at 98%");
        }

        [Test]
        public void ReleaseWindowsPerfectSixGoodTwelve()
        {
            var sim = new Simulation(TestData.Tables);
            var s = sim.CreateInitialState(MatchSetup.Versus(TestData.Fighter("kofi"), TestData.Fighter("meera"), 1));
            int perfect = 0, good = 0;
            for (int off = -20; off <= 20; off++)
            {
                ReleaseGrade g = sim.GradeRelease(s.F0, off, false);
                if (g == ReleaseGrade.Perfect) perfect++;
                if (g == ReleaseGrade.Perfect || g == ReleaseGrade.Good) good++;
            }
            Assert.AreEqual(6, perfect);
            Assert.AreEqual(12, good);
        }

        [Test]
        public void MeeraHasTheWidestPerfectWindowAndHeatCheckWidensByHalf()
        {
            var sim = new Simulation(TestData.Tables);
            var s = sim.CreateInitialState(MatchSetup.Versus(TestData.Fighter("meera"), TestData.Fighter("kofi"), 1));
            int Count(in FighterState f, bool heat)
            {
                int n = 0;
                for (int off = -20; off <= 20; off++) if (sim.GradeRelease(f, off, heat) == ReleaseGrade.Perfect) n++;
                return n;
            }
            Assert.AreEqual(8, Count(s.F0, false), "Meera: 8 frames");
            Assert.AreEqual(9, Count(s.F1, true), "Heat Check: 6 x 1.5");
            Assert.AreEqual(12, Count(s.F0, true), "Meera on heat: 8 x 1.5");
        }

        [Test]
        public void SimpleModeWidensWindows()
        {
            var sim = new Simulation(TestData.Tables);
            var setup = MatchSetup.Versus(TestData.Fighter("kofi"), TestData.Fighter("meera"), 1);
            setup.Simple0 = true;
            var s = sim.CreateInitialState(setup);
            int n = 0;
            for (int off = -20; off <= 20; off++) if (sim.GradeRelease(s.F0, off, false) == ReleaseGrade.Perfect) n++;
            Assert.AreEqual(9, n);
        }

        [Test]
        public void RhythmStackCutsThreeFramesFromMeerasGather()
        {
            var sc = new Scenario("meera", "kofi").Place("-7.2", "-0.6");
            sc.P0.Rhythm = 2;
            sc.Run(1, _ => Scenario.In(Buttons.Shoot));
            Assert.AreEqual(ActionId.GatherJumper, sc.P0.Action);
            int expected = sc.Stats(0).Def.JumperGatherFrames - 2 * sc.Stats(0).Def.MasteryParams.RhythmFramesPerStack;
            Assert.AreEqual(expected, sc.P0.Peak);
        }

        [Test]
        public void LayupOrDunkNearTheRim()
        {
            var kofi = new Scenario("kofi", "meera").Place("-1.2", "-9.0");
            kofi.Run(1, _ => Scenario.In(Buttons.Shoot));
            Assert.AreEqual(ActionId.GatherFinish, kofi.P0.Action);
            Assert.AreEqual(ShotKind.Dunk, kofi.P0.PendingKind, "Kofi can dunk");
            var juno = new Scenario("juno", "meera").Place("-1.2", "-9.0");
            juno.Run(1, _ => Scenario.In(Buttons.Shoot));
            Assert.AreEqual(ShotKind.Layup, juno.P0.PendingKind, "Juno is too short to dunk");
        }
    }

    public class FinisherTests
    {
        private static Scenario Start(int defenderHype)
        {
            var sc = new Scenario("kofi", "meera").Place("-4.2", "-3.0");
            sc.P0.Hype = 300;
            sc.P1.Hype = defenderHype;
            sc.Run(1, _ => Scenario.In(Buttons.Finisher));
            Assert.AreEqual(Phase.Finisher, sc.S.Phase);
            Assert.AreEqual(0, sc.P0.Hype, "a Finisher costs 3 bars");
            return sc;
        }

        [Test]
        public void FinisherNeedsThreeBarsAndTheTriggerZone()
        {
            var sc = new Scenario("kofi", "meera").Place("-7.5", "-6.0");
            sc.P0.Hype = 300;
            sc.Run(2, _ => Scenario.In(Buttons.Finisher));
            Assert.AreEqual(Phase.Live, sc.S.Phase, "outside Kofi's free-throw-line zone");
            var low = new Scenario("kofi", "meera").Place("-4.2", "-3.0");
            low.P0.Hype = 299;
            low.Run(2, _ => Scenario.In(Buttons.Finisher));
            Assert.AreEqual(Phase.Live, low.S.Phase, "needs 300 hype");
        }

        [Test]
        public void UndeniedFinisherScoresThree()
        {
            var sc = Start(0);
            Assert.IsFalse(sc.S.Fin.HasRing, "no bar, no Denial ring");
            sc.RunUntil(s => s.S.Phase == Phase.Score, 400);
            Assert.AreEqual(3, sc.P0.Score);
            Assert.IsTrue(sc.Saw(SimEventType.FinisherScore));
        }

        [Test]
        public void DenyInsideTheWindowStopsItAndFlipsPossession()
        {
            var sc = Start(100);
            Assert.IsTrue(sc.S.Fin.HasRing);
            int start = sc.S.Fin.DenyStart;
            sc.RunUntil(s => s.S.Fin.Frame >= start - 1, 400);
            sc.Run(1, null, _ => Scenario.In(Buttons.Finisher));
            Assert.IsTrue(sc.Saw(SimEventType.Denied));
            Assert.AreEqual(1, sc.S.Offense);
            Assert.AreEqual(0, sc.P1.Hype, "Denial costs one bar");
            Assert.AreEqual(0, sc.P0.Score);
        }

        [Test]
        public void DenyOutsideTheWindowFailsAndTheFinisherLands()
        {
            var sc = Start(100);
            sc.Run(5, null, t => t == 3 ? Scenario.In(Buttons.Finisher) : FrameInput.None);
            Assert.IsTrue(sc.Saw(SimEventType.DenialAttempt));
            Assert.AreEqual(0, sc.First(SimEventType.DenialAttempt).A, "outside the window");
            sc.RunUntil(s => s.S.Phase == Phase.Score, 400);
            Assert.AreEqual(3, sc.P0.Score);
        }

        [Test]
        public void FinisherFrameDataFollowsTheSpec()
        {
            foreach (FighterStats f in TestData.Tables.Fighters)
            {
                FinisherDefinition d = f.Def.Finisher;
                Assert.That(d.Frames, Is.InRange(120, 180), f.Def.Id);
                Assert.That(d.DenyFrames, Is.InRange(4, 6), f.Def.Id);
            }
        }
    }
}
