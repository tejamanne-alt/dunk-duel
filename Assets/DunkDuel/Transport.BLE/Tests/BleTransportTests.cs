using System;
using System.Collections.Generic;
using DunkDuel.Net;
using NUnit.Framework;

namespace DunkDuel.Transport.BLE.Tests
{
    public sealed class BleTransportTests
    {
        private long _now;
        private LoopbackBleBackend _hostRadio;
        private LoopbackBleBackend _guestRadio;
        private BleTransport _host;
        private BleTransport _guest;
        private List<KeyValuePair<TransportChannel, byte[]>> _hostRx;
        private List<KeyValuePair<TransportChannel, byte[]>> _guestRx;

        private long Clock() => _now;

        private void Build(string hostPlatform, string guestPlatform, int hostPayload = 20, int guestPayload = 20)
        {
            _now = 1000;
            _hostRadio = new LoopbackBleBackend(hostPlatform, "host-address") { Payload = hostPayload };
            _guestRadio = new LoopbackBleBackend(guestPlatform, "guest-address") { Payload = guestPayload };
            LoopbackBleBackend.Pair(_hostRadio, _guestRadio);
            _host = new BleTransport(new BleOptions(), Clock, _hostRadio);
            _guest = new BleTransport(new BleOptions(), Clock, _guestRadio);
            _hostRx = new List<KeyValuePair<TransportChannel, byte[]>>();
            _guestRx = new List<KeyValuePair<TransportChannel, byte[]>>();
            _host.OnReceive += (ch, buf, len) => _hostRx.Add(new KeyValuePair<TransportChannel, byte[]>(ch, Copy(buf, len)));
            _guest.OnReceive += (ch, buf, len) => _guestRx.Add(new KeyValuePair<TransportChannel, byte[]>(ch, Copy(buf, len)));
        }

        private static byte[] Copy(byte[] buf, int len)
        {
            var c = new byte[len];
            Buffer.BlockCopy(buf, 0, c, 0, len);
            return c;
        }

        private void PumpBoth(int rounds = 3, int stepMicros = 5000)
        {
            for (int i = 0; i < rounds; i++)
            {
                _host.Poll();
                _guest.Poll();
                _now += stepMicros;
            }
        }

        private void Connect()
        {
            _host.Host("ab12", "Ann");
            _guest.StartScan();
            PumpBoth();
            Assert.AreEqual(1, _guest.Peers.Count, "guest sees the court");
            _guest.Join(_guest.Peers[0]);
            PumpBoth();
            Assert.AreEqual(TransportState.Connected, _host.State);
            Assert.AreEqual(TransportState.Connected, _guest.State);
        }

        [TestCase("android", "ios")]
        [TestCase("ios", "android")]
        [TestCase("ios", "ios")]
        [TestCase("android", "android")]
        public void HostAndGuestConnectAcrossPlatforms(string hostPlatform, string guestPlatform)
        {
            Build(hostPlatform, guestPlatform);
            var hostStates = new List<TransportState>();
            _host.OnStateChanged += s => hostStates.Add(s);
            _host.Host("ab12", "Ann");
            _guest.StartScan();
            PumpBoth();
            Assert.AreEqual(TransportState.Hosting, _host.State);
            Assert.AreEqual(TransportState.Scanning, _guest.State);
            Assert.AreEqual(1, _guest.Peers.Count);
            PeerInfo peer = _guest.Peers[0];
            Assert.AreEqual("AB12", peer.CourtCode);
            Assert.AreEqual("Ann", peer.PlayerName);
            Assert.AreEqual(hostPlatform, peer.Platform);
            _guest.Join(peer);
            PumpBoth();
            Assert.AreEqual(TransportState.Connected, _host.State);
            Assert.AreEqual(TransportState.Connected, _guest.State);
            CollectionAssert.AreEqual(new[] { TransportState.Hosting, TransportState.Connected }, hostStates);
        }

