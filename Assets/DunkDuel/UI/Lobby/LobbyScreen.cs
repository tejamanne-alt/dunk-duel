using System;
using DunkDuel.Net;
using DunkDuel.Presentation;
using DunkDuel.Sim.Data;
using UnityEngine;
using UnityEngine.UI;

namespace DunkDuel.UI
{
    /// <summary>
    /// Creates the transport for a duel. Bluetooth (iPhone and Android over BLE) or same Wi-Fi / hotspot (LAN UDP);
    /// both implement the same ITransport, so the lobby and the netcode never know which one is underneath.
    /// </summary>
    public static class TransportFactory
    {
        public static ITransport CreateBluetooth() => new DunkDuel.Transport.BLE.BleTransport();

        public static ITransport CreateLan() => new DunkDuel.Transport.LAN.LanTransport();

        public static string RandomCourtCode()
        {
            const string alphabet = "ACDEFGHJKLMNPQRTUVWXY3479"; // no look-alike glyphs
            var rnd = new System.Random();
            var c = new char[4];
            for (int i = 0; i < 4; i++) c[i] = alphabet[rnd.Next(alphabet.Length)];
            return new string(c);
        }
    }

    /// <summary>Bluetooth Duel lobby: Host Court (shows a code) or Join Court (nearby list), then the match.</summary>
    public sealed class LobbyScreen : TitledScreen
    {
        private ITransport _transport;
        private NetLobby _lobby;
        private bool _bluetooth = true;
        private int _fighter;
        private int _court;
        private int _roundsToWin = 2;
        private RectTransform _content;
        private UIText _status;
        private UIText _code;
        private RectTransform _peers;
        private float _peerRefresh;
        private int _peerCount = -1;
        private bool _started;

        protected override void Build(RectTransform root)
        {
            Frame(root, Loc.T("lobby.title", "Bluetooth Duel"));
            _fighter = App.LastFighter >= 0 ? App.LastFighter : 0;
            _court = 0;
            _content = UIKit.Node(Body, "Content");
            UIKit.Stretch(_content);
            ShowHome();
        }

        private void Clear()
        {
            for (int i = _content.childCount - 1; i >= 0; i--) UnityEngine.Object.Destroy(_content.GetChild(i).gameObject);
        }

        private void ShowHome()
        {
            Clear();
            Teardown();
            RectTransform col = UIKit.VStack(_content, "Home", 18, 0, TextAnchor.UpperLeft);
            UIKit.Stretch(col);
            UIKit.Switch(col, Loc.T("lobby.transport", "Use Bluetooth (off = same Wi-Fi or hotspot)"), () => _bluetooth, v => _bluetooth = v, 1200, 96);
            UIKit.Stepper(col, Loc.T("lobby.fighter", "Your fighter"), () => App.Tables.Fighters[_fighter].Def.Nickname, d => _fighter = (_fighter + d + App.Tables.FighterCount) % App.Tables.FighterCount, 1200);
            UIKit.Stepper(col, Loc.T("lobby.court", "Court (host picks)"), () => App.Data.Courts[_court].Name, d => _court = (_court + d + App.Data.Courts.Length) % App.Data.Courts.Length, 1200);
            UIKit.Switch(col, Loc.T("lobby.bestof5", "Best of 5"), () => _roundsToWin == 3, v => _roundsToWin = v ? 3 : 2, 1200, 96);
            RectTransform row = UIKit.HStack(col, "Actions", 24, 0, TextAnchor.MiddleLeft);
            UIKit.Layout(row, 1200, 120);
            row.sizeDelta = new Vector2(1200, 120);
            UIKit.Button(row, Loc.T("lobby.host", "Host Court"), Host, 560, 110, Theme.Accent * 0.7f + Color.black * 0.3f);
            UIKit.Button(row, Loc.T("lobby.join", "Join Court"), Join, 560, 110);
            UIText hint = UIKit.Label(col, Loc.T("lobby.hint", "iPhone and Android can play each other. In mixed pairs, let the Android phone host: some Android phones cannot advertise and can only join."), 28, TextAlign.TopLeft, Theme.Dim);
            UIKit.Layout(hint.Rect, 1500, 120);
            hint.Rect.sizeDelta = new Vector2(1500, 120);
        }

        private ITransport MakeTransport()
        {
            try
            {
                return _bluetooth ? TransportFactory.CreateBluetooth() : TransportFactory.CreateLan();
            }
            catch (Exception e)
            {
                App.Toast(Loc.T("lobby.transportFailed", "Could not start the radio") + ": " + e.Message);
                return null;
            }
        }

