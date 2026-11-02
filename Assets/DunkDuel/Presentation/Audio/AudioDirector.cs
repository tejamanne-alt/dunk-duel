using System;
using DunkDuel.Sim;
using UnityEngine;

namespace DunkDuel.Presentation
{
    /// <summary>
    /// Plays synthesized SFX for sim events, a crowd bed scaled by Stage Heat, layered music stems per court
    /// style (calm: pad + light drums, hyped: + drums and bass, frenzy: everything) and per-court reverb.
    /// Persists across matches (one instance on the app root); presentation tells it which court is live.
    /// </summary>
    public sealed class AudioDirector : MonoBehaviour
    {
        private const int OneShotVoices = 10;

        public float MasterVolume = 1f;
        public float SfxVolume = 1f;
        public float MusicVolume = 0.6f;
        public float CrowdVolume = 0.8f;

        private AudioSource[] _voices;
        private int _nextVoice;
        private AudioSource _crowd;
        private AudioSource[] _stems;
        private AudioReverbFilter _reverb;
        private AudioClip _dribble, _squeak, _squeakHigh, _swish, _net, _rim, _board, _buzzer, _whoosh, _thud, _cheer, _bigCheer, _groan, _thunder, _train;
        private AudioClip _stinger, _organ, _brass;
        private string _musicStyle = "";
        private float _heat;
        private float _cheerCooldown;
        private bool _inMatch;

        public static AudioDirector Create(Transform parent)
        {
            var go = new GameObject("Audio");
            go.transform.SetParent(parent, false);
            var a = go.AddComponent<AudioDirector>();
            a.Build();
            return a;
        }

        private void Build()
        {
            _voices = new AudioSource[OneShotVoices];
            for (int i = 0; i < _voices.Length; i++) _voices[i] = Source("Voice" + i, false);
            _crowd = Source("Crowd", true);
            _stems = new[] { Source("StemDrums", true), Source("StemBass", true), Source("StemPad", true) };
            _dribble = SynthClips.Dribble();
            _squeak = SynthClips.Squeak(1f);
            _squeakHigh = SynthClips.Squeak(1.3f);
            _swish = SynthClips.Swish();
            _net = SynthClips.NetSnap();
            _rim = SynthClips.Rim();
            _board = SynthClips.Backboard();
            _buzzer = SynthClips.Buzzer();
            _whoosh = SynthClips.Whoosh();
            _thud = SynthClips.Thud();
            _cheer = SynthClips.Cheer(1.6f, 31);
            _bigCheer = SynthClips.Cheer(2.8f, 97);
            _groan = SynthClips.Groan();
            _thunder = SynthClips.Rumble(2.6f, 0f);
            _train = SynthClips.Rumble(4.2f, 2.2f);
            _stinger = SynthClips.Stinger("pad");
            _organ = SynthClips.Stinger("organ");
            _brass = SynthClips.Stinger("brass");
            _crowd.clip = SynthClips.CrowdBed();
        }

        private AudioSource Source(string name, bool loop)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            var src = go.AddComponent<AudioSource>();
            src.playOnAwake = false;
            src.loop = loop;
            src.spatialBlend = 0f;
            return src;
        }

        /// <summary>Attach the court's reverb to the listener and start that court's music stems.</summary>
        public void EnterCourt(CourtDef court, AudioListener listener)
        {
            _inMatch = true;
            if (listener != null)
            {
                _reverb = listener.GetComponent<AudioReverbFilter>();
                if (_reverb == null) _reverb = listener.gameObject.AddComponent<AudioReverbFilter>();
                AudioReverbPreset preset;
                if (!Enum.TryParse(court.Reverb, true, out preset)) preset = AudioReverbPreset.Room;
                _reverb.reverbPreset = preset;
            }
            if (_musicStyle != court.Music + court.Tempo)
            {
                _musicStyle = court.Music + court.Tempo;
                AudioClip[] stems = SynthClips.MusicStems(court.Music, court.Tempo);
                for (int i = 0; i < _stems.Length; i++)
                {
                    _stems[i].Stop();
                    _stems[i].clip = stems[i];
                }
            }
            for (int i = 0; i < _stems.Length; i++)
            {
                _stems[i].volume = 0f;
                _stems[i].Play();
            }
            _crowd.volume = 0f;
            _crowd.Play();
        }

        public void LeaveCourt()
        {
            _inMatch = false;
            for (int i = 0; i < _stems.Length; i++) _stems[i].Stop();
            _crowd.Stop();
        }

        public void SetHeat(float heat) => _heat = heat;

