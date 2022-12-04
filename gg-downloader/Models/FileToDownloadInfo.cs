namespace gg_downloader.Models
{
    internal class FileToDownloadInfo
    {
        public enum FileType
        {
            Game,
            Goody,
            Patch
        }

        public string FileName { get; set; }
        public FileType Type { get; set; }

    }
}
