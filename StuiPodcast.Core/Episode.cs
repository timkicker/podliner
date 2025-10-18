using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace StuiPodcast.Core
{
    public class Episode
    {
        public bool Saved { get; set; } = false;
        public bool Downloaded { get; set; } = false;
        public long? LastPosMs { get; set; }
        public long? LengthMs { get; set; }
        public bool Played { get; set; }
        public DateTimeOffset? LastPlayedAt { get; set; }

        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid FeedId { get; set; }
        public string Title { get; set; } = "";
        public DateTimeOffset? PubDate { get; set; }
        public string AudioUrl { get; set; } = "";
        public TimeSpan? Duration { get; set; }
        public string DescriptionText { get; set; } = "";
    }
}
