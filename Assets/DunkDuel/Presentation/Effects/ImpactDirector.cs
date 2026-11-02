using System;
using DunkDuel.Sim;
using DunkDuel.Sim.Data;
using DunkDuel.Sim.Session;
using UnityEngine;

namespace DunkDuel.Presentation
{
    public enum CalloutStyle : byte { Highlight = 0, Info = 1, Big = 2, Finisher = 3 }

    /// <summary>A callout for the HUD. <see cref="Key"/> lets a rollback cancel it.</summary>
    public struct CalloutInfo
    {
        public long Key;
        public string Id;        // localization key, e.g. "callout.steal"
        public string Text;      // default text (impacts.json callout or English fallback)
        public int Actor;        // 0/1 fighter, 2 ball/neutral
        public CalloutStyle Style;
        public float Age;        // seconds late (rolled-back spawn)
    }

    /// <summary>
    /// Turns sim events into feel: impact recipes (camera, callout, crowd from impacts.json; hit-stop and slow
    /// motion already happen inside the sim), effects in each fighter's motif, audio, haptics and court
    /// reactions. Rollback-aware through <see cref="EventLedger"/>: orphaned events fade fast and late ones
    /// spawn with their age.
    /// </summary>
    public sealed class ImpactDirector
    {
        private readonly SimTables _tables;
        private readonly EffectPool _fx;
        private readonly GameCamera _cam;
        private readonly AudioDirector _audio;
        private readonly StageHeat _heat;
        private readonly CourtView _court;
        private readonly FighterView[] _fighters;
        private readonly int _local;
        private EventLedger _ledger;
        private GameState _state;
        private readonly System.Collections.Generic.List<SimEvent> _pending = new System.Collections.Generic.List<SimEvent>();
        private readonly System.Collections.Generic.List<int> _pendingAge = new System.Collections.Generic.List<int>();

        public event Action<CalloutInfo> Callout;
        public event Action<long> CalloutCancelled;

        public ImpactDirector(SimTables tables, EffectPool fx, GameCamera cam, AudioDirector audio, StageHeat heat, CourtView court,
            FighterView[] fighters, int localPlayer)
        {
            _tables = tables;
            _fx = fx;
            _cam = cam;
            _audio = audio;
            _heat = heat;
            _court = court;
            _fighters = fighters;
            _local = localPlayer;
        }

        public void Attach(EventLedger ledger)
        {
            _ledger = ledger;
            ledger.Spawned += OnSpawned;
            ledger.Orphaned += OnOrphaned;
        }

        public void Detach()
        {
            if (_ledger == null) return;
            _ledger.Spawned -= OnSpawned;
            _ledger.Orphaned -= OnOrphaned;
            _ledger = null;
        }

        /// <summary>
        /// Events fire inside the session tick; they are queued and processed here with the newest state, once per
        /// rendered frame. Orphans still in the queue are simply dropped.
        /// </summary>
        public void Flush(in GameState s)
        {
            _state = s;
            for (int i = 0; i < _pending.Count; i++) Process(_pending[i], _pendingAge[i]);
            _pending.Clear();
            _pendingAge.Clear();
        }

        private Vector3 At(in SimEvent e, float z = 0f) => new Vector3(e.X.ToFloat(), e.Y.ToFloat(), z);

        private FighterView Fighter(int actor) => actor >= 0 && actor < _fighters.Length ? _fighters[actor] : null;

        private Color Motif(int actor) => Fighter(actor) != null ? Fighter(actor).Visual.Motif : Color.white;

        private void OnOrphaned(SimEvent e)
        {
            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                if (_pending[i].Key == e.Key && _pending[i].Type == e.Type)
                {
                    _pending.RemoveAt(i);
                    _pendingAge.RemoveAt(i);
                    return;
                }
            }
            _fx.Cancel(e.Key);
            _cam.Cancel(e.Key);
            CalloutCancelled?.Invoke(e.Key);
        }

        private void OnSpawned(SimEvent e, int ageTicks)
        {
            _pending.Add(e);
            _pendingAge.Add(ageTicks);
        }

