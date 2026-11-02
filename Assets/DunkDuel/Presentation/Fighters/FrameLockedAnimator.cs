using System.Collections.Generic;
using DunkDuel.Sim;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace DunkDuel.Presentation
{
    /// <summary>
    /// Frame-locked animation for final art: each sim state maps to a clip and a frame, sampled by sim frame
    /// through a manually evaluated PlayableGraph. Animator transitions never drive logic; time never advances
    /// on its own. Root motion is not applied (positions come from the sim; root curves are baked at import).
    /// </summary>
    [RequireComponent(typeof(Animator))]
    public sealed class FrameLockedAnimator : MonoBehaviour
    {
        public const float ClipFrameRate = 60f;

        private PlayableGraph _graph;
        private AnimationPlayableOutput _output;
        private AnimationMixerPlayable _mixer;
        private AnimationClipPlayable _a;
        private AnimationClipPlayable _b;
        private AnimationClip _clipA;
        private AnimationClip _clipB;
        private readonly Dictionary<long, AnimationClip> _clips = new Dictionary<long, AnimationClip>();

        public bool HasClips => _clips.Count > 0;

        private void Awake()
        {
            var animator = GetComponent<Animator>();
            animator.applyRootMotion = false;
            _graph = PlayableGraph.Create("DunkDuelFrameLocked");
            _graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
            _output = AnimationPlayableOutput.Create(_graph, "Animation", animator);
            _mixer = AnimationMixerPlayable.Create(_graph, 2);
            _output.SetSourcePlayable(_mixer);
        }

        /// <summary>Registers the clip for an action (and optional move slot) of this fighter.</summary>
        public void Map(ActionId action, int moveSlot, AnimationClip clip)
        {
            if (clip != null) _clips[Key(action, moveSlot)] = clip;
        }

        private static long Key(ActionId a, int slot) => ((long)a << 8) | (uint)(slot & 0xFF);

        public AnimationClip Find(ActionId action, int moveSlot)
        {
            if (_clips.TryGetValue(Key(action, moveSlot), out AnimationClip c)) return c;
            return _clips.TryGetValue(Key(action, 0), out c) ? c : null;
        }

        /// <summary>Samples the clip for the given sim action at <paramref name="frame"/>, blending from the previous clip.</summary>
        public void Sample(ActionId action, int moveSlot, float frame, float blendFromPrevious = 0f)
        {
            AnimationClip clip = Find(action, moveSlot);
            if (clip == null || !_graph.IsValid()) return;
            if (clip != _clipA)
            {
                // Previous clip moves to slot B for a short cross-fade.
                if (_b.IsValid()) _graph.DestroyPlayable(_b);
                _mixer.DisconnectInput(1);
                _b = _a;
                _clipB = _clipA;
                _mixer.DisconnectInput(0);
                if (_b.IsValid()) _graph.Connect(_b, 0, _mixer, 1);
                _a = AnimationClipPlayable.Create(_graph, clip);
                _a.SetApplyFootIK(false);
                _graph.Connect(_a, 0, _mixer, 0);
                _clipA = clip;
            }
            double t = frame / ClipFrameRate;
            if (clip.isLooping) t %= clip.length;
            else if (t > clip.length) t = clip.length;
            _a.SetTime(t);
            float w = Mathf.Clamp01(blendFromPrevious);
            _mixer.SetInputWeight(0, 1f - w);
            _mixer.SetInputWeight(1, _clipB != null ? w : 0f);
            _graph.Evaluate(0f);
        }

        private void OnDestroy()
        {
            if (_graph.IsValid()) _graph.Destroy();
        }
    }
}
