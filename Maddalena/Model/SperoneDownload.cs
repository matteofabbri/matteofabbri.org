namespace Maddalena.Model;

// Un singolo pacchetto scaricabile di Sperone. Url punta al NOSTRO proxy (/download/sperone/...),
// non a GitHub: il download passa sempre da maddalena.me.
public sealed class SperoneDownload
{
    public string Platform { get; set; } = "";   // win | linux | mac

    public string Name { get; set; } = "";        // es. sperone-win-x64.zip

    public long Size { get; set; }                 // byte

    public string Url { get; set; } = "";          // es. /download/sperone/win
}
