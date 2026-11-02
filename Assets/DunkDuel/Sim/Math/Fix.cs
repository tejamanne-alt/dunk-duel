using System;

namespace DunkDuel.Sim
{
    /// <summary>
    /// Signed Q31.32 fixed-point number in a 64-bit integer. All simulation math uses this type so that
    /// every platform (CoreCLR, Mono, IL2CPP on ARM64) computes bit-identical results.
    /// Arithmetic wraps on overflow (unchecked); simulation values stay far inside the range.
    /// Multiplication and division round to nearest, ties away from zero.
    /// </summary>
    [Serializable]
    public readonly struct Fix : IEquatable<Fix>, IComparable<Fix>, IComparable
    {
        public const int FractionBits = 32;
        public const long OneRaw = 1L << FractionBits;
        private const ulong LowMask = 0xFFFFFFFFUL;

        public readonly long Raw;

        private Fix(long raw) { Raw = raw; }

        public static readonly Fix Zero = new Fix(0);
        public static readonly Fix One = new Fix(OneRaw);
        public static readonly Fix Two = new Fix(OneRaw * 2);
        public static readonly Fix Half = new Fix(OneRaw >> 1);
        public static readonly Fix MinusOne = new Fix(-OneRaw);
        public static readonly Fix MaxValue = new Fix(long.MaxValue);
        public static readonly Fix MinValue = new Fix(long.MinValue);
        public static readonly Fix Epsilon = new Fix(1);

        public static Fix FromRaw(long raw) => new Fix(raw);
        public static Fix FromInt(int value) => new Fix((long)value << FractionBits);

        /// <summary>Exact rational num/den rounded to nearest.</summary>
        public static Fix Ratio(int num, int den) => new Fix(DivRaw((long)num << FractionBits, (long)den << FractionBits));

        public static implicit operator Fix(int value) => FromInt(value);

        public int FloorToInt() => (int)(Raw >> FractionBits);
        public int CeilToInt() => (int)((Raw + (OneRaw - 1)) >> FractionBits);
        public int RoundToInt() => (int)((Raw + (OneRaw >> 1)) >> FractionBits);
        public int TruncToInt() => (int)(Raw / OneRaw);

        public bool IsZero => Raw == 0;
        public bool IsNegative => Raw < 0;
        public bool IsPositive => Raw > 0;
        public int Sign => Raw > 0 ? 1 : (Raw < 0 ? -1 : 0);

        // ---------------------------------------------------------------- operators

        public static Fix operator +(Fix a, Fix b) => new Fix(unchecked(a.Raw + b.Raw));
        public static Fix operator -(Fix a, Fix b) => new Fix(unchecked(a.Raw - b.Raw));
        public static Fix operator -(Fix a) => new Fix(unchecked(-a.Raw));
        public static Fix operator *(Fix a, Fix b) => new Fix(MulRaw(a.Raw, b.Raw));
        public static Fix operator /(Fix a, Fix b) => new Fix(DivRaw(a.Raw, b.Raw));
        public static Fix operator *(Fix a, int b) => new Fix(unchecked(a.Raw * b));
        public static Fix operator *(int a, Fix b) => new Fix(unchecked(a * b.Raw));

        /// <summary>Division by an integer, rounded to nearest (ties away from zero).</summary>
        public static Fix operator /(Fix a, int b)
        {
            if (b == 0) throw new DivideByZeroException();
            long q = a.Raw / b;
            long r = a.Raw - q * b;
            long twiceR = r < 0 ? -2 * r : 2 * r;
            long absB = b < 0 ? -(long)b : b;
            if (twiceR >= absB) q += ((a.Raw < 0) == (b < 0)) ? 1 : -1;
            return new Fix(q);
        }

        public static bool operator ==(Fix a, Fix b) => a.Raw == b.Raw;
        public static bool operator !=(Fix a, Fix b) => a.Raw != b.Raw;
        public static bool operator <(Fix a, Fix b) => a.Raw < b.Raw;
        public static bool operator >(Fix a, Fix b) => a.Raw > b.Raw;
        public static bool operator <=(Fix a, Fix b) => a.Raw <= b.Raw;
        public static bool operator >=(Fix a, Fix b) => a.Raw >= b.Raw;

        public bool Equals(Fix other) => Raw == other.Raw;
        public override bool Equals(object obj) => obj is Fix f && f.Raw == Raw;
        public override int GetHashCode() => unchecked((int)Raw ^ (int)(Raw >> 32));
        public int CompareTo(Fix other) => Raw.CompareTo(other.Raw);
        int IComparable.CompareTo(object obj) => obj is Fix f ? Raw.CompareTo(f.Raw) : 1;

        // ---------------------------------------------------------------- helpers

        public static Fix Abs(Fix a) => a.Raw < 0 ? new Fix(unchecked(-a.Raw)) : a;
        public static Fix Min(Fix a, Fix b) => a.Raw <= b.Raw ? a : b;
        public static Fix Max(Fix a, Fix b) => a.Raw >= b.Raw ? a : b;
        public static Fix Clamp(Fix v, Fix lo, Fix hi) => v.Raw < lo.Raw ? lo : (v.Raw > hi.Raw ? hi : v);
        public static Fix Clamp01(Fix v) => Clamp(v, Zero, One);

        /// <summary>a + (b - a) * t.</summary>
        public static Fix Lerp(Fix a, Fix b, Fix t) => a + (b - a) * t;

        /// <summary>Moves <paramref name="current"/> toward <paramref name="target"/> by at most <paramref name="maxDelta"/>.</summary>
        public static Fix MoveTowards(Fix current, Fix target, Fix maxDelta)
        {
            Fix d = target - current;
            if (Abs(d) <= maxDelta) return target;
            return current + (d.Raw > 0 ? maxDelta : -maxDelta);
        }

        /// <summary>
        /// Linear ramp: 0 at <paramref name="zeroAt"/>, 1 at <paramref name="oneAt"/>, clamped. Works in either direction.
        /// </summary>
        public static Fix Ramp(Fix value, Fix zeroAt, Fix oneAt)
        {
            Fix span = oneAt - zeroAt;
            if (span.Raw == 0) return value >= oneAt ? One : Zero;
            return Clamp01((value - zeroAt) / span);
        }

        /// <summary>Square root, rounded down to the nearest representable value. Negative input returns zero.</summary>
        public static Fix Sqrt(Fix a)
        {
            if (a.Raw <= 0) return Zero;
            // sqrt(raw * 2^32) = sqrt(raw) * 2^16. Start from the 64-bit integer root, then refine with Newton.
            ulong s = ISqrt((ulong)a.Raw);
            long y = (long)(s << 16);
            if (y == 0) y = 1;
            for (int i = 0; i < 4; i++)
            {
                long next = (y + DivRaw(a.Raw, y)) >> 1;
                if (next == y) break;
                y = next;
            }
            // Clamp to floor: ensure y*y <= a.
            while (y > 0 && MulRaw(y, y) > a.Raw) y--;
            return new Fix(y);
        }

        private static ulong ISqrt(ulong v)
        {
            ulong res = 0;
            ulong bit = 1UL << 62;
            while (bit > v) bit >>= 2;
            while (bit != 0)
            {
                if (v >= res + bit)
                {
                    v -= res + bit;
                    res = (res >> 1) + bit;
                }
                else
                {
                    res >>= 1;
                }
                bit >>= 2;
            }
            return res;
        }

        // ---------------------------------------------------------------- raw kernels

        /// <summary>(a * b) >> 32 using a full 128-bit product; rounds to nearest, ties away from zero.</summary>
        public static long MulRaw(long a, long b)
        {
            unchecked
            {
                bool negative = (a < 0) != (b < 0);
                ulong ua = a < 0 ? (ulong)(-a) : (ulong)a;
                ulong ub = b < 0 ? (ulong)(-b) : (ulong)b;

                ulong aLo = ua & LowMask, aHi = ua >> 32;
                ulong bLo = ub & LowMask, bHi = ub >> 32;

                ulong lolo = aLo * bLo;
                ulong lohi = aLo * bHi;
                ulong hilo = aHi * bLo;
                ulong hihi = aHi * bHi;

                // Bits 32..95 of the product, plus rounding from bit 31.
                ulong low = (lolo >> 32) + (((lolo & LowMask) + 0x80000000UL) >> 32) + (lohi & LowMask) + (hilo & LowMask);
                ulong result = ((hihi + (lohi >> 32) + (hilo >> 32)) << 32) + low;
                long r = (long)result;
                return negative ? -r : r;
            }
        }

        /// <summary>(a << 32) / b; rounds to nearest, ties away from zero.</summary>
        public static long DivRaw(long a, long b)
        {
            if (b == 0) throw new DivideByZeroException();
            unchecked
            {
                bool negative = (a < 0) != (b < 0);
                ulong ua = a < 0 ? (ulong)(-a) : (ulong)a;
                ulong ub = b < 0 ? (ulong)(-b) : (ulong)b;

                ulong qInt = ua / ub;
                ulong rem = ua % ub;
                ulong qFrac;
                bool roundUp;
                if (ub <= LowMask)
                {
                    // rem < ub < 2^32, so rem << 32 fits.
                    ulong n = rem << 32;
                    qFrac = n / ub;
                    ulong r2 = n % ub;
                    roundUp = r2 >= ub - r2; // 2*r2 >= ub without overflow
                }
                else
                {
                    qFrac = 0;
                    for (int i = 0; i < 32; i++)
                    {
                        bool carry = (rem >> 63) != 0;
                        rem <<= 1;
                        qFrac <<= 1;
                        if (carry || rem >= ub)
                        {
                            rem -= ub;
                            qFrac |= 1;
                        }
                    }
                    roundUp = (rem >> 63) != 0 || (rem << 1) >= ub;
                }
                ulong q = (qInt << 32) + qFrac;
                if (roundUp) q++;
                long r = (long)q;
                return negative ? -r : r;
            }
        }

        // ---------------------------------------------------------------- text

        /// <summary>
        /// Parses a plain decimal literal such as "-6.75", "3" or "0.125" using integer math only.
        /// At most 9 fractional digits; no exponents. Throws <see cref="FormatException"/> on bad input.
        /// </summary>
        public static Fix Parse(string text)
        {
            if (!TryParse(text, out Fix value)) throw new FormatException("Not a fixed-point decimal: '" + text + "'");
            return value;
        }

        public static bool TryParse(string text, out Fix value)
        {
            value = Zero;
            if (string.IsNullOrEmpty(text)) return false;
            int i = 0;
            bool negative = false;
            if (text[0] == '-' || text[0] == '+')
            {
                negative = text[0] == '-';
                i = 1;
            }
            if (i >= text.Length) return false;

            long intPart = 0;
            int intDigits = 0;
            while (i < text.Length && text[i] >= '0' && text[i] <= '9')
            {
                intPart = intPart * 10 + (text[i] - '0');
                if (intPart > int.MaxValue) return false;
                intDigits++;
                i++;
            }

            long frac = 0;
            long scale = 1;
            int fracDigits = 0;
            if (i < text.Length && text[i] == '.')
            {
                i++;
                while (i < text.Length && text[i] >= '0' && text[i] <= '9')
                {
                    if (fracDigits == 9) return false;
                    frac = frac * 10 + (text[i] - '0');
                    scale *= 10;
                    fracDigits++;
                    i++;
                }
                if (fracDigits == 0) return false;
            }
            if (i != text.Length || (intDigits == 0 && fracDigits == 0)) return false;

            // frac < 10^9 < 2^30, so frac << 32 < 2^62: exact integer rounding.
            long fracRaw = ((frac << FractionBits) + scale / 2) / scale;
            long raw = (intPart << FractionBits) + fracRaw;
            value = new Fix(negative ? -raw : raw);
            return true;
        }

        /// <summary>Invariant decimal rendering with 4 fractional digits, integer math only.</summary>
        public override string ToString() => ToString(4);

        public string ToString(int decimals)
        {
            if (decimals < 0) decimals = 0;
            if (decimals > 9) decimals = 9;
            long scale = 1;
            for (int i = 0; i < decimals; i++) scale *= 10;
            bool negative = Raw < 0;
            ulong mag = negative ? (ulong)(-Raw) : (ulong)Raw;
            ulong whole = mag >> FractionBits;
            ulong fracRaw = mag & LowMask;
            ulong fracScaled = (fracRaw * (ulong)scale + (1UL << 31)) >> FractionBits;
            if (fracScaled >= (ulong)scale)
            {
                whole++;
                fracScaled -= (ulong)scale;
            }
            string s = whole.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (decimals > 0)
            {
                string f = fracScaled.ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(decimals, '0');
                s = s + "." + f;
            }
            return negative && (whole != 0 || fracScaled != 0) ? "-" + s : s;
        }
    }
}
