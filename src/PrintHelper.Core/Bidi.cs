using System;
using System.Collections.Generic;
using System.Text;

namespace PrintHelper
{
    /// <summary>A stretch of text with one direction, in visual (left-to-right) order.</summary>
    internal sealed class BidiRun
    {
        public string Text;
        public bool Rtl;
        /// <summary>Arabic-Indic / Persian digits shown left-to-right (HarfBuzz would otherwise guess right-to-left from the script).</summary>
        public bool ArabicDigits;
    }

    /// <summary>
    /// A compact implementation of the Unicode bidirectional algorithm's essentials: enough for Persian, Kurdish (Sorani),
    /// Arabic and Hebrew mixed with English and numbers. Text is split into direction runs and reordered visually;
    /// glyph shaping inside each run is done by HarfBuzz.
    /// </summary>
    internal static class Bidi
    {
        enum T { L, R, Digit, Neutral }

        static bool IsRtlChar(char c)
        {
            return (c >= 0x0590 && c <= 0x08FF) || (c >= 0xFB1D && c <= 0xFDFF) || (c >= 0xFE70 && c <= 0xFEFF) || c == 0x200F || c == 0x202B;
        }

        static bool IsDigit(char c)
        {
            return (c >= '0' && c <= '9') || (c >= 0x0660 && c <= 0x0669) || (c >= 0x06F0 && c <= 0x06F9);
        }

        static T Classify(char c)
        {
            if (IsDigit(c)) return T.Digit;
            if (IsRtlChar(c))
            {
                // Arabic-script punctuation (، ؛ ؟) behaves as neutral text but is RTL: keep it R so it stays with its words.
                return T.R;
            }
            if (c == 0x200E) return T.L;
            if (char.IsLetter(c) || char.IsSurrogate(c)) return T.L;
            return T.Neutral;
        }

        /// <summary>True when the first strong letter is right-to-left.</summary>
        public static bool DetectRtl(string s)
        {
            if (s == null) return false;
            foreach (var c in s)
            {
                if (IsDigit(c)) continue;
                var t = Classify(c);
                if (t == T.R) return true;
                if (t == T.L) return false;
            }
            return false;
        }

        public static bool ContainsRtl(string s)
        {
            if (s == null) return false;
            foreach (var c in s) if (IsRtlChar(c)) return true;
            return false;
        }

