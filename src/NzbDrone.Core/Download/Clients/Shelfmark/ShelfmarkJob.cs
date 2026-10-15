using NzbDrone.Core.Books;

namespace NzbDrone.Core.Download.Clients.Shelfmark
{
    public class ShelfmarkJob
    {
        public string SourceId { get; set; }
        public string Title { get; set; }
        public string Format { get; set; }
        public long Size { get; set; }
        public BookMediaType MediaType { get; set; }
        public bool Transferred { get; set; }
        public bool SkipLibraryImport { get; set; }
        public string DownloadFolder { get; set; }
    }
}
