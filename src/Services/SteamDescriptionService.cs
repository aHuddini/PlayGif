using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Playnite.SDK;
using Playnite.SDK.Models;
using PlayGif.Common;

namespace PlayGif.Services
{
    public class SteamDescriptionService
    {
        private static readonly ILogger Logger = LogManager.GetLogger();
        private static readonly HttpClient HttpClient = new HttpClient();

        private static readonly Guid SteamPluginId =
            Guid.Parse("CB91DFC9-B977-43BF-8E70-55F46E410FAB");
        private static readonly Guid GogPluginId =
            Guid.Parse("AEBE8B7C-6DC3-4A66-AF31-E7375C6B5E9E");

        private const int MaxRetries = 10;
        private const int RetryDelayMs = 2500;

        private readonly string _cacheBasePath;

        public SteamDescriptionService(string pluginDataPath)
        {
            _cacheBasePath = Path.Combine(pluginDataPath, Constants.GamesCacheFolder);
            Directory.CreateDirectory(_cacheBasePath);
        }

        public async Task<string> GetRichDescriptionAsync(Game game)
        {
            var cached = LoadCachedDescription(game.Id);
            if (cached != null) return cached;

            var appId = ResolveSteamAppId(game);
            if (string.IsNullOrEmpty(appId))
            {
                Logger.Info($"Could not resolve Steam AppId for: {game.Name}");
                return null;
            }

            var html = await FetchWithRetryAsync(appId);
            if (!string.IsNullOrEmpty(html))
            {
                SaveCachedDescription(game.Id, html);
                Logger.Info($"Fetched and cached Steam description for: {game.Name} (AppId: {appId}, {html.Length} chars)");
            }

            return html;
        }

        // Bulk fetch for all resolvable games in the library
        public async Task<(int fetched, int skipped, int failed)> BulkFetchAsync(
            IEnumerable<Game> games, IPlayniteAPI api, CancellationToken ct)
        {
            var toFetch = new List<(Game game, string appId)>();

            foreach (var game in games)
            {
                if (ct.IsCancellationRequested) break;

                // Skip if already cached
                if (LoadCachedDescription(game.Id) != null) continue;

                var appId = ResolveSteamAppId(game);
                if (!string.IsNullOrEmpty(appId))
                {
                    toFetch.Add((game, appId));
                }
            }

            if (toFetch.Count == 0)
            {
                return (0, 0, 0);
            }

            Logger.Info($"Bulk fetch: {toFetch.Count} games to fetch.");

            int fetched = 0;
            int failed = 0;
            var globalProgress = api.Dialogs.ActivateGlobalProgress((progressArgs) =>
            {
                progressArgs.ProgressMaxValue = toFetch.Count;

                for (int i = 0; i < toFetch.Count; i++)
                {
                    if (progressArgs.CancelToken.IsCancellationRequested || ct.IsCancellationRequested)
                        break;

                    var (game, appId) = toFetch[i];
                    progressArgs.Text = $"Fetching description for: {game.Name} ({i + 1}/{toFetch.Count})";

                    try
                    {
                        var html = FetchWithRetryAsync(appId).GetAwaiter().GetResult();
                        if (!string.IsNullOrEmpty(html))
                        {
                            SaveCachedDescription(game.Id, html);
                            fetched++;
                        }
                        else
                        {
                            failed++;
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Error(ex, $"Bulk fetch failed for: {game.Name} (AppId: {appId})");
                        failed++;
                    }

                    progressArgs.CurrentProgressValue = i + 1;
                }
            }, new GlobalProgressOptions($"Fetching Steam descriptions (0/{toFetch.Count})...", true)
            {
                IsIndeterminate = false
            });

            int skipped = toFetch.Count - fetched - failed;
            Logger.Info($"Bulk fetch complete: {fetched} fetched, {failed} failed, {skipped} skipped.");
            return (fetched, skipped, failed);
        }

        public string ResolveSteamAppId(Game game)
        {
            // For Steam library games, GameId IS the AppId
            if (game.PluginId == SteamPluginId && !string.IsNullOrEmpty(game.GameId))
            {
                return game.GameId;
            }

            // For non-Steam games, check if there's a Steam store link
            if (game.Links != null)
            {
                foreach (var link in game.Links)
                {
                    if (link.Url != null && link.Url.Contains("store.steampowered.com/app/"))
                    {
                        var parts = link.Url.Split(new[] { "/app/" }, StringSplitOptions.None);
                        if (parts.Length > 1)
                        {
                            var appIdStr = parts[1].Split('/')[0].Trim();
                            if (int.TryParse(appIdStr, out _))
                                return appIdStr;
                        }
                    }
                }
            }

            return null;
        }

        // Fetch with retry on HTTP 429 (rate limiting), matching UniversalSteamMetadata pattern
        private async Task<string> FetchWithRetryAsync(string appId)
        {
            for (int attempt = 0; attempt < MaxRetries; attempt++)
            {
                try
                {
                    var url = $"https://store.steampowered.com/api/appdetails?appids={appId}";
                    var response = await HttpClient.GetAsync(url);

                    if (response.StatusCode == (HttpStatusCode)429)
                    {
                        Logger.Info($"Rate limited (429) for AppId {appId}, retry {attempt + 1}/{MaxRetries}...");
                        await Task.Delay(RetryDelayMs);
                        continue;
                    }

                    response.EnsureSuccessStatusCode();
                    var content = await response.Content.ReadAsStringAsync();

                    var json = JObject.Parse(content);
                    var appData = json[appId];
                    if (appData == null || appData["success"]?.Value<bool>() != true)
                    {
                        Logger.Info($"Steam API returned no data for AppId: {appId}");
                        return null;
                    }

                    return appData["data"]?["about_the_game"]?.Value<string>();
                }
                catch (HttpRequestException ex) when (attempt < MaxRetries - 1)
                {
                    Logger.Info($"HTTP error for AppId {appId}, retry {attempt + 1}: {ex.Message}");
                    await Task.Delay(RetryDelayMs);
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, $"Failed to fetch Steam description for AppId: {appId}");
                    return null;
                }
            }

            Logger.Error($"Max retries reached for AppId: {appId}");
            return null;
        }

