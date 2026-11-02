using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using DunkDuel.Net;
using DunkDuel.Presentation;
using DunkDuel.Sim;
using DunkDuel.Sim.Data;
using DunkDuel.Sim.Session;
using UnityEngine;
using UnityEngine.UI;
using Debug = UnityEngine.Debug;

namespace DunkDuel.UI
{
    /// <summary>Developer tools (tap the title five times): determinism gate, BLE echo spike, netcode loopback.</summary>
    public sealed class DevMenuScreen : TitledScreen
    {
        protected override void Build(RectTransform root)
        {
            Frame(root, "Developer");
            RectTransform col = UIKit.VStack(Body, "Tools", 18, 0, TextAnchor.UpperLeft);
            UIKit.Stretch(col);
            UIKit.Button(col, "Determinism check (golden 10,000-frame replay)", () => App.Nav.Push(new DeterminismScreen()), 1300, 100, Theme.PanelLight, 34);
            UIKit.Button(col, "P0 spike: BLE echo test (60 Hz, 10 minutes)", () => App.Nav.Push(new EchoSpikeScreen(true)), 1300, 100, Theme.PanelLight, 34);
            UIKit.Button(col, "P0 spike: LAN echo test", () => App.Nav.Push(new EchoSpikeScreen(false)), 1300, 100, Theme.PanelLight, 34);
            UIKit.Button(col, "Netcode loopback soak (two sessions in-process)", () => App.Nav.Push(new LoopbackSoakScreen()), 1300, 100, Theme.PanelLight, 34);
            UIText info = UIKit.Label(col, "Sim version " + SimBuildInfo.Version(App.Tables).ToString("X16") + "\nCode hash " + SimBuildInfo.CodeHash.ToString("X16")
                + "   Data hash " + App.Tables.DataHash.ToString("X16") + "\n" + SystemInfo.deviceModel + "  " + SystemInfo.operatingSystem
                + "\nQuality " + App.Quality.Profile.Tier + "  render scale " + App.Quality.CurrentRenderScale.ToString("0.00") + "  frame " + App.Quality.SmoothedFrameMs.ToString("0.0") + " ms",
                26, TextAlign.TopLeft, Theme.Dim);
            UIKit.Layout(info.Rect, 1500, 200);
            info.Rect.sizeDelta = new Vector2(1500, 200);
        }
    }

    /// <summary>
    /// Determinism gate on device: replays the golden 10,000-frame recording and compares the final and chain
    /// hashes with the values produced in EditMode for the same build.
    /// </summary>
    public sealed class DeterminismScreen : TitledScreen
    {
        private UIText _out;

        protected override void Build(RectTransform root)
        {
            Frame(root, "Determinism check");
            _out = UIKit.Label(Body, "Running...", 34, TextAlign.TopLeft, Theme.Text);
            UIKit.Stretch(_out.Rect);
            _out.Text = Run();
        }

        public static bool TryLoadGolden(out byte[] replayBytes, out string goldenJson)
        {
            replayBytes = null;
            goldenJson = null;
            TextAsset r = Resources.Load<TextAsset>("DunkDuelDev/golden_10000");
            TextAsset j = Resources.Load<TextAsset>("DunkDuelDev/golden_10000_hash");
            if (r != null && j != null)
            {
                replayBytes = r.bytes;
                goldenJson = j.text;
                return true;
            }
#if UNITY_EDITOR
            string dir = Path.Combine(Application.dataPath, "DunkDuel", "Tests", "EditMode", "Data");
            string rp = Path.Combine(dir, "golden_10000.ddreplay"), jp = Path.Combine(dir, "golden_10000.json");
            if (File.Exists(rp) && File.Exists(jp))
            {
                replayBytes = File.ReadAllBytes(rp);
                goldenJson = File.ReadAllText(jp);
                return true;
            }
#endif
            return false;
        }

