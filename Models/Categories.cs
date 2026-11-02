namespace EveryDownload.Models;

/// <summary>Sorts downloads into folders by file type, the way IDM and ADM do.</summary>
public static class Categories
{
    public const string Video = "Video", Music = "Music", Documents = "Documents", Archives = "Archives",
        Programs = "Programs", Images = "Images", Torrents = "Torrents", Other = "Other";

    public static readonly string[] All = { Video, Music, Documents, Archives, Programs, Images, Torrents, Other };

    private static readonly Dictionary<string, string> ByExtension = Build();

    private static Dictionary<string, string> Build()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Add(string category, string extensions)
        {
            foreach (var e in extensions.Split(' ', StringSplitOptions.RemoveEmptyEntries)) map["." + e] = category;
        }
        Add(Video, "mp4 mkv avi mov wmv flv webm m4v mpg mpeg 3gp ts m2ts");
        Add(Music, "mp3 flac wav aac ogg m4a wma opus mid");
        Add(Documents, "pdf doc docx xls xlsx ppt pptx txt rtf odt ods csv epub mobi djvu md");
        Add(Archives, "zip rar 7z tar gz bz2 xz iso img cab zst tgz");
        Add(Programs, "exe msi msix appx apk dmg pkg deb rpm bat ps1 jar");
        Add(Images, "jpg jpeg png gif bmp webp svg tif tiff ico heic psd raw");
        Add(Torrents, "torrent");
        return map;
    }

    public static string For(string fileName) =>
        ByExtension.TryGetValue(Path.GetExtension(fileName), out var c) ? c : Other;

    /// <summary>The Segoe Fluent glyph used for the category's icon.</summary>
    public static string Glyph(string category) => category switch
    {
        Video => "",
        Music => "",
        Documents => "",
        Archives => "",
        Programs => "",
        Images => "",
        Torrents => "",
        _ => "",
    };
}
