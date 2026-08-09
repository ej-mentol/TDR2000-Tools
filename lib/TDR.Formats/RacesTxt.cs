using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace TDR.PakLib.Formats
{
    public sealed class RaceEntry
    {
        public string Track { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public int Type { get; set; }
        public bool IsMission { get; set; }
        public string? MissionDesc { get; set; }
        public string? CameraPath { get; set; }
        public string? Powerups { get; set; }
    }

    public sealed class RacesFile
    {
        public List<RaceEntry> Races { get; } = new();

        public static RacesFile Parse(byte[] data)
        {
            if (data == null || data.Length == 0) return new RacesFile();
            string text = Encoding.ASCII.GetString(data);
            return Parse(text);
        }

        public static RacesFile Parse(string text)
        {
            var result = new RacesFile();
            if (string.IsNullOrWhiteSpace(text)) return result;

            using var reader = new StringReader(text);
            string? line;
            RaceEntry? currentEntry = null;

            while ((line = reader.ReadLine()) != null)
            {
                string clean = line.Contains("//") ? line[..line.IndexOf("//")].Trim() : line.Trim();
                if (string.IsNullOrWhiteSpace(clean)) continue;

                if (clean.Equals("RACE", StringComparison.OrdinalIgnoreCase))
                {
                    currentEntry = new RaceEntry();
                    continue;
                }

                if (clean.Equals("END", StringComparison.OrdinalIgnoreCase))
                {
                    if (currentEntry != null && !string.IsNullOrWhiteSpace(currentEntry.Track))
                    {
                        result.Races.Add(currentEntry);
                    }
                    currentEntry = null;
                    continue;
                }

                if (currentEntry != null)
                {
                    string[] parts = clean.Split(new[] { ' ', '\t' }, 2, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 2) continue;

                    string key = parts[0].ToUpperInvariant();
                    string val = parts[1].Trim('"');

                    switch (key)
                    {
                        case "TRACK":
                            currentEntry.Track = val;
                            break;
                        case "NAME":
                            currentEntry.Name = val;
                            break;
                        case "TYPE":
                            if (int.TryParse(val, out int tVal)) currentEntry.Type = tVal;
                            break;
                        case "MISSION":
                            currentEntry.IsMission = true;
                            break;
                        case "MISSION_DESC":
                            currentEntry.MissionDesc = val;
                            currentEntry.IsMission = true;
                            break;
                        case "CAMERA_PATH":
                            currentEntry.CameraPath = val;
                            break;
                        case "POWERUPS":
                            currentEntry.Powerups = val;
                            break;
                    }
                }
            }

            return result;
        }

        public HashSet<string> GetBaseTrackNames()
        {
            var baseNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var race in Races)
            {
                string baseName = TrackDiscovery.GetBaseTrackName(race.Track);
                if (!string.IsNullOrEmpty(baseName))
                {
                    baseNames.Add(baseName);
                }
            }
            return baseNames;
        }

        public List<string> GetVariantsForTrack(string baseTrackName)
        {
            var variants = new List<string>();
            string cleanBase = TrackDiscovery.GetBaseTrackName(baseTrackName);

            foreach (var race in Races)
            {
                string raceBase = TrackDiscovery.GetBaseTrackName(race.Track);
                if (raceBase.Equals(cleanBase, StringComparison.OrdinalIgnoreCase))
                {
                    if (!variants.Contains(race.Track, StringComparer.OrdinalIgnoreCase))
                    {
                        variants.Add(race.Track);
                    }
                }
            }

            return variants;
        }
    }
}
