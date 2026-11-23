using Jellyfin.Plugin.JellyTag.Services;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.JellyTag.Tests.Services;

// Streams carry the fields ffprobe fills; Jellyfin derives VideoRangeType from them.
public class DetectFormatsTests
{
    private const string Pq = "smpte2084";
    private const string Hlg = "arib-std-b67";

    private static MediaStream Video(string transfer, int? dvProfile = null, int? dvCompatId = null, bool hdr10Plus = false) => new()
    {
        Type = MediaStreamType.Video,
        ColorTransfer = transfer,
        ColorSpace = "bt2020nc",
        ColorPrimaries = "bt2020",
        DvProfile = dvProfile,
        DvBlSignalCompatibilityId = dvCompatId,
        RpuPresentFlag = dvProfile is null ? null : 1,
        BlPresentFlag = dvProfile is null ? null : 1,
        Hdr10PlusPresentFlag = hdr10Plus
    };

    private static string Hdr(MediaStream stream) =>
        string.Join(",", QualityDetectionService.DetectHdr(stream).Select(b => b.DisplayText ?? b.BadgeKey));

    [Theory]
    [InlineData("bt709", "")]
    [InlineData(Pq, "hdr10")]
    [InlineData(Hlg, "hlg")]
    public void HdrWithoutDolbyVision(string transfer, string expected)
    {
        Assert.Equal(expected, Hdr(Video(transfer)));
    }

    [Fact]
    public void HdrTenPlus()
    {
        Assert.Equal("hdr10plus", Hdr(Video(Pq, hdr10Plus: true)));
    }

    // #33: Dolby Vision comes with its base layer, best first for the "Highest" mode.
    // #30: then its profile, an optional text badge.
    [Theory]
    [InlineData(Pq, 5, 0, false, "dv,DV P5")]                   // no base layer to show
    [InlineData(Pq, 8, 1, false, "dv,DV P8.1,hdr10")]
    [InlineData(Pq, 8, 1, true, "dv,DV P8.1,hdr10plus")]        // with HDR10+ metadata
    [InlineData(Hlg, 8, 4, false, "dv,DV P8.4,hlg")]
    [InlineData(Pq, 7, 6, false, "dv,DV P7,hdr10")]             // base layer HDR10
    public void DolbyVision_WithItsBaseLayer(string transfer, int profile, int compatId, bool hdr10Plus, string expected)
    {
        Assert.Equal(expected, Hdr(Video(transfer, profile, compatId, hdr10Plus)));
    }

    [Fact]
    public void DolbyVisionProfile_UsesItsImage()
    {
        var badge = QualityDetectionService.DetectHdr(Video(Pq, 8, 1)).Single(b => b.BadgeKey == "dvprofile");
        Assert.Equal("badge-dvp8_1.svg", badge.ResourceFileName);
    }

    private static string Audio(string codec, string profile, int channels) =>
        string.Join(",", QualityDetectionService.DetectAudio(
            [new MediaStream { Type = MediaStreamType.Audio, Codec = codec, Profile = profile, Channels = channels }])
            .Select(b => b.BadgeKey));

    // #19: Atmos and DTS:X are shown with the codec that carries them.
    [Theory]
    [InlineData("truehd", "Dolby TrueHD + Dolby Atmos", 8, "atmos,truehd,7.1")]
    [InlineData("truehd", "", 8, "truehd,7.1")]
    [InlineData("eac3", "Dolby Digital Plus + Dolby Atmos", 6, "atmos,5.1")]
    [InlineData("dts", "DTS-HD MA + DTS:X", 8, "dtsx,dtshdma,7.1")]
    [InlineData("dts", "DTS-HD MA", 6, "dtshdma,5.1")]
    [InlineData("aac", "LC", 2, "aac,stereo")]
    [InlineData("opus", "", 2, "opus,stereo")]
    [InlineData("opus", "", 6, "opus,5.1")]
    [InlineData("flac", "", 6, "flac,5.1")]
    [InlineData("dts", "DTS-HD HRA", 8, "dtshd,7.1")]
    [InlineData("dts", "DTS", 6, "dts,5.1")]
    [InlineData("dts", "DTS-HD MA + DTS:X", 6, "dtsx,dtshdma,5.1")]
    [InlineData("eac3", "Dolby Digital Plus", 6, "eac3,5.1")]
    [InlineData("ac3", "", 6, "ac3,5.1")]
    public void AudioFormats(string codec, string profile, int channels, string expected)
    {
        Assert.Equal(expected, Audio(codec, profile, channels));
    }
}
