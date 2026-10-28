// Copyright (c) Files Community
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation.Metadata;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Win32;
using IO = System.IO;

namespace Files.App.Utils.FileTags
{

	public static class FileTagsHelper
	{
		private static readonly Lazy<FileTagsDatabase> dbInstance = new(() => new());

		public static FileTagsDatabase GetDbInstance() => dbInstance.Value;

		public static string[] ReadFileTag(string filePath)
		{
			var tagString = Win32Helper.ReadStringFromFile($"{filePath}:files");
			return tagString?.Split(',', StringSplitOptions.RemoveEmptyEntries) ?? [];
		}

		public static async Task<bool> WriteFileTagAsync(string filePath, string[] tag, CancellationToken cancellationToken = default)
		{
			var isDateOk = false;
			var isReadOnly = false;
			System.Runtime.InteropServices.ComTypes.FILETIME dateModified = default;

			try
			{
				cancellationToken.ThrowIfCancellationRequested();
				isDateOk = Win32Helper.GetFileDateModified(filePath, out dateModified); // Backup date modified
				isReadOnly = Win32Helper.HasFileAttribute(filePath, IO.FileAttributes.ReadOnly);
				if (isReadOnly) // Unset read-only attribute (#7534)
					Win32Helper.UnsetFileAttribute(filePath, IO.FileAttributes.ReadOnly);

				var currentTags = ReadFileTag(filePath);
				if (tag.SequenceEqual(currentTags))
					return true;

				bool succeeded = tag.Length == 0
					? PInvoke.DeleteFileFromApp($"{filePath}:files")
					: Win32Helper.WriteStringToFile($"{filePath}:files", string.Join(',', tag));
				if (!succeeded)
				{
					await SafetyExtensions.IgnoreExceptions(() => MainWindow.Instance.DispatcherQueue.EnqueueOrInvokeAsync(async () =>
					{
						ContentDialog dialog = new()
						{
							Title = Strings.ErrorApplyingTagTitle.GetLocalizedResource(),
							Content = Strings.ErrorApplyingTagContent.GetLocalizedResource(),
							PrimaryButtonText = "Ok".GetLocalizedResource()
						};

						if (ApiInformation.IsApiContractPresent("Windows.Foundation.UniversalApiContract", 8))
							dialog.XamlRoot = MainWindow.Instance.Content.XamlRoot;

						await dialog.TryShowAsync();
					}), App.Logger);
				}

				return succeeded;
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				return false;
			}
			catch (Exception ex)
			{
				App.Logger.LogWarning(ex, "Failed to write tags for '{FilePath}'.", LogPathHelper.RedactPath(filePath));
				return false;
			}
			finally
			{
				if (isReadOnly) // Restore read-only attribute (#7534)
					SafetyExtensions.IgnoreExceptions(() => Win32Helper.SetFileAttribute(filePath, IO.FileAttributes.ReadOnly), App.Logger);
				if (isDateOk)
					SafetyExtensions.IgnoreExceptions(() => Win32Helper.SetFileDateModified(filePath, dateModified), App.Logger); // Restore date modified
			}
		}

		public static void UpdateTagsDb()
		{
			var dbInstance = GetDbInstance();
			foreach (var file in dbInstance.GetAll())
			{
				var pathFromFrn = Win32Helper.PathFromFileId(file.Frn ?? 0, file.FilePath);
				if (pathFromFrn is not null)
				{
					// Frn is valid, update file path
					var normalizedPath = RemoveExtendedPathPrefix(pathFromFrn);
					var tag = ReadFileTag(normalizedPath);
					if (tag.Length > 0)
					{
						dbInstance.UpdateTag(file.Frn ?? 0, null, normalizedPath);
						dbInstance.SetTags(normalizedPath, file.Frn, tag);
					}
					else if (!IsUnreachable(normalizedPath))
					{
						dbInstance.SetTags(normalizedPath, file.Frn, []);
					}
				}
				else
				{
					var tag = ReadFileTag(file.FilePath);
					if (tag.Length > 0)
					{
						if (!SafetyExtensions.IgnoreExceptions(() =>
						{
							var frn = GetFileFRN(file.FilePath);
							dbInstance.UpdateTag(file.FilePath, frn, null);
							dbInstance.SetTags(file.FilePath, frn, tag);
						}, App.Logger))
						{
							dbInstance.SetTags(file.FilePath, null, []);
						}
					}
					else if (!IsUnreachable(file.FilePath))
					{
						dbInstance.SetTags(file.FilePath, null, []);
					}
				}
			}
		}

		/// <summary>
		/// Converts extended-length paths, including UNC ones, back to their plain form.
		/// </summary>
		private static string RemoveExtendedPathPrefix(string path)
		{
			if (path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal))
				return @"\\" + path[8..];

			return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
		}

		/// <summary>
		/// Determines whether the item is unreachable, in which case a failed tag read cannot be treated as "untagged".
		/// </summary>
		private static bool IsUnreachable(string path)
		{
			if (IO.File.Exists(path) || IO.Directory.Exists(path))
				return false;

			// The item is gone, but the entry is only stale if its storage is currently reachable.
			var storageRoot = IO.Path.GetPathRoot(path);
			return string.IsNullOrEmpty(storageRoot) || !(IO.Directory.Exists(storageRoot) || IO.File.Exists(storageRoot));
		}

		/// <summary>
		/// Prompts the user for confirmation, then removes all tags from the given items that have tags.
		/// </summary>
		/// <returns>True if the user confirmed and tags were removed; otherwise false.</returns>
		public static async Task<bool> RemoveTagsAsync(IEnumerable<ListedItem> items)
		{
			var itemsWithTags = items.Where(item => item.FileTags is { Length: > 0 }).ToList();
			if (itemsWithTags.Count == 0)
				return false;

			var confirmed = await DialogDisplayHelper.ShowDialogAsync(
				Strings.RemoveTags.GetLocalizedResource(),
				Strings.ConfirmRemoveTagsDialogContent.GetLocalizedResource(),
				Strings.Yes.GetLocalizedResource(),
				Strings.Cancel.GetLocalizedResource());

			if (!confirmed)
				return false;

			foreach (var item in itemsWithTags)
				item.FileTags = [];

			return true;
		}

		public static ulong? GetFileFRN(string filePath) => Win32Helper.GetFileFRN(filePath);

		public static Task<ulong?> GetFileFRN(IStorageItem? item)
		{
			return item switch
			{
				BaseStorageFolder { Properties: not null } folder => GetFileFRN(folder.Properties),
				BaseStorageFile { Properties: not null } file => GetFileFRN(file.Properties),
				_ => Task.FromResult<ulong?>(null),
			};

			static async Task<ulong?> GetFileFRN(IStorageItemExtraProperties properties)
			{
				var extra = await properties.RetrievePropertiesAsync((string[])["System.FileFRN"]);
				return (ulong?)extra["System.FileFRN"];
			}
		}
	}
}