        private void Process(SimEvent e, int ageTicks)
        {
            float age = ageTicks / 60f;
            _audio?.OnSimEvent(e, ageTicks);
            ImpactDef impact = (int)e.Type < _tables.ImpactByEvent.Length ? _tables.ImpactByEvent[(int)e.Type] : null;
            if (impact != null)
            {
                Vector3 focus = FocusFor(e);
                _cam.Play(impact.Camera, focus, age, e.Key);
                if (!string.IsNullOrEmpty(impact.Callout)) Raise(e, "callout." + e.Type, impact.Callout, CalloutStyle.Highlight, age);
                Crowd(impact.Crowd);
            }

            FighterView f = Fighter(e.Actor);
            switch (e.Type)
            {
                case SimEventType.RoundIntro:
                    Raise(e, "callout.round", "ROUND " + (e.A + 1), CalloutStyle.Big, age);
                    _court.RoundStart();
                    break;
                case SimEventType.CheckBall:
                    break;
                case SimEventType.BallLive:
                    if (_state.Round == 0 && _state.F0.Score == 0 && _state.F1.Score == 0) Raise(e, "callout.ball", "BALL!", CalloutStyle.Info, age);
                    break;
                case SimEventType.Steal:
                    _fx.Burst(BurstKind.Sparks, BallPos(), 26, Motif(e.Actor), 5f);
                    f?.Flash(Color.white, 0.2f);
                    Haptic(e, HapticKind.Medium);
                    break;
                case SimEventType.Charge:
                    _fx.Ring(e.Key, new Vector3(e.X.ToFloat(), 0.02f, f != null ? f.Depth : 0f), new Color(1f, 1f, 1f, 0.8f), 2.2f, 0.45f, age);
                    _fx.Burst(BurstKind.Dust, At(e) + Vector3.up * 0.1f, 30, new Color(0.8f, 0.75f, 0.68f, 0.6f), 2.5f, 0.15f);
                    Haptic(e, HapticKind.Heavy);
                    break;
                case SimEventType.AnkleBreak:
                {
                    FighterView attacker = Fighter(e.A);
                    MotifBurst(attacker != null ? attacker.Index : 1 - e.Actor, At(e) + Vector3.up * 0.4f, 1.2f);
                    _fx.Burst(BurstKind.Dust, At(e) + Vector3.up * 0.05f, 40, new Color(0.8f, 0.75f, 0.68f, 0.6f), 3f, 0.16f);
                    _heat.AddHighlight(0.35f);
                    Haptic(e, HapticKind.Heavy);
                    break;
                }
                case SimEventType.Block:
                    _fx.Burst(BurstKind.Sparks, At(e), 34, Motif(e.Actor), 7f);
                    _fx.Flash(e.Key, At(e), new Color(1f, 1f, 1f, 0.8f), 1.2f, 0.18f, age);
                    _heat.AddHighlight(0.3f);
                    Haptic(e, HapticKind.Heavy);
                    break;
                case SimEventType.Posterizer:
                    _fx.Ring(e.Key, new Vector3(e.X.ToFloat(), 0.03f, 0f), Motif(e.Actor), 4.5f, 0.7f, age);
                    MotifBurst(e.Actor, BallPos(), 1.6f);
                    _court.Hoop?.DunkHang(0.6f);
                    _heat.AddHighlight(0.6f);
                    Haptic(e, HapticKind.Heavy);
                    break;
                case SimEventType.Splash:
                    _fx.Burst(BurstKind.Droplets, RimPos(), 40, new Color(0.85f, 0.92f, 1f, 0.9f), 3.5f, 0.05f);
                    _heat.AddHighlight(0.25f);
                    break;
                case SimEventType.Denied:
                    _fx.Flash(e.Key, At(e) + Vector3.up, Motif(e.Actor), 3f, 0.3f, age);
                    MotifBurst(e.Actor, At(e) + Vector3.up, 1.4f);
                    _cam.SetFinisher(false, Vector3.zero, 0f, null);
                    _heat.AddHighlight(0.5f);
                    Haptic(e, HapticKind.Success);
                    break;
                case SimEventType.BuzzerBeater:
                    _heat.AddHighlight(0.6f);
                    break;
                case SimEventType.BlowBy:
                    Raise(e, "callout.blowby", "BLOW-BY", CalloutStyle.Info, age);
                    _fx.Burst(BurstKind.Dust, At(e) + Vector3.up * 0.05f, 12, new Color(0.8f, 0.75f, 0.68f, 0.5f), 1.5f, 0.1f);
                    break;
                case SimEventType.Bite:
                    MotifBurst(1 - e.Actor, At(e) + Vector3.up * 1.2f, 0.5f);
                    break;
                case SimEventType.ComboLink:
                    if (e.A >= 3) Raise(e, "callout.combo", "x" + e.A + " COMBO", CalloutStyle.Info, age);
                    break;
                case SimEventType.HeatCheckOn:
                    Raise(e, "callout.heatcheck", "HEAT CHECK", CalloutStyle.Info, age);
                    f?.SetHeat(true);
                    _heat.AddHighlight(0.2f);
                    break;
                case SimEventType.HeatCheckMiss:
                    f?.SetHeat(false);
                    break;
                case SimEventType.RhythmStack:
                    if (e.A >= 3) Raise(e, "callout.rhythm", "IN RHYTHM", CalloutStyle.Info, age);
                    break;
                case SimEventType.HangTime:
                    Raise(e, "callout.hangtime", "HANG TIME", CalloutStyle.Info, age);
                    MotifBurst(e.Actor, f != null ? f.HeadPosition : At(e), 0.6f);
                    break;
                case SimEventType.SuddenDeath:
                    Raise(e, "callout.suddendeath", "NEXT BUCKET WINS", CalloutStyle.Big, age);
                    _heat.AddHighlight(0.4f);
                    break;
                case SimEventType.ShotMade:
                {
                    if (f != null) _fighters[e.Actor].SetHeat((_state.Get(e.Actor).Status & StatusFlag.HeatCheck) != 0);
                    if (_state.RoundWinner == e.Actor && _state.Get(e.Actor).RoundsWon + 1 >= _state.RoundsToWin) _court.MatchPoint(RimPos());
                    if (e.A >= 3) _heat.AddHighlight(0.15f);
                    if (_court.Jumbotron != null) _court.ShowHighlight(f != null ? f.Stats.Def.Nickname.ToUpperInvariant() + " +" + e.A : "+" + e.A);
                    break;
                }
                case SimEventType.NetSnap:
                    _court.Hoop?.NetSnap(e.A == (int)ShotResult.Swish ? 1f : 0.6f);
                    break;
                case SimEventType.RimHit:
                    _court.Hoop?.RimHit(e.A == 1 ? 0.6f : 1f);
                    break;
                case SimEventType.FinisherStart:
                {
                    FighterStats st = e.A >= 0 && e.A < _tables.FighterCount ? _tables.Fighters[e.A] : null;
                    string name = st != null ? st.Def.Finisher.Name.ToUpperInvariant() : "FINISHER";
                    Raise(e, "finisher." + (st != null ? st.Def.Finisher.Id : "x"), name, CalloutStyle.Finisher, age);
                    float seconds = st != null ? st.Def.Finisher.Frames / 60f : 2.5f;
                    if (f != null) _court.FinisherMoment(f.Visual.MotifKind, f.Feet, f.Visual.Motif, seconds);
                    _heat.AddHighlight(0.5f);
                    Haptic(e, HapticKind.Medium);
                    break;
                }
                case SimEventType.FinisherScore:
                    MotifBurst(e.Actor, RimPos(), 2f);
                    _court.Hoop?.DunkHang(0.8f);
                    _cam.SetFinisher(false, Vector3.zero, 0f, null);
                    Haptic(e, HapticKind.Success);
                    break;
                case SimEventType.RoundEnd:
                    Raise(e, "callout.roundend", (Fighter(e.A) != null ? Fighter(e.A).Stats.Def.Nickname.ToUpperInvariant() : "") + " TAKES THE ROUND", CalloutStyle.Big, age);
                    break;
                case SimEventType.MatchEnd:
                    _court.MatchPoint(RimPos());
                    break;
                case SimEventType.Land:
                    if (f != null) _court.Footstep(f.Feet, f.Primary);
                    break;
                case SimEventType.PostBump:
                    _fx.Ring(e.Key, new Vector3(e.X.ToFloat(), 0.02f, 0f), Motif(e.Actor), e.B == 1 ? 2.2f : 1.1f, 0.35f, age);
                    if (e.B == 1) Haptic(e, HapticKind.Medium);
                    break;
                case SimEventType.Lag:
                    MotifBurst(e.Actor == 0 ? 1 : 0, At(e) + Vector3.up * 1.2f, 0.7f);
                    break;
                case SimEventType.ShotClockViolation:
                    Haptic(e, HapticKind.Warning);
                    break;
            }
        }

