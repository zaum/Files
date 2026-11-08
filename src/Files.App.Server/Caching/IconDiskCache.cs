// Copyright (c) Files Community
// SPDX-License-Identifier: MPL-2.0

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Windows.Storage;

namespace Files.App.Server;

/// <summary>
/// Keeps extracted icons on disk so the first paint after a restart does not have to run the shell
/// extraction for every visible item again. Entries are keyed by path, size and the item's last
/// write time, so an item that changes gets a new key instead of a stale icon.
/// </summary>
internal static class IconDiskCache
{
	// Bounds that keep both the memory and the disk footprint of a long session flat.
	private const int MaxMemoryEntries = 4096;
	private const long MaxDiskBytes = 128L * 1024 * 1024;
	private const long PruneToBytes = 96L * 1024 * 1024;
	private const int MaxEntryBytes = 4 * 1024 * 1024;
	private const int MaxPendingWrites = 64;

	private static readonly ConcurrentDictionary<string, (byte[] Data, long Timestamp)> memory = new();
	private static readonly string? directory;
	private static int initialized;
	private static int pendingWrites;
	private static long hits;
	private static long requests;
	private static long hitTicks;
	private static long missTicks;
	private static long misses;
	private static long extractionTicks;
	private static long extractions;

	static IconDiskCache()
	{
		try
		{
			directory = Path.Combine(ApplicationData.Current.LocalFolder.Path, "iconcache");
		}
		catch (Exception)
		{
			directory = null;
		}
	}

	/// <summary>Returns a previously extracted icon, from memory first and from disk second.</summary>
	internal static bool TryGet(string path, int size, bool isFolder, out byte[] data)
	{
		data = [];

		if (directory is null)
			return false;

		var started = Stopwatch.GetTimestamp();
		var key = GetKey(path, size, isFolder);
		var requestCount = Interlocked.Increment(ref requests);

		if (memory.TryGetValue(key, out var cached))
		{
			data = cached.Data;
			Record(true, started);
			ReportProgress(requestCount);
			return true;
		}

		try
		{
			// Read directly instead of asking whether the file exists: that halves the syscalls on
			// the hot path, and a missing entry simply throws.
			var file = Path.Combine(directory, key + ".bmp");
			data = File.ReadAllBytes(file);
			if (data.Length is 0 or > MaxEntryBytes)
			{
				data = [];
				Record(false, started);
				ReportProgress(requestCount);
				return false;
			}

			memory[key] = (data, Environment.TickCount64);
			TrimMemory();
			Record(true, started);
		}
		catch (Exception)
		{
			data = [];
			Record(false, started);
			ReportProgress(requestCount);
			return false;
		}

		ReportProgress(requestCount);
		return true;
	}

	internal static void RecordExtraction(long startedTimestamp)
	{
		Interlocked.Increment(ref extractions);
		Interlocked.Add(ref extractionTicks, Stopwatch.GetTimestamp() - startedTimestamp);
	}

	private static void Record(bool hit, long started)
	{
		var elapsed = Stopwatch.GetTimestamp() - started;

		if (hit)
		{
			Interlocked.Increment(ref hits);
			Interlocked.Add(ref hitTicks, elapsed);
		}
		else
		{
			Interlocked.Increment(ref misses);
			Interlocked.Add(ref missTicks, elapsed);
		}
	}

	/// <summary>Remembers a freshly extracted icon; the write is best effort and never blocks the caller.</summary>
	internal static void Store(string path, int size, bool isFolder, byte[] data)
	{
		if (directory is null || data.Length is 0 or > MaxEntryBytes)
			return;

		var key = GetKey(path, size, isFolder);
		memory[key] = (data, Environment.TickCount64);
		TrimMemory();
		EnsurePruningStarted();

		if (Interlocked.Increment(ref pendingWrites) > MaxPendingWrites)
		{
			Interlocked.Decrement(ref pendingWrites);
			return;
		}

		_ = Task.Run(() =>
		{
			try
			{
				var file = Path.Combine(directory, key + ".bmp");
				if (File.Exists(file))
					return;

				// Write beside the target and rename, so a half written file is never read back.
				var temp = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
				File.WriteAllBytes(temp, data);
				File.Move(temp, file, true);
			}
			catch (Exception)
			{
			}
			finally
			{
				Interlocked.Decrement(ref pendingWrites);
			}
		});
	}

