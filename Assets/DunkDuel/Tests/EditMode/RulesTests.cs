using System;
using DunkDuel.Sim;
using DunkDuel.Sim.Data;
using NUnit.Framework;

namespace DunkDuel.Tests
{
    /// <summary>Scoring, possession, clocks, rounds and meters.</summary>
    public class RulesTests
    {
        /// <summary>Uncontested jumper from <paramref name="x"/>, released at the peak, trying seeds until it is made.</summary>
        private static Scenario MadeShot(string x, string fighter = "meera", bool wantMade = true)
        {
            for (ulong seed = 1; seed < 200; seed++)
            {
                var sc = new Scenario(fighter, "kofi", seed).Place(x, "-0.5");
                int peak = sc.Stats(0).Def.JumperGatherFrames;
                sc.Run(peak + 1, t => t < peak ? Scenario.In(Buttons.Shoot) : FrameInput.None);
                if (!sc.S.Shot.Active || sc.S.Shot.Made != wantMade) continue;
                sc.RunUntil(s => s.S.Phase != Phase.Live || s.S.Ball.Mode != BallMode.Flight, 200);
                return sc;
            }
            Assert.Fail("no seed produced the wanted outcome");
            return null;
        }

        [Test]
        public void ThreeBeyondTheArcTwoInside()
        {
            var deep = MadeShot("-7.2");
            Assert.AreEqual(3, deep.P0.Score);
            var mid = MadeShot("-4.5");
            Assert.AreEqual(2, mid.P0.Score);
        }

        [Test]
        public void MakeGivesScoringHypeAndScoredOnHype()
        {
            var sc = MadeShot("-7.2");
            Assert.AreEqual(sc.T.Hype.Make3, sc.P0.Hype);
            Assert.AreEqual(sc.T.Hype.ScoredOn, sc.P1.Hype);
        }

        [Test]
        public void LosersBallAfterAMake()
        {
            var sc = MadeShot("-4.5");
            Assert.AreEqual(Phase.Score, sc.S.Phase);
            sc.RunUntil(s => s.S.Phase == Phase.CheckBall, sc.T.Match.ScorePauseFrames + 30);
            Assert.AreEqual(1, sc.S.Offense, "the scored-on player checks the ball");
            Assert.AreEqual(1, sc.S.Ball.Holder);
            Assert.AreEqual(sc.T.Court.CheckX, sc.P1.X, "check at the top");
        }

        [Test]
        public void StopMustBeClearedPastTheArcBeforeScoring()
        {
            var sc = new Scenario().Place("-5.0", "-4.1");
            sc.Run(12, _ => Scenario.In(Buttons.Shoot), t => t == 0 ? Scenario.In(Buttons.Shake) : FrameInput.None);
            Assert.IsTrue(sc.S.NeedsClear);
            sc.Run(sc.S.HitStop + 2);
            int gathers = sc.Count(SimEventType.GatherStart);
            sc.Run(8, _ => FrameInput.None, t => t == 1 ? Scenario.In(Buttons.Shoot) : FrameInput.None);
            Assert.AreEqual(gathers, sc.Count(SimEventType.GatherStart), "no shot before the clear");
            sc.RunUntil(s => s.Saw(SimEventType.Cleared), 400, null, _ => Scenario.In(Buttons.Left | Buttons.Turbo));
            Assert.IsFalse(sc.S.NeedsClear);
            sc.Run(3, null, t => t == 1 ? Scenario.In(Buttons.Shoot) : FrameInput.None);
            Assert.Greater(sc.Count(SimEventType.GatherStart), gathers, "after the clear the ball can score");
        }

        [Test]
        public void ClearingPastTheDefenderIsNotBlocked()
        {
            // Regression: a defender between the rebounder and the arc used to body the clear until the shot clock expired.
            var sc = new Scenario("kofi", "matas").Place("0.5", "-0.2");
            sc.S.NeedsClear = true;
            sc.RunUntil(s => s.Saw(SimEventType.Cleared), 3 * 60, _ => Scenario.In(Buttons.Left | Buttons.Turbo), _ => Scenario.In(Buttons.Right | Buttons.Turbo));
        }

