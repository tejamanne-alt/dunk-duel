using System.Text;
using NUnit.Framework;

namespace DunkDuel.Transport.BLE.Tests
{
    public sealed class BleAdvertTests
    {
        [Test]
        public void CourtCodesAreFourUppercaseAlphanumerics()
        {
            Assert.IsTrue(BleAdvert.IsValidCourtCode("AB12"));
            Assert.IsFalse(BleAdvert.IsValidCourtCode("ab12"));
            Assert.IsFalse(BleAdvert.IsValidCourtCode("AB1"));
            Assert.IsFalse(BleAdvert.IsValidCourtCode("AB-1"));
            Assert.IsFalse(BleAdvert.IsValidCourtCode(null));
            Assert.AreEqual("AB12", BleAdvert.NormalizeCourtCode(" ab12 "));
            Assert.IsNull(BleAdvert.NormalizeCourtCode("ab 12"));
        }

        [Test]
        public void LocalNameRoundTrip()
        {
            string name = BleAdvert.BuildLocalName("k7qz", "Teja");
            Assert.AreEqual("DD-K7QZ:Teja", name);
            Assert.IsTrue(BleAdvert.TryParse(name, null, 0, 0, out string code, out string player, out string platform));
            Assert.AreEqual("K7QZ", code);
            Assert.AreEqual("Teja", player);
            Assert.AreEqual(BleProtocol.PlatformNameIos, platform);
        }

        [Test]
        public void LocalNameWithoutPlayer()
        {
            Assert.AreEqual("DD-AAAA", BleAdvert.BuildLocalName("AAAA", "  "));
            Assert.IsTrue(BleAdvert.TryParse("DD-AAAA", null, 0, 0, out string code, out string player, out _));
            Assert.AreEqual("AAAA", code);
            Assert.AreEqual(string.Empty, player);
        }

        [Test]
        public void ServiceDataRoundTrip()
        {
            byte[] sd = BleAdvert.BuildServiceData("ZZ99", "Meera Rao", BleProtocol.PlatformAndroid);
            Assert.LessOrEqual(sd.Length, BleProtocol.MaxServiceDataBytes);
            Assert.IsTrue(BleAdvert.TryParse(string.Empty, sd, 0, sd.Length, out string code, out string player, out string platform));
            Assert.AreEqual("ZZ99", code);
            Assert.AreEqual("Meera Ra", player, "name truncated to the 8 bytes left in the scan response");
            Assert.AreEqual(BleProtocol.PlatformNameAndroid, platform);
        }

        [Test]
        public void ServiceDataWinsOverDeviceName()
        {
            byte[] sd = BleAdvert.BuildServiceData("CAFE", "Juno", BleProtocol.PlatformAndroid);
            Assert.IsTrue(BleAdvert.TryParse("Pixel 8", sd, 0, sd.Length, out string code, out _, out string platform));
            Assert.AreEqual("CAFE", code);
            Assert.AreEqual(BleProtocol.PlatformNameAndroid, platform);
        }

        [Test]
        public void TruncationNeverSplitsCharacters()
        {
            string longName = "Ünïcødé 名前 🏀🏀🏀";
            string local = BleAdvert.BuildLocalName("ABCD", longName);
            Assert.LessOrEqual(Encoding.UTF8.GetByteCount(local), BleProtocol.MaxLocalNameBytes);
            byte[] sd = BleAdvert.BuildServiceData("ABCD", longName, BleProtocol.PlatformIos);
            Assert.LessOrEqual(sd.Length, BleProtocol.MaxServiceDataBytes);
            Assert.IsTrue(BleAdvert.TryParse(null, sd, 0, sd.Length, out _, out string player, out _));
            Assert.IsFalse(player.Contains("\uFFFD"), "no replacement characters from a split sequence");
            string t = BleAdvert.TruncateUtf8("a🏀", 4);
            Assert.AreEqual("a", t, "a 4-byte emoji does not fit after 'a' in 4 bytes");
        }

        [Test]
        public void ForeignAdvertisementsAreIgnored()
        {
            Assert.IsFalse(BleAdvert.TryParse("Living Room TV", null, 0, 0, out _, out _, out _));
            Assert.IsFalse(BleAdvert.TryParse("DD-ab", null, 0, 0, out _, out _, out _));
            Assert.IsFalse(BleAdvert.TryParse("DD-ABCDE", null, 0, 0, out _, out _, out _));
            Assert.IsFalse(BleAdvert.TryParse(null, new byte[] { 1, 2, 3 }, 0, 3, out _, out _, out _));
        }

        [Test]
        public void SeparatorAndControlCharactersAreStripped()
        {
            Assert.AreEqual("DD-ABCD:ab", BleAdvert.BuildLocalName("ABCD", "a:\nb"));
        }

        [Test]
        public void ShortHashHidesTheAddress()
        {
            string address = "AA:BB:CC:DD:EE:FF";
            string h = BleAdvert.ShortHash(address);
            Assert.AreEqual(h, BleAdvert.ShortHash(address));
            Assert.IsFalse(h.Contains("AA:BB"));
            Assert.AreEqual(9, h.Length);
        }
    }
}
