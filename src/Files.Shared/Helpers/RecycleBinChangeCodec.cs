// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.Globalization;

namespace Files.Shared.Helpers;

/// <summary>
/// Packs a single recycle bin change into one string.
/// </summary>
/// <remarks>
/// The change cannot cross the WinRT boundary as a struct: an array of a struct that contains strings
/// has no generated marshaller, so the projection falls back to reflection, which reads the ABI fields
/// as managed references and crashes the process. Strings are marshalled by a generated marshaller, so
/// the fields are packed here instead.
/// </remarks>
public static class RecycleBinChangeCodec
{
	private const char FieldSeparator = '|';
	private const int FieldCount = 4;

	/// <summary>Packs the fields of one change into a single string.</summary>
	public static string Encode(int kind, string? directory, string? name, string? oldName)
		=> string.Concat(
			kind.ToString(CultureInfo.InvariantCulture),
			FieldSeparator.ToString(),
			Uri.EscapeDataString(directory ?? string.Empty),
			FieldSeparator.ToString(),
			Uri.EscapeDataString(name ?? string.Empty),
			FieldSeparator.ToString(),
			Uri.EscapeDataString(oldName ?? string.Empty));

	/// <summary>Unpacks a string produced by <see cref="Encode"/>.</summary>
	public static bool TryDecode(string? value, out int kind, out string directory, out string name, out string oldName)
	{
		kind = 0;
		directory = string.Empty;
		name = string.Empty;
		oldName = string.Empty;

		if (string.IsNullOrEmpty(value))
			return false;

		var fields = value.Split(FieldSeparator);
		if (fields.Length != FieldCount
			|| !int.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out kind))
		{
			return false;
		}

		directory = Uri.UnescapeDataString(fields[1]);
		name = Uri.UnescapeDataString(fields[2]);
		oldName = Uri.UnescapeDataString(fields[3]);
		return true;
	}
}
