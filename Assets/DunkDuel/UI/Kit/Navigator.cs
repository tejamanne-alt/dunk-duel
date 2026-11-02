using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
#if DD_INPUTSYSTEM
using UnityEngine.InputSystem.UI;
#endif

namespace DunkDuel.UI
{
    /// <summary>A code-built screen. Overlays stack on top without hiding the screen below.</summary>
    public abstract class UIScreen
    {
        public RectTransform Root { get; private set; }
        protected App App => App.Instance;
        public virtual bool IsOverlay => false;
        public virtual string Name => GetType().Name;

        internal void Create(RectTransform parent)
        {
            Root = UIKit.Node(parent, Name);
            UIKit.Stretch(Root);
            Build(Root);
        }

        protected abstract void Build(RectTransform root);
        public virtual void OnShow() { }
        public virtual void OnHide() { }
        public virtual void Tick(float dt) { }

        /// <summary>Back button / Escape. Return true when handled; false lets the navigator pop.</summary>
        public virtual bool OnBack() => false;
    }

    /// <summary>Screen stack with back-button handling.</summary>
    public sealed class Navigator
    {
        private readonly List<UIScreen> _stack = new List<UIScreen>();
        private readonly RectTransform _layer;

        public Navigator(RectTransform layer)
        {
            _layer = layer;
        }

        public UIScreen Top => _stack.Count > 0 ? _stack[_stack.Count - 1] : null;
        public int Depth => _stack.Count;

        public T Push<T>(T screen) where T : UIScreen
        {
            UIScreen below = Top;
            if (below != null && !screen.IsOverlay)
            {
                below.OnHide();
                below.Root.gameObject.SetActive(false);
            }
            screen.Create(_layer);
            _stack.Add(screen);
            screen.OnShow();
            return screen;
        }

        public void Pop()
        {
            if (_stack.Count == 0) return;
            UIScreen top = Top;
            _stack.RemoveAt(_stack.Count - 1);
            top.OnHide();
            Object.Destroy(top.Root.gameObject);
            UIScreen next = Top;
            if (next != null && !next.Root.gameObject.activeSelf)
            {
                next.Root.gameObject.SetActive(true);
                next.OnShow();
            }
        }

        public T Replace<T>(T screen) where T : UIScreen
        {
            if (_stack.Count > 0)
            {
                UIScreen top = Top;
                _stack.RemoveAt(_stack.Count - 1);
                top.OnHide();
                Object.Destroy(top.Root.gameObject);
            }
            return Push(screen);
        }

        public T ResetTo<T>(T screen) where T : UIScreen
        {
            while (_stack.Count > 0)
            {
                UIScreen top = Top;
                _stack.RemoveAt(_stack.Count - 1);
                top.OnHide();
                Object.Destroy(top.Root.gameObject);
            }
            return Push(screen);
        }

        public void PopTo(UIScreen screen)
        {
            while (_stack.Count > 0 && Top != screen) Pop();
        }

        public void Tick(float dt, bool backPressed)
        {
            UIScreen top = Top;
            if (top == null) return;
            top.Tick(dt);
            if (backPressed && !top.OnBack() && _stack.Count > 1) Pop();
        }
    }

    /// <summary>Overlay canvas (landscape reference 1920 x 1080), event system and a safe-area panel.</summary>
    public sealed class UIRoot : MonoBehaviour
    {
        public Canvas Canvas { get; private set; }
        public RectTransform Safe { get; private set; }
        public RectTransform ScreenLayer { get; private set; }
        public RectTransform PopupLayer { get; private set; }
        private Rect _lastSafe;

        public static UIRoot Create(Transform parent)
        {
            var go = new GameObject("UI", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var root = go.AddComponent<UIRoot>();
            root.Canvas = go.AddComponent<Canvas>();
            root.Canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            root.Canvas.sortingOrder = 10;
            var scaler = go.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 1f;
            go.AddComponent<GraphicRaycaster>();

            if (FindAnyObjectByType<EventSystem>() == null)
            {
                var es = new GameObject("EventSystem");
                es.transform.SetParent(parent, false);
                es.AddComponent<EventSystem>();
#if DD_INPUTSYSTEM
                var module = es.AddComponent<InputSystemUIInputModule>();
                module.AssignDefaultActions();
#else
                es.AddComponent<StandaloneInputModule>();
#endif
            }

            root.Safe = UIKit.Node(go.transform, "SafeArea");
            UIKit.Stretch(root.Safe);
            root.ScreenLayer = UIKit.Node(root.Safe, "Screens");
            UIKit.Stretch(root.ScreenLayer);
            root.PopupLayer = UIKit.Node(root.Safe, "Popups");
            UIKit.Stretch(root.PopupLayer);
            root.ApplySafeArea();
            return root;
        }

        private void Update()
        {
            if (Screen.safeArea != _lastSafe) ApplySafeArea();
        }

        private void ApplySafeArea()
        {
            Rect safe = Screen.safeArea;
            _lastSafe = safe;
            if (Screen.width <= 0 || Screen.height <= 0) return;
            Vector2 min = safe.position;
            Vector2 max = safe.position + safe.size;
            min.x /= Screen.width;
            min.y /= Screen.height;
            max.x /= Screen.width;
            max.y /= Screen.height;
            Safe.anchorMin = min;
            Safe.anchorMax = max;
            Safe.offsetMin = Vector2.zero;
            Safe.offsetMax = Vector2.zero;
        }
    }
}
