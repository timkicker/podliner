using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace StuiPodcast.Core
{
    /// <summary>
    /// Persistierte Bibliothek (library/library.json).
    /// Enthält Feeds, Episoden, Queue & History – KEINE Download-Daten.
    /// </summary>
    public sealed class Library
    {
        public int SchemaVersion { get; set; } = 1;

        // WICHTIG: set; erlauben, damit System.Text.Json befüllen kann
        public List<Feed>    Feeds    { get; set; } = new();
        public List<Episode> Episodes { get; set; } = new();

        /// <summary>Wiedergabe-Queue (Episode-IDs, FIFO).</summary>
        public List<Guid> Queue { get; set; } = new();

        /// <summary>Verlauf „zuletzt gehört/abgeschlossen“.</summary>
        public List<HistoryItem> History { get; set; } = new();
    }

    public sealed class HistoryItem
    {
        public Guid EpisodeId { get; set; }
        public DateTimeOffset At { get; set; }
    }
}