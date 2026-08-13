// BOUNDARY SHIM, not a Unity reimplementation.
//
// Three of the mod's Visualization files are pure image mathematics that happen to spell
// a handful of operations in Unity's dialect: FitsWriter.cs uses Mathf twice,
// AstroImageStack.cs uses nine Mathf functions, ColourComposite.cs returns Color[].
// Rewriting those files would fork the mod's validated stacking and export code; this
// shim supplies the dialect instead, so they compile VERBATIM from the mod tree.
//
// Only what those three files actually use is provided, deliberately: a fuller shim
// would invite compiling Unity-shaped code that genuinely needs an engine.

namespace UnityEngine
{
    public static class Mathf
    {
        public static float Abs(float v) => System.Math.Abs(v);
        public static int CeilToInt(float v) => (int)System.Math.Ceiling(v);
        public static int FloorToInt(float v) => (int)System.Math.Floor(v);
        public static int RoundToInt(float v) => (int)System.Math.Round(v, System.MidpointRounding.AwayFromZero);
        public static float Sqrt(float v) => (float)System.Math.Sqrt(v);
        public static float Log(float v) => (float)System.Math.Log(v);
        public static float Log(float v, float b) => (float)System.Math.Log(v, b);
        public static float Pow(float v, float p) => (float)System.Math.Pow(v, p);
        public static float Min(float a, float b) => System.Math.Min(a, b);
        public static float Max(float a, float b) => System.Math.Max(a, b);
        public static int Min(int a, int b) => System.Math.Min(a, b);
        public static int Max(int a, int b) => System.Math.Max(a, b);
        public static float Clamp(float v, float lo, float hi) => System.Math.Clamp(v, lo, hi);
        public static int Clamp(int v, int lo, int hi) => System.Math.Clamp(v, lo, hi);
        public static float Clamp01(float v) => System.Math.Clamp(v, 0f, 1f);
    }

    /// <summary>Unity's Color: four floats, straight alpha, no behaviour the composites rely on beyond storage.</summary>
    public struct Color
    {
        public float r, g, b, a;
        public Color(float r, float g, float b) { this.r = r; this.g = g; this.b = b; a = 1f; }
        public Color(float r, float g, float b, float a) { this.r = r; this.g = g; this.b = b; this.a = a; }

        public static readonly Color black = new(0f, 0f, 0f);
        public static readonly Color white = new(1f, 1f, 1f);
    }
}
