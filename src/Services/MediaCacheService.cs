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

                // A converted file keeps the same hash, only the extension changes.
                // Applies to anything ffmpeg re-encoded, not just WebM.
                var ext = Path.GetExtension(filename).ToLowerInvariant();
                var convertible = ext == ".webm" || ext == ".gif" || ext == ".apng" || ext == ".webp";
                var mp4Path = convertible ? Path.ChangeExtension(localPath, ".mp4") : null;

                if (mp4Path != null && File.Exists(mp4Path))
                {
                    var mp4Url = VirtualUrl(gameId, Path.GetFileName(mp4Path));

                    // An <img> cannot play an MP4, so it has to become a <video>.
                    // Muted, looping and autoplaying keeps GIF-like behaviour.
                    if (node.Name == "img")
                    {
                        var video = node.OwnerDocument.CreateElement("video");
                        video.SetAttributeValue("autoplay", "");
                        video.SetAttributeValue("muted", "");
                        video.SetAttributeValue("loop", "");
                        video.SetAttributeValue("playsinline", "");
                        var src = node.OwnerDocument.CreateElement("source");
                        src.SetAttributeValue("src", mp4Url);
                        src.SetAttributeValue("type", "video/mp4");
                        video.AppendChild(src);
                        node.ParentNode.ReplaceChild(video, node);
                        return;
                    }

                    node.SetAttributeValue(attrName, mp4Url);
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

            RepairConvertedLocalRefs(doc, gameId, gameDir);

            ReclaimStaleWebm(staleWebm, gameId);

            // Auto-download uncached files only if setting is enabled
            if (toDownload.Count > 0 && _settings.AutoCacheMedia)
            {
                _ = DownloadAllAsync(toDownload, gameId);
            }

            return doc.DocumentNode.OuterHtml;
        }

        // Points already-local references at their converted MP4.
        //
        // Map() only runs for remote URLs, so it never sees media the user added
        // themselves — those are written into the cached description as
        // playgif.local URLs at insert time. After bulk conversion the original
        // file is gone and the reference dangles, showing a broken image.
        // Runs on every render, so affected descriptions heal without user action.
        private void RepairConvertedLocalRefs(HtmlDocument doc, Guid gameId, string gameDir)
        {
            var repaired = 0;

            foreach (var node in doc.DocumentNode.SelectNodes("//img[@src] | //source[@src] | //video[@src]")
                ?? Enumerable.Empty<HtmlNode>())
            {
                var src = node.GetAttributeValue("src", "");
                if (src.IndexOf(Constants.VirtualHostName, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                var fileName = src.Split('?')[0].Split('/').Last();
                if (string.IsNullOrEmpty(fileName)) continue;

                var ext = Path.GetExtension(fileName).ToLowerInvariant();
                if (ext != ".gif" && ext != ".webm" && ext != ".apng" && ext != ".webp") continue;

                // Only repair when the original is actually gone and an MP4 replaced it
                if (File.Exists(Path.Combine(gameDir, fileName))) continue;

                var mp4Name = Path.ChangeExtension(fileName, ".mp4");
                if (!File.Exists(Path.Combine(gameDir, mp4Name))) continue;

                var mp4Url = VirtualUrl(gameId, mp4Name);

                if (node.Name == "img")
                {
                    var video = doc.CreateElement("video");
                    video.SetAttributeValue("autoplay", "");
                    video.SetAttributeValue("muted", "");
                    video.SetAttributeValue("loop", "");
                    video.SetAttributeValue("playsinline", "");
                    var source = doc.CreateElement("source");
                    source.SetAttributeValue("src", mp4Url);
                    source.SetAttributeValue("type", "video/mp4");
                    video.AppendChild(source);
                    node.ParentNode.ReplaceChild(video, node);
                }
                else
                {
                    node.SetAttributeValue("src", mp4Url);
                    if (node.Name == "source")
                        node.SetAttributeValue("type", "video/mp4");
                }

                repaired++;
            }

            if (repaired > 0)
                Logger.Info($"Repaired {repaired} converted media reference(s) for game {gameId}");
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
            ConvertToMp4(webmPath);
        }

        // Converts any ffmpeg-readable animation (WebM, GIF, APNG, animated WebP)
        // to H.264 MP4. Returns true when an MP4 exists afterwards.
        //
        // Two arguments are load-bearing and must stay together:
        //  - yuv420p, because libx264 otherwise picks yuv444p for GIF input and
        //    most browsers and hardware decoders will not play that.
        //  - the scale filter, because yuv420p requires even dimensions and GIFs
        //    are often odd-sized; without it ffmpeg fails outright.
        // Supplying only one of the two produces a file that looks converted but
        // does not render, which is worse than not converting at all.
        public bool ConvertToMp4(string sourcePath)
        {
            try
            {
                if (!File.Exists(sourcePath)) return false;

                var mp4Path = Path.ChangeExtension(sourcePath, ".mp4");
                if (string.Equals(sourcePath, mp4Path, StringComparison.OrdinalIgnoreCase))
                    return true;
                if (File.Exists(mp4Path)) return true;

                var ffmpeg = FindFfmpeg();
                if (ffmpeg == null)
                {
                    Logger.Info("FFmpeg not found, skipping conversion to MP4.");
                    return false;
                }

                var args =
                    $"-i \"{sourcePath}\" -c:v libx264 -pix_fmt yuv420p " +
                    "-vf \"scale=trunc(iw/2)*2:trunc(ih/2)*2\" " +
                    $"-preset veryfast -crf 23 -an -movflags +faststart -y \"{mp4Path}\"";

                var stderr = new System.Text.StringBuilder();

                using (var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = ffmpeg,
                        Arguments = args,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardError = true
                    }
                })
                {
                    // Must drain stderr asynchronously. ffmpeg emits enough output on
                    // a large file to fill the pipe buffer; with no reader it blocks
                    // on write and never exits, so WaitForExit reports a timeout for
                    // a conversion that actually succeeded.
                    process.ErrorDataReceived += (s, e) =>
                    {
                        if (e.Data != null) stderr.AppendLine(e.Data);
                    };

                    process.Start();
                    process.BeginErrorReadLine();

                    if (!process.WaitForExit(120000))
                    {
                        Logger.Error($"FFmpeg timed out for: {Path.GetFileName(sourcePath)}");
                        try { process.Kill(); } catch { }
                        if (File.Exists(mp4Path)) File.Delete(mp4Path);
                        return false;
                    }

                    if (process.ExitCode == 0 && File.Exists(mp4Path) && new FileInfo(mp4Path).Length > 0)
                        return true;

                    Logger.Error($"FFmpeg failed on {Path.GetFileName(sourcePath)} " +
                                 $"(exit {process.ExitCode}): {stderr}");
                }

                if (File.Exists(mp4Path)) File.Delete(mp4Path);
                return false;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"Conversion to MP4 failed for {sourcePath}");
                return false;
            }
        }

        public bool IsFfmpegAvailable() => FindFfmpeg() != null;

        // Rewrites cached description files so references to converted media point
        // at the MP4. RepairConvertedLocalRefs patches this at render time, but the
        // file on disk should not stay stale — otherwise every render re-does the
        // same work and anything else reading the cache still sees a dead link.
        // Returns the number of references updated.
        public int RepairDescriptionsOnDisk()
        {
            var total = 0;
            if (!Directory.Exists(_cacheBasePath)) return 0;

            foreach (var gameDir in Directory.GetDirectories(_cacheBasePath))
            {
                foreach (var descPath in Directory.GetFiles(gameDir, "_description*.html"))
                {
                    try
                    {
                        var html = File.ReadAllText(descPath);
                        var updated = html;

                        foreach (var mp4 in Directory.GetFiles(gameDir, "*.mp4"))
                        {
                            var stem = Path.GetFileNameWithoutExtension(mp4);
                            foreach (var ext in new[] { ".gif", ".webm", ".apng", ".webp" })
                            {
                                var original = stem + ext;
                                // Only rewrite when the original is genuinely gone
                                if (File.Exists(Path.Combine(gameDir, original))) continue;
                                if (updated.IndexOf(original, StringComparison.OrdinalIgnoreCase) < 0) continue;

                                updated = updated.Replace(original, stem + ".mp4");
                                total++;
                            }
                        }

                        if (!ReferenceEquals(updated, html) && updated != html)
                            File.WriteAllText(descPath, updated);
                    }
                    catch (Exception ex)
                    {
                        Logger.Error(ex, $"Failed to repair description: {descPath}");
                    }
                }
            }

            if (total > 0) Logger.Info($"Repaired {total} media reference(s) in cached descriptions.");
            return total;
        }

        // Cached media that ffmpeg can re-encode to MP4. Excludes still images and
        // anything already MP4.
        public List<string> FindConvertibleMedia()
        {
            var results = new List<string>();
            if (!Directory.Exists(_cacheBasePath)) return results;

            var convertible = new[] { ".gif", ".webm", ".apng", ".webp" };

            foreach (var gameDir in Directory.GetDirectories(_cacheBasePath))
            {
                foreach (var file in Directory.GetFiles(gameDir))
                {
                    var ext = Path.GetExtension(file).ToLowerInvariant();
                    if (Array.IndexOf(convertible, ext) < 0) continue;

                    // Already converted
                    if (File.Exists(Path.ChangeExtension(file, ".mp4"))) continue;

                    // A still WebP or APNG has nothing to gain from H.264
                    if ((ext == ".webp" || ext == ".apng") && !IsAnimated(file)) continue;

                    results.Add(file);
                }
            }

            return results;
        }

        // ffprobe reports more than one frame for animated content
        private bool IsAnimated(string path)
        {
            try
            {
                var ffmpeg = FindFfmpeg();
                if (ffmpeg == null) return false;
                var ffprobe = Path.Combine(Path.GetDirectoryName(ffmpeg), "ffprobe.exe");
                if (!File.Exists(ffprobe)) return true; // can't tell — let ffmpeg decide

                var p = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = ffprobe,
                        Arguments = $"-v error -select_streams v:0 -count_frames " +
                                    $"-show_entries stream=nb_read_frames -of csv=p=0 \"{path}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true
                    }
                };
                p.Start();
                var output = p.StandardOutput.ReadToEnd().Trim();
                p.WaitForExit(15000);
                return int.TryParse(output, out var frames) && frames > 1;
            }
            catch { return true; }
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
