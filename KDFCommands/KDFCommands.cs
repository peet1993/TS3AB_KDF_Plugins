using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json;
using NLog.Fluent;
using TS3AudioBot;
using TS3AudioBot.Audio;
using TS3AudioBot.CommandSystem;
using TS3AudioBot.Config;
using TS3AudioBot.Plugins;
using TS3AudioBot.Playlists;
using TS3AudioBot.ResourceFactories;
using TS3AudioBot.Helper;
using TS3AudioBot.Localization;
using TS3AudioBot.Web.Api;
using TSLib;
using TSLib.Full;
using TSLib.Helper;
using TSLib.Messages;

namespace KDFCommands {
	public class KDFCommandsPlugin : IBotPlugin {
		internal static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();

		internal static ICommandBag Bag { get; } = new CommandsBag();

		internal class CommandsBag : ICommandBag {
			public IReadOnlyCollection<BotCommand> BagCommands { get; } = ImmutableList<BotCommand>.Empty;

			public IReadOnlyCollection<string> AdditionalRights { get; } = new[]
				{RightOverrideQueueCommandCheck, RightDeleteOther, RightSkipOther};
		}

		private const string YOUTUBE_URL_REGEX =
			"^(?:https?:\\/\\/)?(?:www\\.)?(?:youtube\\.com\\/watch\\?.*?v=(.*?)(?:&.*)*|youtu\\.be\\/(.*?)\\??.*)$";

		private const string TRUNCATED_MESSAGE =
			"\nThe number of songs to add was reduced compared to your request.\n" +
			"This can happen because the requested number of songs was not evenly divisible by the number of playlists " +
			"or at least one playlist had not enough songs (or both).";

		public const string RightDeleteOther = "del.other";
		public const string RightSkipOther = "skip.other";
		public const string RightOverrideQueueCommandCheck = "queue.view.full";

		private delegate string PartHandler(
			PlaylistManager playlistManager,
			PlayManager playManager,
			ExecutionInformation info,
			Uid uid,
			ResolveContext resolver,
			ClientCall cc,
			Ts3Client ts3Client,
			TsFullClient tsFullClient,
			string query,
			string target = null,
			bool silent = false,
			Dictionary<string, string> additionalData = null
		);

		private readonly Player player;
		private readonly PlayManager playManager;
		private readonly PlaylistManager playlistManager;

		private readonly TsFullClient ts3FullClient;
		private readonly Ts3Client ts3Client;

		private readonly ConfBot confBot;
		private readonly ConfPlugins confPlugins;

		internal Voting Voting { get; set; }
		internal Autofill Autofill { get; set; }
		private Description Description { get; set; }
		internal TwitchInfoUpdater TwitchInfoUpdater { get; set; }
		private UpdateWebSocket UpdateWebSocket { get; set; }
		private RecalcGainInfo recalcGainInfo;

		public KDFCommandsPlugin(
			Player player,
			PlayManager playManager,
			PlaylistManager playlistManager,
			Ts3Client ts3Client,
			TsFullClient ts3FullClient,
			CommandManager commandManager,
			ConfBot confBot,
			ConfPlugins confPlugins,
			Bot bot) {
			
			this.player = player;
			this.playManager = playManager;
			this.playlistManager = playlistManager;

			this.ts3FullClient = ts3FullClient;
			bot.RegenerateStatusImage();
			this.ts3Client = ts3Client;
			this.confBot = confBot;
			this.confPlugins = confPlugins;

			commandManager.RegisterCollection(Bag);
		}

		public void Initialize() {
			playManager.AfterResourceStarted += ResourceStarted;
			playManager.PlaybackStopped += PlaybackStopped;
			playManager.ResourceStopped += OnResourceStopped;

			Voting = new Voting(player, ts3Client, ts3FullClient, confBot);
			Autofill = new Autofill(ts3Client, player, playManager, playlistManager, ts3FullClient);
			Description = new Description(player, ts3Client, playManager);
			UpdateWebSocket = new UpdateWebSocket(this, player, playManager, playlistManager, ts3Client, ts3FullClient, confBot.WebSocket);
		}

		public void Dispose() {
			playManager.AfterResourceStarted -= ResourceStarted;
			playManager.PlaybackStopped -= PlaybackStopped;
			playManager.ResourceStopped -= OnResourceStopped;

			Description.Dispose();
			Autofill.DisableAndRemoveShadow();
			UpdateWebSocket.Dispose();

			Voting.CancelAll();
		}

		private void OnResourceStopped(object sender, SongEndEventArgs e) {
			Voting.OnSongEnd();
			if (TwitchInfoUpdater != null) {
				TwitchInfoUpdater.Dispose();
				TwitchInfoUpdater = null;
			}
		}

		private void ResourceStarted(object sender, PlayInfoEventArgs e) {
			using (System.IO.StreamWriter statfile = new System.IO.StreamWriter("stats_play.txt", true)) {
				statfile.WriteLine(DateTimeOffset.UtcNow.ToUnixTimeSeconds() + ":::" + e.ResourceData.ResourceTitle + ":::" + e.ResourceData.ResourceId + ":::" + e.PlayResource.Meta.ContainingPlaylistId + ":::" + e.PlayResource.Meta.ResourceOwnerUid);
			}

			if (e.PlayResource.BaseData.AudioType == "twitch") {
				// Start twitch info collector
				Log.Info("Start twitch update collector.");
				TwitchInfoUpdater = new TwitchInfoUpdater(confPlugins, e.PlayResource.BaseData.ResourceId);
				TwitchInfoUpdater.OnTwitchInfoChanged += UpdateWebSocket.TwitchInfoChanged;
			} else if (TwitchInfoUpdater != null) {
				TwitchInfoUpdater.Dispose();
				TwitchInfoUpdater = null;
			}
			
			var owner = e.PlayResource.Meta.ResourceOwnerUid;
			Description.Data = new DescriptionData(
				e.ResourceData.ResourceTitle,
				e.MetaData.ContainingPlaylistId,
				owner.HasValue ? ClientUtility.GetClientNameFromUid(ts3FullClient, owner.Value) : null);
		}

		private void PlaybackStopped(object sender, PlaybackStoppedEventArgs eventArgs) { Autofill.OnPlaybackStopped(eventArgs); }

		private static void Shuffle<T>(IList<T> list, Random rng) {
			int n = list.Count;
			while (n > 1) {
				n--;
				int k = rng.Next(n + 1);
				T value = list[k];
				list[k] = list[n];
				list[n] = value;
			}
		}

		public static HashSet<AudioResource> ListInteresection(IPlaylist listA, IPlaylist listB) {
			var itemsInB = new HashSet<AudioResource>(listB);
			var itemsInA = new HashSet<AudioResource>(listA);
			itemsInA.IntersectWith(itemsInB);
			return itemsInA;
		}

		public static HashSet<AudioResource> ListDifference(IPlaylist listA, IPlaylist listB) {
			var itemsInB = new HashSet<AudioResource>(listB);
			var itemsInA = new HashSet<AudioResource>(listA);
			itemsInA.ExceptWith(itemsInB);
			return itemsInA;
		}

		public static void AppendAudioResource(StringBuilder builder, int index, AudioResource item) {
			builder.Append(index).Append(": ").Append(item.ResourceTitle);
		}

