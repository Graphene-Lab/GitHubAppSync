using System;
using System.Text.Json;

namespace GitHubAppSync
{
    /// <summary>
    /// Reads and writes the channel manifest. Kept in one place so the wire format is a single
    /// concern: the JSON shape is also what the CI helper produces, so both sides live here.
    /// </summary>
    internal static class ManifestJson
    {
        private static readonly JsonSerializerOptions ReadOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        private static readonly JsonSerializerOptions WriteOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        internal static UpdateManifest Deserialize(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                throw new FormatException("The manifest is empty.");

            try
            {
                return JsonSerializer.Deserialize<UpdateManifest>(json, ReadOptions);
            }
            catch (JsonException ex)
            {
                throw new FormatException("The manifest is not valid JSON: " + ex.Message, ex);
            }
        }

        internal static string Serialize(UpdateManifest manifest)
        {
            if (manifest == null) throw new ArgumentNullException(nameof(manifest));
            return JsonSerializer.Serialize(manifest, WriteOptions);
        }
    }
}
