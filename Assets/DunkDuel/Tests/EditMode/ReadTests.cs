using DunkDuel.Sim;
using DunkDuel.Sim.Data;
using NUnit.Framework;

namespace DunkDuel.Tests
{
    /// <summary>The Read: every outcome of drive/shoot against wall/contest, plus swipes and bites.</summary>
    public class ReadTests
    {
        private static readonly FrameInput Drive = Scenario.In(Buttons.Right | Buttons.Turbo);
        private static readonly FrameInput WallHold = Scenario.In(Buttons.Down);

        [Test]
        public void DriveIntoDefenderPlantedEightFramesIsACharge()
        {
            var sc = new Scenario().Place("-6.0", "-4.0");
            sc.P1.Action = ActionId.Wall;
            sc.P1.PlantFrames = sc.T.Read.PlantFramesForCharge + 2;
            sc.P1.Held = (ushort)Buttons.Down;
            int hypeBefore = sc.P1.Hype;
            sc.RunUntil(s => s.Saw(SimEventType.Charge), 90, _ => Drive, _ => WallHold);
            Assert.AreEqual(1, sc.S.Offense, "charge is a turnover");
            Assert.AreEqual(ActionId.Knockdown, sc.P0.Action);
            Assert.AreEqual(hypeBefore + sc.T.Hype.ChargeDrawn, sc.P1.Hype);
            Assert.IsTrue(sc.S.NeedsClear, "a stop must be cleared past the arc");
            Assert.AreEqual(Phase.StopPause, sc.S.Phase);
        }

        [Test]
        public void LatePlantIsNotACharge()
        {
            var sc = new Scenario().Place("-4.75", "-4.2");
            sc.P0.VX = sc.Stats(0).DriveSpeed;
            sc.Run(20, _ => Drive, t => t >= 0 ? WallHold : FrameInput.None);
            Assert.IsFalse(sc.Saw(SimEventType.Charge), "planted for fewer than 8 frames: no charge");
            Assert.AreEqual(0, sc.S.Offense);
        }

        [Test]
        public void DriveIntoAirborneDefenderIsABlowBy()
        {
            var sc = new Scenario().Place("-5.4", "-4.6");
            sc.P0.VX = sc.Stats(0).DriveSpeed;
            sc.P1.Action = ActionId.Contest;
            sc.P1.ActionFrame = sc.T.Read.ContestStartup + 1;
            sc.P1.Takeoff = sc.T.Read.ContestStartup;
            sc.P1.Y = Scenario.F("0.4");
            sc.P1.VY = sc.Stats(1).JumpV0 / 2;
            sc.P1.Status &= unchecked((ushort)~StatusFlag.Grounded);
            sc.Run(25, _ => Drive);
            Assert.IsTrue(sc.Saw(SimEventType.BlowBy), "airborne defender gets blown by");
            Assert.Greater(sc.P0.X, sc.P1.X, "handler is past the defender");
            Assert.AreEqual(0, sc.S.Offense);
        }

        [Test]
        public void ShotOverPlantedDefenderGetsNoContest()
        {
            var sc = new Scenario().Place("-5.0", "-4.3");
            sc.P1.Action = ActionId.Wall;
            sc.P1.PlantFrames = 20;
            sc.P1.Held = (ushort)Buttons.Down;
            int peak = sc.Stats(0).Def.JumperGatherFrames;
            // Hold SHOOT through the gather, release exactly at the peak.
            sc.Run(peak + 2, t => t < peak ? Scenario.In(Buttons.Shoot) : FrameInput.None, _ => WallHold);
            Assert.IsTrue(sc.Saw(SimEventType.ShotRelease));
            Assert.AreEqual(Fix.Zero, sc.S.Shot.Contest, "leaving a plant takes 12 frames: no contest");
            Assert.AreEqual(ReleaseGrade.Perfect, sc.S.Shot.Grade);
        }

        [Test]
        public void ContestTimedToTheReleaseIsTheTimingBattle()
        {
            var sc = new Scenario().Place("-5.0", "-4.3");
            int peak = sc.Stats(0).Def.JumperGatherFrames;
            int lead = sc.T.Read.ContestStartup + sc.Stats(1).Def.JumpRiseFrames;
            int pressAt = peak - lead;
            Assert.GreaterOrEqual(pressAt, 0);
            sc.Run(peak + 2,
                t => t < peak ? Scenario.In(Buttons.Shoot) : FrameInput.None,
                t => t == pressAt ? Scenario.In(Buttons.Shoot) : FrameInput.None);
            bool blocked = sc.Saw(SimEventType.Block);
            Assert.IsTrue(blocked || sc.S.Shot.Contest >= Scenario.F("0.8"), "apex at the release: near-full contest (or a block)");
        }

        [Test]
        public void SwipeOnTheFirstSixGatherFramesIsASteal()
        {
            var sc = new Scenario().Place("-5.0", "-4.1");
            int hype = sc.P1.Hype;
            sc.Run(12, _ => Scenario.In(Buttons.Shoot), t => t == 0 ? Scenario.In(Buttons.Shake) : FrameInput.None);
            Assert.IsTrue(sc.Saw(SimEventType.Steal));
            Assert.AreEqual(1, sc.S.Offense);
            Assert.AreEqual(hype + sc.T.Hype.Steal, sc.P1.Hype);
            Assert.IsTrue(sc.S.NeedsClear);
        }