        private string Run()
        {
            if (!TryLoadGolden(out byte[] bytes, out string json)) return "FAIL: golden replay not packaged (the build step copies it into Resources/DunkDuelDev).";
            Replay replay;
            try
            {
                replay = Replay.FromBytes(bytes);
            }
            catch (Exception e)
            {
                return "FAIL: unreadable replay: " + e.Message;
            }
            JsonValue g = Json.Parse(json, "golden_10000.json");
            string expectFinal = PJson.Str(g, "finalHash"), expectChain = PJson.Str(g, "chainHash"), expectVersion = PJson.Str(g, "simVersion");
            var sw = Stopwatch.StartNew();
            ReplayRunner.Result res = ReplayRunner.Run(App.Tables, replay);
            sw.Stop();
            string version = SimBuildInfo.Version(App.Tables).ToString("X16");
            bool versionOk = version == expectVersion;
            bool pass = res.FinalHash.ToString("X16") == expectFinal && res.ChainHash.ToString("X16") == expectChain;
            var sb = new StringBuilder();
            sb.Append(pass ? "<color=#5BD98A>PASS</color>" : "<color=#F25C4C>FAIL</color>").Append('\n');
            sb.Append("ticks ").Append(res.Ticks).Append(" in ").Append(sw.ElapsedMilliseconds).Append(" ms (")
                .Append((sw.Elapsed.TotalMilliseconds * 1000.0 / Math.Max(1, res.Ticks)).ToString("0.0")).Append(" us/tick)\n");
            sb.Append("final  ").Append(res.FinalHash.ToString("X16")).Append("  expected ").Append(expectFinal).Append('\n');
            sb.Append("chain  ").Append(res.ChainHash.ToString("X16")).Append("  expected ").Append(expectChain).Append('\n');
            sb.Append("sim version ").Append(version).Append(versionOk ? "  (matches golden)" : "  (golden recorded on " + expectVersion + ": regenerate it for this build)").Append('\n');
            sb.Append(SystemInfo.deviceModel).Append("  ").Append(SystemInfo.processorType).Append("  ").Append(Application.platform);
            Debug.Log("Dunk Duel determinism: " + (pass ? "PASS" : "FAIL") + " final " + res.FinalHash.ToString("X16"));
            return sb.ToString();
        }
    }

    /// <summary>
    /// P0 radio spike: one phone hosts and echoes, the other sends a sequenced 20-byte packet at 60 Hz on INPUT
    /// and measures round trips, jitter and loss for ten minutes, then exports a CSV log.
    /// </summary>
    public sealed class EchoSpikeScreen : TitledScreen
    {
        private const int DurationSeconds = 600;
        private readonly bool _bluetooth;
        private ITransport _transport;
        private bool _echoer;
        private UIText _out;
        private readonly byte[] _pkt = new byte[20];
        private uint _seq;
        private long _start;
        private readonly Dictionary<uint, long> _inflight = new Dictionary<uint, long>();
        private readonly List<int> _rtts = new List<int>();
        private readonly int[] _hist = new int[12];
        private int _received;
        private int _lost;
        private float _sendAcc;
        private readonly StringBuilder _log = new StringBuilder();
        private bool _done;
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        public EchoSpikeScreen(bool bluetooth)
        {
            _bluetooth = bluetooth;
        }

        protected override void Build(RectTransform root)
        {
            Frame(root, (_bluetooth ? "BLE" : "LAN") + " echo spike");
            RectTransform row = UIKit.HStack(Body, "Buttons", 20, 0, TextAnchor.UpperLeft);
            UIKit.Place(row, new Vector2(0f, 1f), new Vector2(0f, 1f), Vector2.zero, new Vector2(1600f, 110f));
            UIKit.Button(row, "Echo (host)", () => Begin(true), 400, 100);
            UIKit.Button(row, "Measure (join)", () => Begin(false), 400, 100);
            UIKit.Button(row, "Export log", Export, 400, 100);
            _out = UIKit.Label(Body, "Host on one phone, measure on the other.", 32, TextAlign.TopLeft, Theme.Text);
            UIKit.Place(_out.Rect, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, -130f), new Vector2(1700f, 700f));
        }

