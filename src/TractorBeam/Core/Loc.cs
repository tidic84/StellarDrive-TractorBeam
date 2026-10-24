using UnityEngine.Localization.Settings;

namespace TractorBeam
{
    /// <summary>French or English, following the language picked in the game options.</summary>
    internal static class Loc
    {
        public static bool French
        {
            get
            {
                try
                {
                    string code = LocalizationSettings.SelectedLocale?.Identifier.Code;
                    return code != null && code.StartsWith("fr");
                }
                catch
                {
                    return false;
                }
            }
        }

        public static string T(string fr, string en) => French ? fr : en;
    }
}
