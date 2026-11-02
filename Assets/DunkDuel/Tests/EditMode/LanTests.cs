using System.Collections.Generic;
using System.Threading;
using DunkDuel.Net;
using DunkDuel.Transport.LAN;
using NUnit.Framework;

namespace DunkDuel.Tests
{
    /// <summary>LAN UDP transport over the loopback interface: connect, reliable ordered CONTROL, INPUT delivery.</summary>
    public class LanTests
    {
        [Test]
        public void HostAndGuestExchangeControlInOrderAndInputs()
        {
            const int port = 47930;
            using (var host = new LanTransport(port))
            using (var guest = new LanTransport(port + 10))
            {
                host.Host("LANX", "host");
                Assume.That(host.State, Is.EqualTo(TransportState.Hosting), "UDP sockets unavailable here");
                var got = new List<byte>();
                int inputs = 0;
                host.OnReceive += (ch, buf, len) =>
                {
                    if (ch == TransportChannel.Control) got.Add(buf[0]);
                    else if (len == InputPacket.Size) inputs++;
                };
                guest.Join(new PeerInfo { Address = "127.0.0.1:" + port });
                for (int i = 0; i < 200 && (host.State != TransportState.Connected || guest.State != TransportState.Connected); i++)
                {
                    host.Poll();
                    guest.Poll();
                    Thread.Sleep(2);
                }
                Assert.AreEqual(TransportState.Connected, host.State);
                Assert.AreEqual(TransportState.Connected, guest.State);
                for (byte b = 1; b <= 20; b++) guest.Send(TransportChannel.Control, new[] { b, (byte)0 }, 0, 2);
                var packet = new byte[InputPacket.Size];
                for (int i = 0; i < 10; i++) guest.Send(TransportChannel.Input, packet, 0, packet.Length);
                for (int i = 0; i < 300 && got.Count < 20; i++)
                {
                    host.Poll();
                    guest.Poll();
                    Thread.Sleep(2);
                }
                Assert.AreEqual(20, got.Count, "every control message arrives");
                for (int i = 0; i < got.Count; i++) Assert.AreEqual(i + 1, got[i], "in order");
                Assert.Greater(inputs, 0);
            }
        }
    }
}