        private void Raise(in SimEvent e, string id, string text, CalloutStyle style, float age)
        {
            Callout?.Invoke(new CalloutInfo { Key = e.Key, Id = id, Text = text, Actor = e.Actor, Style = style, Age = age });
        }

        private void Crowd(string reaction)
        {
            switch (reaction)
            {
                case "pop": _heat.AddHighlight(0.15f); _audio?.Cheer(false); break;
                case "erupt": _heat.AddHighlight(0.3f); _audio?.Cheer(true); break;
                case "frenzy": _heat.AddHighlight(0.5f); _audio?.Cheer(true); break;
                case "groan": _heat.AddHighlight(-0.2f); break; // the groan itself plays from AudioDirector.OnSimEvent
            }
        }

        private void Haptic(in SimEvent e, HapticKind kind)
        {
            // Full strength when the local player is involved, lighter otherwise.
            bool mine = e.Actor == _local || e.A == _local;
            Haptics.Play(kind, mine ? 1f : 0.5f);
        }

        private Vector3 FocusFor(in SimEvent e)
        {
            if (e.Type == SimEventType.Splash || e.Type == SimEventType.Posterizer || e.Type == SimEventType.BuzzerBeater) return RimPos();
            if (e.Actor < 2 && Fighter(e.Actor) != null) return Fighter(e.Actor).Feet + Vector3.up * 1.2f;
            return At(e);
        }