		public static void AppendItemsIndexed(StringBuilder builder, IEnumerable<AudioResource> items) {
			var index = 0;
			foreach (var item in items) {
				builder.AppendLine();
				AppendAudioResource(builder, index++, item);
			}
		}

		private static void ListRemoveAll(StringBuilder builder,
			PlaylistManager playlistManager, ExecutionInformation info, string listId, HashSet<AudioResource> items) {
			MainCommands.ModifyPlaylist(playlistManager, listId, info, editor => {
				var indices = new List<int>(items.Count);
				for (var i = editor.Playlist.Count - 1; i >= 0; i--) {
					var res = editor.Playlist[i];
					if (!items.Contains(res)) 
						continue;
					indices.Add(i);

					builder.AppendLine().Append("Removed ");
					AppendAudioResource(builder, i, res);
				}

				editor.RemoveIndices(indices);
			}).UnwrapThrow();
		}

		[Command("list intersect")]
		public static string CommandListIntersect(PlaylistManager playlistManager, ExecutionInformation info, string listId, string listTo, string[] args = null) {
			var additionalArgs = args ?? Array.Empty<string>();

			var (listA, aId) = playlistManager.GetPlaylist(listId).UnwrapThrow();
			var (listB, bId) = playlistManager.GetPlaylist(listTo).UnwrapThrow();

			// Return songs in both lists
			var inBoth = ListInteresection(listA, listB);

			var builder = new StringBuilder();
			builder.Append($"{inBoth.Count} songs in the intersection of \"{aId}\" and \"{bId}\". ");

			if (!additionalArgs.Contains("--remove")) {
				builder.Append($"Append --remove to remove them from \"{aId}\".");

				if (inBoth.Count > 0)
					AppendItemsIndexed(builder, inBoth);
			} else {
				builder.Append($"Removed them from \"{aId}\".");
				ListRemoveAll(builder, playlistManager, info, aId, inBoth);
			}

			return builder.ToString();
		}

		[Command("list difference")]
		public static string CommandListDifference(PlaylistManager playlistManager, ExecutionInformation info, string listId, string listTo, string[] args = null) {
			var additionalArgs = args ?? Array.Empty<string>();

			var (listA, aId) = playlistManager.GetPlaylist(listId).UnwrapThrow();
			var (listB, bId) = playlistManager.GetPlaylist(listTo).UnwrapThrow();

			var difference = ListDifference(listA, listB);

			var builder = new StringBuilder();
			builder.Append($"{difference.Count} songs in the difference of \"{aId}\" and \"{bId}\". ");

			if (!additionalArgs.Contains("--remove")) {
				builder.Append($"Append --remove to remove them from \"{aId}\".");

				if (difference.Count > 0) {
					AppendItemsIndexed(builder, difference);
				}
			} else {
				builder.Append($"Removed them from \"{aId}\".");
				ListRemoveAll(builder, playlistManager, info, aId, difference);
			}

			return builder.ToString();
		}

		[Command("list rqueue")]
		public static string CommandListRQueue(
			PlaylistManager playlistManager,
			PlayManager playManager,
			InvokerData invoker,
			string[] parts) {

			bool truncated = false;

			// Check if last element is a number.
			// If yes, remember it as number as songs to randomly queue and the last array entry when iterating playlists.
			int count = 1;
			int numPlaylists = parts.Length;
			if (int.TryParse(parts[^1], out var countOutput)) {
				// The only element is a number --> playlist name missing
				if (parts.Length == 1) {
					throw new CommandException("No playlist to add from given.", CommandExceptionReason.CommandError);
				}

				count = countOutput;
				numPlaylists--;

				if (count < 0) {
					throw new CommandException("You can't add a negative number of songs.",
						CommandExceptionReason.CommandError);
				}

				if (count == 0) {
					throw new CommandException("Adding no songs doesn't make any sense.",
						CommandExceptionReason.CommandError);
				}
			}

			// Calculate items per playlist
			int songsPerPlaylist = count / numPlaylists;
			if (count % numPlaylists != 0) {
				truncated = true;
			}

			if (songsPerPlaylist == 0) {
				throw new CommandException("You need to add least at one song per playlist.",
					CommandExceptionReason.CommandError);
			}

			var numSongsAdded = 0;
			var allItems = new List<QueueItem>();
			for (var i = 0; i < numPlaylists; i++) {
				var userProvidedId = parts[i];
				var (plist, id) = playlistManager.GetPlaylist(userProvidedId).UnwrapThrow();
				var items = new List<AudioResource>(plist);

				int numSongsToTake = Tools.Clamp(songsPerPlaylist, 0, items.Count);
				if (numSongsToTake != songsPerPlaylist) {
					truncated = true;
				}

				numSongsAdded += numSongsToTake;

				var meta = new MetaData(invoker.ClientUid, id);
				Shuffle(items, new Random());
				allItems.AddRange(items.Take(numSongsToTake).Select(item => new QueueItem(item, meta)));
			}

			// Shuffle again across all added songs from all playlists
			Shuffle(allItems, new Random());
			int startPos;
			lock (playManager.Lock) {
				startPos = playManager.Queue.Items.Count - playManager.Queue.Index;
				playManager.Enqueue(allItems).UnwrapThrow();
			}

			return
				"Added a total of " + numSongsAdded +
				" songs across " + numPlaylists +
				" playlists to the queue at positions " + startPos +
				"-" + (startPos + numSongsAdded) +
				"." + (truncated ? TRUNCATED_MESSAGE : "");
		}

		[Command("youtube")]
		public static void CommandYoutube(
			PlayManager playManager,
			PlaylistManager playlistManager,
			ExecutionInformation execInfo,
			ResolveContext resolver,
			InvokerData invoker,
			ClientCall cc,
			Ts3Client ts3Client,
			TsFullClient ts3FullClient,
			string message) {
			string[] parts = Regex.Split(message, ";+");

			ClientUtility.SendMessage(ts3Client, cc,
				"Received your request to add " + parts.Length + " songs, processing...");

			PartHandler urlHandler = AddUrl;
			PartHandler queryHandler = AddQuery;
			ParseMulti(urlHandler, queryHandler, playManager, playlistManager, execInfo, resolver, invoker, cc,
				ts3Client, ts3FullClient, parts, "");
		}

		[Command("youtubewithuid")]
		public static string CommandYoutubeWithUid(
			PlayManager playManager,
			PlaylistManager playlistManager,
			ExecutionInformation execInfo,
			ResolveContext resolver,
			Ts3Client ts3Client,
			TsFullClient ts3FullClient,
			string uidStr,
			string message) {
			Uid uid = Uid.To(uidStr);
			
			string query = message.Replace("[URL]", "").Replace("[/URL]", "").Trim(' ');
			if (Regex.Match(query, YOUTUBE_URL_REGEX).Success) {
				return AddUrl(playlistManager, playManager, execInfo, uid, resolver, null, ts3Client, ts3FullClient,
					query, silent: true);
			} else {
				return AddQuery(playlistManager, playManager, execInfo, uid, resolver, null, ts3Client,
					ts3FullClient, query, silent: true);
			}
		}

