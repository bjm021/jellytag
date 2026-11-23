using System.Collections.Concurrent;
using System.Globalization;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.JellyTag.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;
using VideoRange = Jellyfin.Data.Enums.VideoRange;
using VideoRangeType = Jellyfin.Data.Enums.VideoRangeType;

namespace Jellyfin.Plugin.JellyTag.Services;

/// <summary>
/// Service for detecting video quality from media items.
/// </summary>
public class QualityDetectionService : IQualityDetectionService
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<QualityDetectionService> _logger;
    private readonly ConcurrentDictionary<Guid, (List<BadgeInfo> Badges, DateTime CachedAt)> _badgeCache = new();
    private static readonly TimeSpan BadgeCacheTtl = TimeSpan.FromMinutes(5);
    private DateTime _lastCacheCleanup = DateTime.UtcNow;
    private static readonly TimeSpan CacheCleanupInterval = TimeSpan.FromMinutes(10);

    public QualityDetectionService(
        ILibraryManager libraryManager,
        ILogger<QualityDetectionService> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public VideoQuality GetQuality(Guid itemId)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item == null)
        {
            _logger.LogDebug("Item not found: {ItemId}", itemId);
            return VideoQuality.Unknown;
        }

        return GetQualityFromItem(item);
    }

    public static VideoQuality DetermineQuality(int width, int height)
    {
        // A tier is reached by the width (wide and scope formats) or the height
        // (4:3 and other narrow encodes, e.g. 1440x1080 is 1080p). Portrait videos
        // are handled by comparing the long and the short side.
        var longSide = Math.Max(width, height);
        var shortSide = Math.Min(width, height);

        if (longSide >= 3800 || shortSide >= 2100) return VideoQuality.UHD4K;
        if (longSide >= 1900 || shortSide >= 1000) return VideoQuality.FHD1080p;
        if (longSide >= 1260 || shortSide >= 700) return VideoQuality.HD720p;
        if (longSide > 0) return VideoQuality.SD;
        return VideoQuality.Unknown;
    }

    /// <inheritdoc />
    public VideoQuality GetQualityFromItem(BaseItem item)
    {
        if (item is Video video)
        {
            return GetQualityFromVideo(video);
        }

        var query = new InternalItemsQuery
        {
            ParentId = item.Id,
            Recursive = true,
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode],
            Limit = 10
        };
        var children = _libraryManager.GetItemList(query);
        var bestQuality = VideoQuality.Unknown;
        foreach (var child in children)
        {
            if (child is Video childVideo)
            {
                var q = GetQualityFromVideo(childVideo);
                if (q != VideoQuality.Unknown && (bestQuality == VideoQuality.Unknown || q > bestQuality))
                {
                    bestQuality = q;
                    if (bestQuality == VideoQuality.UHD4K) break;
                }
            }
        }

        if (bestQuality != VideoQuality.Unknown)
        {
            _logger.LogDebug("Resolved quality {Quality} for parent item: {ItemName}", bestQuality, item.Name);
        }

        return bestQuality;
    }

    /// <inheritdoc />
    public List<BadgeInfo> DetectAllBadges(BaseItem item)
    {
        if (_badgeCache.TryGetValue(item.Id, out var cached) && DateTime.UtcNow - cached.CachedAt < BadgeCacheTtl)
        {
            return new List<BadgeInfo>(cached.Badges);
        }

        var badges = DetectAllBadgesInternal(item);
        _badgeCache[item.Id] = (badges, DateTime.UtcNow);

        // Periodically evict expired entries to prevent unbounded memory growth
        if (DateTime.UtcNow - _lastCacheCleanup > CacheCleanupInterval)
        {
            _lastCacheCleanup = DateTime.UtcNow;
            var expiredKeys = _badgeCache
                .Where(kvp => DateTime.UtcNow - kvp.Value.CachedAt > BadgeCacheTtl)
                .Select(kvp => kvp.Key)
                .ToList();
            foreach (var key in expiredKeys)
            {
                _badgeCache.TryRemove(key, out _);
            }
        }
        return badges;
    }

    /// <inheritdoc />
    public void ClearBadgeCache()
    {
        _badgeCache.Clear();
    }

    private List<BadgeInfo> DetectAllBadgesInternal(BaseItem item)
    {
        var badges = new List<BadgeInfo>();

        if (item is Video video)
        {
            DetectBadgesFromVideo(video, badges);
        }
        else
        {
            var query = new InternalItemsQuery
            {
                ParentId = item.Id,
                Recursive = true,
                IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode],
                Limit = 10
            };
            var children = _libraryManager.GetItemList(query);

            var bestResolution = VideoQuality.Unknown;
            Video? bestVideo = null;

            foreach (var child in children)
            {
                if (child is Video childVideo)
                {
                    var q = GetQualityFromVideo(childVideo);
                    if (q != VideoQuality.Unknown && (bestResolution == VideoQuality.Unknown || q > bestResolution))
                    {
                        bestResolution = q;
                        bestVideo = childVideo;
                    }

                    bestVideo ??= childVideo;
                }
            }

            if (bestResolution != VideoQuality.Unknown)
            {
                badges.Add(CreateResolutionBadge(bestResolution));
            }

            if (bestVideo != null)
            {
                DetectHdrAndAudioBadges(bestVideo, badges);
            }
        }

        badges.AddRange(DetectRatings(item));
        return badges;
    }

    /// <summary>
    /// Rating badges from the item's own metadata: community rating out of 10, critic rating
    /// in percent. The provider that wrote them is not recorded, hence no source label.
    /// </summary>
    internal static List<BadgeInfo> DetectRatings(BaseItem item)
    {
        var badges = new List<BadgeInfo>();

        // Providers without a rating may store 0: no badge rather than "0.0/10".
        if (item.CommunityRating is float community && community > 0)
        {
            badges.Add(RatingBadge("rating", community.ToString("0.0", CultureInfo.InvariantCulture)));
        }

        if (item.CriticRating is float critic && critic > 0)
        {
            badges.Add(RatingBadge("critic", ((int)Math.Round(critic)).ToString(CultureInfo.InvariantCulture) + "%"));
        }

        return badges;
    }

    private static BadgeInfo RatingBadge(string key, string text) => new()
    {
        Category = BadgeCategory.Rating,
        BadgeKey = key,
        ResourceFileName = string.Empty,
        DisplayText = text,
        IconFileName = $"badge-{key}.svg"
    };

    private void DetectBadgesFromVideo(Video video, List<BadgeInfo> badges)
    {
        DetectBadgesFromVideo(video, badges, includeResolution: true);
    }

    private void DetectHdrAndAudioBadges(Video video, List<BadgeInfo> badges)
    {
        DetectBadgesFromVideo(video, badges, includeResolution: false);
    }

    private void DetectBadgesFromVideo(Video video, List<BadgeInfo> badges, bool includeResolution)
    {
        try
        {
            // GetMediaSources returns one entry per version (merged / alternate versions),
            // so scan all of them instead of only the first.
            var mediaSources = video.GetMediaSources(false) ?? new List<MediaSourceInfo>();

            var merged = MergeVersionBadges(mediaSources, includeResolution);
            var seen = new HashSet<string>(merged.Select(UniqueKey), StringComparer.OrdinalIgnoreCase);

            void AddUnique(BadgeInfo badge)
            {
                if (seen.Add(UniqueKey(badge)))
                {
                    merged.Add(badge);
                }
            }

            // Runtime detection (item-level, only for actual movies/episodes, not series/season aggregates)
            if (includeResolution)
            {
                var runtimeText = FormatRuntime(video.RunTimeTicks);
                if (runtimeText != null)
                {
                    AddUnique(new BadgeInfo
                    {
                        Category = BadgeCategory.Runtime,
                        BadgeKey = "runtime",
                        ResourceFileName = string.Empty,
                        DisplayText = runtimeText
                    });
                }
            }

            // 3D detection (item-level)
            if (video.Video3DFormat.HasValue)
            {
                AddUnique(new BadgeInfo
                {
                    Category = BadgeCategory.ThreeD,
                    BadgeKey = "3d",
                    ResourceFileName = "badge-3d.svg"
                });
            }

            // A subtitle language may have its audio in another version of the item.
            var audioLanguages = merged
                .Where(b => b.Category == BadgeCategory.Language)
                .Select(b => NormalizeLanguage(b.BadgeKey))
                .ToHashSet();
            foreach (var sub in merged.Where(b => b.Category == BadgeCategory.Subtitle))
            {
                sub.HasMatchingAudio |= audioLanguages.Contains(SubtitleLanguage(sub.BadgeKey));
            }

            // Order best-first so the "Highest" display mode keeps the best badge across versions.
            // OrderBy is stable, so languages keep the order of the primary version first.
            badges.AddRange(OrderBestFirst(merged));

            if (mediaSources.Count > 1)
            {
                _logger.LogDebug("Merged badges from {Count} versions of {ItemName}", mediaSources.Count, video.Name);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to detect badges for video: {ItemName}", video.Name);
        }
    }

    private static string UniqueKey(BadgeInfo badge) => $"{badge.Category}:{badge.BadgeKey}";

    /// <summary>
    /// Badges of every version of an item, a badge found in several versions kept once.
    /// </summary>
    internal static List<BadgeInfo> MergeVersionBadges(IEnumerable<MediaSourceInfo> mediaSources, bool includeResolution)
    {
        var merged = new List<BadgeInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mediaSource in mediaSources)
        {
            merged.AddRange(DetectBadgesFromMediaSource(mediaSource, includeResolution).Where(b => seen.Add(UniqueKey(b))));
        }

        return merged;
    }

    /// <summary>
    /// The "Highest" display mode keeps the first badge of a panel, so the order must be best first.
    /// </summary>
    internal static List<BadgeInfo> OrderBestFirst(IEnumerable<BadgeInfo> badges) => badges.OrderBy(GetBadgeRank).ToList();

    private static int GetBadgeRank(BadgeInfo badge)
    {
        return badge.Category switch
        {
            BadgeCategory.Resolution => badge.BadgeKey switch { "4k" => 0, "1080p" => 1, "720p" => 2, "sd" => 3, _ => 4 },
            BadgeCategory.Hdr => badge.BadgeKey switch { "dv" => 0, "dvprofile" => 1, "hdr10plus" => 2, "hlg" => 3, "hdr10" => 4, _ => 5 },
            BadgeCategory.ThreeD => 10,
            BadgeCategory.Audio => badge.BadgeKey switch
            {
                "atmos" => 0, "truehd" => 1, "dtsx" => 2, "dtshdma" => 3,
                "flac" => 4, "dtshd" => 5, "eac3" => 6, "dts" => 7, "ac3" => 8, "aac" => 9, "opus" => 10,
                "7.1" => 11, "5.1" => 12, "stereo" => 13, _ => 14
            },
            _ => 0
        };
    }

    private static List<BadgeInfo> DetectBadgesFromMediaSource(MediaSourceInfo mediaSource, bool includeResolution)
    {
        var badges = new List<BadgeInfo>();
        var streams = mediaSource.MediaStreams;
        if (streams == null || streams.Count == 0)
        {
            return badges;
        }

        var videoStream = streams.FirstOrDefault(s => s.Type == MediaStreamType.Video);
        if (videoStream != null)
        {
            if (includeResolution)
            {
                var quality = DetermineQuality(videoStream.Width ?? 0, videoStream.Height ?? 0);
                if (quality != VideoQuality.Unknown)
                {
                    badges.Add(CreateResolutionBadge(quality));
                }
            }

            // HDR detection - always detect, filtering happens in ShouldShowBadge
            badges.AddRange(DetectHdr(videoStream));

            // Video codec detection
            var codec = videoStream.Codec?.ToLowerInvariant() ?? string.Empty;
            if (codec is "h264" or "avc")
            {
                badges.Add(new BadgeInfo { Category = BadgeCategory.VideoCodec, BadgeKey = "h264", ResourceFileName = "badge-h264.svg" });
            }
            else if (codec is "hevc" or "h265")
            {
                badges.Add(new BadgeInfo { Category = BadgeCategory.VideoCodec, BadgeKey = "hevc", ResourceFileName = "badge-hevc.svg" });
            }
            else if (codec == "av1")
            {
                badges.Add(new BadgeInfo { Category = BadgeCategory.VideoCodec, BadgeKey = "av1", ResourceFileName = "badge-av1.svg" });
            }
            else if (codec == "vp9")
            {
                badges.Add(new BadgeInfo { Category = BadgeCategory.VideoCodec, BadgeKey = "vp9", ResourceFileName = "badge-vp9.svg" });
            }
        }

        // Audio detection: the default track, or the best of all tracks (AudioTrackMode)
        var allAudioStreams = streams.Where(s => s.Type == MediaStreamType.Audio).ToList();
        if (allAudioStreams.Count > 0)
        {
            if (Plugin.Instance?.Configuration.AudioTrackMode == AudioTrackMode.BestTrack)
            {
                badges.AddRange(DetectAudio(allAudioStreams));
            }
            else
            {
                var defaultStream = allAudioStreams.FirstOrDefault(s => s.IsDefault) ?? allAudioStreams[0];
                badges.AddRange(DetectAudio(new List<MediaStream> { defaultStream }));
            }
        }

        // Language detection - always detect all, filtering by mode happens in ShouldShowBadge
        badges.AddRange(DetectLanguages(streams.ToList()));

        return badges;
    }

    /// <summary>
    /// Formats a runtime as "1h 52m" (or "45m" under an hour). Returns null when unknown or under a minute.
    /// </summary>
    internal static string? FormatRuntime(long? runTimeTicks)
    {
        if (runTimeTicks is not > 0) return null;

        var totalMinutes = (int)Math.Round(TimeSpan.FromTicks(runTimeTicks.Value).TotalMinutes);
        if (totalMinutes <= 0) return null;

        var hours = totalMinutes / 60;
        var minutes = totalMinutes % 60;
        return hours > 0 ? $"{hours}h {minutes:00}m" : $"{minutes}m";
    }

    private static readonly Dictionary<string, string> LangCodeToFlag = new(StringComparer.OrdinalIgnoreCase)
    {
        { "fre", "fra" }, { "ger", "deu" }, { "dut", "nld" }, { "cze", "ces" }, { "rum", "ron" }, { "chi", "zho" },
        { "gre", "ell" }, { "may", "msa" }, { "tgl", "fil" }, { "slo", "slk" }, { "baq", "eus" }, { "wel", "cym" }
    };

    // Only include language codes that have a matching flag-{code}.svg asset
    internal static readonly HashSet<string> KnownFlagCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "fra", "eng", "jpn", "deu", "spa", "ita", "por", "kor", "zho", "rus",
        "nld", "ara", "hin", "tha", "pol", "tur", "swe", "dan", "nor", "fin",
        "ces", "hun", "ron", "ukr", "vie", "heb", "can",
        "bul", "cat", "cym", "ell", "est", "eus", "fil", "glg", "hrv", "ind",
        "lav", "lit", "msa", "slk", "srp"
    };

    private static readonly Dictionary<string, string> FrenchVariantFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        { "vff", "fra" },
        { "vfi", "fra" },
        { "vq", "can" }
    };

    /// <summary>
    /// Brings a language code to the form flags use: "fre" -> "fra", French variants -> "fra".
    /// </summary>
    internal static string NormalizeLanguage(string code)
    {
        var lower = code.Trim().ToLowerInvariant();
        if (FrenchVariantFlags.ContainsKey(lower))
        {
            return "fra";
        }

        return LangCodeToFlag.TryGetValue(lower, out var mapped) ? mapped : lower;
    }

    /// <summary>
    /// Language of a subtitle badge: "subfra" -> "fra".
    /// </summary>
    internal static string SubtitleLanguage(string badgeKey) => badgeKey["sub".Length..];

    private static string GetFlagResourceFileName(string langCode)
    {
        if (FrenchVariantFlags.TryGetValue(langCode, out var variantFlag))
        {
            return KnownFlagCodes.Contains(variantFlag) ? $"flag-{variantFlag.ToLowerInvariant()}.svg" : string.Empty;
        }
        var normalized = LangCodeToFlag.TryGetValue(langCode, out var mapped) ? mapped : langCode;
        return KnownFlagCodes.Contains(normalized) ? $"flag-{normalized.ToLowerInvariant()}.svg" : string.Empty;
    }

    /// <summary>
    /// Detects all language and subtitle badges. Always detects all languages;
    /// filtering by mode (DefaultOnly/All) is done in ShouldShowBadge.
    /// </summary>
    internal static List<BadgeInfo> DetectLanguages(List<MediaStream> allStreams)
    {
        var badges = new List<BadgeInfo>();
        var audioStreams = allStreams.Where(s => s.Type == MediaStreamType.Audio).ToList();
        if (audioStreams.Count == 0) return badges;

        var addedLanguages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Default track first: the "Highest" mode shows only the first flag. The others
        // alphabetically, so the flags do not depend on the track order in the file.
        var primary = audioStreams.FirstOrDefault(s => s.IsDefault) ?? audioStreams[0];
        var ordered = audioStreams
            .OrderBy(s => s == primary ? 0 : 1)
            .ThenBy(s => s.Language, StringComparer.OrdinalIgnoreCase);

        foreach (var stream in ordered)
        {
            var lang = stream.Language;
            if (string.IsNullOrEmpty(lang)) continue;

            var langLower = lang.ToLowerInvariant();
            var displayTitle = stream.DisplayTitle?.ToUpperInvariant() ?? "";

            // Detect French variants
            string? variant = null;
            if (displayTitle.Contains("VFQ"))
                variant = "vq";
            else if (displayTitle.Contains("VFF") || displayTitle.Contains("VFR") || displayTitle.Contains("VFI"))
                variant = "vff";

            var badgeKey = variant != null && (langLower == "fre" || langLower == "fra" || langLower == "fr")
                ? variant
                : langLower;

            if (addedLanguages.Add(badgeKey))
            {
                badges.Add(new BadgeInfo
                {
                    Category = BadgeCategory.Language,
                    BadgeKey = badgeKey,
                    ResourceFileName = GetFlagResourceFileName(badgeKey)
                });
            }
        }

        // Every subtitle language, alphabetically; the subtitle panel decides which to show.
        var audioLanguages = audioStreams
            .Where(s => !string.IsNullOrEmpty(s.Language))
            .Select(s => NormalizeLanguage(s.Language!))
            .ToHashSet();

        var subtitleLanguages = allStreams
            .Where(s => s.Type == MediaStreamType.Subtitle && !string.IsNullOrEmpty(s.Language))
            .Select(s => NormalizeLanguage(s.Language!))
            .Distinct()
            .Order(StringComparer.Ordinal);
        foreach (var subLang in subtitleLanguages)
        {
            badges.Add(new BadgeInfo
            {
                Category = BadgeCategory.Subtitle,
                BadgeKey = "sub" + subLang,
                ResourceFileName = string.Empty,
                HasMatchingAudio = audioLanguages.Contains(subLang)
            });
        }

        return badges;
    }

    /// <summary>
    /// Formats a Dolby Vision profile as "DV P5", "DV P7" or "DV P8.1". Profiles 8 and 10 come in
    /// variants named after the base layer compatibility ID (1 = HDR10, 2 = SDR, 4 = HLG).
    /// FEL and MEL (profile 7) are not exposed by Jellyfin.
    /// </summary>
    internal static readonly string[] KnownDvProfiles = ["5", "7", "8.1", "8.2", "8.4", "10.0", "10.1", "10.2", "10.4"];

    /// <summary>
    /// "DV P8.1" -> "badge-dvp8_1.svg". A profile without its SVG is drawn as text.
    /// </summary>
    internal static string DvProfileFileName(string profileText) =>
        $"badge-dvp{profileText["DV P".Length..].Replace('.', '_')}.svg";

    internal static string? FormatDvProfile(int? profile, int? compatibilityId)
    {
        if (profile is not > 0)
        {
            return null;
        }

        return profile is 8 or 10 && compatibilityId is not null
            ? $"DV P{profile}.{compatibilityId}"
            : $"DV P{profile}";
    }

    /// <summary>
    /// Dolby Vision is a layer on top of a base format (HDR10, HDR10+, HLG): both are returned,
    /// the panel's "Highest" mode then keeps only Dolby Vision.
    /// </summary>
    internal static List<BadgeInfo> DetectHdr(MediaStream videoStream)
    {
        var badges = new List<BadgeInfo>();
        var rangeType = videoStream.VideoRangeType;

        if (rangeType is VideoRangeType.DOVI
            or VideoRangeType.DOVIWithHDR10
            or VideoRangeType.DOVIWithHLG
            or VideoRangeType.DOVIWithSDR
            or VideoRangeType.DOVIWithEL
            or VideoRangeType.DOVIWithHDR10Plus
            or VideoRangeType.DOVIWithELHDR10Plus)
        {
            badges.Add(new BadgeInfo { Category = BadgeCategory.Hdr, BadgeKey = "dv", ResourceFileName = "badge-dv.svg" });

            // Optional badge (off by default): the profile, e.g. "DV P8.1" from badge-dvp8_1.svg.
            var profile = FormatDvProfile(videoStream.DvProfile, videoStream.DvBlSignalCompatibilityId);
            if (profile != null)
            {
                badges.Add(new BadgeInfo
                {
                    Category = BadgeCategory.Hdr,
                    BadgeKey = "dvprofile",
                    ResourceFileName = DvProfileFileName(profile),
                    DisplayText = profile
                });
            }
        }

        if (rangeType is VideoRangeType.HDR10Plus or VideoRangeType.DOVIWithHDR10Plus or VideoRangeType.DOVIWithELHDR10Plus)
        {
            badges.Add(new BadgeInfo { Category = BadgeCategory.Hdr, BadgeKey = "hdr10plus", ResourceFileName = "badge-hdr10plus.svg" });
        }
        else if (rangeType is VideoRangeType.HLG or VideoRangeType.DOVIWithHLG)
        {
            badges.Add(new BadgeInfo { Category = BadgeCategory.Hdr, BadgeKey = "hlg", ResourceFileName = "badge-hlg.svg" });
        }
        else if (rangeType is VideoRangeType.HDR10 or VideoRangeType.DOVIWithHDR10 or VideoRangeType.DOVIWithEL)
        {
            badges.Add(new BadgeInfo { Category = BadgeCategory.Hdr, BadgeKey = "hdr10", ResourceFileName = "badge-hdr10.svg" });
        }
        else if (badges.Count == 0 && videoStream.VideoRange == VideoRange.HDR)
        {
            badges.Add(new BadgeInfo { Category = BadgeCategory.Hdr, BadgeKey = "hdr", ResourceFileName = "badge-hdr.svg" });
        }

        return badges;
    }

    /// <summary>
    /// Atmos and DTS:X are extensions carried by a base codec (TrueHD, DTS-HD MA): each format
    /// found is returned, the panel's "Highest" mode then keeps only the best one.
    /// </summary>
    internal static List<BadgeInfo> DetectAudio(IEnumerable<MediaStream> audioStreams)
    {
        var badges = new List<BadgeInfo>();
        var formats = new HashSet<string>();
        int bestChannels = 0;

        foreach (var stream in audioStreams)
        {
            var codec = stream.Codec?.ToUpperInvariant() ?? string.Empty;
            var profile = stream.Profile?.ToUpperInvariant() ?? string.Empty;
            var channels = stream.Channels ?? 0;

            if (channels > bestChannels) bestChannels = channels;

            if (profile.Contains("ATMOS")) formats.Add("atmos");
            if (codec == "TRUEHD") formats.Add("truehd");
            if (profile.Contains("DTS:X") || profile.Contains("DTS-X") || profile.Contains("DTSX")) formats.Add("dtsx");
            if (profile.Contains("DTS-HD MA") || profile.Contains("DTS-HD MASTER") || (codec == "DTS" && profile.Contains("MA")))
                formats.Add("dtshdma");
            else if (codec == "DTS" && profile.Contains("DTS-HD")) formats.Add("dtshd");
            else if (codec == "DTS") formats.Add("dts");

            if (codec == "FLAC") formats.Add("flac");
            if (codec == "EAC3") formats.Add("eac3");
            if (codec == "AC3") formats.Add("ac3");
            if (codec == "AAC") formats.Add("aac");
            if (codec == "OPUS") formats.Add("opus");
        }

        // A carrier codec is not listed on top of the format it carries (E-AC-3 with Atmos, DTS core under DTS:X).
        if (formats.Contains("atmos")) formats.Remove("eac3");
        if (formats.Contains("dtsx")) { formats.Remove("dts"); formats.Remove("dtshd"); }

        foreach (var key in new[] { "atmos", "truehd", "dtsx", "dtshdma", "flac", "dtshd", "eac3", "dts", "ac3", "aac", "opus" }.Where(formats.Contains))
        {
            badges.Add(new BadgeInfo { Category = BadgeCategory.Audio, BadgeKey = key, ResourceFileName = $"badge-{key}.svg" });
        }

        if (bestChannels >= 8)
            badges.Add(new BadgeInfo { Category = BadgeCategory.Audio, BadgeKey = "7.1", ResourceFileName = "badge-7_1.svg" });
        else if (bestChannels >= 6)
            badges.Add(new BadgeInfo { Category = BadgeCategory.Audio, BadgeKey = "5.1", ResourceFileName = "badge-5_1.svg" });
        else if (bestChannels >= 2)
            badges.Add(new BadgeInfo { Category = BadgeCategory.Audio, BadgeKey = "stereo", ResourceFileName = "badge-stereo.svg" });

        return badges;
    }

    private static BadgeInfo CreateResolutionBadge(VideoQuality quality)
    {
        return quality switch
        {
            VideoQuality.UHD4K => new BadgeInfo { Category = BadgeCategory.Resolution, BadgeKey = "4k", ResourceFileName = "badge-4k.svg" },
            VideoQuality.FHD1080p => new BadgeInfo { Category = BadgeCategory.Resolution, BadgeKey = "1080p", ResourceFileName = "badge-1080p.svg" },
            VideoQuality.HD720p => new BadgeInfo { Category = BadgeCategory.Resolution, BadgeKey = "720p", ResourceFileName = "badge-720p.svg" },
            VideoQuality.SD => new BadgeInfo { Category = BadgeCategory.Resolution, BadgeKey = "sd", ResourceFileName = "badge-sd.svg" },
            _ => new BadgeInfo { Category = BadgeCategory.Resolution, BadgeKey = "unknown", ResourceFileName = string.Empty }
        };
    }

    private VideoQuality GetQualityFromVideo(Video video)
    {
        try
        {
            // Best quality across all versions of the item
            var best = VideoQuality.Unknown;
            foreach (var mediaSource in video.GetMediaSources(false) ?? new List<MediaSourceInfo>())
            {
                var videoStream = mediaSource.MediaStreams?.FirstOrDefault(s => s.Type == MediaStreamType.Video);
                if (videoStream == null) continue;

                var q = DetermineQuality(videoStream.Width ?? 0, videoStream.Height ?? 0);
                if (q > best) best = q;
            }

            return best;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get media sources for video item: {ItemName}", video.Name);
            return VideoQuality.Unknown;
        }
    }
}
