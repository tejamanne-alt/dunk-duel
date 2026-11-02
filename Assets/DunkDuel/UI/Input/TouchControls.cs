using DunkDuel.Sim;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace DunkDuel.UI
{
    /// <summary>
    /// Landscape touch controls: a floating stick on the left half (appears where the thumb lands, 8-way with a
    /// dead zone, right is toward the hoop) and SHOOT / SHAKE / TRICK / TURBO / FINISHER on the right.
    /// Swipe up from TRICK within a few frames sends the Amped version. FINISHER lights at full Hype and becomes
    /// DENY during the opponent's Finisher. Multi-touch; respects the safe area via its parent.
    /// </summary>
    public sealed class TouchControls : MonoBehaviour
    {
        private HumanInput _input;
        private StickZone _stick;
        private TouchButton _shoot, _shake, _trick, _turbo, _finisher;
        private UIText _finisherLabel;
        private Image _finisherImage;
        private float _pulse;

        public static TouchControls Create(RectTransform parent, HumanInput input, bool leftHanded)
        {
            RectTransform root = UIKit.Node(parent, "TouchControls");
            UIKit.Stretch(root);
            var tc = root.gameObject.AddComponent<TouchControls>();
            tc._input = input;

            // Stick zone: the half of the screen away from the buttons.
            RectTransform zone = UIKit.Node(root, "StickZone");
            zone.anchorMin = leftHanded ? new Vector2(0.5f, 0f) : new Vector2(0f, 0f);
            zone.anchorMax = leftHanded ? new Vector2(1f, 0.78f) : new Vector2(0.5f, 0.78f);
            zone.offsetMin = Vector2.zero;
            zone.offsetMax = Vector2.zero;
            var hit = zone.gameObject.AddComponent<Image>();
            hit.color = new Color(1f, 1f, 1f, 0f);
            tc._stick = zone.gameObject.AddComponent<StickZone>();
            tc._stick.Init(input, zone);

            // Buttons in a thumb arc.
            float side = leftHanded ? 0f : 1f;
            float sign = leftHanded ? 1f : -1f;
            tc._shoot = Make(root, "SHOOT", Buttons.Shoot, input, new Vector2(side, 0f), new Vector2(sign * 230f, 200f), 230f, new Color(1f, 0.55f, 0.2f, 0.85f));
            tc._shake = Make(root, "SHAKE", Buttons.Shake, input, new Vector2(side, 0f), new Vector2(sign * 470f, 140f), 170f, new Color(0.3f, 0.7f, 1f, 0.8f));
            tc._trick = Make(root, "TRICK", Buttons.Trick, input, new Vector2(side, 0f), new Vector2(sign * 420f, 360f), 170f, new Color(0.75f, 0.45f, 1f, 0.8f));
            tc._trick.SwipeForAmp = true;
            tc._turbo = Make(root, "TURBO", Buttons.Turbo, input, new Vector2(side, 0f), new Vector2(sign * 160f, 440f), 150f, new Color(0.35f, 0.9f, 0.55f, 0.8f));
            tc._finisher = Make(root, "FINISHER", Buttons.Finisher, input, new Vector2(side, 0f), new Vector2(sign * 640f, 300f), 150f, new Color(0.45f, 0.45f, 0.45f, 0.6f));
            tc._finisherImage = tc._finisher.GetComponent<Image>();
            tc._finisherLabel = UIText.Wrap((RectTransform)tc._finisher.transform.GetChild(0));
            return tc;
        }

        private static TouchButton Make(RectTransform root, string label, Buttons b, HumanInput input, Vector2 anchor, Vector2 pos, float size, Color c)
        {
            Image img = UIKit.Circle(root, label, c);
            UIKit.Place(img.rectTransform, anchor, new Vector2(0.5f, 0.5f), pos, new Vector2(size, size));
            UIText t = UIKit.Label(img.transform, label, size * 0.17f, TextAlign.Center, Color.white, true);
            UIKit.Stretch(t.Rect);
            var tb = img.gameObject.AddComponent<TouchButton>();
            tb.Init(input, b, img);
            return tb;
        }

        /// <summary>FINISHER lights at full Hype in the trigger zone; becomes DENY during the opponent's Finisher.</summary>
        public void SetFinisherState(bool ready, bool deny, bool denyWindow)
        {
            _pulse += Time.unscaledDeltaTime * 6f;
            if (deny)
            {
                _finisherLabel.Text = "DENY";
                float glow = denyWindow ? 1f : 0.6f + 0.2f * Mathf.Sin(_pulse);
                _finisherImage.color = new Color(1f, 0.25f, 0.3f, glow);
            }
            else
            {
                _finisherLabel.Text = "FINISHER";
                _finisherImage.color = ready ? new Color(1f, 0.82f, 0.25f, 0.75f + 0.25f * Mathf.Sin(_pulse)) : new Color(0.45f, 0.45f, 0.45f, 0.45f);
            }
        }

        public void ReleaseAll()
        {
            _input.TouchHeld = Buttons.None;
            _input.TouchH = 0;
            _input.TouchV = 0;
            _stick.ResetStick();
        }

        private void OnDisable()
        {
            if (_input != null) ReleaseAll();
        }
    }

    /// <summary>Floating stick: appears under the thumb; 8-way with a dead zone.</summary>
    public sealed class StickZone : MonoBehaviour, IPointerDownHandler, IDragHandler, IPointerUpHandler
    {
        private const float DeadZone = 26f;
        private const float Radius = 120f;
        private HumanInput _input;
        private RectTransform _zone;
        private Image _base;
        private Image _knob;
        private int _pointer = int.MinValue;
        private Vector2 _origin;

        public void Init(HumanInput input, RectTransform zone)
        {
            _input = input;
            _zone = zone;
            _base = UIKit.Circle(zone, "StickBase", new Color(1f, 1f, 1f, 0.15f), true);
            _base.rectTransform.sizeDelta = new Vector2(Radius * 2f, Radius * 2f);
            _knob = UIKit.Circle(zone, "StickKnob", new Color(1f, 1f, 1f, 0.45f));
            _knob.rectTransform.sizeDelta = new Vector2(110f, 110f);
            _base.raycastTarget = false;
            _knob.raycastTarget = false;
            SetVisible(false);
        }

        private void SetVisible(bool on)
        {
            _base.enabled = on;
            _knob.enabled = on;
        }

        private Vector2 Local(PointerEventData e)
        {
            RectTransformUtility.ScreenPointToLocalPointInRectangle(_zone, e.position, e.pressEventCamera, out Vector2 p);
            return p;
        }

        public void OnPointerDown(PointerEventData e)
        {
            if (_pointer != int.MinValue) return;
            _pointer = e.pointerId;
            _origin = Local(e);
            _base.rectTransform.anchoredPosition = _origin;
            _knob.rectTransform.anchoredPosition = _origin;
            _base.rectTransform.anchorMin = _base.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
            _knob.rectTransform.anchorMin = _knob.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
            SetVisible(true);
            Apply(Vector2.zero);
        }

        public void OnDrag(PointerEventData e)
        {
            if (e.pointerId != _pointer) return;
            Vector2 offset = Local(e) - _origin;
            if (offset.magnitude > Radius) offset = offset.normalized * Radius;
            _knob.rectTransform.anchoredPosition = _origin + offset;
            Apply(offset);
        }

        public void OnPointerUp(PointerEventData e)
        {
            if (e.pointerId != _pointer) return;
            ResetStick();
        }

        public void ResetStick()
        {
            _pointer = int.MinValue;
            SetVisible(false);
            Apply(Vector2.zero);
        }

        private void Apply(Vector2 offset)
        {
            if (offset.magnitude < DeadZone)
            {
                _input.TouchH = 0;
                _input.TouchV = 0;
                return;
            }
            // 8 sectors of 45 degrees.
            float angle = Mathf.Atan2(offset.y, offset.x) * Mathf.Rad2Deg;
            int sector = Mathf.RoundToInt(angle / 45f);
            if (sector < 0) sector += 8;
            int[] hs = { 1, 1, 0, -1, -1, -1, 0, 1 };
            int[] vs = { 0, 1, 1, 1, 0, -1, -1, -1 };
            _input.TouchH = hs[sector % 8];
            _input.TouchV = vs[sector % 8];
        }
    }

    /// <summary>A held touch button. TRICK waits a few frames for a swipe-up to send TRICK + AMP.</summary>
    public sealed class TouchButton : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IDragHandler
    {
        private const float SwipeUp = 45f;
        private const float AmpWindow = 0.075f;
        public bool SwipeForAmp;
        private HumanInput _input;
        private Buttons _button;
        private Image _img;
        private Color _color;
        private int _pointer = int.MinValue;
        private bool _pending;
        private float _pendingTime;
        private Vector2 _downPos;
        private bool _amped;

        public void Init(HumanInput input, Buttons button, Image img)
        {
            _input = input;
            _button = button;
            _img = img;
            _color = img.color;
        }

        public void OnPointerDown(PointerEventData e)
        {
            if (_pointer != int.MinValue) return;
            _pointer = e.pointerId;
            _downPos = e.position;
            transform.localScale = new Vector3(0.92f, 0.92f, 1f);
            if (SwipeForAmp)
            {
                _pending = true;
                _pendingTime = 0f;
                _amped = false;
                return;
            }
            Press();
        }

        private void Press()
        {
            _input.TouchHeld |= _button;
            _input.Latch(_button);
        }

        public void OnDrag(PointerEventData e)
        {
            if (e.pointerId != _pointer || !_pending) return;
            if (e.position.y - _downPos.y >= SwipeUp)
            {
                _pending = false;
                _amped = true;
                _input.TouchHeld |= _button | Buttons.Amp;
                _input.Latch(_button | Buttons.Amp);
            }
        }

        private void Update()
        {
            if (!_pending) return;
            _pendingTime += Time.unscaledDeltaTime;
            if (_pendingTime >= AmpWindow)
            {
                _pending = false;
                Press();
            }
        }

        public void OnPointerUp(PointerEventData e)
        {
            if (e.pointerId != _pointer) return;
            _pointer = int.MinValue;
            transform.localScale = Vector3.one;
            if (_pending)
            {
                // Released before the swipe window: a plain tap.
                _pending = false;
                _input.Latch(_button);
            }
            _input.TouchHeld &= ~(_button | (_amped ? Buttons.Amp : Buttons.None));
            _amped = false;
        }

        private void OnDisable()
        {
            _pointer = int.MinValue;
            _pending = false;
            transform.localScale = Vector3.one;
            if (_img != null) _img.color = _color;
        }
    }
}