		private static void ParseMulti(
			PartHandler ifUrl,
			PartHandler ifQuery,
			PlayManager playManager,
			PlaylistManager playlistManager,
			ExecutionInformation execInfo,
			ResolveContext resolver,
			InvokerData invoker,
			ClientCall cc,
			Ts3Client ts3Client,
			TsFullClient ts3FullClient,
			string[] parts,
			string target) {
			foreach (string part in parts) {
				// Skip if empty
				if (part == "") {
					return;
				}

				// Check if URL
				string query = part.Replace("[URL]", "").Replace("[/URL]", "").Trim(' ');
				if (Regex.Match(query, YOUTUBE_URL_REGEX).Success) {
					ifUrl(playlistManager, playManager, execInfo, invoker.ClientUid, resolver, cc, ts3Client, ts3FullClient,
						query, target);
				} else {
					ifQuery(playlistManager, playManager, execInfo, invoker.ClientUid, resolver, cc, ts3Client, ts3FullClient,
						query, target);
				}
			}
		}

		private static string ComposeAddMessage(PlayManager playManager) {
			PlayQueue queue = playManager.Queue;
			int realIndex = queue.Items.Count - 1;
			int index = realIndex - queue.Index;
			return "Added '" + queue.Items[realIndex].AudioResource.ResourceTitle + "' at queue position " + index;
		}

		private static void PrintAddMessage(Ts3Client ts3Client, ClientCall cc, PlayManager playManager) {
			ClientUtility.SendMessage(ts3Client, cc, ComposeAddMessage(playManager)); // This will fail if async
		}

		private static void SendAddFailure(Ts3Client ts3Client, string query, LocalStr error, ClientCall client) {
			ClientUtility.SendMessage(ts3Client, client, "Error occured for + '" + query + "': " + error);
			if (client == null) {
				throw new CommandException(error.ToString(), CommandExceptionReason.CommandError);
			}
		}

		private static string AddUrl(
			PlaylistManager playlistManager,
			PlayManager playManager,
			ExecutionInformation info,
			Uid uid,
			ResolveContext resolver,
			ClientCall cc,
			Ts3Client ts3Client,
			TsFullClient ts3FullClient,
			string url,
			string target = null,
			bool silent = false,
			Dictionary<string, string> additionalData = null) {
			
			R<PlayResource, LocalStr> resource;
			if (silent) {
				resource = resolver.Load(url);
				playManager.Enqueue(resource.UnwrapThrow().BaseData, new MetaData(uid)).UnwrapThrow();
				return ComposeAddMessage(playManager);
			}

			if (cc == null) {
				throw new CommandException(
					"Tried to call AddURL with invalid parameter combination 'cc == null' and 'silent == false'.",
					CommandExceptionReason.CommandError
				);
			}

			resource = resolver.Load(url);
			if (!resource.Ok) {
				SendAddFailure(ts3Client, url, resource.Error, cc);
			}

			var res = playManager.Enqueue(resource.Value.BaseData, new MetaData(uid));
			if (res.Ok) {
				PrintAddMessage(ts3Client, cc, playManager);
			} else {
				SendAddFailure(ts3Client, url, res.Error, cc);
			}

			// Only reached of not silent.
			return null;
		}

		private static string AddQuery(
			PlaylistManager playlistManager,
			PlayManager playManager,
			ExecutionInformation info,
			Uid uid,
			ResolveContext resolver,
			ClientCall cc,
			Ts3Client ts3Client,
			TsFullClient ts3FullClient,
			string query,
			string target = null,
			bool silent = false,
			Dictionary<string, string> additionalData = null) {

			IList<AudioResource> result;
			if (silent) {
				result = resolver.Search("youtube", query).UnwrapThrow();
			} else {
				if (cc == null) {
					throw new CommandException(
						"Tried to call AddQuery with invalid parameter combination 'cc == null' and 'silent == false'.",
						CommandExceptionReason.CommandError
					);
				}

				var r = resolver.Search("youtube", query);
				if (!r.Ok) {
					SendAddFailure(ts3Client, query, r.Error, cc);
					return null;
				}
				result = r.Value;
			}

			if (result.Count == 0) {
				SendAddFailure(ts3Client, query, new LocalStr($"Youtube returned no results for query '{query}'"), cc);
				return null;
			}

			AudioResource audioResource = result[0];
			if (silent) {
				playManager.Enqueue(audioResource, new MetaData(uid)).UnwrapThrow();
				return ComposeAddMessage(playManager);
			}

			var res = playManager.Enqueue(audioResource, new MetaData(uid));
			if (res.Ok) {
				PrintAddMessage(ts3Client, cc, playManager);
			} else {
				SendAddFailure(ts3Client, query, res.Error, cc);
			}

			// Only reached if not silent
			return null;
		}

		public class LoggingHandler : DelegatingHandler {
			public LoggingHandler(HttpMessageHandler innerHandler)
				: base(innerHandler) { }

			protected override async Task<HttpResponseMessage> SendAsync(
				HttpRequestMessage request, CancellationToken cancellationToken) {
				Console.WriteLine("Request:");
				Console.WriteLine(request.ToString());
				if (request.Content != null) {
					Console.WriteLine(await request.Content.ReadAsStringAsync());
				}

				Console.WriteLine();

				HttpResponseMessage response = await base.SendAsync(request, cancellationToken);

				Console.WriteLine("Response:");
				Console.WriteLine(response.ToString());
				if (response.Content != null) {
					Console.WriteLine(await response.Content.ReadAsStringAsync());
				}

				Console.WriteLine();

				return response;
			}
		}

		[Command("list add")]
		public static void CommandListAdd(
			PlayManager playManager,
			PlaylistManager playlistManager,
			ExecutionInformation execInfo,
			ResolveContext resolver,
			InvokerData invoker,
			ClientCall cc,
			Ts3Client ts3Client,
			TsFullClient ts3FullClient,
			string userProvidedId,
			string message) {
			if(!playlistManager.TryGetPlaylistId(userProvidedId, out var id))
				throw new CommandException($"The playlist {userProvidedId} does not exist.", CommandExceptionReason.CommandError);

			string[] parts = Regex.Split(message, ";+");
			ClientUtility.SendMessage(ts3Client, cc,
				"Received your request to add " + parts.Length + " songs to the playlist '" + id +
				"', processing...");

			PartHandler urlHandler = ListAddUrl;
			PartHandler queryHandler = ListAddQuery;
			ParseMulti(urlHandler, queryHandler, playManager, playlistManager, execInfo, resolver, invoker, cc,
				ts3Client, ts3FullClient, parts, id);
		}
		
		[Command("list createwithuid")]
		public static void CommandListCreateWithUid(PlaylistManager playlistManager, TsFullClient ts3FullClient, string uidStr, string listId) {
			// Check if user exists, throws exception if not.
			Uid uid = Uid.To(uidStr);
			ClientUtility.GetClientNameFromUid(ts3FullClient, uid);
			MainCommands.CommandListCreate(playlistManager, new InvokerData(uid), listId);
		}
		
		[Command("list addwithuid")]
		public static void CommandListAddWithUid(
			PlayManager playManager,
			PlaylistManager playlistManager,
			ExecutionInformation execInfo,
			ResolveContext resolver,
			Ts3Client ts3Client,
			TsFullClient ts3FullClient,
			string uidStr,
			string userProvidedId,
			string message) {

			// Check if user exists, throws exception if not.
			Uid uid = Uid.To(uidStr);
			ClientUtility.GetClientNameFromUid(ts3FullClient, uid);
			CommandListAdd(playManager, playlistManager, execInfo, resolver, new InvokerData(uid), null, ts3Client, ts3FullClient, userProvidedId, message);
		}

