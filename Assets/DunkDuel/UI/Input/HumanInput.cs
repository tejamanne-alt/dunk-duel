using DunkDuel.Sim;
using DunkDuel.Sim.Session;
using UnityEngine;
#if DD_INPUTSYSTEM
using UnityEngine.InputSystem;
#endif

namespace DunkDuel.UI
{
    /// <summary>
    /// The local player's controller as the sim sees it: touch, gamepad and keyboard merged into one
    /// <see cref="FrameInput"/>, sampled exactly once per sim tick. A press that starts and ends between two
    /// ticks is latched so it is still seen for one tick.
    /// </summary>
    public sealed class HumanInput : IInputSource
    {
        // Touch state written by TouchControls.
        public int TouchH;
        public int TouchV;
        public Buttons TouchHeld;

        private Buttons _latched;
        private Buttons _lastSample;
        public readonly GameInputBindings Bindings = new GameInputBindings();

        /// <summary>Recent inputs for the training input display (newest last).</summary>
        public readonly FrameInput[] History = new FrameInput[24];
        public int HistoryHead { get; private set; }
        public bool Enabled = true;

        public void Latch(Buttons pressed) => _latched |= pressed;

        public FrameInput NextInput(in GameState state, int player)
        {
            if (!Enabled) return FrameInput.None;
            Buttons b = TouchHeld | Bindings.Held() | _latched;
            int h = TouchH + Bindings.Horizontal();
            int v = TouchV + Bindings.Vertical();
            h = h > 0 ? 1 : (h < 0 ? -1 : 0);
            v = v > 0 ? 1 : (v < 0 ? -1 : 0);
            if (h > 0) b |= Buttons.Right;
            if (h < 0) b |= Buttons.Left;
            if (v > 0) b |= Buttons.Up;
            if (v < 0) b |= Buttons.Down;
            _latched = Buttons.None;
            _lastSample = b;
            var input = new FrameInput(b);
            History[HistoryHead] = input;
            HistoryHead = (HistoryHead + 1) % History.Length;
            return input;
        }
    }

    /// <summary>
    /// Gamepad and keyboard bindings. With the Input System package: an InputActionAsset built in code, fully
    /// remappable (interactive rebinding, overrides saved as JSON in settings). Without it: fixed legacy keys.
    /// Defaults: stick/WASD; SHOOT = South/J, SHAKE = West/K, TRICK = North/L, TURBO = Right trigger/Space,
    /// FINISHER/DENY = East/I, AMP = Left shoulder/U. Right on the stick is always toward the hoop.
    /// </summary>
    public sealed class GameInputBindings
    {
        public static readonly string[] ActionNames = { "Shoot", "Shake", "Trick", "Turbo", "Finisher", "Amp" };
        private static readonly Buttons[] ActionButtons = { Buttons.Shoot, Buttons.Shake, Buttons.Trick, Buttons.Turbo, Buttons.Finisher, Buttons.Amp };

#if DD_INPUTSYSTEM
        public InputActionAsset Asset { get; private set; }
        private InputAction _move;
        private readonly InputAction[] _actions = new InputAction[6];
        private InputActionRebindingExtensions.RebindingOperation _rebind;

        public GameInputBindings()
        {
            Asset = ScriptableObject.CreateInstance<InputActionAsset>();
            InputActionMap map = Asset.AddActionMap("Gameplay");
            _move = map.AddAction("Move", InputActionType.Value);
            _move.AddCompositeBinding("2DVector")
                .With("Up", "<Keyboard>/w").With("Down", "<Keyboard>/s").With("Left", "<Keyboard>/a").With("Right", "<Keyboard>/d");
            _move.AddCompositeBinding("2DVector")
                .With("Up", "<Keyboard>/upArrow").With("Down", "<Keyboard>/downArrow").With("Left", "<Keyboard>/leftArrow").With("Right", "<Keyboard>/rightArrow");
            _move.AddBinding("<Gamepad>/leftStick");
            _move.AddBinding("<Gamepad>/dpad");
            string[][] defaults =
            {
                new[] { "<Gamepad>/buttonSouth", "<Keyboard>/j" },
                new[] { "<Gamepad>/buttonWest", "<Keyboard>/k" },
                new[] { "<Gamepad>/buttonNorth", "<Keyboard>/l" },
                new[] { "<Gamepad>/rightTrigger", "<Keyboard>/space" },
                new[] { "<Gamepad>/buttonEast", "<Keyboard>/i" },
                new[] { "<Gamepad>/leftShoulder", "<Keyboard>/u" },
            };
            for (int i = 0; i < ActionNames.Length; i++)
            {
                _actions[i] = map.AddAction(ActionNames[i], InputActionType.Button);
                for (int k = 0; k < defaults[i].Length; k++) _actions[i].AddBinding(defaults[i][k]);
            }
            _actions[3].AddBinding("<Gamepad>/rightShoulder");
            Asset.Enable();
        }