        private Vector3 BallPos() => new Vector3(_state.Ball.X.ToFloat(), _state.Ball.Y.ToFloat(), 0f);

        private Vector3 RimPos() => new Vector3(_tables.T.Court.RimX.ToFloat(), _tables.T.Court.RimY.ToFloat(), 0f);

        /// <summary>The fighter's effect identity: solar embers, storm droplets and bolts, glitch pixels, seismic rings.</summary>
        private void MotifBurst(int actor, Vector3 pos, float scale)
        {
            FighterView f = Fighter(actor);
            if (f == null) return;
            Color c = f.Visual.Motif;
            switch (f.Visual.MotifKind)
            {
                case "solar":
                    _fx.Burst(BurstKind.Embers, pos, Mathf.RoundToInt(30 * scale), c, 3f * scale, 0.07f, 1f);
                    _fx.Glow(0, pos, new Color(c.r, c.g, c.b, 0.5f), 1.2f * scale, 0.35f);
                    break;
                case "storm":
                    _fx.Burst(BurstKind.Droplets, pos, Mathf.RoundToInt(36 * scale), c, 4f * scale, 0.05f, 0.8f);
                    _fx.Bolt(0, pos, new Color(0.9f, 0.93f, 1f), 1.4f * scale, 0.2f);
                    break;
                case "glitch":
                    _fx.Burst(BurstKind.Pixels, pos, Mathf.RoundToInt(40 * scale), c, 3.5f * scale, 0.09f, 0.5f);
                    _fx.Flash(0, pos, new Color(c.r, c.g, c.b, 0.6f), 0.9f * scale, 0.12f);
                    break;
                default:
                    _fx.Ring(0, new Vector3(pos.x, 0.03f, f.Depth), c, 2f * scale, 0.5f);
                    _fx.Burst(BurstKind.Shards, new Vector3(pos.x, 0.1f, f.Depth), Mathf.RoundToInt(24 * scale), new Color(0.55f, 0.52f, 0.48f), 3f * scale, 0.08f, 0.9f);
                    break;
            }
        }
    }
}
