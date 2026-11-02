using System;
using DunkDuel.Sim;
using DunkDuel.Sim.Session;
using UnityEngine;

namespace DunkDuel.Presentation
{
    /// <summary>
    /// Runs an <see cref="IMatchSession"/> at a fixed 60 Hz from Unity's variable frame rate (accumulator with a
    /// catch-up limit) and exposes the previous and current states plus an interpolation alpha for rendering.
    /// The sim never sees Unity time: it only advances one tick per <see cref="IMatchSession.Tick"/>.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public sealed class MatchDriver : MonoBehaviour
    {
        public const float TickSeconds = 1f / 60f;

        public IMatchSession Session { get; private set; }
        public int MaxCatchUpTicks = 5;
        public bool Paused;
        /// <summary>Training: advance exactly one tick per <see cref="StepOnce"/> while paused.</summary>
        public bool FrameStepMode;

        public float Alpha { get; private set; } = 1f;
        public GameState Previous { get; private set; }
        public GameState Current { get; private set; }
        public int TicksThisFrame { get; private set; }
        public long TotalTicks { get; private set; }

        /// <summary>Raised before each tick (netplay pumps the lobby here).</summary>
        public event Action BeforeTick;
        /// <summary>Raised after each tick with the new state.</summary>
        public event Action<GameState> AfterTick;

        private float _accumulator;
        private int _pendingSteps;

        public void Begin(IMatchSession session)
        {
            Session = session;
            Previous = session.State;
            Current = session.State;
            _accumulator = 0f;
            TotalTicks = 0;
        }

        public void End()
        {
            Session = null;
        }

        public void StepOnce() => _pendingSteps++;

        private void Update()
        {
            TicksThisFrame = 0;
            if (Session == null) return;
            if (Paused)
            {
                while (FrameStepMode && _pendingSteps > 0)
                {
                    _pendingSteps--;
                    TickOnce();
                }
                Alpha = 1f;
                return;
            }
            _accumulator += Mathf.Min(Time.unscaledDeltaTime, 0.25f);
            while (_accumulator >= TickSeconds && TicksThisFrame < MaxCatchUpTicks)
            {
                TickOnce();
                _accumulator -= TickSeconds;
            }
            // Never build an unbounded backlog after a hitch: drop it rather than fast-forwarding.
            if (_accumulator > TickSeconds) _accumulator = TickSeconds * 0.5f;
            Alpha = Mathf.Clamp01(_accumulator / TickSeconds);
        }

        private void TickOnce()
        {
            if (Session == null || Session.IsOver)
            {
                if (Session != null) Current = Session.State;
                return;
            }
            Previous = Session.State;
            BeforeTick?.Invoke();
            Session.Tick();
            Current = Session.State;
            TicksThisFrame++;
            TotalTicks++;
            AfterTick?.Invoke(Current);
        }
    }

    /// <summary>
    /// Stage Heat: crowd intensity from the score margin plus recent highlights (decaying), with a close-and-late
    /// bonus. Drives crowd states (calm, hyped, frenzy), crowd audio and music stems.
    /// </summary>
    public sealed class StageHeat
    {
        public enum State : byte { Calm = 0, Hyped = 1, Frenzy = 2 }

        public float Value { get; private set; } = 0.2f;
        public State Level { get; private set; }
        private float _highlights;

        public void AddHighlight(float amount) => _highlights = Mathf.Min(1.2f, _highlights + amount);

        public void Update(in GameState s, int roundFrames, float dt)
        {
            _highlights = Mathf.Max(0f, _highlights - dt * 0.12f);
            int margin = Mathf.Abs(s.F0.Score - s.F1.Score);
            float marginHeat = Mathf.Min(margin, 4) * 0.06f;
            float total = (s.F0.Score + s.F1.Score) * 0.015f;
            bool late = roundFrames > 0 && s.RoundClock < roundFrames / 5;
            float clutch = (late && margin <= 2) || s.SuddenDeath ? 0.22f : 0f;
            float matchPoint = s.F0.RoundsWon + 1 >= s.RoundsToWin || s.F1.RoundsWon + 1 >= s.RoundsToWin ? 0.08f : 0f;
            float target = Mathf.Clamp01(0.15f + marginHeat + total + clutch + matchPoint + _highlights);
            Value = Mathf.Lerp(Value, target, 1f - Mathf.Exp(-dt * 2.5f));
            Level = Value >= 0.75f ? State.Frenzy : (Value >= 0.42f ? State.Hyped : State.Calm);
        }
    }
}