        private void Host()
        {
            _transport = MakeTransport();
            if (_transport == null) return;
            if (!_transport.CanHost)
            {
                App.Toast(Loc.T("lobby.cannotHost", "This phone cannot host over Bluetooth. Join instead, or use Wi-Fi."));
                Teardown();
                return;
            }
            App.LastFighter = _fighter;
            _lobby = new NetLobby(App.Tables, _transport, App.NetClock, new NetConfig(), (ulong)DateTime.UtcNow.Ticks ^ 0xA5A5A5A5UL, App.Settings.PlayerName);
            string code = TransportFactory.RandomCourtCode();
            _lobby.Host(code, _fighter, _court, _roundsToWin);
            Clear();
            _code = UIKit.Label(_content, Loc.T("lobby.code", "COURT CODE") + "\n" + code, 96, TextAlign.Center, Theme.Accent, true);
            UIKit.Place(_code.Rect, new Vector2(0.5f, 0.65f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(1400f, 300f));
            _status = UIKit.Label(_content, Loc.T("lobby.waiting", "Waiting for a challenger..."), 40, TextAlign.Center, Theme.Text);
            UIKit.Place(_status.Rect, new Vector2(0.5f, 0.35f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(1400f, 120f));
            Button cancel = UIKit.Button(_content, Loc.T("lobby.cancel", "Cancel"), ShowHome, 420, 100);
            UIKit.Place((RectTransform)cancel.transform, new Vector2(0.5f, 0.1f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(420f, 100f));
        }

        private void Join()
        {
            _transport = MakeTransport();
            if (_transport == null) return;
            App.LastFighter = _fighter;
            _lobby = new NetLobby(App.Tables, _transport, App.NetClock, new NetConfig(), (ulong)DateTime.UtcNow.Ticks ^ 0x5A5A5A5AUL, App.Settings.PlayerName);
            _transport.StartScan();
            Clear();
            _status = UIKit.Label(_content, Loc.T("lobby.scanning", "Looking for nearby courts..."), 40, TextAlign.Left, Theme.Text);
            UIKit.Place(_status.Rect, new Vector2(0f, 1f), new Vector2(0f, 1f), Vector2.zero, new Vector2(1600f, 70f));
            ScrollRect list = UIKit.Scroll(_content, "Peers", out _peers);
            UIKit.Place(list.GetComponent<RectTransform>(), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, -90f), new Vector2(1700f, 600f));
            Button cancel = UIKit.Button(_content, Loc.T("lobby.cancel", "Cancel"), ShowHome, 420, 100);
            UIKit.Place((RectTransform)cancel.transform, new Vector2(1f, 0f), new Vector2(1f, 0f), Vector2.zero, new Vector2(420f, 100f));
            _peerCount = -1;
        }

        private void RefreshPeers()
        {
            var peers = _transport.Peers;
            if (peers.Count == _peerCount) return;
            _peerCount = peers.Count;
            for (int i = _peers.childCount - 1; i >= 0; i--) UnityEngine.Object.Destroy(_peers.GetChild(i).gameObject);
            for (int i = 0; i < peers.Count; i++)
            {
                PeerInfo p = peers[i];
                int bars = p.SignalDbm > -55 ? 4 : (p.SignalDbm > -67 ? 3 : (p.SignalDbm > -78 ? 2 : 1));
                string label = p.CourtCode + "   " + (string.IsNullOrEmpty(p.PlayerName) ? "?" : p.PlayerName) + "   " + p.Platform + "   " + new string('|', bars);
                Button b = UIKit.Button(_peers, label, () =>
                {
                    _transport.StopScan();
                    _lobby.Join(p, _fighter);
                    _status.Text = Loc.T("lobby.connecting", "Connecting...");
                }, 1600, 100, Theme.PanelLight, 34);
                UIKit.Layout(b, 1600, 100);
            }
        }

        public override void Tick(float dt)
        {
            if (_lobby == null || _started) return;
            _lobby.Update();
            if (_transport != null && _transport.State == TransportState.Scanning && _peers != null)
            {
                _peerRefresh -= dt;
                if (_peerRefresh <= 0f)
                {
                    _peerRefresh = 0.5f;
                    RefreshPeers();
                }
            }
            if (_status != null)
            {
                switch (_lobby.State)
                {
                    case LobbyState.Handshaking: _status.Text = Loc.T("lobby.handshake", "Checking versions and picking a seed..."); break;
                    case LobbyState.Failed: _status.Text = Loc.T("lobby.failed", "Could not connect") + ": " + _lobby.Failure + (_transport.Error != TransportError.None ? " (" + _transport.Error + ")" : ""); break;
                }
            }
            if (_lobby.State == LobbyState.Ready)
            {
                _lobby.SetLocalInput(App.Input);
            }
            if (_lobby.State == LobbyState.InMatch && _lobby.Session != null)
            {
                _started = true;
                App.StartDuel(_lobby);
            }
        }

        private void Teardown()
        {
            _started = false;
            if (_lobby != null && App.ActiveLobby != _lobby) _lobby.Dispose();
            if (_transport != null && App.ActiveLobby == null) _transport.Dispose();
            _lobby = null;
            _transport = null;
        }

        public override void OnShow()
        {
            if (_started && App.ActiveLobby == null)
            {
                _started = false;
                ShowHome();
            }
        }

        public override bool OnBack()
        {
            Teardown();
            return false;
        }
    }
}
