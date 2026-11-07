// Copyright (c) Files Community
// Licensed under the MIT License.

using System.Collections.Concurrent;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Storage.Streams;

namespace Files.App.Server;

internal static class IconCache
{
	private static readonly ConcurrentDictionary<(string Extension, bool Folder, int Size), CachedTask<byte[]>> generic = new();

	internal static async Task<IBuffer> GetGenericAsync(string extension, bool folder, int size)
	{
		ValidateSize(size);
		if (generic.Count > 512)
			generic.Clear();
		var key = (extension.ToLowerInvariant(), folder, size);
		var cache = generic.GetOrAdd(key, _ => new CachedTask<byte[]>(async () =>
		{
			var path = folder ? Environment.SystemDirectory : Path.Combine(Path.GetPathRoot(Environment.SystemDirectory)!, "x46696c6573") + extension;
			return await STATask.RunPooled(() => ShellImage.GetIcon(path, size, folder, IconOptions.ReturnIconOnly), Program.Logger, priority: true) ?? [];
		}, TimeSpan.FromMinutes(5)));
		return (await cache.GetAsync()).AsBuffer();
	}

	internal static async Task<IBuffer> GetAsync(string path, int size, bool folder, uint options, bool overlay)
	{
		ValidateSize(size);
		// Icons and cache-only thumbnail lookups are needed for the current folder's first paint, so they
		// take the priority pool; generating a brand new thumbnail can wait behind earlier work.
		// Overlays are decorative badges drawn on top of an icon that is already visible, so they wait too.
		var iconOptions = (IconOptions)options;
		var priority = !overlay
			&& (iconOptions.HasFlag(IconOptions.ReturnIconOnly)
			|| iconOptions.HasFlag(IconOptions.ReturnOnlyIfCached));

		var data = await STATask.RunPooled(() =>
		{
			var resolved = path.StartsWith(@"\\?\", StringComparison.Ordinal) ? MtpHelpers.ResolveMtpShellPath(path) ?? path : path;
			return overlay ? ShellImage.GetIconOverlay(resolved, size, folder)
				: ShellImage.GetIcon(resolved, size, folder, iconOptions);
		}, Program.Logger, priority);
		return (data ?? []).AsBuffer();
	}

	private static void ValidateSize(int size)
	{
		if (size is < 1 or > 1024)
			throw new ArgumentOutOfRangeException(nameof(size));
	}
}
