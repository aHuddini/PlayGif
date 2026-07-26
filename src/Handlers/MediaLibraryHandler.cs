using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HtmlAgilityPack;
using Playnite.SDK;
using Playnite.SDK.Models;
using PlayGif.Common;
using PlayGif.Services;

namespace PlayGif.Handlers
{
    // Backs the "Add media" and "Manage cached files" menu entries.
    // Both used to be six and three separate menu rows respectively.
    public class MediaLibraryHandler
    {
        private static readonly ILogger Logger = LogManager.GetLogger();

        private readonly IPlayniteAPI _api;
        private readonly Func<Guid, string> _gameDirProvider;
        private readonly Action<Game> _rerender;

        public MediaLibraryHandler(
            IPlayniteAPI api,
            Func<Guid, string> gameDirProvider,
            Action<Game> rerender)
        {
            _api = api;
            _gameDirProvider = gameDirProvider;
            _rerender = rerender;
        }

        // Pick a media file and remove it from the game
        public void RemoveMedia(Game game)
        {
            var gameDir = _gameDirProvider(game.Id);
            if (!Directory.Exists(gameDir))
            {
                _api.Dialogs.ShowMessage("No media to remove for this game.", Constants.PluginName);
                return;
            }

            var files = ListMediaFiles(gameDir);
            if (files.Count == 0)
            {
                _api.Dialogs.ShowMessage("No media to remove for this game.", Constants.PluginName);
                return;
            }

            var selected = Choose(files, $"Select media to remove ({files.Count} total)");
            if (selected == null) return;

            DeleteFile(game, gameDir, selected.Name);
        }

        private void DeleteFile(Game game, string gameDir, string fileName)
        {
            try
            {
                File.Delete(Path.Combine(gameDir, fileName));
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"Failed to delete cached file: {fileName}");
                _api.Dialogs.ShowMessage($"Failed: {ex.Message}", Constants.PluginName);
                return;
            }

            var strippedCache = StripFromCachedDescription(game.Id, gameDir, fileName);
            var strippedStored = StripFromStoredDescription(game, fileName);

            _api.Dialogs.ShowMessage(
                strippedCache || strippedStored
                    ? $"Removed {fileName} and updated the description."
                    : $"Removed {fileName}.",
                Constants.PluginName);

            _rerender(game);
        }

        // Removes media nodes whose source hashes to the deleted file, so the
        // description doesn't fall back to streaming it from the original URL
        private bool StripFromCachedDescription(Guid gameId, string gameDir, string fileName)
        {
            var descPath = Path.Combine(gameDir, "_description.html");
            if (!File.Exists(descPath)) return false;

            try
            {
                var doc = new HtmlDocument();
                doc.LoadHtml(File.ReadAllText(descPath));

                var fileBase = Path.GetFileNameWithoutExtension(fileName);
                var toRemove = new List<HtmlNode>();

                foreach (var source in doc.DocumentNode.SelectNodes("//source[@src]")
                    ?? Enumerable.Empty<HtmlNode>())
                {
                    var hash = MediaCacheService.GetCacheFilename(source.GetAttributeValue("src", ""));
                    if (Path.GetFileNameWithoutExtension(hash) != fileBase) continue;

                    // Drop the whole <video>, not just the one source
                    var video = source.ParentNode;
                    var target = video?.Name == "video" ? video : source;
                    if (!toRemove.Contains(target)) toRemove.Add(target);
                }

                foreach (var img in doc.DocumentNode.SelectNodes("//img[@src]")
                    ?? Enumerable.Empty<HtmlNode>())
                {
                    var hash = MediaCacheService.GetCacheFilename(img.GetAttributeValue("src", ""));
                    if (hash == fileName && !toRemove.Contains(img)) toRemove.Add(img);
                }

                // A video whose poster was the deleted file would render as a broken frame
                foreach (var video in doc.DocumentNode.SelectNodes("//video[@poster]")
                    ?? Enumerable.Empty<HtmlNode>())
                {
                    var hash = MediaCacheService.GetCacheFilename(video.GetAttributeValue("poster", ""));
                    if (Path.GetFileNameWithoutExtension(hash) == fileBase && !toRemove.Contains(video))
                        toRemove.Add(video);
                }

                if (toRemove.Count == 0) return false;

                foreach (var node in toRemove)
                    node.ParentNode.RemoveChild(node);

                RemoveEmptyMedia(doc);
                File.WriteAllText(descPath, doc.DocumentNode.OuterHtml);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to strip media from cached description");
                return false;
            }
        }

        // Removes PlayGif-inserted tags pointing at this file from the Playnite DB description
        private bool StripFromStoredDescription(Game game, string fileName)
        {
            var desc = game.Description;
            if (string.IsNullOrEmpty(desc) || !desc.Contains(fileName)) return false;

            try
            {
                var doc = new HtmlDocument();
                doc.LoadHtml(desc);

                var toRemove = new List<HtmlNode>();

                foreach (var node in doc.DocumentNode.SelectNodes("//img[@src] | //source[@src]")
                    ?? Enumerable.Empty<HtmlNode>())
                {
                    var src = node.GetAttributeValue("src", "");
                    if (!src.Contains(Constants.VirtualHostName) ||
                        !src.EndsWith("/" + fileName, StringComparison.OrdinalIgnoreCase)) continue;

                    var video = node.ParentNode;
                    var target = video?.Name == "video" ? video : node;
                    if (!toRemove.Contains(target)) toRemove.Add(target);
                }

                if (toRemove.Count == 0) return false;

                foreach (var node in toRemove)
                    node.ParentNode.RemoveChild(node);

                RemoveEmptyMedia(doc);
                game.Description = doc.DocumentNode.OuterHtml.Trim();
                _api.Database.Games.Update(game);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to strip media from stored description");
                return false;
            }
        }

        // Strips media elements left with nothing to display — a sourceless <video>
        // or a src-less <img> renders as a broken-image box
        private static void RemoveEmptyMedia(HtmlDocument doc)
        {
            var empties = new List<HtmlNode>();

            foreach (var video in doc.DocumentNode.SelectNodes("//video")
                ?? Enumerable.Empty<HtmlNode>())
            {
                var hasSource = (video.SelectNodes(".//source[@src]")?.Count ?? 0) > 0
                    || video.GetAttributeValue("src", "").Length > 0;
                if (!hasSource && video.GetAttributeValue("poster", "").Length == 0)
                    empties.Add(video);
            }

            foreach (var img in doc.DocumentNode.SelectNodes("//img")
                ?? Enumerable.Empty<HtmlNode>())
            {
                if (img.GetAttributeValue("src", "").Length == 0)
                    empties.Add(img);
            }

            foreach (var node in empties)
                node.ParentNode?.RemoveChild(node);
        }

        private static List<GenericItemOption> ListMediaFiles(string gameDir)
        {
            return Directory.GetFiles(gameDir)
                .Where(f => !Path.GetFileName(f).StartsWith("_"))
                .Select(f => new GenericItemOption(
                    Path.GetFileName(f), $"{new FileInfo(f).Length / 1024} KB"))
                .ToList();
        }

        private GenericItemOption Choose(List<GenericItemOption> options, string caption)
        {
            return _api.Dialogs.ChooseItemWithSearch(
                options,
                (s) => string.IsNullOrEmpty(s)
                    ? options
                    : options.Where(o => o.Name.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0).ToList(),
                "",
                caption);
        }
    }
}
