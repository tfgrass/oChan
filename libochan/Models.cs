using System.Collections.Generic;

namespace oChan.Models
{
    /// <summary>
    /// Represents the URLs to be saved and loaded.
    /// </summary>
    public class SavedUrls
    {
        public List<string> ThreadUrls { get; set; } = new List<string>();
        public List<string> BoardUrls { get; set; } = new List<string>();
    }
}