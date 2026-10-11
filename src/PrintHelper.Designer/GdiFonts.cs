using System;
using System.Drawing;
using System.Drawing.Text;
using System.Linq;
using System.Runtime.InteropServices;

namespace PrintHelper.Designer
{
    /// <summary>Makes fonts loaded through FontManager available to GDI+ so the design canvas shows them.</summary>
    internal static class GdiFonts
    {
        static PrivateFontCollection _pfc = new PrivateFontCollection();
        static int _version = -1;

        static void Sync()
        {
            if (_version == FontManager.Version) return;
            _version = FontManager.Version;
            _pfc = new PrivateFontCollection();   // old collection is left to the finalizer; fonts change rarely
            foreach (var data in FontManager.LoadedFontData())
            {
                var ptr = Marshal.AllocCoTaskMem(data.Length);
                Marshal.Copy(data, 0, ptr, data.Length);
                try { _pfc.AddMemoryFont(ptr, data.Length); } catch (Exception) { }
            }
        }

        public static Font Get(string name, float size, FontStyle style, GraphicsUnit unit)
        {
            Sync();
            if (string.IsNullOrEmpty(name)) name = "Arial";
            var fam = _pfc.Families.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
            if (fam != null)
            {
                if (!fam.IsStyleAvailable(style)) style = fam.IsStyleAvailable(FontStyle.Regular) ? FontStyle.Regular : style;
                try { return new Font(fam, size, style, unit); } catch (Exception) { }
            }
            try { return new Font(name, size, style, unit); }
            catch (Exception) { return new Font("Arial", size, style, unit); }
        }
    }
}