        [Test]
        public void OffensiveReboundNeedsNoClear()
        {
            var sc = new Scenario().Place("-5.0", "-6.0");
            sc.S.Ball.Mode = BallMode.Loose;
            sc.S.Ball.X = Scenario.F("-4.9");
            sc.S.Ball.Y = Scenario.F("1.0");
            sc.S.Ball.VX = Fix.Zero;
            sc.S.Ball.VY = Fix.Zero;
            sc.Run(3);
            Assert.IsTrue(sc.Saw(SimEventType.Rebound));
            Assert.AreEqual(1, sc.First(SimEventType.Rebound).A, "offensive board");
            Assert.IsFalse(sc.S.NeedsClear);
        }

        [Test]
        public void DefensiveReboundIsAStopThatNeedsAClear()
        {
            var sc = new Scenario().Place("-8.0", "-2.0");
            sc.S.Ball.Mode = BallMode.Loose;
            sc.S.Ball.X = Scenario.F("-2.05");
            sc.S.Ball.Y = Scenario.F("1.0");
            sc.Run(3);
            Assert.AreEqual(0, sc.First(SimEventType.Rebound).A, "defensive board");
            Assert.AreEqual(1, sc.S.Offense);
            Assert.IsTrue(sc.S.NeedsClear);
        }

        [Test]
        public void EightSecondShotClock()
        {
            var sc = new Scenario().Place("-7.6", "-6.3");
            Assert.AreEqual(480, sc.T.Match.ShotClockFrames, "8 seconds at 60 Hz");
            sc.S.ShotClock = sc.T.Match.ShotClockFrames;
            sc.Run(sc.T.Match.ShotClockFrames + 1);
            Assert.IsTrue(sc.Saw(SimEventType.ShotClockViolation));
            Assert.AreEqual(Phase.CheckBall, sc.S.Phase);
            Assert.AreEqual(1, sc.S.Offense);
        }

        [Test]
        public void RoundEndsAtSevenPoints()
        {
            for (ulong seed = 1; seed < 200; seed++)
            {
                var sc = new Scenario("meera", "kofi", seed).Place("-4.5", "-0.5");
                sc.P0.Score = 5;
                int peak = sc.Stats(0).Def.JumperGatherFrames;
                sc.Run(peak + 1, t => t < peak ? Scenario.In(Buttons.Shoot) : FrameInput.None);
                if (!sc.S.Shot.Made) continue;
                sc.RunUntil(s => s.S.Phase == Phase.RoundEnd, 400);
                Assert.AreEqual(1, sc.P0.RoundsWon);
                Assert.AreEqual(0, sc.First(SimEventType.RoundEnd).A);
                return;
            }
            Assert.Fail("no made shot");
        }

        [Test]
        public void NinetySecondClockLeaderWins()
        {
            var sc = new Scenario().Place("-7.6", "-6.3");
            Assert.AreEqual(5400, sc.T.Match.RoundFrames, "90 seconds at 60 Hz");
            sc.S.RoundClock = 2;
            sc.P0.Score = 3;
            sc.P1.Score = 2;
            sc.Run(3);
            Assert.AreEqual(Phase.RoundEnd, sc.S.Phase);
            Assert.AreEqual(0, sc.S.RoundWinner);
        }

        [Test]
        public void TiedAtTheBuzzerIsNextBucketWins()
        {
            var sc = new Scenario().Place("-7.6", "-6.3");
            sc.S.RoundClock = 2;
            sc.P0.Score = 4;
            sc.P1.Score = 4;
            sc.Run(3);
            Assert.AreEqual(Phase.Live, sc.S.Phase);
            Assert.IsTrue(sc.S.SuddenDeath);
            Assert.IsTrue(sc.Saw(SimEventType.SuddenDeath));
        }

        [Test]
        public void HypeCarriesOverAndBalanceResetsBetweenRounds()
        {
            var sc = new Scenario().Place("-7.6", "-6.3");
            sc.P0.Hype = 140;
            sc.P1.Balance = Scenario.F("12");
            sc.S.RoundClock = 2;
            sc.P0.Score = 3;
            sc.Run(3);
            sc.RunUntil(s => s.S.Phase == Phase.RoundIntro, sc.T.Match.RoundEndFrames + 10);
            Assert.AreEqual(140, sc.P0.Hype, "hype carries over");
            Assert.AreEqual(sc.T.Balance.Max, sc.P1.Balance, "balance resets each round");
            Assert.AreEqual(0, sc.P0.Score);
        }

