namespace DunkDuel.Sim
{
    /// <summary>
    /// PCG-XSH-RR 32-bit generator over a single 64-bit state word. The match RNG lives inside
    /// <see cref="GameState"/>; the AI keeps its own stream. No System.Random anywhere in the sim.
    /// </summary>
    public static class Rng
    {
        private const ulong Multiplier = 6364136223846793005UL;
        private const ulong Increment = 1442695040888963407UL;

        /// <summary>Scrambles an arbitrary seed into a well-mixed starting state (SplitMix64 finalizer).</summary>
        public static ulong Seed(ulong seed)
        {
            unchecked
            {
                ulong z = seed + 0x9E3779B97F4A7C15UL;
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                return z ^ (z >> 31);
            }
        }

        public static uint NextU32(ref ulong state)
        {
            unchecked
            {
                ulong old = state;
                state = old * Multiplier + Increment;
                uint xorShifted = (uint)(((old >> 18) ^ old) >> 27);
                int rot = (int)(old >> 59);
                return (xorShifted >> rot) | (xorShifted << ((-rot) & 31));
            }
        }

        /// <summary>Uniform integer in [minInclusive, maxExclusive). Multiply-shift range reduction.</summary>
        public static int Range(ref ulong state, int minInclusive, int maxExclusive)
        {
            if (maxExclusive <= minInclusive) return minInclusive;
            ulong span = (ulong)(maxExclusive - minInclusive);
            ulong r = ((ulong)NextU32(ref state) * span) >> 32;
            return minInclusive + (int)r;
        }

        /// <summary>Uniform fixed-point value in [0, 1).</summary>
        public static Fix Unit(ref ulong state) => Fix.FromRaw(NextU32(ref state));

        /// <summary>True with probability <paramref name="chance"/> (clamped to [0, 1]).</summary>
        public static bool Chance(ref ulong state, Fix chance) => Unit(ref state) < chance;

        /// <summary>Approximately normal value with mean 0, standard deviation ~1 (sum of four uniforms).</summary>
        public static Fix Gaussianish(ref ulong state)
        {
            // Irwin-Hall with n = 4: mean 2, variance 1/3. Scale by sqrt(3) ~= 1.7320508.
            Fix sum = Unit(ref state) + Unit(ref state) + Unit(ref state) + Unit(ref state) - Fix.Two;
            return sum * Fix.FromRaw(7439101574L); // 1.7320508 * 2^32
        }
    }
}