        // GOG support
        public string ResolveGogProductId(Game game)
        {
            if (game.PluginId == GogPluginId && !string.IsNullOrEmpty(game.GameId))
                return game.GameId;

            if (game.Links != null)
            {
                foreach (var link in game.Links)
                {
                    if (link.Url != null && link.Url.Contains("gog.com/"))
                    {
                        // Try to extract product ID from GOG URL
                        var parts = link.Url.Split('/');
                        for (int i = parts.Length - 1; i >= 0; i--)
                        {
                            if (long.TryParse(parts[i], out _))
                                return parts[i];
                        }
                    }
                }
            }
            return null;
        }

        public async Task<string> FetchGogDescriptionAsync(Game game)
        {
            var cached = LoadCachedDescription(game.Id);
            if (cached != null) return cached;

            var productId = ResolveGogProductId(game);
            if (string.IsNullOrEmpty(productId))
            {
                Logger.Info($"Could not resolve GOG product ID for: {game.Name}");
                return null;
            }

            try
            {
                var url = $"https://api.gog.com/products/{productId}?expand=description";
                var response = await HttpClient.GetStringAsync(url);

                var json = JObject.Parse(response);
                var description = json["description"]?["full"]?.ToString();

                if (!string.IsNullOrEmpty(description))
                {
                    SaveCachedDescription(game.Id, description);
                    Logger.Info($"Fetched GOG description for: {game.Name} (ID: {productId}, {description.Length} chars)");
                }

                return description;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"Failed to fetch GOG description for: {game.Name} (ID: {productId})");
                return null;
            }
        }

        public bool HasCachedDescription(Guid gameId)
        {
            return File.Exists(GetDescriptionCachePath(gameId));
        }

        #region Cache

        private string GetDescriptionCachePath(Guid gameId)
        {
            var gameDir = Path.Combine(_cacheBasePath, gameId.ToString());
            return Path.Combine(gameDir, "_description.html");
        }

        private string LoadCachedDescription(Guid gameId)
        {
            var path = GetDescriptionCachePath(gameId);
            if (File.Exists(path))
            {
                try { return File.ReadAllText(path); }
                catch { }
            }
            return null;
        }

        public void SaveCachedDescription(Guid gameId, string html)
        {
            var path = GetDescriptionCachePath(gameId);
            var dir = Path.GetDirectoryName(path);
            Directory.CreateDirectory(dir);
            File.WriteAllText(path, html);
        }

        public void UpdateCachedDescription(Guid gameId, string tag, string position)
        {
            var path = GetDescriptionCachePath(gameId);
            if (File.Exists(path))
            {
                var html = File.ReadAllText(path);
                if (position == "top")
                    html = tag + "\n" + html;
                else
                    html = html + "\n" + tag;
                File.WriteAllText(path, html);
            }
        }

        public void ClearCachedDescription(Guid gameId)
        {
            var path = GetDescriptionCachePath(gameId);
            if (File.Exists(path))
            {
                try { File.Delete(path); }
                catch { }
            }
        }

        #endregion
    }
}