        /// <summary>Splits one logical line into direction runs and returns them left-to-right as displayed.</summary>
        public static List<BidiRun> Reorder(string text, bool paraRtl)
        {
            var result = new List<BidiRun>();
            if (string.IsNullOrEmpty(text)) return result;
            int n = text.Length;

            // fast path: nothing right-to-left and a left-to-right paragraph
            if (!paraRtl && !ContainsRtl(text)) { result.Add(new BidiRun { Text = text, Rtl = false }); return result; }

            var type = new T[n];
            for (int i = 0; i < n; i++) type[i] = Classify(text[i]);

            // separators inside numbers (1,234.50  12/05  10:30) belong to the number
            for (int i = 1; i < n - 1; i++)
                if (type[i] == T.Neutral && ",.:/-+٫٬".IndexOf(text[i]) >= 0 && type[i - 1] == T.Digit && type[i + 1] == T.Digit)
                    type[i] = T.Digit;

            // W7: a digit that follows a left-to-right letter behaves as left-to-right, otherwise as right-to-left
            var eff = new T[n];
            T seen = paraRtl ? T.R : T.L;
            for (int i = 0; i < n; i++)
            {
                if (type[i] == T.L || type[i] == T.R) { seen = type[i]; eff[i] = type[i]; }
                else if (type[i] == T.Digit) eff[i] = seen == T.L ? T.L : T.R;
                else eff[i] = T.Neutral;
            }

            // resolve neutrals from the surrounding strong types
            var strong = new T[n];
            for (int i = 0; i < n; i++)
            {
                if (eff[i] != T.Neutral) { strong[i] = eff[i]; continue; }
                int j = i;
                while (j < n && eff[j] == T.Neutral) j++;
                T before = i == 0 ? (paraRtl ? T.R : T.L) : eff[i - 1];
                T after = j == n ? (paraRtl ? T.R : T.L) : eff[j];
                T res = before == after ? before : (paraRtl ? T.R : T.L);
                for (int k = i; k < j; k++) strong[k] = res;
                i = j - 1;
            }

            // embedding levels
            int baseLevel = paraRtl ? 1 : 0;
            var level = new int[n];
            for (int i = 0; i < n; i++)
            {
                if (type[i] == T.Digit) level[i] = eff[i] == T.R ? 2 : (paraRtl ? 2 : 0);   // numbers are always shown left-to-right
                else if (strong[i] == T.R) level[i] = 1;
                else level[i] = paraRtl ? 2 : 0;
            }

            // Arabic-script digit runs (incl. separators inside them) are split into their own runs
            var arNum = new bool[n];
            for (int i = 0; i < n; i++)
            {
                char c = text[i];
                if (type[i] != T.Digit) continue;
                if ((c >= 0x0660 && c <= 0x0669) || (c >= 0x06F0 && c <= 0x06F9)) arNum[i] = true;
                else if (!(c >= '0' && c <= '9') && i > 0 && i < n - 1 && (arNumCharAt(text, i - 1) || arNumCharAt(text, i + 1))) arNum[i] = true;
            }

            // runs of equal level
            var levels = new List<int>();
            var runs = new List<BidiRun>();
            int start = 0;
            for (int i = 1; i <= n; i++)
            {
                if (i == n || level[i] != level[start] || arNum[i] != arNum[start])
                {
                    bool odd = (level[start] & 1) == 1;
                    var seg = text.Substring(start, i - start);
                    runs.Add(new BidiRun { Text = odd ? Mirror(seg) : seg, Rtl = odd, ArabicDigits = arNum[start] && !odd });
                    levels.Add(level[start]);
                    start = i;
                }
            }

            // reorder: from the highest level down to the lowest odd level, reverse contiguous runs at that level or higher
            int max = 0, minOdd = int.MaxValue;
            foreach (var l in levels) { if (l > max) max = l; if ((l & 1) == 1 && l < minOdd) minOdd = l; }
            if (paraRtl && baseLevel < minOdd) minOdd = baseLevel;
            for (int lv = max; lv >= minOdd && lv >= 1; lv--)
            {
                int i = 0;
                while (i < runs.Count)
                {
                    if (levels[i] >= lv)
                    {
                        int j = i;
                        while (j < runs.Count && levels[j] >= lv) j++;
                        runs.Reverse(i, j - i);
                        levels.Reverse(i, j - i);
                        i = j;
                    }
                    else i++;
                }
            }
            return runs;
        }

        static bool arNumCharAt(string t, int i)
        {
            char c = t[i];
            return (c >= 0x0660 && c <= 0x0669) || (c >= 0x06F0 && c <= 0x06F9);
        }

        static string Mirror(string s)
        {
            const string from = "()[]{}<>«»";
            const string to = ")(][}{><»«";
            StringBuilder sb = null;
            for (int i = 0; i < s.Length; i++)
            {
                int k = from.IndexOf(s[i]);
                if (k < 0) continue;
                if (sb == null) sb = new StringBuilder(s);
                sb[i] = to[k];
            }
            return sb == null ? s : sb.ToString();
        }

        /// <summary>Converts digits to the requested style.</summary>
        public static string ConvertDigits(string s, DigitStyle style)
        {
            if (string.IsNullOrEmpty(s) || style == DigitStyle.AsIs) return s;
            var sb = new StringBuilder(s.Length);
            foreach (var c in s)
            {
                int d = -1;
                if (c >= '0' && c <= '9') d = c - '0';
                else if (c >= 0x0660 && c <= 0x0669) d = c - 0x0660;
                else if (c >= 0x06F0 && c <= 0x06F9) d = c - 0x06F0;
                if (d < 0) { sb.Append(c); continue; }
                switch (style)
                {
                    case DigitStyle.Latin: sb.Append((char)('0' + d)); break;
                    case DigitStyle.ArabicIndic: sb.Append((char)(0x0660 + d)); break;
                    case DigitStyle.Persian: sb.Append((char)(0x06F0 + d)); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }
    }
}
