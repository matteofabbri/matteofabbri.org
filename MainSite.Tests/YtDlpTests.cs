using System.Runtime.InteropServices;
using MainSite.Blog;

namespace MainSite.Tests;

public sealed class YtDlpTests
{
    [Theory]
    [InlineData("Windows", "yt-dlp.exe")]
    [InlineData("Linux", "yt-dlp_linux")]
    [InlineData("OSX", "yt-dlp_macos")]
    public void Sceglie_l_asset_giusto_per_piattaforma(string platform, string expected)
    {
        OSPlatform os = platform switch
        {
            "Windows" => OSPlatform.Windows,
            "Linux" => OSPlatform.Linux,
            _ => OSPlatform.OSX,
        };

        Assert.Equal(expected, YtDlp.AssetName(os, Architecture.X64));
    }

    [Fact]
    public void Su_linux_arm_usa_il_binario_aarch64()
    {
        Assert.Equal("yt-dlp_linux_aarch64", YtDlp.AssetName(OSPlatform.Linux, Architecture.Arm64));
    }

    [Fact]
    public void Una_piattaforma_sconosciuta_non_ha_asset()
    {
        Assert.Null(YtDlp.AssetName(OSPlatform.FreeBSD, Architecture.X64));
    }

    [Fact]
    public void Il_nome_dell_eseguibile_segue_la_piattaforma()
    {
        Assert.Equal(OperatingSystem.IsWindows() ? "yt-dlp.exe" : "yt-dlp", YtDlp.ExecutableName);
    }

    [Fact]
    public void Con_ffmpeg_punta_al_merge_senza_ffmpeg_a_un_file_unico()
    {
        string withoutFfmpeg = YtDlp.DefaultFormatSelector(hasFfmpeg: false);
        string withFfmpeg = YtDlp.DefaultFormatSelector(hasFfmpeg: true);

        Assert.Equal("b[ext=mp4]/b", withoutFfmpeg);
        Assert.Contains("+ba", withFfmpeg);
        Assert.Contains("[ext=mp4]", withFfmpeg);
    }
}