        [Test]
        public void InputPacketsArriveIntactBothWays()
        {
            Build("android", "ios");
            Connect();
            var packet = new byte[BleProtocol.InputPacketBytes];
            for (int i = 0; i < packet.Length; i++) packet[i] = (byte)(200 - i);
            _guest.Send(TransportChannel.Input, packet, 0, packet.Length);
            _host.Send(TransportChannel.Input, packet, 0, packet.Length);
            PumpBoth(2);
            Assert.AreEqual(1, _hostRx.FindAll(p => p.Key == TransportChannel.Input).Count);
            Assert.AreEqual(1, _guestRx.FindAll(p => p.Key == TransportChannel.Input).Count);
            CollectionAssert.AreEqual(packet, _hostRx.Find(p => p.Key == TransportChannel.Input).Value);
            Assert.AreEqual(0, _host.Stats.LossPermille);
        }

        [Test]
        public void LargeControlMessagesAreFragmentedToThePayload()
        {
            Build("android", "ios", 20, 20);
            Connect();
            var msg = new byte[900];
            for (int i = 0; i < msg.Length; i++) msg[i] = (byte)(i % 251);
            msg[0] = 0x10; // a session message type
            _guest.Send(TransportChannel.Control, msg, 0, msg.Length);
            PumpBoth(2);
            List<KeyValuePair<TransportChannel, byte[]>> control = _hostRx.FindAll(p => p.Key == TransportChannel.Control);
            Assert.AreEqual(1, control.Count);
            CollectionAssert.AreEqual(msg, control[0].Value);
            Assert.AreEqual(0, _guestRadio.OversizeFragments);
            Assert.Greater(_guestRadio.ControlFragmentsSent, 40);
            Assert.AreEqual(0, _host.FramingErrors);
        }

        [Test]
        public void NegotiatedPayloadMeansFewerFragments()
        {
            Build("ios", "android", 182, 182);
            Connect();
            Assert.AreEqual(182, _guest.Stats.MaxPayload);
            var msg = new byte[500];
            msg[0] = 0x11;
            int before = _guestRadio.ControlFragmentsSent;
            _guest.Send(TransportChannel.Control, msg, 0, msg.Length);
            Assert.AreEqual(3, _guestRadio.ControlFragmentsSent - before);
            PumpBoth(2);
            Assert.AreEqual(1, _hostRx.FindAll(p => p.Key == TransportChannel.Control).Count);
        }

        [Test]
        public void HeartbeatMeasuresRttAndStaysInsideTheTransport()
        {
            Build("android", "ios");
            Connect();
            for (int i = 0; i < 400; i++)
            {
                _host.Poll();
                _now += 1000;
                _guest.Poll();
                _now += 1000;
            }
            Assert.Greater(_host.HeartbeatSamples, 0);
            Assert.Greater(_guest.HeartbeatSamples, 0);
            Assert.Greater(_host.Stats.RttMicros, 0);
            Assert.Less(_host.Stats.RttMicros, 20000);
            Assert.AreEqual(0, _hostRx.Count, "pings and pongs never reach the session");
            Assert.AreEqual(0, _guestRx.Count);
        }

