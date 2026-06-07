// Copyright (C) 2022  Kevin Jilissen

// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.

// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Globalization;
using Newtonsoft.Json;

namespace Jellyfin.Xtream.Client;

/// <summary>
/// Converts EPG datetime strings to <see cref="DateTimeOffset"/> values, preserving the timezone offset.
/// Handles ISO 8601 strings ("2024-01-15 18:00:00 +0200") and XMLTV compact format ("20240115180000 +0200").
/// Returns <c>null</c> when the string does not contain an explicit timezone offset so the caller can
/// fall back to the corresponding Unix timestamp field.
/// </summary>
public class XtreamDateTimeConverter : JsonConverter
{
    private static readonly string[] _xmltvFormats =
    [
        "yyyyMMddHHmmss zzz",
        "yyyyMMddHHmmsszzz",
    ];

    /// <inheritdoc />
    public override bool CanConvert(Type objectType)
    {
        return objectType == typeof(DateTimeOffset?);
    }

    /// <inheritdoc />
    public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
    {
        if (reader.TokenType == JsonToken.Null)
        {
            return null;
        }

        if (reader.Value is not string value || string.IsNullOrEmpty(value))
        {
            return null;
        }

        // Only handle strings that explicitly carry a timezone offset.
        // Strings without an offset are left null so the caller falls back to start_timestamp.
        if (!HasExplicitOffset(value))
        {
            return null;
        }

        // Normalize "+HHMM" (4-digit, no-colon) offset to "+HH:MM" so .NET can parse it.
        string normalized = NormalizeOffset(value);

        // Try standard DateTimeOffset parsing — covers "yyyy-MM-dd HH:mm:ss +HH:MM", ISO 8601, etc.
        if (DateTimeOffset.TryParse(normalized, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset result))
        {
            return result;
        }

        // Try XMLTV compact format: "yyyyMMddHHmmss +HH:MM" (with or without space before offset).
        if (DateTimeOffset.TryParseExact(normalized, _xmltvFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out result))
        {
            return result;
        }

        return null;
    }

    /// <inheritdoc />
    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
    {
        if (value is DateTimeOffset dto)
        {
            writer.WriteValue(dto.ToString("o", CultureInfo.InvariantCulture));
        }
        else
        {
            writer.WriteNull();
        }
    }

    /// <summary>
    /// Returns <c>true</c> when the string ends with a recognisable timezone offset
    /// (i.e., "Z", "[+-]HH:MM", or "[+-]HHMM").
    /// </summary>
    private static bool HasExplicitOffset(string value)
    {
        int len = value.Length;

        // "Z" suffix (UTC)
        if (value.EndsWith('Z') || value.EndsWith('z'))
        {
            return true;
        }

        // "[+-]HH:MM" — 6 trailing chars with colon at position len-3
        if (len >= 6 &&
            (value[len - 6] == '+' || value[len - 6] == '-') &&
            char.IsDigit(value[len - 5]) &&
            char.IsDigit(value[len - 4]) &&
            value[len - 3] == ':' &&
            char.IsDigit(value[len - 2]) &&
            char.IsDigit(value[len - 1]))
        {
            return true;
        }

        // "[+-]HHMM" — 5 trailing chars, no colon
        if (len >= 5 &&
            (value[len - 5] == '+' || value[len - 5] == '-') &&
            char.IsDigit(value[len - 4]) &&
            char.IsDigit(value[len - 3]) &&
            char.IsDigit(value[len - 2]) &&
            char.IsDigit(value[len - 1]))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Converts a "[+-]HHMM" offset suffix (no colon) to "[+-]HH:MM" so that
    /// standard <see cref="DateTimeOffset"/> parsing methods can handle it.
    /// Strings already in "[+-]HH:MM" form are returned unchanged.
    /// </summary>
    private static string NormalizeOffset(string value)
    {
        int len = value.Length;

        if (len >= 5 &&
            (value[len - 5] == '+' || value[len - 5] == '-') &&
            char.IsDigit(value[len - 4]) &&
            char.IsDigit(value[len - 3]) &&
            char.IsDigit(value[len - 2]) &&
            char.IsDigit(value[len - 1]))
        {
            // Insert ":" before the last 2 digits: "+0200" → "+02:00"
            return string.Concat(value.AsSpan(0, len - 2), ":", value.AsSpan(len - 2, 2));
        }

        return value;
    }
}
