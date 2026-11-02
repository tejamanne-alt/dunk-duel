using System;
using DunkDuel.Sim;
using DunkDuel.Sim.Data;

namespace DunkDuel.AI
{
    public enum CpuTier : byte
    {
        Rookie = 0,
        Varsity = 1,
        Pro = 2,
        AllStar = 3,
        Legend = 4,
    }

    public enum HypeStyle : byte
    {
        Rarely = 0,
        WhenFull = 1,
        Purpose = 2,
        SaveForDenial = 3,
        Optimal = 4,
    }

    /// <summary>One CPU difficulty tier (ai.json). Reaction and read values follow the design table.</summary>
    public sealed class TierParams
    {
        public string Name;
        public int ReactionFrames;
        /// <summary>Spread (about one standard deviation, frames) of release timing.</summary>
        public Fix ReleaseError;
        /// <summary>Spread (frames) of Denial ring timing.</summary>
        public Fix DenyError;
        /// <summary>Chance to pick the counter to the opponent's predicted choice.</summary>
        public Fix ReadChance;
        public bool LearnsHabits;
        public HypeStyle HypeStyle;
        /// <summary>Chance per decision of an unforced, sloppy choice.</summary>
        public Fix MistakeChance;
        /// <summary>Frames between offensive/defensive decisions.</summary>
        public int DecisionInterval;
        /// <summary>Chance per defensive decision to commit (wall/contest/swipe) on a read instead of holding.</summary>
        public Fix CommitChance;
        /// <summary>Chance to react at all to a visible gather/drive (lower tiers miss cues).</summary>
        public Fix ReactChance;
    }

    /// <summary>Per-fighter CPU personality: where it likes to attack and what it likes to do.</summary>
    public sealed class Personality
    {
        public string Fighter;
        public Fix AttackX;
        public Fix ShootWeight;
        public Fix DriveWeight;
        public Fix ShakeWeight;
        public Fix PostWeight;
        public Fix TrickForwardWeight;
        public Fix TrickBackWeight;
        public Fix DefenseTrickWeight;
        public Fix DefendGap;
        public Fix WallWeight;
        public Fix ContestWeight;
        public Fix SwipeWeight;
        public int MaxShakeChain;
    }

    public sealed class RivalParams
    {
        public Fix StepPerRound;
        public Fix MinSkill;
        public Fix MaxSkill;
    }

    public sealed class AiTable
    {
        public TierParams[] Tiers;
        public Personality[] Personalities;
        public RivalParams Rival;
        public Fix ShootThreshold;
        public Fix DeepPreference;
        public Fix ReboundJumpLead;
        public int WallHoldFrames;
        public int ShakeObserveFrames;
        public int PostBumpsBeforeMove;
        public Fix FinishZoneSlack;
        /// <summary>Frames of probing (dribble moves, repositioning) before attacking, per possession.</summary>
        public int PatienceMin;
        public int PatienceMax;
        /// <summary>Reaction the offense assumes for the defender when estimating a contest.</summary>
        public int AssumedReaction;
        public Fix TightGapScale;
        public Fix LooseGapScale;
        public int StanceFrames;
        /// <summary>Weights of probing actions: dribble move, reposition, hold.</summary>
        public Fix ProbeShake;
        public Fix ProbeMove;
        public Fix ProbeHold;
        /// <summary>Guard-gap shrink per Range point above 5 when the shooter is beyond the arc.</summary>
        public Fix ShooterRespect;
        /// <summary>Guard-gap growth per Speed point above 5.</summary>
        public Fix DriverRespect;
    }

    /// <summary>Baked CPU tables shared by every CPU player.</summary>
    public sealed class CpuTiers
    {
        public AiTable Table;
        public Personality[] ByFighter; // indexed like SimTables.Fighters

        public TierParams this[CpuTier tier] => Table.Tiers[(int)tier];

        public static CpuTiers Load(SimTables tables, IDataSource source)
        {
            string text = source?.Read("ai");
            if (text == null) throw new DataException("missing data table 'ai'");
            int tickRate = tables.T.TickRate;
            AiTable t = TableBinder.Bind<AiTable>(Json.Parse(text, "ai.json"), tickRate);
            if (t.Tiers.Length != 5) throw new DataException("ai.json: need exactly five tiers");
            var result = new CpuTiers { Table = t, ByFighter = new Personality[tables.FighterCount] };
            for (int i = 0; i < tables.FighterCount; i++)
            {
                string id = tables.Fighters[i].Def.Id;
                for (int k = 0; k < t.Personalities.Length; k++)
                {
                    if (t.Personalities[k].Fighter == id) result.ByFighter[i] = t.Personalities[k];
                }
                if (result.ByFighter[i] == null) throw new DataException("ai.json: no personality for " + id);
            }
            for (int k = 1; k < t.Tiers.Length; k++)
            {
                if (t.Tiers[k].ReactionFrames > t.Tiers[k - 1].ReactionFrames) throw new DataException("ai.json: reaction must not slow down with tier");
            }
            if (t.Tiers[4].ReactionFrames < 12) throw new DataException("ai.json: Legend must not react faster than 12 frames");
            return result;
        }

        /// <summary>Blends two adjacent tiers for Rival mode; <paramref name="skill"/> is 0 (Rookie) .. 4 (Legend).</summary>
        public TierParams Blend(Fix skill)
        {
            skill = Fix.Clamp(skill, Fix.Zero, Fix.FromInt(4));
            int lo = skill.FloorToInt();
            if (lo >= 4) return Table.Tiers[4];
            Fix t = skill - lo;
            TierParams a = Table.Tiers[lo], b = Table.Tiers[lo + 1];
            return new TierParams
            {
                Name = "Rival",
                ReactionFrames = Fix.Lerp(a.ReactionFrames, b.ReactionFrames, t).RoundToInt(),
                ReleaseError = Fix.Lerp(a.ReleaseError, b.ReleaseError, t),
                DenyError = Fix.Lerp(a.DenyError, b.DenyError, t),
                ReadChance = Fix.Lerp(a.ReadChance, b.ReadChance, t),
                LearnsHabits = t >= Fix.Half ? b.LearnsHabits : a.LearnsHabits,
                HypeStyle = t >= Fix.Half ? b.HypeStyle : a.HypeStyle,
                MistakeChance = Fix.Lerp(a.MistakeChance, b.MistakeChance, t),
                DecisionInterval = Fix.Lerp(a.DecisionInterval, b.DecisionInterval, t).RoundToInt(),
                CommitChance = Fix.Lerp(a.CommitChance, b.CommitChance, t),
                ReactChance = Fix.Lerp(a.ReactChance, b.ReactChance, t),
            };
        }
    }
}