		private static string ListAddUrl(
			PlaylistManager playlistManager,
			PlayManager playManager,
			ExecutionInformation info,
			Uid uid,
			ResolveContext resolver,
			ClientCall cc,
			Ts3Client ts3Client,
			TsFullClient ts3FullClient,
			string url,
			string target = null,
			bool silent = false,
			Dictionary<string, string> additionalData = null) {

			int index;
			string title;
			try {
				var playResource = resolver.Load(url).UnwrapThrow();
				title = playResource.BaseData.ResourceTitle;
				if (additionalData != null) {
					foreach (var (key, value) in additionalData) {
						playResource.BaseData.AdditionalData.TryAdd(key, value);
					}
				}

				var r = MainCommands.ListAddItem(playlistManager, info, target, playResource.BaseData);
				if (!r.Ok) {
					ClientUtility.SendMessage(ts3Client, cc, "Error occured for '" + url + "': Already contained in the playlist");
					return null;
				}

				index = r.Value;
			} catch (CommandException e) {
				ClientUtility.SendMessage(ts3Client, cc, "Error occured for '" + url + "': " + e.Message);
				return null;
			}

			ClientUtility.SendMessage(ts3Client, cc,
				"Added '" + title +
				"' to playlist '" + target +
				"' at position " + index
			);
			return null;
		}

		internal static string ListAddQuery(
			PlaylistManager playlistManager,
			PlayManager playManager,
			ExecutionInformation info,
			Uid uid,
			ResolveContext resolver,
			ClientCall cc,
			Ts3Client ts3Client,
			TsFullClient ts3FullClient,
			string query,
			string target = null,
			bool silent = false,
			Dictionary<string, string> additionalData = null) {

			var result = resolver.Search("youtube", query);
			if (!result.Ok) {
				SendAddFailure(ts3Client, query, result.Error, cc);
				return null;
			}

			AudioResource audioResource = result.Value[0];
			if (additionalData != null) {
				foreach (var (key, value) in additionalData) {
					audioResource.AdditionalData.TryAdd(key, value);
				}
			}
			
			int index;
			try {
				var r = MainCommands.ListAddItem(playlistManager, info, target, audioResource);
				if (!r.Ok) {
					ClientUtility.SendMessage(ts3Client, cc, "Error occured for '" + query + "': Already contained in the playlist");
					if (cc == null) {
						throw new CommandException("Song already is in the playlist.", CommandExceptionReason.CommandError);
					}
					return null;
				}

				index = r.Value;
			} catch (CommandException e) {
				ClientUtility.SendMessage(ts3Client, cc, "Error occured for '" + query + "': " + e.Message);
				if (cc == null) {
					throw new CommandException(e.Message, CommandExceptionReason.CommandError);
				}
				return null;
			}

			ClientUtility.SendMessage(ts3Client, cc,
				"Added '" + audioResource.ResourceTitle +
				"' for your request '" + query +
				"' to playlist '" + target +
				"' at position " + index
			);
			return null;
		}

		private static SortedSet<int> ParseIndicesInBounds(string indicesString, int lower, int upper) {
			SortedSet<int> indices = new SortedSet<int>();
			if (upper < lower)
				return indices;
			string[] parts = Regex.Split(indicesString, ",+");
			foreach (string part in parts) {
				if (part.Length == 0) {
					continue;
				}

				var result = Regex.Match(part, "^(\\d+)-(\\d+)$");
				int start;
				int end;
				if (result.Success) {
					// Range, parse it
					start = int.Parse(result.Groups[1].Value);
					end = int.Parse(result.Groups[2].Value);
				} else if (int.TryParse(part, out int index)) {
					start = end = index;
				} else {
					throw new CommandException("Invalid index: " + part, CommandExceptionReason.CommandError);
				}

				if (end < start) {
					throw new CommandException("Given range is invalid: " + start + "-" + end,
						CommandExceptionReason.CommandError);
				}

				if (upper < end) {
					throw new CommandException("The given index is too big: " + end + " (max " + upper + ")",
						CommandExceptionReason.CommandError);
				}

				if (start < lower) {
					throw new CommandException("The given index is too small: " + start + " (min " + lower + ")",
						CommandExceptionReason.CommandError);
				}

				for (int i = start; i <= end; i++) {
					indices.Add(i);
				}
			}

			return indices;
		}

		private static SortedSet<int> ParseAndMap(PlayQueue playQueue, string indicesString) {
			return new SortedSet<int>(ParseIndicesInBounds(indicesString, 1, playQueue.Items.Count - playQueue.Index - 1).Select(entry => entry + playQueue.Index));
		}
		
		[Command("delwithuid")]
		public static string CommandDeleteWithUid(PlayManager playManager, CallerInfo ci, ExecutionInformation info, TsFullClient ts3FullClient, string uidStr, string idList) {
			// Check if user exists, throws exception if not.
			Uid uid = Uid.To(uidStr);
			ClientUtility.GetClientNameFromUid(ts3FullClient, uid);
			return CommandDeleteInternal(playManager, ci, info, uid, idList);
		}

		[Command("del")]
		public static string CommandDelete(PlayManager playManager, CallerInfo ci, ExecutionInformation info, InvokerData invoker, string idList) {
			return CommandDeleteInternal(playManager, ci, info, invoker.ClientUid, idList);
		}
	
		private static string CommandDeleteInternal(PlayManager playManager, CallerInfo ci, ExecutionInformation info, Uid uid, string idList) {
			if (uid.ToString() == "Anonymous") {
				throw new CommandException("You can't delete songs as anonymous user.", CommandExceptionReason.Unauthorized);
			}
			
			var queue = playManager.Queue;
			var currentSongIndex = queue.Index;

			// Parse id list and map to real ids. Throws CommandException when one does not exist or there is a syntax error.
			// Set because duplicates should be ignored

			List<(int, string)> succeeded = new List<(int, string)>();
			List<(int, string)> failed = new List<(int, string)>();
			lock (playManager.Lock) {
				SortedSet<int> indices = ParseAndMap(queue, idList);
				
				foreach (int index in indices.Reverse()) {
					QueueItem item = queue.Items[index];
					if (uid == item.MetaData.ResourceOwnerUid || (!ci.ApiCall && info.HasRights(RightDeleteOther))) {
						queue.Remove(index);
						succeeded.Add((index - currentSongIndex, item.AudioResource.ResourceTitle));
					} else {
						failed.Add((index - currentSongIndex, item.AudioResource.ResourceTitle));
					}
				}

				playManager.OnQueueChanged(indices.Min);
			}

			succeeded.Reverse();
			failed.Reverse();

			StringBuilder output = new StringBuilder();
			if (succeeded.Count > 0) {
				output.Append("Removed the following songs:");
				foreach ((int index, string title) in succeeded) {
					output.Append('\n');
					output.Append('[').Append(index).Append("] ").Append(title);
				}
			}

			if (failed.Count > 0) {
				if (succeeded.Count > 0)
					output.Append('\n');
				output.Append("Failed to remove the following songs:");
				foreach ((int index, string title) in failed) {
					output.Append('\n');
					output.Append('[').Append(index).Append("] ").Append(title);
				}
			}

			return output.ToString();
		}

