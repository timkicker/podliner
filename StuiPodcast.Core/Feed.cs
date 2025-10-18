using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace StuiPodcast.Core
{
    public class Feed
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Title { get; set; } = "";
        public string Url { get; set; } = "";
        public DateTimeOffset? LastChecked { get; set; }
    }

}