        [Test]
        public void ReservedControlTypesAreRejected()
        {
            Build("android", "ios");
            Connect();
            Assert.Throws<ArgumentException>(() => _guest.Send(TransportChannel.Control, new byte[] { BleProtocol.HeartbeatPing, 1 }, 0, 2));
            Assert.Throws<ArgumentException>(() => _guest.Send(TransportChannel.Control, new byte[] { BleProtocol.HeartbeatPong }, 0, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => _guest.Send(TransportChannel.Control, new byte[1025], 0, 1025));
            Assert.Throws<ArgumentException>(() => _guest.Send(TransportChannel.Input, new byte[64], 0, 64));
        }

        [Test]
        public void LocalDisconnectIsIdleAndPeerSeesDisconnected()
        {
            Build("android", "ios");
            Connect();
            _guest.Disconnect();
            PumpBoth();
            Assert.AreEqual(TransportState.Idle, _guest.State);
            Assert.AreEqual(TransportState.Disconnected, _host.State);
        }

        [Test]
        public void LinkLossReportsDisconnectedOnBothSides()
        {
            Build("ios", "android");
            Connect();
            _hostRadio.DropLink();
            PumpBoth();
            Assert.AreEqual(TransportState.Disconnected, _host.State);
            Assert.AreEqual(TransportState.Disconnected, _guest.State);
            // Data sent while down is not delivered and is not counted as loss.
            _guest.Send(TransportChannel.Input, new byte[20], 0, 20);
            PumpBoth();
            Assert.AreEqual(0, _hostRx.Count);
            Assert.AreEqual(0, _guest.Stats.LossPermille);
        }

        [Test]
        public void StaleNativeDisconnectAfterLocalDisconnectIsIgnored()
        {
            Build("android", "ios");
            Connect();
            _guest.Disconnect();
            _guestRadio.Push(BleRecords.State(TransportState.Disconnected, TransportError.None));
            PumpBoth();
            Assert.AreEqual(TransportState.Idle, _guest.State);
        }

        [Test]
        public void HostingIsRefusedWhenThePhoneCannotAdvertise()
        {
            Build("android", "ios");
            _hostRadio.AllowHost = false;
            _host = new BleTransport(new BleOptions(), Clock, _hostRadio);
            Assert.IsFalse(_host.CanHost);
            _host.Host("ABCD", "Ann");
            _host.Poll();
            Assert.AreEqual(TransportState.Failed, _host.State);
            Assert.AreEqual(TransportError.AdvertisingUnsupported, _host.Error);
            StringAssert.Contains("Join", _host.FailureReason);
        }

        [Test]
        public void AndroidPrefersHostingInMixedPairs()
        {
            Build("android", "ios");
            Assert.IsTrue(_host.PrefersHosting);
            Assert.IsFalse(_guest.PrefersHosting);
        }

        [Test]
        public void EditorBackendFailsCleanly()
        {
            var t = new BleTransport(null, Clock, new UnsupportedBleBackend("editor", "no radio here"));
            var states = new List<TransportState>();
            t.OnStateChanged += s => states.Add(s);
            t.Poll();
            Assert.AreEqual(TransportState.Failed, t.State);
            Assert.AreEqual(TransportError.Internal, t.Error);
            Assert.AreEqual("no radio here", t.FailureReason);
            Assert.DoesNotThrow(() => t.Host("ABCD", "x"));
            Assert.DoesNotThrow(() => t.StartScan());
            Assert.DoesNotThrow(() => t.Send(TransportChannel.Input, new byte[20], 0, 20));
            t.Poll();
            Assert.AreEqual(TransportState.Failed, t.State);
            Assert.IsTrue(states.Contains(TransportState.Failed));
            t.Dispose();
            Assert.Throws<ObjectDisposedException>(() => t.Host("ABCD", "x"));
        }

        [Test]
        public void InvalidCourtCodeIsAnArgumentError()
        {
            Build("android", "ios");
            Assert.Throws<ArgumentException>(() => _host.Host("toolong", "x"));
        }

        [Test]
        public void StalePeersExpire()
        {
            Build("android", "ios");
            _host.Host("ab12", "Ann");
            _guest.StartScan();
            PumpBoth();
            Assert.AreEqual(1, _guest.Peers.Count);
            _now += 7000000;
            _guest.Poll();
            Assert.AreEqual(0, _guest.Peers.Count);
        }

        [Test]
        public void SessionLogNeverContainsAddressesOrNames()
        {
            Build("android", "ios");
            var log = new BleSessionLog(Clock);
            _guest = new BleTransport(new BleOptions { Log = log }, Clock, _guestRadio);
            _host.Host("ab12", "Ann Secret");
            _guest.StartScan();
            PumpBoth();
            _guest.Join(_guest.Peers[0]);
            PumpBoth(5, 600000);
            string text = log.Text;
            StringAssert.Contains("Connected", text);
            StringAssert.Contains("AB12", text);
            Assert.IsFalse(text.Contains("host-address"), text);
            Assert.IsFalse(text.Contains("Ann"), text);
        }
    }
}