        [Test]
        public void SwipeAfterTheGatherExposureWhiffsAndLeavesTheDefenderOffBalance()
        {
            var sc = new Scenario().Place("-5.0", "-4.1");
            sc.Run(14, _ => Scenario.In(Buttons.Shoot), t => t == 3 ? Scenario.In(Buttons.Shake) : FrameInput.None);
            Assert.IsFalse(sc.Saw(SimEventType.Steal));
            Assert.AreEqual(ActionId.OffBalance, sc.P1.Action);
            Assert.GreaterOrEqual(sc.P1.Param, sc.T.Read.WhiffOffBalanceFrames, "whiff leaves the defender off-balance 20+ frames");
        }

        [Test]
        public void WhiffedSwipeGivesUpAFreeDrive()
        {
            var sc = new Scenario().Place("-5.0", "-4.3");
            // Swipe at an idle dribble: the ball is not exposed.
            sc.Run(10, _ => FrameInput.None, t => t == 0 ? Scenario.In(Buttons.Shake) : FrameInput.None);
            Assert.AreEqual(ActionId.OffBalance, sc.P1.Action);
            sc.Run(30, _ => Drive);
            Assert.IsTrue(sc.Saw(SimEventType.BlowBy), "off-balance defender has no pushbox");
        }

        [Test]
        public void SwipeOnShakeExposedFramesIsASteal()
        {
            var sc = new Scenario().Place("-5.0", "-4.1");
            sc.Run(12, t => t == 2 ? Scenario.In(Buttons.Shake | Buttons.Right) : FrameInput.None,
                t => t == 0 ? Scenario.In(Buttons.Shake) : FrameInput.None);
            Assert.IsTrue(sc.Saw(SimEventType.Steal));
        }

        [Test]
        public void CommitInsideTheBaitWindowBitesWithTheBalanceFormula()
        {
            var sc = new Scenario().Place("-5.0", "-3.9");
            MoveDefinition cross = sc.Tables.Moves.Crossover;
            int commitAt = cross.BaitStart + 1;
            int hype = sc.P0.Hype;
            Fix balance = sc.P1.Balance;
            sc.Run(commitAt + 1, t => t == 0 ? Scenario.In(Buttons.Shake | Buttons.Right) : FrameInput.None,
                t => t >= commitAt ? Scenario.In(Buttons.Down) : FrameInput.None);
            Assert.IsTrue(sc.Saw(SimEventType.Bite), "a wall started inside the bait window is a bite");
            FighterStats k = sc.Stats(0), m = sc.Stats(1);
            Fix expected = cross.Power * k.HandlesMult * sc.T.Balance.ComboMultipliers[0] * (Fix.One - m.Resist);
            Assert.AreEqual(expected, balance - sc.P1.Balance, "damage = power x handles x combo x (1 - resistance)");
            Assert.AreEqual(hype + sc.T.Hype.BiteDrawn, sc.P0.Hype);
        }

        [Test]
        public void CommitBeforeTheBaitWindowDoesNotBite()
        {
            var sc = new Scenario().Place("-5.0", "-3.9");
            sc.Run(4, t => t == 1 ? Scenario.In(Buttons.Shake | Buttons.Right) : FrameInput.None,
                t => Scenario.In(Buttons.Down));
            Assert.IsFalse(sc.Saw(SimEventType.Bite));
        }

        [Test]
        public void EmptyBalanceIsAnAnkleBreak()
        {
            var sc = new Scenario().Place("-5.0", "-3.9");
            sc.P1.Balance = Scenario.F("5");
            int hype = sc.P0.Hype;
            int commitAt = sc.Tables.Moves.Crossover.BaitStart + 1;
            sc.Run(commitAt + 1, t => t == 0 ? Scenario.In(Buttons.Shake | Buttons.Right) : FrameInput.None,
                t => t >= commitAt ? Scenario.In(Buttons.Down) : FrameInput.None);
            Assert.IsTrue(sc.Saw(SimEventType.AnkleBreak));
            Assert.AreEqual(ActionId.Stumble, sc.P1.Action);
            Assert.GreaterOrEqual(sc.P0.Hype, hype + sc.T.Hype.AnkleBreak);
            int stumble = sc.T.Balance.StumbleFrames;
            sc.Run(stumble + sc.S.HitStop + 40);
            Assert.AreNotEqual(ActionId.Stumble, sc.P1.Action, "stumble lasts 60 frames");
            Assert.AreEqual(sc.T.Balance.AfterStumble, sc.P1.Balance);
        }

        [Test]
        public void DriveBumpsRespectTheirInterval()
        {
            // Regression: a slow driver dropping under drive speed after each push used to bump every 2-3 frames.
            var sc = new Scenario("matas", "kofi").Place("-6.0", "-5.3");
            int ticks = 120;
            sc.Run(ticks, _ => Drive);
            int maxBumps = ticks / sc.T.Read.DriveBumpInterval + 1;
            Assert.LessOrEqual(sc.Count(SimEventType.DriveBump), maxBumps);
        }

        [Test]
        public void ComboMultiplierGrowsPerLink()
        {
            Fix[] combo = TestData.Tables.T.Balance.ComboMultipliers;
            for (int i = 1; i < combo.Length; i++) Assert.Greater(combo[i], combo[i - 1]);
            Assert.AreEqual(5, TestData.Tables.Fighters[TestData.Fighter("juno")].MaxComboLinks, "Juno chains five");
            Assert.AreEqual(3, TestData.Tables.Fighters[TestData.Fighter("kofi")].MaxComboLinks, "others chain three");
        }
    }
}
