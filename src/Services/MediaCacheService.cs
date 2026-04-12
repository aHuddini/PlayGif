using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using HtmlAgilityPack;
using Playnite.SDK;
using PlayGif.Common;

namespace PlayGif.Services
{
    public class MediaCacheService
    {
        private static readonly ILogger Logger = LogManager.GetLogger();
        private static readonly HttpClient HttpClient = new HttpClient();

        private readonly PlayGifSettings _settings;
        private readonly string _cacheBasePath;

        public MediaCacheService(PlayGifSettings settings, string cacheBasePath)
        {
            _settings = settings;
            _cacheBasePath = Path.Combine(cacheBasePath, Constants.GamesCacheFolder);
            Directory.CreateDirectory(_cacheBasePath);
        }

        // Rewrites remote media URLs to local cache paths, queues downloads for uncached items
        public string RewriteDescriptionHtml(string html, Guid gameId)
        {
            if (string.IsNullOrEmpty(html)) return html;
            if (!_settings.AutoCacheMedia) return html;

            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var gameDir = GetGameCacheDir(gameId);
            var mediaUrls = new List<(HtmlNode node, string attrName, string url)>();

            // Find img sources
            foreach (var img in doc.DocumentNode.SelectNodes("//img[@src]") ?? Enumerable.Empty<HtmlNode>())
            {
                var src = img.GetAttributeValue("src", "");
                if (IsRemoteUrl(src))
                    mediaUrls.Add((img, "src", src));
            }

            // Find video/source sources
            foreach (var source in doc.DocumentNode.SelectNodes("//source[@src]") ?? Enumerable.Empty<HtmlNode>())
            {
                var src = source.GetAttributeValue("src", "");
                if (IsRemoteUrl(src))
                    mediaUrls.Add((source, "src", src));
            }

            // Find video poster attributes
            foreach (var video in doc.DocumentNode.SelectNodes("//video[@poster]") ?? Enumerable.Empty<HtmlNode>())
            {
                var poster = video.GetAttributeValue("poster", "");
                if (IsRemoteUrl(poster))
                    mediaUrls.Add((video, "poster", poster));
            }

            // Rewrite cached URLs, queue downloads for uncached
            var toDownload = new List<(string url, string localPath)>();

            foreach (var (node, attrName, url) in mediaUrls)
            {
                var filename = GetCacheFilename(url);
                var localPath = Path.Combine(gameDir, filename);

                if (File.Exists(localPath))
                {
                    var virtualUrl = $"https://{Constants.VirtualHostName}/{gameId}/{filename}";
                    node.SetAttributeValue(attrName, virtualUrl);
                }
                else
                {
                    toDownload.Add((url, localPath));
                }
            }

            // Fire and forget background downloads
            if (toDownload.Count > 0)
            {
                _ = DownloadAllAsync(toDownload, gameId);
            }

            return doc.DocumentNode.OuterHtml;
        }

        private async Task DownloadAllAsync(List<(string url, string localPath)> items, Guid gameId)
        {
            var gameDir = GetGameCacheDir(gameId);
            Directory.CreateDirectory(gameDir);

            foreach (var (url, localPath) in items)
            {
                try
                {
                    if (GetGameCacheSize(gameId) > _settings.MaxCachePerGameMB * 1024L * 1024L)
                    {
                        Logger.Info($"Cache limit reached for game {gameId}, skipping remaining downloads.");
                        break;
                    }

                    var response = await HttpClient.GetAsync(url);
                    response.EnsureSuccessStatusCode();
                    var bytes = await response.Content.ReadAsByteArrayAsync();
                    File.WriteAllBytes(localPath, bytes);
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, $"Failed to cache media: {url}");
                }
            }
        }

        public void ClearGameCache(Guid gameId)
        {
            var gameDir = GetGameCacheDir(gameId);
            if (Directory.Exists(gameDir))
            {
                Directory.Delete(gameDir, true);
            }
        }

        public long GetGameCacheSize(Guid gameId)
        {
            var gameDir = GetGameCacheDir(gameId);
            if (!Directory.Exists(gameDir)) return 0;
            return new DirectoryInfo(gameDir)
                .GetFiles("*", SearchOption.AllDirectories)
                .Sum(f => f.Length);
        }

        private string GetGameCacheDir(Guid gameId)
        {
            return Path.Combine(_cacheBasePath, gameId.ToString());
        }

        private static string GetCacheFilename(string url)
        {
            // Strip query parameters for the extension
            var uri = new Uri(url);
            var path = uri.AbsolutePath;
            var ext = Path.GetExtension(path);
            if (string.IsNullOrEmpty(ext)) ext = ".bin";

            // Hash the full URL for uniqueness
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(url));
                var hex = BitConverter.ToString(hash).Replace("-", "").Substring(0, 12).ToLowerInvariant();
                return hex + ext;
            }
        }

        private static bool IsRemoteUrl(string url)
        {
            return !string.IsNullOrEmpty(url)
                && (url.StartsWith("http://") || url.StartsWith("https://"))
                && !url.Contains(Constants.VirtualHostName);
        }
    }
}
