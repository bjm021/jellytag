using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.JellyTag.Configuration;
using Jellyfin.Plugin.JellyTag.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.JellyTag.Controllers;

/// <summary>
/// Controller for JellyTag plugin admin and debug endpoints.
/// </summary>
[ApiController]
[Route("JellyTag")]
public partial class JellyTagController : ControllerBase
{
    private readonly IImageCacheService _cacheService;
    private readonly IImageOverlayService _overlayService;
    private readonly IQualityDetectionService _qualityService;
    private readonly ILibraryManager _libraryManager;

    private static readonly string[] SupportedBadgeExtensions = { ".svg", ".png", ".jpg", ".jpeg" };

    [GeneratedRegex(@"^[a-zA-Z0-9._-]+$")]
    private static partial Regex SafeBadgeKeyRegex();

    /// <summary>
    /// File name without extension of a badge, as the overlay service loads it:
    /// flags keep their key ("flag-eng"), other badges get a prefix ("5.1" -> "badge-5_1").
    /// </summary>
    private static string BadgeFileStem(string badgeKey) =>
        badgeKey.StartsWith("flag-", StringComparison.OrdinalIgnoreCase)
            ? badgeKey.ToLowerInvariant()
            : "badge-" + badgeKey.Replace('.', '_');

    /// <summary>
    /// Initializes a new instance of the <see cref="JellyTagController"/> class.
    /// </summary>
    public JellyTagController(IImageCacheService cacheService, IImageOverlayService overlayService, IQualityDetectionService qualityService, ILibraryManager libraryManager)
    {
        _cacheService = cacheService;
        _overlayService = overlayService;
        _qualityService = qualityService;
        _libraryManager = libraryManager;
    }

