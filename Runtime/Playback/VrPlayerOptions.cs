using System;
using UnityEngine;

namespace SkyPlayer.Engine
{
    /// <summary>Runtime AR/background-key parameters. Persistence belongs to the client.</summary>
    [Serializable]
    public struct BackgroundKeySettings
    {
        public bool Enabled;
        [Range(0.02f, 0.95f)] public float Threshold;
        [Range(0, 7)] public int Mode;
        [Range(0, 3)] public int DetailMode;
        [Range(0, 3)] public int SpillMode;
        public Color KeyColor;

        public static BackgroundKeySettings Default => new BackgroundKeySettings
        {
            Enabled = false,
            Threshold = 0.15f,
            Mode = 1,
            DetailMode = 1,
            SpillMode = 2,
            KeyColor = new Color(0.10f, 0.12f, 0.16f, 1f)
        };
    }

    [Serializable]
    public struct VrPlayerOptions
    {
        [Range(0f, 1f)] public float DefaultZoom;
        public BackgroundKeySettings BackgroundKey;

        public static VrPlayerOptions Default => new VrPlayerOptions
        {
            DefaultZoom = 0.5f,
            BackgroundKey = BackgroundKeySettings.Default
        };
    }
}