		public class QueueItemInfo {
			[JsonProperty(PropertyName = "Title")] public string Title { get; set; }

			[JsonProperty(PropertyName = "UserId")]
			public string UserId { get; set; }
			
			[JsonProperty(PropertyName = "UserName")]
			public string UserName { get; set; }

			[JsonProperty(PropertyName = "ContainingListId")]
			public string ContainingListId { get; set; }
			
			[JsonProperty(PropertyName = "ResourceId")]
			public string ResourceId { get; set; }
		}

		public class CurrentQueueInfo {
			[JsonProperty(PropertyName = "Current")]
			public QueueItemInfo Current { get; set; }

			[JsonProperty(PropertyName = "Items")] public List<QueueItemInfo> Items { get; set; }
		}

		private void AppendSong(StringBuilder target, QueueItemInfo qi, bool restrict) {
			target.Append(restrict ? "Hidden Song Name" : qi.Title);
			target.Append(" - ").Append(ClientUtility.GetClientNameFromUid(ts3FullClient, Uid.To(qi.UserId)));

			if (qi.ContainingListId != null)
				target.Append(" <Playlist: ").Append(qi.ContainingListId).Append(">");
		}

		private string QueueInfoToString(CurrentQueueInfo queueInfo) {
			if (queueInfo.Current == null) {
				return "There is nothing on right now...";
			}

			var output = new StringBuilder();
			if (queueInfo.Current != null) {
				output.Append("Current song: ");
				AppendSong(output, queueInfo.Current, false);
			}

			for (var index = 0; index < queueInfo.Items.Count; index++) {
				var item = queueInfo.Items[index];
				output.AppendLine();
				output.Append('[').Append(index + 1).Append("] ");
				AppendSong(output, item, item.Title == null);
			}

			return output.ToString();
		}

		private QueueItemInfo ToQueueItemInfo(QueueItem qi, bool restrict) {
			return new QueueItemInfo {
				ContainingListId = qi.MetaData.ContainingPlaylistId,
				Title = restrict ? null : qi.AudioResource.ResourceTitle,
				UserId = qi.MetaData.ResourceOwnerUid.GetValueOrDefault(Uid.Null).Value,
				UserName = ClientUtility.GetClientNameFromUid(ts3FullClient, qi.MetaData.ResourceOwnerUid.GetValueOrDefault(Uid.Null)),
				ResourceId = qi.AudioResource.ResourceId
			};
		}

		[Command("queuewithuid")]
		public JsonValue<CurrentQueueInfo> CommandQueueWithUid(
			ExecutionInformation info,
			string uidStr) {

			// If the uid is garbage, the queue will be completely hidden.
			return CommandQueueInternal(Uid.To(uidStr), info);
		}

		[Command("queue")]
		public JsonValue<CurrentQueueInfo> CommandQueue(
			InvokerData invoker,
			ExecutionInformation info,
			string arg = null) {
			return CommandQueueInternal(invoker.ClientUid, info, arg);
		}

		public JsonValue<CurrentQueueInfo> CommandQueueInternal(Uid uid, ExecutionInformation info = null, string arg = null) {
			bool hideSongsOfOthers = arg != "full";
			if (!hideSongsOfOthers && (info == null || !info.HasRights(RightOverrideQueueCommandCheck)))
				throw new CommandException("You have no permission to view the full queue.",
					CommandExceptionReason.CommandError);
			var queueInfo = new CurrentQueueInfo();
			lock (playManager) {
				bool ShouldRestrict(QueueItem qi) => hideSongsOfOthers && qi.MetaData.ResourceOwnerUid != uid;
				queueInfo.Items = playManager.Queue.Items.Skip(playManager.Queue.Index + 1)
					.Select(qi => ToQueueItemInfo(qi, ShouldRestrict(qi))).ToList();
				if (playManager.IsPlaying)
					queueInfo.Current = ToQueueItemInfo(playManager.Queue.Current, false);
			}

			return new JsonValue<CurrentQueueInfo>(queueInfo, QueueInfoToString);
		}

		[Command("recentlyplayed")]
		public JsonArray<QueueItemInfo> CommandRecentlyPlayed(
			PlayManager playManager, int? count) {
			List<QueueItemInfo> items;
			lock (playManager) {
				int start = Math.Max(0, playManager.Queue.Index - count ?? 5);
				var take = playManager.Queue.Index - start;
				items = playManager.Queue.Items.Skip(start).Take(take).Select(qi => ToQueueItemInfo(qi, false))
					.ToList();
			}

			return new JsonArray<QueueItemInfo>(items, infos => {
				var builder = new StringBuilder();
				for (int i = 0; i < infos.Count; ++i) {
					if (i != 0)
						builder.AppendLine();
					builder.Append("[-").Append(infos.Count - i).Append("] ");
					AppendSong(builder, infos[i], false);
				}

				return builder.ToString();
			});
		}

		[Command("skip")]
		public static string CommandSkip(
			PlayManager playManager,
			ExecutionInformation info,
			InvokerData invoker,
			int? countOpt = null) {
			var queue = playManager.Queue;

			int count;
			lock (playManager.Lock) {
				if (!playManager.IsPlaying)
					throw new CommandException("There is no song currently playing.",
						CommandExceptionReason.CommandError);
				count = Tools.Clamp(countOpt.GetValueOrDefault(1), 0, queue.Items.Count - queue.Index);
				for (int i = 0; i < count; i++) {
					if (invoker.ClientUid != queue.Items[queue.Index].MetaData.ResourceOwnerUid &&
					    !info.HasRights(RightSkipOther)) {
						throw new CommandException($"You have no permission to skip song {i}.",
							CommandExceptionReason.CommandError);
					}
				}

				playManager.Next(count).UnwrapThrow();
			}

			return $"Skipped {count} songs.";
		}

		[Command("front")]
		public static string CommandYoutubeFront(
			PlayManager playManager,
			PlaylistManager playlistManager,
			ExecutionInformation execInfo,
			ResolveContext resolver,
			InvokerData invoker,
			ClientCall cc,
			Ts3Client ts3Client,
			TsFullClient ts3FullClient,
			string message) {
			// If the current song is the last in the queue, add normally
			var queue = playManager.Queue;

			if (queue.Index == queue.Items.Count || queue.Index == queue.Items.Count - 1) {
				CommandYoutube(playManager, playlistManager, execInfo, resolver, invoker, cc, ts3Client, ts3FullClient,
					message);
				return null;
			}

			// The index of front is outside of the queue, reject
			if (queue.Index + 1 >= queue.Items.Count) {
				// is that even possible?
				throw new CommandException("The index of the front would be outside of the queue!",
					CommandExceptionReason.CommandError);
			}

			AudioResource resource;

			// Check if URL
			var query = message.Replace("[URL]", "").Replace("[/URL]", "").Trim(' ');
			if (Regex.Match(query, YOUTUBE_URL_REGEX).Success) {
				resource = resolver.Load(query, "youtube").UnwrapThrow().BaseData;
			} else {
				var result = resolver.Search("youtube", query).UnwrapThrow();
				resource = result[0];
			}

			MetaData meta = new MetaData(invoker.ClientUid);
			playManager.EnqueueAsNextSong(new QueueItem(resource, meta));
			return "Added '" + resource.ResourceTitle + "' to the front of the queue.";
		}