        private void Update()
        {
            _cheerCooldown -= Time.unscaledDeltaTime;
            if (!_inMatch) return;
            float m = MasterVolume * MusicVolume;
            float k = 1f - Mathf.Exp(-Time.unscaledDeltaTime * 2f);
            float drums = Mathf.Lerp(0.25f, 1f, Mathf.Clamp01(_heat * 1.6f));
            float bass = Mathf.Clamp01((_heat - 0.3f) * 2.5f);
            float pad = Mathf.Lerp(0.8f, 0.5f, _heat);
            _stems[0].volume = Mathf.Lerp(_stems[0].volume, drums * m, k);
            _stems[1].volume = Mathf.Lerp(_stems[1].volume, bass * m, k);
            _stems[2].volume = Mathf.Lerp(_stems[2].volume, pad * m, k);
            _crowd.volume = Mathf.Lerp(_crowd.volume, MasterVolume * CrowdVolume * Mathf.Lerp(0.15f, 0.9f, _heat), k);
            _crowd.pitch = Mathf.Lerp(0.95f, 1.08f, _heat);
        }

        public void Play(AudioClip clip, float volume = 1f, float pitch = 1f)
        {
            if (clip == null) return;
            AudioSource v = _voices[_nextVoice];
            _nextVoice = (_nextVoice + 1) % _voices.Length;
            v.pitch = pitch;
            v.PlayOneShot(clip, volume * SfxVolume * MasterVolume);
        }

        public void DribbleBounce(float speed) => Play(_dribble, 0.55f + 0.2f * Mathf.Clamp01(speed / 6f), 0.95f + VisualNoise.Hash01(Time.frameCount) * 0.1f);

        public void Cheer(bool big)
        {
            if (_cheerCooldown > 0f) return;
            _cheerCooldown = big ? 1.2f : 0.6f;
            Play(big ? _bigCheer : _cheer, (big ? 0.9f : 0.6f) * CrowdVolume);
        }

        public void SetPiece(string kind)
        {
            switch (kind)
            {
                case "downpour":
                case "blackout": Play(_thunder, 0.9f); break;
                case "train": Play(_train, 0.8f); break;
                case "shatter": Play(_organ, 0.8f); Play(_board, 1f, 1.6f); break;
                case "surge": Play(_brass, 0.85f); break;
                default: Play(_stinger, 0.7f); break;
            }
        }

        /// <summary>Event-driven SFX. <paramref name="age"/> &gt; 0 means a late (rolled-back) spawn: skip short sounds.</summary>
        public void OnSimEvent(in SimEvent e, int age)
        {
            if (age > 4) return;
            switch (e.Type)
            {
                case SimEventType.NetSnap: Play(e.A == (int)ShotResult.Swish ? _swish : _net, 0.9f); break;
                case SimEventType.RimHit: Play(_rim, 0.7f, 0.95f + VisualNoise.Hash01(e.Frame) * 0.1f); break;
                case SimEventType.BackboardHit: Play(_board, 0.8f); break;
                case SimEventType.Jump: Play(_squeak, 0.35f, 1.1f); break;
                case SimEventType.Land: Play(_thud, 0.25f, 1.4f); break;
                case SimEventType.Plant: Play(_squeakHigh, 0.45f); break;
                case SimEventType.ShakeStart: Play(_squeak, 0.4f, 0.9f + 0.1f * e.B); break;
                case SimEventType.Swipe: Play(_whoosh, 0.5f, 1.2f); break;
                case SimEventType.TrickStart: Play(_whoosh, 0.6f, 0.8f); break;
                case SimEventType.Steal: Play(_whoosh, 0.8f, 1.5f); Cheer(false); break;
                case SimEventType.Block: Play(_board, 1f, 0.7f); Cheer(true); break;
                case SimEventType.Charge: Play(_thud, 1f, 0.8f); Cheer(false); break;
                case SimEventType.AnkleBreak: Play(_squeakHigh, 0.9f, 0.7f); Cheer(true); break;
                case SimEventType.Posterizer: Play(_rim, 1f, 0.7f); Cheer(true); break;
                case SimEventType.ShotMade: Cheer(e.A >= 3); break;
                case SimEventType.Airball: Play(_groan, 0.7f); break;
                case SimEventType.ShotClockViolation: Play(_buzzer, 0.5f, 1.2f); Play(_groan, 0.6f); break;
                case SimEventType.ClockExpired: Play(_buzzer, 0.8f); break;
                case SimEventType.BuzzerBeater: Cheer(true); break;
                case SimEventType.Denied: Play(_thud, 1f, 0.6f); Cheer(true); break;
                case SimEventType.FinisherStart: Play(_stinger, 0.8f); break;
                case SimEventType.FinisherScore: Cheer(true); break;
                case SimEventType.RoundEnd: Play(_buzzer, 0.7f, 0.9f); break;
                case SimEventType.MatchEnd: Cheer(true); break;
                case SimEventType.Knockdown: Play(_thud, 0.8f); break;
            }
        }
    }
}
