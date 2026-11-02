using System;
using System.Text;

namespace DunkDuel.Transport.BLE
{
    /// <summary>
    /// Builds and parses what a host advertises: the 4-character court code, a short player name and the
    /// host platform. iOS hosts use the local name "DD-XXXX[:name]"; Android hosts use service data
    /// [code x4][platform][name]. Engine-free.
    /// </summary>
    public static class BleAdvert
    {
        public static bool IsValidCourtCode(string code)
        {
            if (code == null || code.Length != BleProtocol.CourtCodeLength) return false;
            for (int i = 0; i < code.Length; i++)
            {
                char c = code[i];
                bool ok = (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9');
                if (!ok) return false;
            }
            return true;
        }

        /// <summary>Upper-cases and trims; returns null when the result is not a valid court code.</summary>
        public static string NormalizeCourtCode(string code)
        {
            if (code == null) return null;
            string c = code.Trim().ToUpperInvariant();
            return IsValidCourtCode(c) ? c : null;
        }

        /// <summary>Removes control characters and the name separator; keeps names printable.</summary>
        public static string SanitizeName(string name)
        {
            if (string.IsNullOrEmpty(name)) return string.Empty;
            var sb = new StringBuilder(name.Length);
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if (char.IsControl(c) || c == BleProtocol.LocalNameSeparator) continue;
                sb.Append(c);
            }
            return sb.ToString().Trim();
        }

        /// <summary>Longest prefix of <paramref name="s"/> whose UTF-8 encoding fits <paramref name="maxBytes"/> (never splits a character).</summary>
        public static string TruncateUtf8(string s, int maxBytes)
        {
            if (string.IsNullOrEmpty(s) || maxBytes <= 0) return string.Empty;
            if (Encoding.UTF8.GetByteCount(s) <= maxBytes) return s;
            int bytes = 0;
            int i = 0;
            while (i < s.Length)
            {
                int step = char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]) ? 2 : 1;
                int size = Encoding.UTF8.GetByteCount(s.ToCharArray(i, step));
                if (bytes + size > maxBytes) break;
                bytes += size;
                i += step;
            }
            return s.Substring(0, i);
        }

        /// <summary>"DD-XXXX" or "DD-XXXX:name", at most <see cref="BleProtocol.MaxLocalNameBytes"/> UTF-8 bytes.</summary>
        public static string BuildLocalName(string courtCode, string playerName)
        {
            string code = NormalizeCourtCode(courtCode) ?? throw new ArgumentException("court code must be 4 characters A-Z/0-9", nameof(courtCode));
            string head = BleProtocol.LocalNamePrefix + code;
            string name = SanitizeName(playerName);
            if (name.Length == 0) return head;
            int room = BleProtocol.MaxLocalNameBytes - Encoding.UTF8.GetByteCount(head) - 1;
            name = TruncateUtf8(name, room);
            return name.Length == 0 ? head : head + BleProtocol.LocalNameSeparator + name;
        }

        /// <summary>[code x4 ASCII][platform byte][name UTF-8], at most <see cref="BleProtocol.MaxServiceDataBytes"/> bytes.</summary>
        public static byte[] BuildServiceData(string courtCode, string playerName, byte platform)
        {
            string code = NormalizeCourtCode(courtCode) ?? throw new ArgumentException("court code must be 4 characters A-Z/0-9", nameof(courtCode));
            string name = TruncateUtf8(SanitizeName(playerName), BleProtocol.MaxServiceDataNameBytes);
            byte[] nameBytes = Encoding.UTF8.GetBytes(name);
            var data = new byte[BleProtocol.CourtCodeLength + 1 + nameBytes.Length];
            for (int i = 0; i < BleProtocol.CourtCodeLength; i++) data[i] = (byte)code[i];
            data[BleProtocol.CourtCodeLength] = platform;
            Buffer.BlockCopy(nameBytes, 0, data, BleProtocol.CourtCodeLength + 1, nameBytes.Length);
            return data;
        }

        /// <summary>
        /// Extracts court code, player name and platform from an advertisement. Service data wins over the
        /// local name. Returns false when neither carries a valid court code.
        /// </summary>
        public static bool TryParse(string localName, byte[] serviceData, int sdOffset, int sdCount,
            out string courtCode, out string playerName, out string platform)
        {
            courtCode = null;
            playerName = string.Empty;
            platform = BleProtocol.PlatformNameUnknown;

            if (serviceData != null && sdCount >= BleProtocol.CourtCodeLength + 1 && sdOffset >= 0 && sdOffset + sdCount <= serviceData.Length)
            {
                var chars = new char[BleProtocol.CourtCodeLength];
                for (int i = 0; i < chars.Length; i++) chars[i] = (char)serviceData[sdOffset + i];
                string code = new string(chars);
                if (IsValidCourtCode(code))
                {
                    courtCode = code;
                    byte p = serviceData[sdOffset + BleProtocol.CourtCodeLength];
                    platform = p == BleProtocol.PlatformAndroid ? BleProtocol.PlatformNameAndroid
                        : (p == BleProtocol.PlatformIos ? BleProtocol.PlatformNameIos : BleProtocol.PlatformNameUnknown);
                    int nameStart = sdOffset + BleProtocol.CourtCodeLength + 1;
                    int nameLen = sdCount - BleProtocol.CourtCodeLength - 1;
                    playerName = nameLen > 0 ? SanitizeName(DecodeUtf8(serviceData, nameStart, nameLen)) : string.Empty;
                    return true;
                }
            }

            if (!string.IsNullOrEmpty(localName) && localName.StartsWith(BleProtocol.LocalNamePrefix, StringComparison.Ordinal)
                && localName.Length >= BleProtocol.LocalNamePrefix.Length + BleProtocol.CourtCodeLength)
            {
                string code = localName.Substring(BleProtocol.LocalNamePrefix.Length, BleProtocol.CourtCodeLength);
                if (!IsValidCourtCode(code)) return false;
                int rest = BleProtocol.LocalNamePrefix.Length + BleProtocol.CourtCodeLength;
                if (localName.Length > rest && localName[rest] != BleProtocol.LocalNameSeparator) return false;
                courtCode = code;
                playerName = localName.Length > rest + 1 ? SanitizeName(localName.Substring(rest + 1)) : string.Empty;
                platform = BleProtocol.PlatformNameIos; // only iOS hosts advertise a local name
                return true;
            }
            return false;
        }

        /// <summary>Short stable tag for logs: identifiers and MAC addresses never reach a log in clear.</summary>
        public static string ShortHash(string s)
        {
            if (string.IsNullOrEmpty(s)) return "-";
            uint h = 2166136261u;
            unchecked
            {
                for (int i = 0; i < s.Length; i++)
                {
                    h ^= s[i];
                    h *= 16777619u;
                }
            }
            return "#" + h.ToString("x8");
        }

        private static string DecodeUtf8(byte[] data, int offset, int count)
        {
            try
            {
                return new UTF8Encoding(false, false).GetString(data, offset, count);
            }
            catch (ArgumentException)
            {
                return string.Empty;
            }
        }
    }
}