		[Command("list item queue")]
		public static string CommandItemQueue(
			InvokerData invoker, PlaylistManager playlistManager, PlayManager playManager, string userProvidedId,
			string indicesString, string uid = null) {
			var (plist, id) = playlistManager.GetPlaylist(userProvidedId).UnwrapThrow();
			var indices = ParseIndicesInBounds(indicesString, 0, plist.Count - 1);
			var items = new List<AudioResource>(indices.Count);
			items.AddRange(indices.Select(index => plist[index]));

			playManager.Enqueue(items,
				new MetaData(uid != null ? Uid.To(uid) : invoker.ClientUid, id)).UnwrapThrow();

			if (indices.Count == 1) {
				return $"Queued '{items[0].ResourceTitle}' from playlist {id}.";
			}

			return $"Queued {items.Count} items from playlist {id}.";
		}
		
		[Command("list item front")]
		public static string CommandItemFront(
			InvokerData invoker, PlaylistManager playlistManager, PlayManager playManager, 
			string userProvidedId, string indicesString, string uid = null) {

			var (plist, id) = playlistManager.GetPlaylist(userProvidedId).UnwrapThrow();
			var indices = ParseIndicesInBounds(indicesString, 0, plist.Count - 1);

			if (indices.Count != 1) {
				throw new CommandException("Only single index is allowed for fronting.", CommandExceptionReason.CommandError);
			}

			var resource = plist[indices.First()];
			playManager.EnqueueAsNextSong(new QueueItem(resource, new MetaData(uid != null ? Uid.To(uid) : invoker.ClientUid, id)));
			return "Added '" + resource.ResourceTitle + "' to the front of the queue.";
		}
		
		[Command("list item replaceurl")]
		public static string ListItemReplaceUrlCommand(
			PlaylistManager playlistManager,
			ExecutionInformation info,
			ResolveContext resolver,
			string listId,
			int index,
			string url,
			string[] args = null) {
			var additionalArgs = args ?? Array.Empty<string>();
			
			string id;
			AudioResource resource;

			{
				var (list, s) = playlistManager.GetPlaylist(listId).UnwrapThrow();
				MainCommands.DoBoundsCheck(list, index);
				id = s;
				resource = list[index];
				if (resource.AudioType != "youtube")
					throw new CommandException("You can't replace the URL of a non-youtube entry.",
						CommandExceptionReason.CommandError);
			}

			AudioResource newResource;
			{
				var newRes = resolver.Load(url).UnwrapThrow().BaseData;
				newResource = new AudioResource(
					newRes.ResourceId,
					resource.TitleIsUserSet != null && resource.TitleIsUserSet.Value
						? resource.ResourceTitle
						: newRes.ResourceTitle,
					newRes.AudioType,
					resource.AdditionalData,
					resource.TitleIsUserSet
				);
			}

			if (additionalArgs.Contains("--all")) {
				if (!playlistManager.TryGetUniqueResourceInfo(resource, out var resourceInfo))
					throw new CommandException("Could not find the list item", CommandExceptionReason.InternalError);

				var builder = new StringBuilder();
				builder.AppendJoin(", ", resourceInfo.ContainingLists.Select(kv => '\'' + kv.Key + '\''));

				// Check all lists for modifiable
				foreach (var occKv in resourceInfo.ContainingLists) {
					var (list, lid) = playlistManager.GetPlaylist(occKv.Key).UnwrapThrow();
					MainCommands.CheckPlaylistModifiable(lid, list, info);
				}

				playlistManager.ChangeItemAtDeep(id, index, newResource).UnwrapThrow();
				return $"Successfully replaced the youtube URL of '{resource.ResourceTitle}' in playlists {builder}.";
			} else {
				bool replaced = false;
				MainCommands.ModifyPlaylist(playlistManager, id, info, editor => {
					replaced = editor.ChangeItemAt(index, newResource);
					if(!replaced)
						editor.RemoveItemAt(index);
				}).UnwrapThrow();
				if(!replaced)
					return $"This song already existed in the playlist '{id}', removed the entry to replace.";
				return $"Successfully replaced the youtube URL of '{resource.ResourceTitle}' in playlist '{id}' at index {index}.";
			}
		}

		[Command("checkuser online byuid")]
		public static bool CommandCheckUserOnlineByUid(TsFullClient ts3FullClient, string uid) {
			return ClientUtility.ClientIsOnline(ts3FullClient, Uid.To(uid));
		}

		[Command("autofillstatus")]
		public JsonValue<AutofillStatus> CommandAutofillStatus() { return Autofill.Status(); }

		[Command("autofilloff")]
		public void CommandAutofillOff(InvokerData invoker) {
			Autofill.Disable(invoker.ClientUid);
		}

		[Command("autofilloffwithuid")]
		public void CommandAutofillOffWithUid(string uidStr) {
			var uid = Uid.To(uidStr);
			Autofill.Disable(uid);
		}

		[Command("autofill")]
		public void CommandAutofill(InvokerData invoker, string[] playlistIds = null) {
			if (invoker.IsAnonymous) {
				throw new CommandException("An anonymous user can't enable autofill", CommandExceptionReason.CommandError);
			}
			Autofill.CommandAutofill(invoker.ClientUid, playlistIds);
		}

		[Command("autofillwithuid")]
		public void CommandAutofillWithUid(string uidStr, string[] playlistIds = null) {
			var uid = Uid.To(uidStr);
			Autofill.CommandAutofill(uid, playlistIds);
		}

		private static void ThrowNotInSameChannel() {
			throw new CommandException("You have to be in the same channel as the bot to use votes.", CommandExceptionReason.CommandError);
		}

		[Command("skiporvoteskipwithuid")]
		public JsonValue<Voting.Result> CommandStartVoteWithUid(ExecutionInformation info, string clientUid) {
			var uid = Uid.To(clientUid);

			lock (playManager.Lock) {
				var current = playManager.Queue.Current;
				if (current != null && current.MetaData.ResourceOwnerUid == uid) {
					playManager.Next();
					ts3Client.SendChannelMessage($"{ClientUtility.GetClientNameFromUid(ts3FullClient, uid)} skipped the current song.");
					return new JsonValue<Voting.Result>(new Voting.Result {
						VoteAdded = true,
						VoteComplete = true,
						VoteCount = 1,
						VotesChanged = true,
						VotesNeeded = 1
					});
				}
			}
			
			return CommandStartVoteWithUid(info, uid.Value, "skip", null);
		}

		[Command("votewithuid")]
		public JsonValue<Voting.Result> CommandStartVoteWithUid(
			ExecutionInformation info,
			string clientUid, string command, string? args = null) {
			var uid = Uid.To(clientUid);

			// var botChannel = ts3FullClient.Book.CurrentChannel().Id;
			// var hasClientWithUidInBotChannel = ClientUtility.GetClientsByUidOnline(ts3FullClient, uid).Any(c => botChannel == c.Channel);
			// if (!hasClientWithUidInBotChannel)
				// ThrowNotInSameChannel();

			return CommandStartVote(info, uid, command, args);
		}

