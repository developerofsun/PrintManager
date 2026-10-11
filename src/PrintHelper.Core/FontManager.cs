using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SkiaSharp;

namespace PrintHelper
{
    /// <summary>
    /// Load your own fonts (TTF/OTF) so reports look the same on every machine, and so Persian/Kurdish/Arabic
    /// glyphs exist. Loaded fonts win over installed fonts of the same family name.
    /// </summary>
    public static class FontManager
    {
        static readonly object Gate = new object();
        static readonly List<SKTypeface> Custom = new List<SKTypeface>();
        static readonly Dictionary<string, SKTypeface> Cache = new Dictionary<string, SKTypeface>();
        static readonly Dictionary<string, SKTypeface> FallbackCache = new Dictionary<string, SKTypeface>();
        static readonly HashSet<string> LoadedHashes = new HashSet<string>();
        static readonly List<byte[]> CustomData = new List<byte[]>();

        /// <summary>Increases whenever a font is loaded (UI layers use it to refresh their own font caches).</summary>
        public static int Version { get; private set; }

        /// <summary>Raw bytes of every font loaded so far (lets a GDI/WPF UI register the same fonts).</summary>
        public static IList<byte[]> LoadedFontData() { lock (Gate) return CustomData.ToList(); }

        /// <summary>Used when a family is not found.</summary>
        public static string DefaultFamily = "Arial";

        /// <summary>Loads one font file. Returns the font's family name.</summary>
        public static string LoadFontFile(string path)
        {
            return LoadFontBytes(File.ReadAllBytes(path));
        }

        /// <summary>Loads a font from memory (e.g. an embedded resource). Returns the family name.</summary>
        public static string LoadFontBytes(byte[] data)
        {
            var tf = SKTypeface.FromData(SKData.CreateCopy(data));
            if (tf == null) throw new ReportException("Not a valid font file.");
            lock (Gate)
            {
                Custom.Add(tf);
                CustomData.Add(data);
                Version++;
                Cache.Clear();
                FallbackCache.Clear();
            }
            return tf.FamilyName;
        }

        /// <summary>Loads every .ttf/.otf in a folder. Returns how many fonts were loaded.</summary>
        public static int LoadFontDirectory(string dir)
        {
            int n = 0;
            foreach (var f in Directory.GetFiles(dir))
            {
                var ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext != ".ttf" && ext != ".otf") continue;
                try { LoadFontFile(f); n++; } catch (Exception) { }
            }
            return n;
        }

        /// <summary>Loads fonts embedded in a report (once per process each). ReportEngine calls this automatically.</summary>
        public static void LoadEmbedded(IEnumerable<EmbeddedFont> fonts)
        {
            foreach (var f in fonts)
            {
                if (string.IsNullOrEmpty(f.Base64)) continue;
                string key = f.FileName + ":" + f.Base64.Length + ":" + f.Base64.GetHashCode();
                lock (Gate) { if (!LoadedHashes.Add(key)) continue; }
                try { LoadFontBytes(Convert.FromBase64String(f.Base64)); } catch (Exception) { }
            }
        }

        public static IEnumerable<string> FamilyNames()
        {
            var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            lock (Gate) foreach (var c in Custom) names.Add(c.FamilyName);
            try { foreach (var f in SKFontManager.Default.FontFamilies) names.Add(f); } catch (Exception) { }
            return names.ToList();
        }

        public static IEnumerable<string> LoadedFamilyNames()
        {
            lock (Gate) return Custom.Select(c => c.FamilyName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static SKTypeface Get(string family, bool bold, bool italic)
        {
            if (string.IsNullOrEmpty(family)) family = DefaultFamily;
            var key = family.ToLowerInvariant() + "|" + bold + "|" + italic;
            lock (Gate)
            {
                SKTypeface tf;
                if (Cache.TryGetValue(key, out tf)) return tf;

                var mine = Custom.Where(c => string.Equals(c.FamilyName, family, StringComparison.OrdinalIgnoreCase)).ToList();
                if (mine.Count > 0)
                {
                    tf = mine.OrderBy(c => (c.FontWeight >= 600) == bold ? 0 : 1)
                             .ThenBy(c => (c.FontSlant != SKFontStyleSlant.Upright) == italic ? 0 : 1).First();
                }
                else
                {
                    var style = new SKFontStyle(bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal, SKFontStyleWidth.Normal,
                                                italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright);
                    tf = SKTypeface.FromFamilyName(family, style) ?? SKTypeface.Default;
                }
                Cache[key] = tf;
                return tf;
            }
        }

        /// <summary>A typeface that has the given character, preferring loaded fonts (so your Persian font is used for missing glyphs).</summary>
        internal static SKTypeface FindFor(SKTypeface primary, int codepoint, bool bold, bool italic)
        {
            lock (Gate)
            {
                var key = primary.FamilyName + "|" + codepoint + "|" + bold + "|" + italic;
                SKTypeface tf;
                if (FallbackCache.TryGetValue(key, out tf)) return tf;

                tf = Custom.FirstOrDefault(c => c.ContainsGlyph(codepoint));
                if (tf == null)
                {
                    var style = new SKFontStyle(bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal, SKFontStyleWidth.Normal,
                                                italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright);
                    try { tf = SKFontManager.Default.MatchCharacter(primary.FamilyName, style, null, codepoint); } catch (Exception) { }
                }
                FallbackCache[key] = tf ?? primary;
                return tf ?? primary;
            }
        }
    }
}
