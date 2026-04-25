using System.Reflection;
using UnityEngine;
using UnityEngine.UI;

namespace Dio.Common
{
    /// Runtime helper for adding SVG-backed UI graphics. Uses the
    /// `com.unity.vectorgraphics` SVGImage component (vector-quality rendering)
    /// when the package is installed, falling back to a regular Image with the
    /// SVG-imported Sprite, falling back to a flat colored Image when no
    /// sprite is available.
    ///
    /// All access to Unity.VectorGraphics types goes through reflection so the
    /// rest of the codebase compiles whether or not the package is present.
    public static class SvgIconLoader
    {
        static bool _resolved;
        static System.Type _svgImageType;
        static PropertyInfo _svgImageSpriteProp;

        public static System.Type SvgImageType
        {
            get
            {
                if (!_resolved)
                {
                    _svgImageType = System.Type.GetType("Unity.VectorGraphics.SVGImage, Unity.VectorGraphics");
                    if (_svgImageType != null)
                        _svgImageSpriteProp = _svgImageType.GetProperty("sprite");
                    _resolved = true;
                }
                return _svgImageType;
            }
        }

        public static bool VectorGraphicsAvailable => SvgImageType != null;

        /// Attach an Image-or-SVGImage to the GameObject and assign the sprite.
        /// Returns the Graphic component for further tweaking.
        public static Graphic AttachIcon(GameObject go, Sprite sprite, Color tint, bool preserveAspect = true)
        {
            if (SvgImageType != null && sprite != null)
            {
                var comp = go.AddComponent(SvgImageType) as Graphic;
                if (comp != null)
                {
                    _svgImageSpriteProp?.SetValue(comp, sprite);
                    comp.color = tint;
                    comp.raycastTarget = false;
                    return comp;
                }
            }
            var img = go.AddComponent<Image>();
            img.sprite = sprite;
            img.color = sprite != null ? tint : tint;
            img.preserveAspect = preserveAspect;
            img.raycastTarget = false;
            return img;
        }
    }
}