	private static string GetKey(string path, int size, bool isFolder)
	{
		long stamp = 0;
		try
		{
			stamp = isFolder
				? Directory.GetLastWriteTimeUtc(path).Ticks
				: File.GetLastWriteTimeUtc(path).Ticks;
		}
		catch (Exception)
		{
		}

		var text = $"{path.ToLowerInvariant()}|{size}|{(isFolder ? 'd' : 'f')}|{stamp}";
		var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
		return Convert.ToHexString(hash.AsSpan(0, 16));
	}

	private static void TrimMemory()
	{
		if (memory.Count <= MaxMemoryEntries)
			return;

		// Drop the oldest quarter; icons are cheap to re-read from disk.
		foreach (var entry in memory.OrderBy(x => x.Value.Timestamp).Take(MaxMemoryEntries / 4))
			memory.TryRemove(entry.Key, out _);
	}

	// A single line per session, logged once enough icon requests have been made to be meaningful.
	private static void ReportProgress(long requestCount)
	{
		if (requestCount != 100 && requestCount % 500 != 0)
			return;

		var hitsSoFar = Interlocked.Read(ref hits);
		var missesSoFar = Interlocked.Read(ref misses);
		var extractionsSoFar = Interlocked.Read(ref extractions);
		var hitMilliseconds = Interlocked.Read(ref hitTicks) * 1000d / Stopwatch.Frequency;
		var missMilliseconds = Interlocked.Read(ref missTicks) * 1000d / Stopwatch.Frequency;
		var extractionMilliseconds = Interlocked.Read(ref extractionTicks) * 1000d / Stopwatch.Frequency;

		Program.Logger.LogInformation(
			"Icon cache: {HitCount} of {RequestCount} requests served from the cache (hit {HitMs:F2} ms, lookup {MissMs:F2} ms, shell extraction {ExtractMs:F2} ms on average).",
			hitsSoFar, requestCount,
			hitsSoFar > 0 ? hitMilliseconds / hitsSoFar : 0,
			missesSoFar > 0 ? missMilliseconds / missesSoFar : 0,
			extractionsSoFar > 0 ? extractionMilliseconds / extractionsSoFar : 0);
	}

	private static void EnsurePruningStarted()
	{
		if (Interlocked.Exchange(ref initialized, 1) == 1)
			return;

		_ = Task.Run(Prune);
	}

	private static void Prune()
	{
		try
		{
			var info = new DirectoryInfo(directory!);
			if (!info.Exists)
				info.Create();

			var cutoff = DateTime.UtcNow.AddDays(-30);
			long total = 0;
			var kept = new List<(FileInfo File, long Length)>();
			foreach (var file in info.GetFiles("*.bmp"))
			{
				try
				{
					if (file.LastWriteTimeUtc < cutoff)
					{
						file.Delete();
						continue;
					}

					total += file.Length;
					kept.Add((file, file.Length));
				}
				catch (Exception)
				{
				}
			}

			if (total > MaxDiskBytes)
			{
				foreach (var entry in kept.OrderBy(x => x.File.LastWriteTimeUtc))
				{
					if (total <= PruneToBytes)
						break;

					try
					{
						entry.File.Delete();
						total -= entry.Length;
					}
					catch (Exception)
					{
					}
				}
			}

			// Leftovers of an interrupted write
			var stale = DateTime.UtcNow.AddHours(-1);
			foreach (var temp in info.GetFiles("*.tmp"))
			{
				try
				{
					if (temp.LastWriteTimeUtc < stale)
						temp.Delete();
				}
				catch (Exception)
				{
				}
			}

			Program.Logger.LogInformation(
				"Icon cache: {FileCount} icons, {TotalKilobytes} KB on disk.",
				kept.Count, total / 1024);
		}
		catch (Exception)
		{
		}
	}
}
