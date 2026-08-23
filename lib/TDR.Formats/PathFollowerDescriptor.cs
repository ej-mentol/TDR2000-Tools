using System;
using System.Collections.Generic;
using System.Text;

namespace TDR.PakLib.Formats
{
    public sealed class PathFollowerEntry
    {
        public string Tag { get; set; } = string.Empty;
        public string ModelHie { get; set; } = string.Empty;
        public string PathHie { get; set; } = string.Empty;
        public string SoundName { get; set; } = string.Empty;
        public float Speed { get; set; } = 4.0f;
        public float SwayTime { get; set; } = 0.0f;
        public float SwayMag { get; set; } = 0.0f;
        public float AnimateTime { get; set; } = 4.0f;
        public float StartProgress { get; set; } = 0.0f;
    }

    public sealed class PathFollowerDescriptor
    {
        public List<PathFollowerEntry> Followers { get; } = new();

        public static PathFollowerDescriptor Load(string text)
        {
            var result = new PathFollowerDescriptor();
            if (string.IsNullOrWhiteSpace(text)) return result;

            var lines = DescriptorReader.GetCleanLines(text);
            string currentTag = string.Empty;

            for (int i = 0; i < lines.Count; i++)
            {
                string line = lines[i];
                if (line.StartsWith("@"))
                {
                    currentTag = line.TrimStart('@').Trim();
                    continue;
                }

                var tokens = DescriptorReader.TokenizeLine(line);
                if (tokens.Count > 0 && tokens[0].EndsWith(".hie", StringComparison.OrdinalIgnoreCase))
                {
                    var entry = new PathFollowerEntry
                    {
                        Tag = currentTag,
                        ModelHie = tokens[0]
                    };

                    // Next line is typically Path Hie
                    if (i + 1 < lines.Count)
                    {
                        var pathTokens = DescriptorReader.TokenizeLine(lines[++i]);
                        if (pathTokens.Count > 0 && pathTokens[0].EndsWith(".hie", StringComparison.OrdinalIgnoreCase))
                        {
                            entry.PathHie = pathTokens[0];
                        }
                    }

                    // Next line is typically sound name ("none" or sound id)
                    if (i + 1 < lines.Count && !lines[i + 1].StartsWith("@") && !lines[i + 1].EndsWith(".hie", StringComparison.OrdinalIgnoreCase))
                    {
                        var sndTokens = DescriptorReader.TokenizeLine(lines[++i]);
                        if (sndTokens.Count > 0) entry.SoundName = sndTokens[0];
                    }

                    // Next line is speed & sway parameters (4 floats)
                    bool hasParams = false;
                    if (i + 1 < lines.Count && !lines[i + 1].StartsWith("@") && !lines[i + 1].EndsWith(".hie", StringComparison.OrdinalIgnoreCase))
                    {
                        var paramTokens = DescriptorReader.TokenizeLine(lines[++i]);
                        if (paramTokens.Count >= 4)
                        {
                            DescriptorReader.TryParseFloat(paramTokens[0], out float spd);
                            DescriptorReader.TryParseFloat(paramTokens[1], out float swt);
                            DescriptorReader.TryParseFloat(paramTokens[2], out float swm);
                            DescriptorReader.TryParseFloat(paramTokens[3], out float ant);
                            entry.Speed = spd;
                            entry.SwayTime = swt;
                            entry.SwayMag = swm;
                            entry.AnimateTime = ant;
                            hasParams = true;
                        }
                    }

                    // Next line is start time / progress
                    if (i + 1 < lines.Count && !lines[i + 1].StartsWith("@") && !lines[i + 1].EndsWith(".hie", StringComparison.OrdinalIgnoreCase))
                    {
                        var timeTokens = DescriptorReader.TokenizeLine(lines[++i]);
                        if (timeTokens.Count >= 1 && DescriptorReader.TryParseFloat(timeTokens[0], out float st))
                        {
                            entry.StartProgress = st;
                        }
                    }

                    // Only add if this was genuinely a path follower descriptor entry (has tag, speed params, or path spline)
                    if (!string.IsNullOrEmpty(entry.PathHie) && (hasParams || !string.IsNullOrEmpty(entry.Tag)))
                    {
                        result.Followers.Add(entry);
                    }
                    currentTag = string.Empty;
                }
            }

            return result;
        }

        public static PathFollowerDescriptor? Load(byte[] data)
        {
            if (data == null || data.Length == 0) return null;
            return Load(Encoding.ASCII.GetString(data));
        }
    }
}