        [Test]
        public void BestOfThreeEndsAtTwoRounds()
        {
            var sc = new Scenario().Place("-7.6", "-6.3");
            sc.P0.RoundsWon = 1;
            sc.S.RoundClock = 2;
            sc.P0.Score = 3;
            sc.Run(3);
            sc.RunUntil(s => s.S.Phase == Phase.MatchEnd, sc.T.Match.RoundEndFrames + 10);
            Assert.AreEqual(0, sc.S.MatchWinner);
        }

        [Test]
        public void BalanceRefillsOnlyAfterOneSecondUntouched()
        {
            var sc = new Scenario().Place("-7.6", "-6.3");
            sc.P1.Balance = Scenario.F("50");
            sc.P1.BalanceIdle = 0;
            sc.Run(sc.T.Balance.RegenDelayFrames);
            Assert.AreEqual(Scenario.F("50"), sc.P1.Balance, "no refill inside the first second");
            sc.Run(10);
            Assert.Greater(sc.P1.Balance, Scenario.F("50"));
        }

        [Test]
        public void HypeCostsAndCaps()
        {
            Tuning t = TestData.Tables.T;
            Assert.AreEqual(300, t.Hype.Max);
            Assert.AreEqual(100, t.Hype.AmpedCost);
            Assert.AreEqual(100, t.Hype.DenialCost);
            Assert.AreEqual(300, t.Hype.FinisherCost);
            int[] gains = { t.Hype.Make2, t.Hype.Make3, t.Hype.Dunk, t.Hype.Steal, t.Hype.Block, t.Hype.ChargeDrawn, t.Hype.AnkleBreak, t.Hype.Posterizer, t.Hype.BiteDrawn, t.Hype.ScoredOn };
            int[] spec = { 30, 45, 40, 40, 50, 60, 60, 70, 10, 10 };
            CollectionAssert.AreEqual(spec, gains, "starting gains from the design");
        }

        [Test]
        public void ThreeStraightScoresIgnitesHeatCheck()
        {
            for (ulong seed = 1; seed < 200; seed++)
            {
                var sc = new Scenario("meera", "kofi", seed).Place("-4.5", "-0.5");
                sc.P0.Streak = 2;
                int peak = sc.Stats(0).Def.JumperGatherFrames;
                sc.Run(peak + 1, t => t < peak ? Scenario.In(Buttons.Shoot) : FrameInput.None);
                if (!sc.S.Shot.Made) continue;
                sc.RunUntil(s => s.S.Phase == Phase.Score, 200);
                Assert.IsTrue(sc.Saw(SimEventType.HeatCheckOn));
                Assert.IsTrue(sc.P0.Has(StatusFlag.HeatCheck));
                return;
            }
            Assert.Fail("no made shot");
        }

        [Test]
        public void MissingTheHeatCheckShotGivesTheDefenderAFullBar()
        {
            var sc = MadeShot("-8.5", "matas", false);
            // Re-run the same miss with heat on.
            for (ulong seed = 1; seed < 400; seed++)
            {
                var h = new Scenario("matas", "kofi", seed).Place("-8.5", "-0.5");
                h.P0.Status |= StatusFlag.HeatCheck;
                h.P0.Streak = 3;
                int peak = h.Stats(0).Def.JumperGatherFrames;
                h.Run(peak + 1, t => t < peak ? Scenario.In(Buttons.Shoot) : FrameInput.None);
                if (!h.S.Shot.Active || h.S.Shot.Made) continue;
                Assert.IsTrue(h.S.Shot.HeatShot);
                h.RunUntil(s => s.S.Ball.Mode != BallMode.Flight, 200);
                Assert.IsTrue(h.Saw(SimEventType.HeatCheckMiss));
                Assert.AreEqual(h.T.Heat.MissHype, h.P1.Hype);
                Assert.IsFalse(h.P0.Has(StatusFlag.HeatCheck));
                return;
            }
            Assert.NotNull(sc);
            Assert.Fail("no missed shot");
        }
    }
}
