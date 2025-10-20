using System;

namespace StuiPodcast.Core
{
    /// <summary>
    /// Episode-Metadaten + Nutzungsstatus.
    /// KEIN Download-Status und KEIN LocalPath.
    /// </summary>
    public class Episode
    {
        // Identität & Zuordnung
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid FeedId { get; set; }

        /// <summary>Primär-Identität der Audio-Ressource (HTTP/HTTPS, kanonisiert).</summary>
        public string AudioUrl { get; set; } = string.Empty;

        /// <summary>
        /// Optionaler RSS-GUID-Wert des Items (falls vorhanden im Feed).
        /// Dient nur zum Matching bei URL-Wechseln.
        /// </summary>
        public string? RssGuid { get; set; }

        // Metadaten
        public string Title { get; set; } = string.Empty;
        public DateTimeOffset? PubDate { get; set; }
        public long DurationMs { get; set; } = 0;         // 0, wenn unbekannt
        public string DescriptionText { get; set; } = string.Empty;

        // Nutzungsstatus
        public bool Saved { get; set; } = false;          // ★ Favorit

        /// <summary>Fortschritt & Zeitstempel der letzten Wiedergabe.</summary>
        public EpisodeProgress Progress { get; set; } = new();

        /// <summary>Optional: Manuell als „gespielt“ markiert (UI-Funktion). Wird NICHT automatisch gesetzt.</summary>
        public bool ManuallyMarkedPlayed { get; set; } = false;
    }

    public sealed class EpisodeProgress
    {
        /// <summary>Letzte bekannte Position in Millisekunden. Invariante: 0 ≤ LastPosMs ≤ DurationMs.</summary>
        public long LastPosMs { get; set; } = 0;

        /// <summary>Zeitpunkt der letzten Wiedergabe (UTC empfohlen), oder null.</summary>
        public DateTimeOffset? LastPlayedAt { get; set; }
    }
}