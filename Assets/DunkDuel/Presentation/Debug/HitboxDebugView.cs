using DunkDuel.Sim;
using DunkDuel.Sim.Data;
using UnityEngine;

namespace DunkDuel.Presentation
{
    /// <summary>
    /// Training hitbox view: pushboxes (solid = blocks drives; dashed color when passable), standing reach and
    /// jump apex, the active swipe reach, the ball radius, the arc and the finisher trigger zone. Draws the
    /// sim's numbers directly (tables + state), so what you see is what the rules use.
    /// </summary>
    public sealed class HitboxDebugView : MonoBehaviour
    {
        private LineRenderer[] _push;
        private LineRenderer[] _reach;
        private LineRenderer[] _swipe;
        private LineRenderer _ball;
        private LineRenderer[] _zones;
        private SimTables _tables;
        public bool Visible { get; private set; }

        public static HitboxDebugView Create(Transform parent, SimTables tables, MaterialLibrary mats)
        {
            var go = new GameObject("Hitboxes");
            go.transform.SetParent(parent, false);
            var v = go.AddComponent<HitboxDebugView>();
            v._tables = tables;
            v._push = new[] { v.Line("Push0", mats, 5), v.Line("Push1", mats, 5) };
            v._reach = new[] { v.Line("Reach0", mats, 2), v.Line("Reach1", mats, 2) };
            v._swipe = new[] { v.Line("Swipe0", mats, 2), v.Line("Swipe1", mats, 2) };
            v._ball = v.Line("Ball", mats, 17);
            v._zones = new[] { v.Line("Zone0", mats, 2), v.Line("Zone1", mats, 2) };
            v.SetVisible(false);
            return v;
        }

        private LineRenderer Line(string name, MaterialLibrary mats, int points)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            var lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = true;
            lr.positionCount = points;
            lr.startWidth = 0.025f;
            lr.endWidth = 0.025f;
            lr.material = mats.TransparentInstance(Color.white, false);
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return lr;
        }

        public void SetVisible(bool on)
        {
            Visible = on;
            gameObject.SetActive(on);
        }

        public void Apply(in GameState s, FighterView[] fighters)
        {
            if (!Visible) return;
            for (int i = 0; i < 2; i++)
            {
                FighterState f = s.Get(i);
                FighterStats st = _tables.Fighters[f.CharIndex];
                float x = f.X.ToFloat(), y = f.Y.ToFloat();
                float w = st.PushW.ToFloat(), h = st.Height.ToFloat();
                float z = fighters[i].Depth;
                bool solid = f.Y <= _tables.T.Physics.AirborneHeight && f.Beaten == 0 && f.Action != ActionId.OffBalance && f.Action != ActionId.Stumble;
                Color c = solid ? new Color(0.2f, 1f, 0.4f, 0.9f) : new Color(1f, 0.85f, 0.2f, 0.9f);
                if (f.Action == ActionId.Wall && f.PlantFrames >= _tables.T.Read.PlantFramesForCharge) c = new Color(1f, 0.25f, 0.25f, 0.95f);
                Rect(_push[i], x - w, y, x + w, y + h, z, c);
                float reach = st.Reach.ToFloat();
                SetLine(_reach[i], new Vector3(x, y + reach, z), new Vector3(x, y + reach + st.JumpHeight.ToFloat(), z), new Color(0.4f, 0.7f, 1f, 0.9f));
                bool swiping = f.Action == ActionId.Swipe || f.Action == ActionId.Trick && st.Tricks[f.MoveSlot].Kind == MoveKind.DefenseTrick;
                float swipeReach = f.Action == ActionId.Swipe ? (_tables.Moves.Swipe.Reach + st.SwipeReachBonus).ToFloat() : st.Tricks[(int)TrickSlot.Defense].Reach.ToFloat();
                Vector3 hand = new Vector3(x, y + h * 0.6f, z);
                SetLine(_swipe[i], hand, hand + new Vector3(f.Facing * (swiping ? swipeReach : 0.01f), 0f, 0f), new Color(1f, 0.3f, 0.9f, swiping ? 1f : 0f));
                FinisherDefinition fd = st.Def.Finisher;
                SetLine(_zones[i], new Vector3(fd.ZoneMin.ToFloat(), 0.03f, z), new Vector3(fd.ZoneMax.ToFloat(), 0.03f, z), new Color(fighters[i].Primary.r, fighters[i].Primary.g, fighters[i].Primary.b, f.Hype >= _tables.T.Hype.FinisherCost ? 1f : 0.25f));
            }
            float r = _tables.T.Court.BallRadius.ToFloat();
            Vector3 b = new Vector3(s.Ball.X.ToFloat(), s.Ball.Y.ToFloat(), 0f);
            for (int k = 0; k < 17; k++)
            {
                float a = k / 16f * Mathf.PI * 2f;
                _ball.SetPosition(k, b + new Vector3(Mathf.Cos(a) * r, Mathf.Sin(a) * r, 0f));
            }
            _ball.startColor = _ball.endColor = new Color(1f, 0.6f, 0.2f, 0.9f);
        }

        private static void Rect(LineRenderer lr, float x0, float y0, float x1, float y1, float z, Color c)
        {
            lr.SetPosition(0, new Vector3(x0, y0, z));
            lr.SetPosition(1, new Vector3(x1, y0, z));
            lr.SetPosition(2, new Vector3(x1, y1, z));
            lr.SetPosition(3, new Vector3(x0, y1, z));
            lr.SetPosition(4, new Vector3(x0, y0, z));
            lr.startColor = c;
            lr.endColor = c;
        }

        private static void SetLine(LineRenderer lr, Vector3 a, Vector3 b, Color c)
        {
            lr.SetPosition(0, a);
            lr.SetPosition(1, b);
            lr.startColor = c;
            lr.endColor = c;
        }
    }
}
