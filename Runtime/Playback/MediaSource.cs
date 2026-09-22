using System;

namespace SkyPlayer.Engine
{
    /// <summary>Product-neutral description of a playable media item.</summary>
    public readonly struct MediaSource
    {
        public string Url { get; }
        public double ResumePositionSeconds { get; }

        public MediaSource(string url, double resumePositionSeconds = 0)
        {
            if (string.IsNullOrWhiteSpace(url))
                throw new ArgumentException("A media URL is required.", nameof(url));

            Url = url;
            ResumePositionSeconds = Math.Max(0, resumePositionSeconds);
        }
    }
}