        public Buttons Held()
        {
            Buttons b = Buttons.None;
            for (int i = 0; i < _actions.Length; i++) if (_actions[i].IsPressed()) b |= ActionButtons[i];
            return b;
        }

        private Vector2 Stick()
        {
            Vector2 v = _move.ReadValue<Vector2>();
            return v;
        }

        public int Horizontal()
        {
            float x = Stick().x;
            return x > 0.4f ? 1 : (x < -0.4f ? -1 : 0);
        }

        public int Vertical()
        {
            float y = Stick().y;
            return y > 0.5f ? 1 : (y < -0.5f ? -1 : 0);
        }

        public bool BackPressed()
        {
            Keyboard k = Keyboard.current;
            Gamepad g = Gamepad.current;
            return (k != null && k.escapeKey.wasPressedThisFrame) || (g != null && g.startButton.wasPressedThisFrame);
        }

        public string Describe(int action, int binding)
        {
            if (action < 0 || action >= _actions.Length) return "";
            InputAction a = _actions[action];
            if (binding < 0 || binding >= a.bindings.Count) return "-";
            return a.GetBindingDisplayString(binding);
        }

        public int BindingCount(int action) => _actions[action].bindings.Count;

        /// <summary>Interactive rebinding of one binding; <paramref name="done"/> fires when finished or cancelled.</summary>
        public void Rebind(int action, int binding, System.Action done)
        {
            _rebind?.Dispose();
            InputAction a = _actions[action];
            a.Disable();
            _rebind = a.PerformInteractiveRebinding(binding)
                .WithControlsExcluding("<Mouse>")
                .WithCancelingThrough("<Keyboard>/escape")
                .OnComplete(op =>
                {
                    op.Dispose();
                    _rebind = null;
                    a.Enable();
                    done?.Invoke();
                })
                .OnCancel(op =>
                {
                    op.Dispose();
                    _rebind = null;
                    a.Enable();
                    done?.Invoke();
                })
                .Start();
        }

        public string SaveOverrides() => Asset.SaveBindingOverridesAsJson();

        public void LoadOverrides(string json)
        {
            if (string.IsNullOrEmpty(json)) return;
            try
            {
                Asset.LoadBindingOverridesFromJson(json);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("Dunk Duel: ignoring bad binding overrides: " + e.Message);
            }
        }

        public void ResetOverrides() => Asset.RemoveAllBindingOverrides();
#else
        private static readonly KeyCode[][] Keys =
        {
            new[] { KeyCode.J, KeyCode.JoystickButton0 },
            new[] { KeyCode.K, KeyCode.JoystickButton2 },
            new[] { KeyCode.L, KeyCode.JoystickButton3 },
            new[] { KeyCode.Space, KeyCode.JoystickButton5 },
            new[] { KeyCode.I, KeyCode.JoystickButton1 },
            new[] { KeyCode.U, KeyCode.JoystickButton4 },
        };

        public Buttons Held()
        {
            Buttons b = Buttons.None;
            for (int i = 0; i < Keys.Length; i++)
            {
                for (int k = 0; k < Keys[i].Length; k++) if (Input.GetKey(Keys[i][k])) b |= ActionButtons[i];
            }
            return b;
        }

        public int Horizontal() => (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow) ? 1 : 0) - (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow) ? 1 : 0);
        public int Vertical() => (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow) ? 1 : 0) - (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow) ? 1 : 0);
        public bool BackPressed() => Input.GetKeyDown(KeyCode.Escape);
        public string Describe(int action, int binding) => binding < Keys[action].Length ? Keys[action][binding].ToString() : "-";
        public int BindingCount(int action) => Keys[action].Length;
        public void Rebind(int action, int binding, System.Action done) => done?.Invoke();
        public string SaveOverrides() => "";
        public void LoadOverrides(string json) { }
        public void ResetOverrides() { }
#endif
    }
}
