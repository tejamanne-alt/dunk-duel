using DunkDuel.Sim;

namespace DunkDuel.AI
{
    public enum OffenseChoice : byte { Shoot = 0, Drive = 1, Shake = 2, Post = 3, Trick = 4 }

    public enum DefenseChoice : byte { Wall = 0, Contest = 1, Swipe = 2, Hold = 3 }

    /// <summary>
    /// Rolling counts of the opponent's choices per court zone and shot-clock bucket, within one match.
    /// Fed only from what is visible on screen (the CPU's delayed snapshot).
    /// </summary>
    public sealed class HabitModel
    {
        public const int Zones = 4;   // rim, paint, mid, deep
        public const int Buckets = 3; // early, mid, late shot clock
        private const int OffenseKinds = 5;
        private const int DefenseKinds = 4;

        private readonly int[] _offense = new int[Zones * Buckets * OffenseKinds];
        private readonly int[] _defense = new int[Zones * Buckets * DefenseKinds];

        public static int Zone(Fix distToRim, Fix rimZone, Fix arcDistance)
        {
            if (distToRim <= rimZone) return 0;
            if (distToRim <= rimZone * 2) return 1;
            if (distToRim < arcDistance) return 2;
            return 3;
        }

        public static int Bucket(int shotClock, int fullClock)
        {
            if (shotClock * 3 > fullClock * 2) return 0;
            if (shotClock * 4 > fullClock) return 1;
            return 2;
        }

        public void SeeOffense(int zone, int bucket, OffenseChoice c) => _offense[(zone * Buckets + bucket) * OffenseKinds + (int)c]++;
        public void SeeDefense(int zone, int bucket, DefenseChoice c) => _defense[(zone * Buckets + bucket) * DefenseKinds + (int)c]++;

        public int OffenseSamples(int zone, int bucket)
        {
            int n = 0;
            for (int k = 0; k < OffenseKinds; k++) n += _offense[(zone * Buckets + bucket) * OffenseKinds + k];
            return n;
        }

        /// <summary>Most frequent offensive choice here, or false with fewer than <paramref name="minSamples"/>.</summary>
        public bool PredictOffense(int zone, int bucket, int minSamples, out OffenseChoice choice)
        {
            choice = OffenseChoice.Shoot;
            int baseIdx = (zone * Buckets + bucket) * OffenseKinds;
            int best = -1, total = 0;
            for (int k = 0; k < OffenseKinds; k++)
            {
                int c = _offense[baseIdx + k];
                total += c;
                if (c > best)
                {
                    best = c;
                    choice = (OffenseChoice)k;
                }
            }
            return total >= minSamples;
        }

        public bool PredictDefense(int zone, int bucket, int minSamples, out DefenseChoice choice)
        {
            choice = DefenseChoice.Hold;
            int baseIdx = (zone * Buckets + bucket) * DefenseKinds;
            int best = -1, total = 0;
            for (int k = 0; k < DefenseKinds; k++)
            {
                int c = _defense[baseIdx + k];
                total += c;
                if (c > best)
                {
                    best = c;
                    choice = (DefenseChoice)k;
                }
            }
            return total >= minSamples;
        }

        /// <summary>Share (0..1) of defensive commitments (anything but Hold) seen in this zone.</summary>
        public Fix CommitRate(int zone)
        {
            int commits = 0, total = 0;
            for (int b = 0; b < Buckets; b++)
            {
                int baseIdx = (zone * Buckets + b) * DefenseKinds;
                for (int k = 0; k < DefenseKinds; k++)
                {
                    total += _defense[baseIdx + k];
                    if (k != (int)DefenseChoice.Hold) commits += _defense[baseIdx + k];
                }
            }
            return total == 0 ? Fix.Half : Fix.Ratio(commits, total);
        }

        public void Clear()
        {
            System.Array.Clear(_offense, 0, _offense.Length);
            System.Array.Clear(_defense, 0, _defense.Length);
        }
    }
}