		[Command("vote")]
		public JsonValue<Voting.Result> CommandStartVote(ExecutionInformation info, ClientCall invoker, string command, string? args = null) {
			if (!invoker.ChannelId.HasValue)
				throw new CommandException("Could not get user channel", CommandExceptionReason.InternalError);
			// var botChannel = ts3FullClient.Book.CurrentChannel().Id;
			// if (botChannel != invoker.ChannelId.Value)
				// ThrowNotInSameChannel();

			return CommandStartVote(info, invoker.ClientUid, command, args);
		}

		private JsonValue<Voting.Result> CommandStartVote(ExecutionInformation info, Uid client, string command, string args) {
			var res = Voting.CommandVote(info, client, command, args);
			return new JsonValue<Voting.Result>(res, r => null);
		}

		[Command("twitchinfo")]
		public JsonValue<TwitchInfo> CommandTwitchInfo() {
			if (TwitchInfoUpdater == null) {
				throw new CommandException("No twitch stream currently running.", CommandExceptionReason.MissingContext);
			}

			if (playManager == null) {
				throw new CommandException("Missing playManager for some reason.", CommandExceptionReason.MissingContext);
			}

			string issuerName;
			Uid? issuerUid;
			lock (playManager) {
				if (playManager.CurrentPlayData == null) {
					throw new CommandException("Missing CurrentPlayData for some reason.",
						CommandExceptionReason.MissingContext);
				}

				if (playManager.CurrentPlayData.MetaData == null) {
					throw new CommandException("Missing MetaData for some reason.",
						CommandExceptionReason.MissingContext);
				}

				issuerName = null;
				issuerUid = playManager.CurrentPlayData.MetaData.ResourceOwnerUid;
				if (issuerUid != null) {
					issuerName = ClientUtility.GetClientNameFromUid(ts3FullClient, issuerUid.Value);
				}
			}

			var streamInfo = TwitchInfoUpdater?.StreamInfo?.Data[0];
			var streamerInfo = TwitchInfoUpdater?.StreamerInfo?.Data[0];

			if (streamInfo == null) {
				throw new CommandException("Missing StreamInfo for some reason.", CommandExceptionReason.MissingContext);
			}
			
			if (streamerInfo == null) {
				throw new CommandException("Missing StreamerInfo for some reason.", CommandExceptionReason.MissingContext);
			}
			
			return JsonValue.Create(new TwitchInfo {
				ViewerCount = streamInfo.ViewerCount,
				Uptime = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - streamInfo.StartedAt.ToUnix(),
				ThumbnailUrl = streamInfo.ThumbnailUrl,
				AvatarUrl = streamerInfo.ProfileImageUrl.ToString(),
				StreamerName = streamerInfo.DisplayName,
				StreamerLogin = streamerInfo.Login,
				StreamTitle = streamInfo.Title,
				IssuerUid = issuerUid.ToString(),
				IssuerName = issuerName,
			}, info => "[" + info.IssuerName + " " + info.Uptime + "] - " + info.StreamTitle + " (" + info.ViewerCount + " viewers) - https://twitch.tv/" + info.StreamerLogin);
		}
		
		[Command("listeners")]
		public static JsonValue<Dictionary<string, IList<(string, string)>>> CommandListeners(Ts3Client ts3Client, TsFullClient ts3FullClient, Player player) {
			var channelListeners = ClientUtility.GetListeningClients(ts3Client, ts3FullClient)
				.Where(client => !player.WebSocketPipe.Listeners.Contains(client.Uid.ToString()))
				.Select(client => (client.Uid.ToString(), ClientUtility.GetClientNameFromUid(ts3FullClient, client.Uid)))
				.ToList();

			var websocketListeners = player.WebSocketPipe.Listeners.Select(
				listener => (listener, ClientUtility.GetClientNameFromUid(ts3FullClient, Uid.To(listener)))
			).ToList();

			return JsonValue.Create(new Dictionary<string, IList<(string, string)>> {
				{ "channel", channelListeners }	,
				{ "websocket", websocketListeners }
			}, data => $"Via Website: {string.Join(", ", data["websocket"].Select(value => value.Item2).ToList())}\nIn Channel: {string.Join(", ", data["channel"].Select(value => value.Item2).ToList())}");
		}

		[Command("randomsongs")]
		public static JsonArray<SongRandomizerResult> CommandRandomSongs(PlaylistManager playlistManager, int count, string[] playlists = null) {
			if (count <= 0) {
				throw new CommandException("You cannot randomize 0 or less songs.", CommandExceptionReason.CommandError);
			}

			var playlistsHashMap = playlists != null && playlists.Length != 0 ? new HashSet<string>(playlists) : null;
			var randomSongs = SongRandomizer.GetRandomSongs(count, playlistManager, playlistsHashMap);
			return new JsonArray<SongRandomizerResult>(randomSongs);
		}
		
		[Command("youtubestream")]
		public static string CommandYoutubeStream(PlayManager playManager, ResolveContext resolver, ClientCall cc, string url) {
			var resource = resolver.Load(url).UnwrapThrow();
			var newResource = resource.BaseData.WithGain(0);
			playManager.Enqueue(newResource, new MetaData(cc.ClientUid)).UnwrapThrow();
			return ComposeAddMessage(playManager);
		}

		[Command("songcount")]
		public static string CommandSongCount(PlaylistManager playlistManager) {
			return $"There are currently {playlistManager.Count} items"
			       + $" across {playlistManager.GetAvailablePlaylists().Length} playlists.\n"
			       + $" {playlistManager.UniqueCount} or"
			       + $" {(double) playlistManager.UniqueCount / playlistManager.Count * 100:0.00}% of them are unique.";
		}

		private bool isGainRecalculationCandidate(AudioResource resource, string flagKey) {
			// Gain calculation only for "youtube" tracks
			return resource.AudioType == "youtube" && (
				resource.AdditionalData == null
				|| resource.Gain == null
				|| !resource.AdditionalData.ContainsKey(flagKey)
				|| resource.AdditionalData[flagKey] != "true"
			);
		}

