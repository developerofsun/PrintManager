namespace PrintHelper
{
    /// <summary>Text helpers for UI layers that draw report text themselves (designer canvas, previews).</summary>
    public static class TextUtil
    {
        /// <summary>Whether an element's text is laid out right-to-left, following the same rules as the engine.</summary>
        public static bool IsRtl(string text, TextDirection element, TextDirection reportDefault)
        {
            return TextEngine.ResolveRtl(text, element, reportDefault);
        }

        public static string ConvertDigits(string s, DigitStyle style) { return Bidi.ConvertDigits(s, style); }
    }
}