    /// <summary>
    /// Clears the image cache.
    /// </summary>
    [HttpPost("ClearCache")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public IActionResult ClearCache()
    {
        _cacheService.ClearCache();
        _qualityService.ClearBadgeCache();
        return NoContent();
    }

    /// <summary>
    /// Gets cache statistics.
    /// </summary>
    [HttpGet("CacheStats")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetCacheStats()
    {
        var stats = _cacheService.GetCacheStats();
        return Ok(new
        {
            FileCount = stats.FileCount,
            TotalSizeMB = Math.Round(stats.TotalSizeBytes / (1024.0 * 1024.0), 2),
            OldestEntry = stats.OldestEntry,
            NewestEntry = stats.NewestEntry
        });
    }

    /// <summary>
    /// Gets the plugin status.
    /// </summary>
    [HttpGet("Status")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetStatus()
    {
        var config = Plugin.Instance?.Configuration;
        return Ok(new
        {
            Enabled = config?.Enabled ?? false,
            PosterEnabled = config?.PosterConfig?.Enabled ?? false,
            ThumbnailEnabled = config?.ThumbnailConfig?.Enabled ?? false,
            ThumbnailSameAsPoster = config?.ThumbnailSameAsPoster ?? false,
            OutputFormat = config?.OutputFormat.ToString() ?? "Jpeg"
        });
    }

    /// <summary>
    /// Debug endpoint to list embedded resources.
    /// </summary>
    [HttpGet("Debug/Resources")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetResources()
    {
        var assembly = System.Reflection.Assembly.GetExecutingAssembly();
        var resources = assembly.GetManifestResourceNames();
        return Ok(new
        {
            AssemblyName = assembly.FullName,
            Resources = resources
        });
    }

    /// <summary>
    /// Debug endpoint to get a raw badge image.
    /// </summary>
    [HttpGet("Debug/Badge/{quality}")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetBadge(string quality)
    {
        var assembly = System.Reflection.Assembly.GetExecutingAssembly();
        var resourceNames = assembly.GetManifestResourceNames();

        var fileName = quality.ToLower() switch
        {
            "4k" => "badge-4k.svg",
            "1080p" => "badge-1080p.svg",
            "720p" => "badge-720p.svg",
            "sd" => "badge-sd.svg",
            "hdr10" => "badge-hdr10.svg",
            "hdr10plus" => "badge-hdr10plus.svg",
            "dv" => "badge-dv.svg",
            "hlg" => "badge-hlg.svg",
            "atmos" => "badge-atmos.svg",
            "dtsx" => "badge-dtsx.svg",
            "truehd" => "badge-truehd.svg",
            "dtshdma" => "badge-dtshdma.svg",
            "flac" => "badge-flac.svg",
            "dtshd" => "badge-dtshd.svg",
            "dts" => "badge-dts.svg",
            "eac3" => "badge-eac3.svg",
            "ac3" => "badge-ac3.svg",
            "aac" => "badge-aac.svg",
            "opus" => "badge-opus.svg",
            "5.1" => "badge-5_1.svg",
            "7.1" => "badge-7_1.svg",
            "stereo" => "badge-stereo.svg",
            _ => null
        };

        if (fileName == null)
            return NotFound("Invalid quality");

        var resourceName = resourceNames.FirstOrDefault(r => r.EndsWith(fileName, StringComparison.OrdinalIgnoreCase));
        if (resourceName == null)
            return NotFound($"Resource not found: {fileName}");

        var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
            return NotFound("Stream is null");

        var contentType = fileName.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) ? "image/svg+xml" : "image/png";
        return File(stream, contentType);
    }

    /// <summary>
    /// Uploads a custom badge to override the default badge for a given key.
    /// Accepts PNG, JPEG, and SVG files.
    /// </summary>
    [HttpPost("CustomBadge/{badgeKey}")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UploadCustomBadge(string badgeKey, IFormFile file)
    {
        if (!SafeBadgeKeyRegex().IsMatch(badgeKey))
        {
            return BadRequest("Invalid badge key");
        }

        if (file == null || file.Length == 0)
        {
            return BadRequest("No file uploaded");
        }

        const long maxFileSize = 5 * 1024 * 1024; // 5 MB
        if (file.Length > maxFileSize)
        {
            return BadRequest("File too large. Maximum size is 5 MB.");
        }

        var extension = file.ContentType.ToLowerInvariant() switch
        {
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "image/svg+xml" => ".svg",
            _ => null
        };

        if (extension == null)
        {
            return BadRequest("Only PNG, JPEG, and SVG files are accepted");
        }

        var dataFolder = Plugin.Instance?.BadgeFolderPath;
        if (string.IsNullOrEmpty(dataFolder))
        {
            return BadRequest("Plugin data folder not available");
        }

        var customDir = Path.Combine(dataFolder, "custom-badges");
        Directory.CreateDirectory(customDir);

        // Delete existing custom badges for this key (all extensions)
        var stem = BadgeFileStem(badgeKey);
        foreach (var ext in SupportedBadgeExtensions)
        {
            var existing = Path.Combine(customDir, stem + ext);
            if (System.IO.File.Exists(existing))
            {
                System.IO.File.Delete(existing);
            }
        }

        var fileName = stem + extension;
        var filePath = Path.Combine(customDir, fileName);

        using (var stream = new FileStream(filePath, FileMode.Create))
        {
            await file.CopyToAsync(stream).ConfigureAwait(false);
        }

        // Reload badges and clear cache
        _overlayService.ReloadBadges();
        _cacheService.ClearCache();

        return NoContent();
    }

    /// <summary>
    /// Deletes a custom badge override, reverting to the default embedded badge.
    /// </summary>
    [HttpDelete("CustomBadge/{badgeKey}")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult DeleteCustomBadge(string badgeKey)
    {
        if (!SafeBadgeKeyRegex().IsMatch(badgeKey))
        {
            return BadRequest("Invalid badge key");
        }

        var dataFolder = Plugin.Instance?.BadgeFolderPath;
        if (string.IsNullOrEmpty(dataFolder))
        {
            return NotFound();
        }

        var stem = BadgeFileStem(badgeKey);
        var customDir = Path.Combine(dataFolder, "custom-badges");
        var found = false;

        foreach (var ext in SupportedBadgeExtensions)
        {
            var filePath = Path.Combine(customDir, stem + ext);
            if (System.IO.File.Exists(filePath))
            {
                System.IO.File.Delete(filePath);
                found = true;
            }
        }

        if (!found)
        {
            return NotFound("Custom badge not found");
        }

        _overlayService.ReloadBadges();
        _cacheService.ClearCache();

        return NoContent();
    }

    /// <summary>
    /// Lists all custom badge overrides, as file stems ("badge-5_1", "flag-eng").
    /// </summary>
    [HttpGet("CustomBadges")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetCustomBadges()
    {
        var dataFolder = Plugin.Instance?.BadgeFolderPath;
        if (string.IsNullOrEmpty(dataFolder))
        {
            return Ok(Array.Empty<string>());
        }

        var customDir = Path.Combine(dataFolder, "custom-badges");
        if (!Directory.Exists(customDir))
        {
            return Ok(Array.Empty<string>());
        }

        var files = Directory.GetFiles(customDir)
            .Where(f => SupportedBadgeExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .Select(Path.GetFileNameWithoutExtension)
            .Where(stem => stem!.StartsWith("badge-", StringComparison.Ordinal) || stem.StartsWith("flag-", StringComparison.Ordinal))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return Ok(files);
    }

    /// <summary>
    /// Lists the language codes that have a flag, for the custom badge gallery.
    /// </summary>
    [HttpGet("Flags")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetFlags()
    {
        return Ok(QualityDetectionService.KnownFlagCodes.Order(StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// Serves a badge preview image. Returns custom badge if present, otherwise embedded default.
    /// Order: custom (svg > png > jpg) → embedded (svg > png).
    /// </summary>
    [HttpGet("BadgePreview/{badgeKey}")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetBadgePreview(string badgeKey)
    {
        if (!SafeBadgeKeyRegex().IsMatch(badgeKey))
        {
            return BadRequest("Invalid badge key");
        }

        return ServeBadgeFile(BadgeFileStem(badgeKey));
    }

    /// <summary>
    /// Gets an embedded or custom badge image by its file name stem (e.g. "badge-dtshd", "flag-eng").
    /// </summary>
    [HttpGet("BadgeImage/{name}")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetBadgeImage(string name)
    {
        if (!SafeBadgeKeyRegex().IsMatch(name)
            || !(name.StartsWith("badge-", StringComparison.OrdinalIgnoreCase) || name.StartsWith("flag-", StringComparison.OrdinalIgnoreCase)))
        {
            return BadRequest("Invalid badge name");
        }

        return ServeBadgeFile(name.ToLowerInvariant());
    }

    /// <summary>
    /// Gets the badges to show on the detail page of an item.
    /// </summary>
    [HttpGet("ItemBadges/{itemId}")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetItemBadges(Guid itemId)
    {
        var config = Plugin.Instance?.Configuration;
        var empty = new { Height = 0, Badges = Array.Empty<object>() };
        if (config == null || !config.Enabled || !config.ShowOnDetailPage)
        {
            return Ok(empty);
        }

        var item = _libraryManager.GetItemById(itemId);
        if (item is not (Movie or Series or Season or Episode or Video))
        {
            return Ok(empty);
        }

        var collectionFolders = _libraryManager.GetCollectionFolders(item);
        if (collectionFolders.Count == 0
            || collectionFolders.Any(f => config.ExcludedLibraryIds.Contains(f.Id.ToString("N"))))
        {
            return Ok(empty);
        }

        var badges = _overlayService
            .SelectBadgesForDisplay(_qualityService.DetectAllBadges(item), config.PosterConfig)
            .Where(b => b.Category != BadgeCategory.Subtitle && !string.IsNullOrEmpty(b.ResourceFileName))
            .Select(b => new { Key = b.BadgeKey, Image = Path.GetFileNameWithoutExtension(b.ResourceFileName) })
            .ToArray();

        return Ok(new { Height = Math.Clamp(config.DetailPageBadgeHeight, 12, 80), Badges = badges });
    }

    /// <summary>
    /// Serves the script that renders badges on item detail pages.
    /// </summary>
    [HttpGet("detail.js")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetDetailScript()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(r => r.EndsWith(".Web.detail.js", StringComparison.Ordinal));
        var stream = resourceName == null ? null : assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
        {
            return NotFound();
        }

        Response.Headers.CacheControl = "no-cache";
        return File(stream, "application/javascript");
    }

    private IActionResult ServeBadgeFile(string stem)
    {
        // Check custom badges first: SVG > PNG > JPG > JPEG
        var dataFolder = Plugin.Instance?.BadgeFolderPath;
        if (!string.IsNullOrEmpty(dataFolder))
        {
            var customDir = Path.Combine(dataFolder, "custom-badges");
            foreach (var ext in SupportedBadgeExtensions)
            {
                var customPath = Path.Combine(customDir, stem + ext);
                if (System.IO.File.Exists(customPath))
                {
                    var ct = ext switch
                    {
                        ".svg" => "image/svg+xml",
                        ".png" => "image/png",
                        _ => "image/jpeg"
                    };
                    return PhysicalFile(customPath, ct);
                }
            }
        }

        // Fall back to embedded resources: SVG > PNG
        var assembly = Assembly.GetExecutingAssembly();
        var resourceNames = assembly.GetManifestResourceNames();

        // Try SVG first
        var svgResourceName = resourceNames
            .FirstOrDefault(r => r.EndsWith($".{stem}.svg", StringComparison.OrdinalIgnoreCase));
        if (svgResourceName != null)
        {
            var stream = assembly.GetManifestResourceStream(svgResourceName);
            if (stream != null)
            {
                return File(stream, "image/svg+xml");
            }
        }

        // Then PNG
        var pngResourceName = resourceNames
            .FirstOrDefault(r => r.EndsWith($".{stem}.png", StringComparison.OrdinalIgnoreCase));
        if (pngResourceName != null)
        {
            var stream = assembly.GetManifestResourceStream(pngResourceName);
            if (stream != null)
            {
                return File(stream, "image/png");
            }
        }

        return NotFound();
    }

    /// <summary>
    /// Exports the plugin configuration as a JSON file.
    /// </summary>
    [HttpGet("ExportConfig")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult ExportConfig()
    {
        var plugin = Plugin.Instance;
        if (plugin == null)
        {
            return BadRequest("Plugin not loaded");
        }

        var json = JsonSerializer.SerializeToUtf8Bytes(plugin.Configuration, new JsonSerializerOptions { WriteIndented = true });
        return File(json, "application/json", "jellytag-config.json");
    }

    /// <summary>
    /// Imports a plugin configuration from a JSON file.
    /// </summary>
    [HttpPost("ImportConfig")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ImportConfig(IFormFile file)
    {
        var plugin = Plugin.Instance;
        if (plugin == null)
        {
            return BadRequest("Plugin not loaded");
        }

        if (file == null || file.Length == 0)
        {
            return BadRequest("No file uploaded");
        }

        const long maxFileSize = 1 * 1024 * 1024; // 1 MB
        if (file.Length > maxFileSize)
        {
            return BadRequest("File too large. Maximum size is 1 MB.");
        }

        try
        {
            using var stream = file.OpenReadStream();
            var imported = await JsonSerializer.DeserializeAsync<PluginConfiguration>(stream).ConfigureAwait(false);
            if (imported == null)
            {
                return BadRequest("Invalid configuration file");
            }

            imported.EnsureSubtitlePanels();
            plugin.UpdateConfiguration(imported);
            _cacheService.ClearCache();
            _qualityService.ClearBadgeCache();
            _overlayService.ReloadBadges();

            return NoContent();
        }
        catch (JsonException)
        {
            return BadRequest("Invalid JSON format");
        }
    }

    /// <summary>
    /// Resets all plugin configuration to defaults.
    /// </summary>
    [HttpPost("ResetConfig")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public IActionResult ResetConfig()
    {
        var plugin = Plugin.Instance;
        if (plugin == null)
        {
            return BadRequest("Plugin not loaded");
        }

        plugin.UpdateConfiguration(new Configuration.PluginConfiguration());
        _cacheService.ClearCache();
        _qualityService.ClearBadgeCache();
        _overlayService.ReloadBadges();

        return NoContent();
    }
}
