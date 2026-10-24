using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace TractorBeam.Client
{
    /// <summary>uGUI with the game's two fonts, on the game's 3840 x 2160 reference canvas (from Arsenal).</summary>
    internal static class UiKit
    {
        private static TMP_FontAsset _title, _body;

        public static void ClearCache() => _title = _body = null;

        public static TMP_FontAsset TitleFont => _title != null ? _title : (_title = Font("Anta"));
        public static TMP_FontAsset BodyFont => _body != null ? _body : (_body = Font("Mulish"));

        private const string Charset =
            " !\"#$%&'()*+,-./0123456789:;<=>?@ABCDEFGHIJKLMNOPQRSTUVWXYZ[\\]^_`abcdefghijklmnopqrstuvwxyz{|}~" +
            "àâäçéèêëîïôöùûüÿœæÀÂÄÇÉÈÊËÎÏÔÖÙÛÜŸŒÆ«»·–—…’×°%";

        private static TMP_FontAsset Font(string part)
        {
            TMP_FontAsset f = Resources.FindObjectsOfTypeAll<TMP_FontAsset>().FirstOrDefault(x => x != null && x.name.Contains(part)) ??
                              Resources.FindObjectsOfTypeAll<TMP_FontAsset>().FirstOrDefault();
            if (f != null) CompleteGlyphs(f);
            return f;
        }

        /// <summary>
        /// The game's fonts are dynamic atlases that only hold the characters the game itself displayed.
        /// Add ours if the font can, and fall back on the game's full LiberationSans for anything else.
        /// </summary>
        private static void CompleteGlyphs(TMP_FontAsset f)
        {
            try
            {
                f.TryAddCharacters(Charset, out string missing);
                if (string.IsNullOrEmpty(missing)) return;
            }
            catch
            {
                // no source font data: fallback below
            }
            TMP_FontAsset full = Resources.FindObjectsOfTypeAll<TMP_FontAsset>()
                .FirstOrDefault(x => x != null && x.atlasPopulationMode == AtlasPopulationMode.Static && x.characterTable != null && x.characterTable.Count > 150);
            if (full == null || full == f) return;
            if (f.fallbackFontAssetTable == null) f.fallbackFontAssetTable = new List<TMP_FontAsset>();
            if (!f.fallbackFontAssetTable.Contains(full)) f.fallbackFontAssetTable.Add(full);
        }

        /// <summary>A screen-space canvas scaled like the game's (3840 x 2160 reference).</summary>
        public static Canvas MakeCanvas(string name, int sortingOrder)
        {
            var go = new GameObject(name);
            Object.DontDestroyOnLoad(go);
            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = sortingOrder;
            var scaler = go.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(3840, 2160);
            scaler.matchWidthOrHeight = 0.5f;
            go.AddComponent<GraphicRaycaster>();
            EnsureEventSystem();
            return canvas;
        }

        public static void EnsureEventSystem()
        {
            if (EventSystem.current != null) return;
            if (Object.FindObjectsByType<EventSystem>(FindObjectsSortMode.None).Any()) return;
            var es = new GameObject("TractorBeam.EventSystem");
            Object.DontDestroyOnLoad(es);
            es.AddComponent<EventSystem>();
            es.AddComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>();
        }

        public static RectTransform Rect(Transform parent, string name, Vector2 anchorMin, Vector2 anchorMax, Vector2 pos, Vector2 size, Vector2? pivot = null)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.pivot = pivot ?? new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
            return rt;
        }

        public static TextMeshProUGUI Label(RectTransform rt, string text, float size, Color color, bool title = true, TextAlignmentOptions align = TextAlignmentOptions.Left)
        {
            var t = rt.gameObject.AddComponent<TextMeshProUGUI>();
            t.font = title ? TitleFont : BodyFont;
            t.fontSize = size;
            t.color = color;
            t.text = text;
            t.alignment = align;
            t.raycastTarget = false;
            t.textWrappingMode = title ? TextWrappingModes.NoWrap : TextWrappingModes.Normal;
            t.fontFeatures?.RemoveAll(f => f.ToString() == "liga"); // the dynamic atlas drops the glyph after a ligature
            t.overflowMode = TextOverflowModes.Overflow;
            return t;
        }

    }
}
