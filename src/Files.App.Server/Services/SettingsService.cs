// Copyright (c) Files Community
// Licensed under the MIT License.

using System.Text.Json;

namespace Files.App.Server;

public sealed partial class SettingsService
{
	private SettingsService() { }

	public static string GetSettings(string fileName) => SettingsStore.Get(fileName).ReadJson();

	public static bool SetSetting(string fileName, string key, string valueJson)
	{
		using var document = JsonDocument.Parse(valueJson);
		return SettingsStore.Get(fileName).Set(key, document.RootElement.Clone(), false);
	}

	public static bool RemoveSetting(string fileName, string key) => SettingsStore.Get(fileName).Set(key, default, true);

	public static bool FlushSettings(string fileName) => SettingsStore.Get(fileName).Flush();

	public static bool FlushAllSettings() => SettingsStore.FlushAll();

	public static bool ImportSettings(string fileName, string json)
	{
		using var document = JsonDocument.Parse(json);
		if (document.RootElement.ValueKind != JsonValueKind.Object)
			return false;
		var replacement = document.RootElement.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone());
		return SettingsStore.Get(fileName).Import(replacement);
	}
}