		[Command("recalcgain")]
		public string CommandRecalculateGain(ResolveContext resolver, ClientCall cc) {
			const string flagKey = "fully_analysed";

			// Already running.
			if (recalcGainInfo != null) {
				var analyzed = recalcGainInfo.Successful + recalcGainInfo.Failed;
				var elapsed = recalcGainInfo.Watch.Elapsed;
				var ppm = elapsed.TotalMinutes < 1 ? analyzed : analyzed / elapsed.TotalMinutes;
				var etaString = "N/A";
				try {
					var eta = TimeSpan.FromMinutes((recalcGainInfo.ToProcessEstimate - analyzed) / ppm);
					etaString = $"{eta.Days}d, {eta.Hours:00}h {eta.Minutes:00}m {eta.Seconds:00}s";
				} catch (OverflowException) {
					// Its fine, we have a default string for that.
				}

				return "Gain recalculation already running.\n"
				       + $"{recalcGainInfo.Progress}/{recalcGainInfo.DbSize}"
				       + $" ({(double) recalcGainInfo.Progress / recalcGainInfo.DbSize * 100:0.00}%) scanned,"
				       + $" {analyzed} analysed: {recalcGainInfo.Successful} successful, {recalcGainInfo.Failed} failed.\n"
				       + $" Running since {elapsed.Days}d, {elapsed.Hours:00}h {elapsed.Minutes:00}m {elapsed.Seconds:00}s,"
				       + $" {ppm:0.0} items/m,"
				       + $" ETA {etaString}.";
			}

			// Estimate how many items there are to process (estimation because that can change if an item in a playlist
			// is removed or added).
			var toProcess = 0;
			foreach (var info in playlistManager.GetAvailablePlaylists()) {
				foreach (var entry in playlistManager.GetPlaylist(info.Id).UnwrapThrow().list) {
					if (isGainRecalculationCandidate(entry, flagKey)) {
						toProcess++;
					}
				}
			}

			recalcGainInfo = new RecalcGainInfo(playlistManager.Count, toProcess);
			var thread = new Thread(() => {
				var playlists = playlistManager.GetAvailablePlaylists();
				for (var plIdx = 0; plIdx < playlists.Length; plIdx++) {
					// Get playlist.
					var playlistOption = playlistManager.GetPlaylist(playlists[plIdx].Id);

					if (!playlistOption.Ok) {
						Log.Error($"Failed to fetch playlist {playlists[plIdx].Id}.");
						return;
					}

					// Remember analyze stats from before starting the playlist in order to remove results
					// in case the playlist disappears.
					var toProcessEstimate = playlistOption.Value.list.Count(r => isGainRecalculationCandidate(r, flagKey));
					var previousSuccessfuls = recalcGainInfo.Successful;
					var previousFails = recalcGainInfo.Failed;
					var previousProgress = recalcGainInfo.Progress;
					
					// Find candidate that is not fully analyzed yet.
					for (var resIdx = 0; resIdx < playlistOption.Value.list.Count; resIdx++) {
						var resource = playlistOption.Value.list[resIdx];
						var identifier = $"{playlistOption.Value.id}:{resIdx}:{resource.UniqueId}";

						recalcGainInfo.Progress++;
						
						if (!isGainRecalculationCandidate(resource, flagKey)) {
							continue;
						}

						var newResource = resource.DeepCopy();

						// Add additional data if it does not exist yet.
						if (newResource.AdditionalData == null) {
							newResource = newResource.WithNewAdditionalData();
						}

						// Resolve the resource to an analyzable URL.
						var playResourceOption = resolver.Load(newResource);
						if (!playResourceOption.Ok) {
							Log.Error($"Problem resolving '{identifier}': {playResourceOption.Error}");
							recalcGainInfo.Failed++;
							continue;
						}

						// Analyse the resource fully.
						var gain = player.FfmpegProducer.VolumeDetect(
							playResourceOption.Value.PlayUri,
							new CancellationToken(),
							true
						);
						newResource = newResource.WithGain(gain);
						newResource.AdditionalData[flagKey] = "true";

						// Lock the playlistManager
						lock (playlistManager.Lock) {
							// Re-fetch the playlist in order to check if it still exists.
							var plOpt = playlistManager.GetPlaylist(playlists[plIdx].Id);
							if (!plOpt.Ok) {
								Log.Error($"Playlist {playlists[plIdx].Id} disappeared during analysis. Skipping playlist.");
								
								// Update the DbSize to ignore the disappeared playlist for stats.
								recalcGainInfo.DbSize -= playlists[plIdx].SongCount;
								
								// Update process estimate to ignore the disappeared playlist for stats.
								// Only subtract as many items as there where candidates in the playlist.
								recalcGainInfo.ToProcessEstimate -= toProcessEstimate;
								
								// Update stats by resetting them to the value bofore starting to scan the disappeared playlist.
								recalcGainInfo.Successful = previousSuccessfuls;
								recalcGainInfo.Failed = previousFails;
								recalcGainInfo.Progress = previousProgress;
								
								break;
							}
							
							// Check if there is still the same resource at this index in the playlist.
							if (!plOpt.Value.list[resIdx].Equals(newResource)) {
								Log.Error($"Failed replacing '{identifier}': Resource at index {resIdx} changed during analysis.");
								
								// Trigger that playlist is completely scanned again.
								plIdx--;
								break;
							}
							
							if (!playlistManager.TryGetUniqueResourceInfo(newResource, out var resourceInfo)) {
								Log.Error($"Failed replacing '{identifier}': Could not find unique resource info.");
								recalcGainInfo.Failed++;
								continue;
							}
							
							// Replace the old entry with the new one.
							// Do that for all occurences of this audio resource across all playlists.
							var result = playlistManager.ChangeItemAtDeep(playlists[plIdx].Id, resIdx, newResource);
							if (!result.Ok) {
								Log.Error($"Failed replacing '{identifier}': {result.Error}");
								recalcGainInfo.Failed++;
								return;
							}

							Log.Info(
								$"Finished full analysis of '{identifier}': " +
								$"Changed gain from {resource.Gain ?? 0} dB to {gain} dB, affected the playlists" +
								$" {string.Join(", ", resourceInfo.ContainingLists.Select(kv => '\'' + kv.Key + '\''))}."
							);
							recalcGainInfo.Successful += resourceInfo.ContainingLists.Count();
						}
					}
				}

				recalcGainInfo.Watch.Stop();
				var analyzed = recalcGainInfo.Successful + recalcGainInfo.Failed;
				var elapsed = recalcGainInfo.Watch.Elapsed;
				var msg = "Recalculation of gain values done.\n"
				          + $"{analyzed}/{recalcGainInfo.DbSize}"
				          + $" ({(double) analyzed / recalcGainInfo.DbSize * 100:0.00}%) analysed"
				          + $" (estimation was {recalcGainInfo.ToProcessEstimate}/{recalcGainInfo.DbSize}):"
				          + $" {recalcGainInfo.Successful} successful, {recalcGainInfo.Failed} failed."
				          + $" Recalucation took {elapsed.Days}d, {elapsed.Hours:00}h {elapsed.Minutes:00}m {elapsed.Seconds:00}s.";
				Log.Info(msg);
				ClientUtility.SendMessage(ts3Client, cc, msg);
				recalcGainInfo = null;
			}) {
				IsBackground = true
			};
			thread.Start();

			return $"Started gain recalculation for {recalcGainInfo.ToProcessEstimate}/{recalcGainInfo.DbSize} elements.";
		}

		public class TwitchInfo {
			[JsonProperty("ViewerCount")]
			public long ViewerCount { get; set; }
			
			[JsonProperty("Uptime")]
			public long Uptime { get; set; }

			[JsonProperty("ThumbnailUrl")]
			public string ThumbnailUrl { get; set; }
			
			[JsonProperty("AvatarUrl")]
			public string AvatarUrl { get; set; }

			[JsonProperty("StreamerName")]
			public string StreamerName { get; set; }
			
			[JsonProperty("StreamerLogin")]
			public string StreamerLogin { get; set; }

			[JsonProperty("StreamTitle")]
			public string StreamTitle { get; set; }
			
			[JsonProperty("IssuerUid")]
			public string IssuerUid { get; set; }
			
			[JsonProperty("IssuerName")]
			public string IssuerName { get; set; }
		}

		private class RecalcGainInfo {
			public int DbSize { get; set; }
			public int ToProcessEstimate { get; set; }
			public int Progress { get; set; }
			public int Successful { get; set; }
			public int Failed { get; set; }
			public Stopwatch Watch { get; }

			public RecalcGainInfo(int dbSize, int toProcessEstimate) {
				DbSize = dbSize;
				ToProcessEstimate = toProcessEstimate;
				Watch = Stopwatch.StartNew();
			} 
		}
	}
}