using System;

namespace DunkDuel.Sim
{
    /// <summary>Button bits of <see cref="FrameInput"/>.</summary>
    [Flags]
    public enum Buttons : ushort
    {
        None = 0,
        Left = 1 << 0,
        Right = 1 << 1,
        Up = 1 << 2,
        Down = 1 << 3,
        Shoot = 1 << 4,
        Shake = 1 << 5,
        Trick = 1 << 6,
        Turbo = 1 << 7,
        Finisher = 1 << 8, // DENY while the opponent's Finisher plays
        Amp = 1 << 9,      // swipe-up from TRICK (touch) or a modifier (controller)

        DirMask = Left | Right | Up | Down,
        ActionMask = Shoot | Shake | Trick | Turbo | Finisher | Amp,
        ValidMask = DirMask | ActionMask,
    }

    /// <summary>
    /// Stick direction relative to the hoop. The hoop is always at +X (screen right), so "toward"
    /// means Right for both players and "away" means Left.
    /// </summary>
    public enum StickDir : byte
    {
        Neutral = 0,
        Toward = 1,
        Away = 2,
        Down = 3,
        Up = 4,
        DownToward = 5,
        DownAway = 6,
        UpToward = 7,
        UpAway = 8,
    }

    /// <summary>
    /// One player's controller state for one 60 Hz tick: 16 bits, 10 used. This is the only thing that
    /// crosses the radio, gets recorded in replays, or is produced by the CPU.
    /// </summary>
    [Serializable]
    public readonly struct FrameInput : IEquatable<FrameInput>
    {
        public readonly ushort Bits;

        public FrameInput(ushort bits) { Bits = (ushort)(bits & (ushort)Buttons.ValidMask); }
        public FrameInput(Buttons buttons) : this((ushort)buttons) { }

        public static readonly FrameInput None = new FrameInput((ushort)0);

        public bool Has(Buttons b) => (Bits & (ushort)b) == (ushort)b;
        public bool Any(Buttons b) => (Bits & (ushort)b) != 0;
        public Buttons Buttons => (Buttons)Bits;

        /// <summary>Horizontal axis after SOCD cleaning: -1 away, 0 neutral, +1 toward the hoop.</summary>
        public int Horizontal
        {
            get
            {
                bool l = Has(Buttons.Left), r = Has(Buttons.Right);
                return l == r ? 0 : (r ? 1 : -1);
            }
        }

        /// <summary>Vertical axis after SOCD cleaning: -1 down, 0 neutral, +1 up.</summary>
        public int Vertical
        {
            get
            {
                bool d = Has(Buttons.Down), u = Has(Buttons.Up);
                return d == u ? 0 : (u ? 1 : -1);
            }
        }

        public StickDir Dir => ToDir(Horizontal, Vertical);

        public static StickDir ToDir(int h, int v)
        {
            if (v < 0) return h > 0 ? StickDir.DownToward : (h < 0 ? StickDir.DownAway : StickDir.Down);
            if (v > 0) return h > 0 ? StickDir.UpToward : (h < 0 ? StickDir.UpAway : StickDir.Up);
            return h > 0 ? StickDir.Toward : (h < 0 ? StickDir.Away : StickDir.Neutral);
        }

        /// <summary>4-bit packed direction (L,R,U,D after cleaning) used by the direction history.</summary>
        public byte DirNibble
        {
            get
            {
                int h = Horizontal, v = Vertical;
                int n = 0;
                if (h < 0) n |= 1;
                if (h > 0) n |= 2;
                if (v > 0) n |= 4;
                if (v < 0) n |= 8;
                return (byte)n;
            }
        }

        public static StickDir NibbleToDir(int nibble)
        {
            int h = ((nibble & 2) != 0 ? 1 : 0) - ((nibble & 1) != 0 ? 1 : 0);
            int v = ((nibble & 4) != 0 ? 1 : 0) - ((nibble & 8) != 0 ? 1 : 0);
            return ToDir(h, v);
        }

        public FrameInput With(Buttons b) => new FrameInput((ushort)(Bits | (ushort)b));
        public FrameInput Without(Buttons b) => new FrameInput((ushort)(Bits & ~(ushort)b));

        public bool Equals(FrameInput other) => Bits == other.Bits;
        public override bool Equals(object obj) => obj is FrameInput f && f.Bits == Bits;
        public override int GetHashCode() => Bits;
        public static bool operator ==(FrameInput a, FrameInput b) => a.Bits == b.Bits;
        public static bool operator !=(FrameInput a, FrameInput b) => a.Bits != b.Bits;

        public override string ToString()
        {
            var sb = new System.Text.StringBuilder(16);
            sb.Append(Dir.ToString());
            if (Has(Buttons.Shoot)) sb.Append("+SHOOT");
            if (Has(Buttons.Shake)) sb.Append("+SHAKE");
            if (Has(Buttons.Trick)) sb.Append("+TRICK");
            if (Has(Buttons.Turbo)) sb.Append("+TURBO");
            if (Has(Buttons.Finisher)) sb.Append("+FIN");
            if (Has(Buttons.Amp)) sb.Append("+AMP");
            return sb.ToString();
        }
    }

    public static class StickDirExtensions
    {
        public static bool IsToward(this StickDir d) => d == StickDir.Toward || d == StickDir.DownToward || d == StickDir.UpToward;
        public static bool IsAway(this StickDir d) => d == StickDir.Away || d == StickDir.DownAway || d == StickDir.UpAway;
        public static bool IsDown(this StickDir d) => d == StickDir.Down || d == StickDir.DownToward || d == StickDir.DownAway;
        public static int Horizontal(this StickDir d) => d.IsToward() ? 1 : (d.IsAway() ? -1 : 0);
    }
}