        private long Now => _clock.ElapsedTicks * 1000000L / Stopwatch.Frequency;

        private void Begin(bool echo)
        {
            _transport?.Dispose();
            _transport = _bluetooth ? TransportFactory.CreateBluetooth() : TransportFactory.CreateLan();
            _echoer = echo;
            _transport.OnReceive += OnReceive;
            if (echo) _transport.Host("ECHO", "echo");
            else _transport.StartScan();
            _start = 0;
            _log.Clear();
            _log.Append("t_ms,seq,rtt_us\n");
        }

        private void OnReceive(TransportChannel ch, byte[] buf, int len)
        {
            if (ch != TransportChannel.Input || len < 8) return;
            if (_echoer)
            {
                _transport.Send(TransportChannel.Input, buf, 0, len);
                _received++;
                return;
            }
            uint seq = (uint)(buf[0] | buf[1] << 8 | buf[2] << 16 | buf[3] << 24);
            if (_inflight.TryGetValue(seq, out long sent))
            {
                _inflight.Remove(seq);
                int rtt = (int)(Now - sent);
                _rtts.Add(rtt);
                _hist[Mathf.Clamp(rtt / 10000, 0, _hist.Length - 1)]++;
                _received++;
                _log.Append((Now - _start) / 1000).Append(',').Append(seq).Append(',').Append(rtt).Append('\n');
            }
        }

        public override void Tick(float dt)
        {
            if (_transport == null) return;
            _transport.Poll();
            if (!_echoer && _transport.State == TransportState.Scanning && _transport.Peers.Count > 0)
            {
                for (int i = 0; i < _transport.Peers.Count; i++)
                {
                    if (_transport.Peers[i].CourtCode == "ECHO")
                    {
                        _transport.StopScan();
                        _transport.Join(_transport.Peers[i]);
                        break;
                    }
                }
            }
            if (_transport.State == TransportState.Connected && !_echoer && !_done)
            {
                if (_start == 0) _start = Now;
                _sendAcc += dt;
                while (_sendAcc >= 1f / 60f)
                {
                    _sendAcc -= 1f / 60f;
                    uint seq = ++_seq;
                    _pkt[0] = (byte)seq;
                    _pkt[1] = (byte)(seq >> 8);
                    _pkt[2] = (byte)(seq >> 16);
                    _pkt[3] = (byte)(seq >> 24);
                    _inflight[seq] = Now;
                    _transport.Send(TransportChannel.Input, _pkt, 0, _pkt.Length);
                }
                // Anything older than a second is lost.
                var stale = new List<uint>();
                foreach (KeyValuePair<uint, long> kv in _inflight) if (Now - kv.Value > 1000000) stale.Add(kv.Key);
                foreach (uint s in stale)
                {
                    _inflight.Remove(s);
                    _lost++;
                }
                if ((Now - _start) / 1000000 >= DurationSeconds) _done = true;
            }
            _out.Text = Report();
        }

        private string Report()
        {
            var sb = new StringBuilder();
            sb.Append("state ").Append(_transport.State).Append("  error ").Append(_transport.Error).Append('\n');
            if (_echoer)
            {
                sb.Append("echoed ").Append(_received).Append(" packets");
                return sb.ToString();
            }
            int n = _rtts.Count;
            if (n == 0) return sb.Append("waiting for echoes...").ToString();
            var sorted = new List<int>(_rtts);
            sorted.Sort();
            double mean = 0;
            for (int i = 0; i < n; i++) mean += _rtts[i];
            mean /= n;
            double jitter = 0;
            for (int i = 1; i < n; i++) jitter += Math.Abs(_rtts[i] - _rtts[i - 1]);
            jitter /= Math.Max(1, n - 1);
            long elapsed = (Now - _start) / 1000000;
            sb.Append("elapsed ").Append(elapsed).Append(" / ").Append(DurationSeconds).Append(" s").Append(_done ? "  DONE" : "").Append('\n');
            sb.Append("RTT mean ").Append((mean / 1000).ToString("0.0")).Append(" ms  p50 ").Append(sorted[n / 2] / 1000).Append("  p95 ").Append(sorted[n * 95 / 100] / 1000)
                .Append("  max ").Append(sorted[n - 1] / 1000).Append(" ms\n");
            sb.Append("jitter ").Append((jitter / 1000).ToString("0.0")).Append(" ms   loss ").Append((_lost * 100.0 / Math.Max(1, _lost + _received)).ToString("0.00")).Append(" %\n");
            sb.Append("histogram (10 ms buckets): ");
            for (int i = 0; i < _hist.Length; i++) sb.Append(_hist[i]).Append(i < _hist.Length - 1 ? " | " : "");
            return sb.ToString();
        }

