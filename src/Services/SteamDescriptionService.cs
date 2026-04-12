using System;
using System.IO;
using System.Net.Http;
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

        // Steam library plugin GUID (built-in)
        private static readonly Guid SteamPluginId =
            Guid.Parse("CB91DFC9-B977-43BF-8E70-55F46E410FAB");

        private readonly string _cacheBasePath;

        public SteamDescriptionService(string pluginDataPath)
        {
            _cacheBasePath = Path.Combine(pluginDataPath, Constants.GamesCacheFolder);
            Directory.CreateDirectory(_cacheBasePath);
        }

        // Get the rich description for a game — from cache or Steam API
        public async Task<string> GetRichDescriptionAsync(Game game)
        {
            // Check local cache first
            var cached = LoadCachedDescription(game.Id);
            if (cached != null) return cached;

            // Resolve Steam AppId
            var appId = ResolveSteamAppId(game);
            if (string.IsNullOrEmpty(appId))
            {
                Logger.Info($"Could not resolve Steam AppId for: {game.Name}");
                return null;
            }

            // Fetch from Steam API
            var html = await FetchSteamDescriptionAsync(appId);
            if (!string.IsNullOrEmpty(html))
            {
                SaveCachedDescription(game.Id, html);
                Logger.Info($"Fetched and cached Steam description for: {game.Name} (AppId: {appId}, {html.Length} chars)");
            }

            return html;
        }

        private string ResolveSteamAppId(Game game)
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

            // TODO: future enhancement — cross-match by name via Steam search API
            return null;
        }

        private async Task<string> FetchSteamDescriptionAsync(string appId)
        {
            try
            {
                var url = $"https://store.steampowered.com/api/appdetails?appids={appId}";
                var response = await HttpClient.GetStringAsync(url);

                var json = JObject.Parse(response);
                var appData = json[appId];
                if (appData == null || appData["success"]?.Value<bool>() != true)
                {
                    Logger.Info($"Steam API returned no data for AppId: {appId}");
                    return null;
                }

                var description = appData["data"]?["about_the_game"]?.Value<string>();
                return description;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"Failed to fetch Steam description for AppId: {appId}");
                return null;
            }
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

        private void SaveCachedDescription(Guid gameId, string html)
        {
            var path = GetDescriptionCachePath(gameId);
            var dir = Path.GetDirectoryName(path);
            Directory.CreateDirectory(dir);
            File.WriteAllText(path, html);
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
