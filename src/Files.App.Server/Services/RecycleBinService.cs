// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Shared.Helpers;
using Windows.Foundation;

namespace Files.App.Server;

public sealed partial class RecycleBinService
{
	private readonly AppService client;

	internal RecycleBinService(AppService client) => this.client = client;

	// The changes are packed into strings: an array of a struct that contains strings has no generated
	// marshaller, so the projection falls back to reflection and crashes (see RecycleBinChangeCodec).
	public string[] GetChanges() => RecycleBinCatalog.GetChanges(client.ClientId)
		.Select(change => RecycleBinChangeCodec.Encode((int)change.Kind, change.Directory, change.Name, change.OldName))
		.ToArray();

	public static IAsyncOperation<bool> HasRecycleBinItemsAsync() => ServerAsync.Run(token => RecycleBinCatalog.HasItemsAsync().WaitAsync(token));

	public static IAsyncOperation<RecycleBinInfo> QueryRecycleBinAsync(string path) => ServerAsync.Run(token => RecycleBinCatalog.QueryAsync(path).WaitAsync(token));
}
