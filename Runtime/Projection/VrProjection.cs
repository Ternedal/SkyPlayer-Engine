namespace SkyPlayer.Engine
{
    public enum Geometry { Flat = 0, Sphere = 1 }
    public enum Stereo { Mono = 0, SBS = 1, OU = 2 }

    /// <summary>Resolved playback projection derived from a formatHint string.</summary>
    public struct Projection
    {
        public Geometry Geometry;
        public float Hfov;      // 180 or 360 (sphere only)
        public Stereo Stereo;
        public string Label;

        public static Projection FromHint(string hint)
        {
            switch ((hint ?? "FLAT").ToUpperInvariant())
            {
                case "VR180_MONO": return Make(Geometry.Sphere, 180, Stereo.Mono, "VR180 Mono");
                case "VR180_SBS":  return Make(Geometry.Sphere, 180, Stereo.SBS,  "VR180 SBS");
                case "VR360_MONO": return Make(Geometry.Sphere, 360, Stereo.Mono, "VR360 Mono");
                case "VR360_SBS":  return Make(Geometry.Sphere, 360, Stereo.SBS,  "VR360 SBS");
                case "OU_TB":      return Make(Geometry.Sphere, 180, Stereo.OU,   "VR180 OU");
                case "SBS_3D":     return Make(Geometry.Flat,   0,   Stereo.SBS,  "Flad 3D SBS");
                default:           return Make(Geometry.Flat,   0,   Stereo.Mono, "Flad 2D");
            }
        }

        private static Projection Make(Geometry g, float fov, Stereo s, string label)
            => new Projection { Geometry = g, Hfov = fov, Stereo = s, Label = label };

        // Manual override cycle for the in-headset "Format" button (spec §9:
        // korrekt manuelt formatvalg vigtigere end perfekt auto-detektion).
        public static readonly string[] Cycle =
        {
            "FLAT", "SBS_3D", "VR180_MONO", "VR180_SBS", "VR360_MONO", "VR360_SBS", "OU_TB"
        };
    }
}