        private void Export()
        {
            try
            {
                string path = Path.Combine(Application.persistentDataPath, "echo_" + (_bluetooth ? "ble" : "lan") + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".csv");
                File.WriteAllText(path, _log.ToString());
                App.Toast("Saved " + path);
            }
            catch (IOException e)
            {
                App.Toast("Export failed: " + e.Message);
            }
        }

        public override void OnHide()
        {
            _transport?.Dispose();
            _transport = null;
        }
    }

    /// <summary>Two rollback sessions in one process over a simulated BLE link: quick on-device netcode sanity check.</summary>
    public sealed class LoopbackSoakScreen : TitledScreen
    {
        private UIText _out;
        private RollbackSession _a;
        private RollbackSession _b;
        private ManualClock _clock;
        private int _ticks;

        protected override void Build(RectTransform root)
        {
            Frame(root, "Netcode loopback soak");
            _out = UIKit.Label(Body, "", 32, TextAlign.TopLeft, Theme.Text);
            UIKit.Stretch(_out.Rect);
            _clock = new ManualClock();
            SimulatedLink link = SimulatedLink.CreatePair(_clock, 77, 22000, 18000, 30);
            link.A.Host("LOOP", "A");
            link.B.Join(new PeerInfo { CourtCode = "LOOP" });
            var setup = MatchSetup.Versus(0, 1 % App.Tables.FighterCount, 1234);
            var cpuA = new DunkDuel.AI.CpuPlayer(App.Tables, App.Tiers, DunkDuel.AI.CpuTier.Pro, 0, 1);
            var cpuB = new DunkDuel.AI.CpuPlayer(App.Tables, App.Tiers, DunkDuel.AI.CpuTier.Pro, 1, 2);
            _a = new RollbackSession(App.Tables, setup, 0, true, link.A, cpuA, _clock, new NetConfig(), 2);
            _b = new RollbackSession(App.Tables, setup, 1, false, link.B, cpuB, _clock, new NetConfig(), 2);
        }

        public override void Tick(float dt)
        {
            for (int i = 0; i < 60 && !_a.IsOver; i++)
            {
                _clock.Advance(16667);
                _a.Tick();
                _b.Tick();
                _ticks++;
            }
            NetStats sa = _a.Stats, sb = _b.Stats;
            _out.Text = "ticks " + _ticks + "  (A at " + _a.CurrentTick + ", B at " + _b.CurrentTick + ")\n"
                + "desyncs A " + sa.Desyncs + " B " + sb.Desyncs + "   recoveries " + (sa.DesyncRecoveries + sb.DesyncRecoveries) + "\n"
                + "rollback p95 " + sa.RollbackP95 + " / " + sb.RollbackP95 + "  max " + sa.MaxRollback + " / " + sb.MaxRollback + "\n"
                + "stalls " + sa.StallTicks + " / " + sb.StallTicks + "   slowdowns " + sa.SlowdownTicks + " / " + sb.SlowdownTicks + "\n"
                + "RTT " + sa.RttMicros / 1000 + " ms   jitter " + sa.JitterMicros / 1000 + " ms\n"
                + (_a.IsOver ? "match over: " + _a.State.F0.Score + "-" + _a.State.F1.Score : "running...");
        }

        public override void OnHide()
        {
            _a?.Dispose();
            _b?.Dispose();
        }
    }
}
