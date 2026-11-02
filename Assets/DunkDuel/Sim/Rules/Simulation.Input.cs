namespace DunkDuel.Sim
{
    public sealed partial class Simulation
    {
        private const byte NoPress = 255;

        private static void ResetInput(ref FighterState f)
        {
            f.Held = 0;
            f.PrevHeld = 0;
            f.AgeShoot = NoPress;
            f.AgeShake = NoPress;
            f.AgeTrick = NoPress;
            f.AgeFinisher = NoPress;
            f.TrickAmp = false;
            f.DirHistory = 0;
        }

        /// <summary>Runs every tick, including hit-stop ticks, so presses are never lost.</summary>
        private void Ingest(ref FighterState f, FrameInput input)
        {
            f.PrevHeld = f.Held;
            f.Held = input.Bits;
            ushort pressed = (ushort)(f.Held & ~f.PrevHeld);
            if ((pressed & (ushort)Buttons.Shoot) != 0) f.AgeShoot = 0;
            if ((pressed & (ushort)Buttons.Shake) != 0) f.AgeShake = 0;
            if ((pressed & (ushort)Buttons.Trick) != 0)
            {
                f.AgeTrick = 0;
                f.TrickAmp = input.Has(Buttons.Amp);
            }
            // A late AMP within the simultaneity window upgrades a pending TRICK press.
            if ((pressed & (ushort)Buttons.Amp) != 0 && f.AgeTrick != NoPress && f.AgeTrick <= T.Input.SimultaneousFrames)
            {
                f.TrickAmp = true;
            }
            if ((pressed & (ushort)Buttons.Finisher) != 0) f.AgeFinisher = 0;
        }

        /// <summary>Shifts the current stick direction into the 8-entry history (one nibble per gameplay frame).</summary>
        private static void RecordDirection(ref FighterState f)
        {
            f.DirHistory = (f.DirHistory << 4) | new FrameInput(f.Held).DirNibble;
        }

        /// <summary>Ages buffered presses once per gameplay frame. Lag (Lag Spike) freezes the buffer.</summary>
        private void AgeInputs(ref FighterState f)
        {
            if (f.Lag > 0) return;
            int max = T.Input.BufferFrames;
            f.AgeShoot = Age(f.AgeShoot, max);
            f.AgeShake = Age(f.AgeShake, max);
            f.AgeTrick = Age(f.AgeTrick, max);
            f.AgeFinisher = Age(f.AgeFinisher, max);
        }

        private static byte Age(byte age, int max)
        {
            if (age == NoPress) return NoPress;
            int next = age + 1;
            return next > max ? NoPress : (byte)next;
        }

        private static bool Pressed(in FighterState f, Buttons b)
        {
            switch (b)
            {
                case Buttons.Shoot: return f.AgeShoot != NoPress;
                case Buttons.Shake: return f.AgeShake != NoPress;
                case Buttons.Trick: return f.AgeTrick != NoPress;
                case Buttons.Finisher: return f.AgeFinisher != NoPress;
                default: return false;
            }
        }

        private static void Consume(ref FighterState f, Buttons b)
        {
            switch (b)
            {
                case Buttons.Shoot: f.AgeShoot = NoPress; break;
                case Buttons.Shake: f.AgeShake = NoPress; break;
                case Buttons.Trick: f.AgeTrick = NoPress; break;
                case Buttons.Finisher: f.AgeFinisher = NoPress; break;
            }
        }

        /// <summary>
        /// Direction for a button press: the most recent non-neutral direction within the simultaneity window,
        /// so a direction and a button within 3 frames count as one input.
        /// </summary>
        private StickDir PressDir(in FighterState f)
        {
            int window = T.Input.SimultaneousFrames;
            for (int i = 0; i < window && i < 8; i++)
            {
                int nibble = (int)((f.DirHistory >> (4 * i)) & 0xF);
                if (nibble != 0) return FrameInput.NibbleToDir(nibble);
            }
            return StickDir.Neutral;
        }
    }
}
