using System;
using System.Collections.Generic;
using System.Diagnostics;
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

            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var gameDir = GetGameCacheDir(gameId);
            var toDownload = new List<(string url, string localPath)>();
            var staleWebm = new List<(string webmPath, string keptPath)>();

            // Rewrites one attribute to its cached local URL, or queues a download
            void Map(HtmlNode node, string attrName, string url)
            {
                var filename = GetCacheFilename(url);
                var localPath = Path.Combine(gameDir, filename);

                // An ffmpeg-converted orphan keeps the same hash, only the extension changes
                var mp4Path = filename.EndsWith(".webm", StringComparison.OrdinalIgnoreCase)
                    ? Path.ChangeExtension(localPath, ".mp4") : null;

                if (mp4Path != null && File.Exists(mp4Path))
                {
                    node.SetAttributeValue(attrName, VirtualUrl(gameId, Path.GetFileName(mp4Path)));
                    if (node.Name == "source")
                        node.SetAttributeValue("type", "video/mp4");
                }
                else if (File.Exists(localPath))
                {
                    node.SetAttributeValue(attrName, VirtualUrl(gameId, filename));
                }
                else
                {
                    toDownload.Add((url, localPath));
                }
            }

            // Pass 1: collapse each <video> to a single best <source>.
            // Steam ships VP9 WebM first and H.264 MP4 second; keeping both means the
            // browser picks the WebM, so the MP4 gets cached and never played.
            foreach (var video in doc.DocumentNode.SelectNodes("//video") ?? Enumerable.Empty<HtmlNode>())
            {
                var poster = video.GetAttributeValue("poster", "");
                if (IsRemoteUrl(poster))
                    Map(video, "poster", poster);

                var sources = video.SelectNodes(".//source[@src]");
                if (sources == null || sources.Count == 0) continue;

                var best = PickBestSource(sources);
                var bestUrl = best.GetAttributeValue("src", "");

                // The replacement must already be on disk before we drop the old file
                var keptPath = IsRemoteUrl(bestUrl)
                    ? Path.Combine(gameDir, GetCacheFilename(bestUrl)) : null;

                // Never prune a lone source — it may already be a local playgif.local URL
                if (sources.Count > 1)
                {
                    foreach (var s in sources.Where(s => s != best).ToList())
                    {
                        // The dropped sibling may already be cached from an older version
                        var droppedUrl = s.GetAttributeValue("src", "");
                        if (keptPath != null && IsRemoteUrl(droppedUrl) &&
                            StripQuery(droppedUrl).EndsWith(".webm", StringComparison.OrdinalIgnoreCase))
                            staleWebm.Add((Path.Combine(gameDir, GetCacheFilename(droppedUrl)), keptPath));

                        s.Remove();
                    }
                }

                var src = bestUrl;
                if (IsRemoteUrl(src))
                {
                    Map(best, "src", src);
                    if (best.GetAttributeValue("type", "").Length > 0)
                        best.SetAttributeValue("type",
                            StripQuery(src).EndsWith(".webm", StringComparison.OrdinalIgnoreCase)
                                ? "video/webm" : "video/mp4");
                }
            }

            // Pass 2: standalone media not inside a <video>
            foreach (var img in doc.DocumentNode.SelectNodes("//img[@src]") ?? Enumerable.Empty<HtmlNode>())
            {
                var src = img.GetAttributeValue("src", "");
                if (IsRemoteUrl(src))
                    Map(img, "src", src);
            }

            foreach (var source in doc.DocumentNode.SelectNodes("//source[@src]") ?? Enumerable.Empty<HtmlNode>())
            {
                if (source.ParentNode?.Name == "video") continue;
                var src = source.GetAttributeValue("src", "");
                if (IsRemoteUrl(src))
                    Map(source, "src", src);
            }

            ReclaimStaleWebm(staleWebm, gameId);

            // Auto-download uncached files only if setting is enabled
            if (toDownload.Count > 0 && _settings.AutoCacheMedia)
            {
                _ = DownloadAllAsync(toDownload, gameId);
            }

            return doc.DocumentNode.OuterHtml;
        }

        // Prefers MP4 over WebM — H.264 is hardware-decoded far more widely than VP9
        private static HtmlNode PickBestSource(IEnumerable<HtmlNode> sources)
        {
            var list = sources.ToList();
            return list.FirstOrDefault(s => EndsWith(s, ".mp4"))
                ?? list.FirstOrDefault(s => EndsWith(s, ".webm"))
                ?? list[0];
        }

        private static bool EndsWith(HtmlNode source, string ext)
        {
            return StripQuery(source.GetAttributeValue("src", ""))
                .EndsWith(ext, StringComparison.OrdinalIgnoreCase);
        }

        // Steam appends ?t=<timestamp> to media URLs
        private static string StripQuery(string url)
        {
            var idx = url.IndexOf('?');
            return idx >= 0 ? url.Substring(0, idx) : url;
        }

        private static string VirtualUrl(Guid gameId, string filename)
        {
            return $"https://{Constants.VirtualHostName}/{gameId}/{filename}";
        }

        // Deletes WebM cached by older versions once the MP4 that replaced it is on disk.
        // ponytail: only runs when a game is viewed — a game never opened keeps its stale
        // WebM forever, which is fine for a cache.
        private void ReclaimStaleWebm(List<(string webmPath, string keptPath)> candidates, Guid gameId)
        {
            var freed = 0L;
            var count = 0;

            foreach (var (webmPath, keptPath) in candidates.Distinct())
            {
                // Never delete while the replacement is still queued for download
                if (!File.Exists(webmPath) || !File.Exists(keptPath)) continue;

                try
                {
                    var size = new FileInfo(webmPath).Length;
                    File.Delete(webmPath);
                    freed += size;
                    count++;
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, $"Failed to delete stale WebM: {webmPath}");
                }
            }

            if (count > 0)
                Logger.Info($"Reclaimed {count} stale WebM ({freed / 1024} KB) for game {gameId}");
        }

        // Shared so callers reuse the pooled connection instead of creating an HttpClient per download
        public static async Task DownloadToFileAsync(string url, string destPath)
        {
            var bytes = await HttpClient.GetByteArrayAsync(url);
            File.WriteAllBytes(destPath, bytes);
        }

        public async Task DownloadAllAsync(List<(string url, string localPath)> items, Guid gameId)
        {
            var gameDir = GetGameCacheDir(gameId);
            Directory.CreateDirectory(gameDir);

            // Track the running total instead of walking the directory once per file
            var cacheSize = GetGameCacheSize(gameId);
            var limit = _settings.MaxCachePerGameMB * 1024L * 1024L;

            foreach (var (url, localPath) in items)
            {
                try
                {
                    if (cacheSize > limit)
                    {
                        Logger.Info($"Cache limit reached for game {gameId}, skipping remaining downloads.");
                        break;
                    }

                    // Skip if already downloaded
                    if (File.Exists(localPath)) continue;

                    var response = await HttpClient.GetAsync(url);
                    response.EnsureSuccessStatusCode();
                    var bytes = await response.Content.ReadAsByteArrayAsync();
                    File.WriteAllBytes(localPath, bytes);
                    cacheSize += bytes.Length;

                    // Only orphan WebM reach here — paired ones are dropped during rewrite.
                    // No-op when FFmpeg is missing; the WebM just plays as-is.
                    if (localPath.EndsWith(".webm", StringComparison.OrdinalIgnoreCase))
                    {
                        var pathToConvert = localPath;
                        await Task.Run(() => ConvertWebmToMp4(pathToConvert));
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, $"Failed to cache media: {url}");
                }
            }
        }

        private void ConvertWebmToMp4(string webmPath)
        {
            try
            {
                var mp4Path = Path.ChangeExtension(webmPath, ".mp4");
                if (File.Exists(mp4Path)) return;

                var ffmpeg = FindFfmpeg();
                if (ffmpeg == null)
                {
                    Logger.Info("FFmpeg not found, skipping WebM to MP4 conversion.");
                    return;
                }

                Logger.Info($"Converting {Path.GetFileName(webmPath)} to MP4...");

                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = ffmpeg,
                        Arguments = $"-i \"{webmPath}\" -c:v libx264 -preset veryfast -crf 23 -an -y \"{mp4Path}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardError = true
                    }
                };

                process.Start();
                var exited = process.WaitForExit(60000); // 60 second timeout

                if (!exited)
                {
                    Logger.Error($"FFmpeg timed out for: {Path.GetFileName(webmPath)}");
                    try { process.Kill(); } catch { }
                    if (File.Exists(mp4Path)) File.Delete(mp4Path);
                }
                else if (process.ExitCode == 0 && File.Exists(mp4Path))
                {
                    Logger.Info($"Converted: {Path.GetFileName(mp4Path)} ({new FileInfo(mp4Path).Length / 1024} KB)");
                }
                else
                {
                    var error = process.StandardError.ReadToEnd();
                    Logger.Error($"FFmpeg failed (exit {process.ExitCode}): {error}");
                    if (File.Exists(mp4Path)) File.Delete(mp4Path);
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "WebM to MP4 conversion failed");
            }
        }

        private string FindFfmpeg()
        {
            // Check user-configured path first
            if (!string.IsNullOrEmpty(_settings.FfmpegPath) && File.Exists(_settings.FfmpegPath))
                return _settings.FfmpegPath;

            // Check PATH
            var pathDirs = Environment.GetEnvironmentVariable("PATH")?.Split(';') ?? new string[0];
            foreach (var dir in pathDirs)
            {
                var ffmpegPath = Path.Combine(dir.Trim(), "ffmpeg.exe");
                if (File.Exists(ffmpegPath)) return ffmpegPath;
            }

            return null;
        }

        public int? GetGameVideoScale(Guid gameId)
        {
            var path = Path.Combine(GetGameCacheDir(gameId), "_videoScale.txt");
            if (File.Exists(path))
            {
                try
                {
                    if (int.TryParse(File.ReadAllText(path).Trim(), out var scale))
                        return scale;
                }
                catch { }
            }
            return null;
        }

        public void SetGameVideoScale(Guid gameId, int? scale)
        {
            var dir = GetGameCacheDir(gameId);
            var path = Path.Combine(dir, "_videoScale.txt");
            if (scale.HasValue)
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(path, scale.Value.ToString());
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
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

        public static string GetCacheFilename(string url)
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
