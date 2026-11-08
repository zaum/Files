// Copyright (c) Files Community
// SPDX-License-Identifier: MPL-2.0

namespace Files.App.Utils.Storage
{
	/// <summary>
	/// Remembers the listing of recently visited folders so returning to one paints its items
	/// immediately. The enumeration that follows always replaces the cached items: the cache only
	/// shortens the wait, it never decides what a folder contains.
	/// </summary>
	internal static class FolderListingCache
	{
		// An entry older than this is dropped, so a folder that changed while the user was
		// elsewhere cannot be shown for long.
		private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(5);

		// Bounds that keep the memory use of a long browsing session flat.
		private const int MaxEntries = 8;
		private const int MaxTotalItems = 20000;

		private sealed class Entry
		{
			public required List<ListedItem> Items { get; init; }
			public required long Timestamp { get; init; }
		}

		private static readonly object syncRoot = new();
		private static readonly Dictionary<string, Entry> entries = new(StringComparer.OrdinalIgnoreCase);
		private static int totalItems;

		/// <summary>
		/// Only real file system folders are cached; shell namespaces, portable devices, FTP
		/// servers and the recycle bin are either cheap to enumerate or change too often.
		/// </summary>
		public static bool IsCacheable(string? path)
		{
			if (string.IsNullOrEmpty(path))
				return false;

			if (path.StartsWith(@"\\SHELL\", StringComparison.OrdinalIgnoreCase))
				return false;

			if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
				return false;

			if (path.StartsWith("Shell:", StringComparison.OrdinalIgnoreCase))
				return false;

			if (path.Contains("://", StringComparison.Ordinal))
				return false;

			return true;
		}

		public static bool TryGet(string path, out List<ListedItem> items)
		{
			items = [];

			lock (syncRoot)
			{
				if (!entries.TryGetValue(path, out var entry))
					return false;

				if (Environment.TickCount64 - entry.Timestamp > (long)MaxAge.TotalMilliseconds)
				{
					Remove(path, entry);
					return false;
				}

				items = entry.Items;

				return true;
			}
		}

		public static void Set(string path, List<ListedItem> items)
		{
			if (items.Count is 0 || items.Count > MaxTotalItems)
				return;

			lock (syncRoot)
			{
				if (entries.TryGetValue(path, out var previous))
					Remove(path, previous);

				entries[path] = new Entry { Items = items, Timestamp = Environment.TickCount64 };
				totalItems += items.Count;

				// Drop the oldest entries until both bounds hold again.
				while (entries.Count > MaxEntries || totalItems > MaxTotalItems)
				{
					var oldest = entries.OrderBy(x => x.Value.Timestamp).First();
					Remove(oldest.Key, oldest.Value);
				}
			}
		}

		public static void Invalidate(string? path)
		{
			if (string.IsNullOrEmpty(path))
				return;

			lock (syncRoot)
			{
				if (entries.TryGetValue(path, out var entry))
					Remove(path, entry);
			}
		}

		private static void Remove(string path, Entry entry)
		{
			entries.Remove(path);
			totalItems -= entry.Items.Count;
		}
	}
}
