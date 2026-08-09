using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace TDR.PakLib.Formats
{
    public class SurfaceMaterialPhysics
    {
        public string Name { get; set; } = "";
        public int MaterialId { get; set; }
        public float CollisionFriction { get; set; } = 1.0f;
        public float WheelFriction { get; set; } = 1.0f;
        public float Hardness { get; set; } = 1.0f;
        public float Bumpyness { get; set; } = 0.0f;
        public float FfbBumpyness { get; set; } = 0.0f;
        public float Dampening { get; set; } = 0.0f;
        public float Sparkyness { get; set; } = 0.0f;
        public string Sound { get; set; } = "";
        public string SkidMarkTexture { get; set; } = "";
        public bool GenerateSmoke { get; set; }
        public bool GenerateDust { get; set; }
        public bool GenerateGrass { get; set; }
        public bool GenerateTyreScrap { get; set; }
        public bool GenerateSkidMark { get; set; }
        public string DustColour { get; set; } = "#FFFFFF";
        public string GrassColour { get; set; } = "#00FF00";
        public string SkidMarkColour { get; set; } = "#000000";
    }

    public static class HParser
    {
        public static Dictionary<string, SurfaceMaterialPhysics> Parse(byte[] data)
        {
            var result = new Dictionary<string, SurfaceMaterialPhysics>(StringComparer.OrdinalIgnoreCase);
            if (data == null || data.Length == 0) return result;

            string text = Encoding.ASCII.GetString(data);
            string[] lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);

            SurfaceMaterialPhysics? currentMat = null;

            foreach (string rawLine in lines)
            {
                string line = rawLine.Contains("//") ? rawLine[..rawLine.IndexOf("//")].Trim() : rawLine.Trim();
                if (string.IsNullOrWhiteSpace(line)) continue;

                if (line.StartsWith("#define", StringComparison.OrdinalIgnoreCase))
                {
                    string[] tokens = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (tokens.Length >= 3 && int.TryParse(tokens[2], out int id))
                    {
                        var mat = new SurfaceMaterialPhysics { Name = tokens[1], MaterialId = id };
                        result[tokens[1]] = mat;
                        currentMat = mat;
                    }
                    continue;
                }

                if (currentMat == null) continue;

                string[] parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) continue;

                string key = parts[0];
                string val = parts[1].Trim('"');

                switch (key.ToLowerInvariant())
                {
                    case "collisionfriction":
                        if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out float cf)) currentMat.CollisionFriction = cf;
                        break;
                    case "wheelfriction":
                        if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out float wf)) currentMat.WheelFriction = wf;
                        break;
                    case "hardness":
                        if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out float h)) currentMat.Hardness = h;
                        break;
                    case "bumpyness":
                        if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out float b)) currentMat.Bumpyness = b;
                        break;
                    case "ffbbumpyness":
                        if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out float ffb)) currentMat.FfbBumpyness = ffb;
                        break;
                    case "dampening":
                        if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out float d)) currentMat.Dampening = d;
                        break;
                    case "sparkyness":
                        if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out float s)) currentMat.Sparkyness = s;
                        break;
                    case "sound":
                        currentMat.Sound = val;
                        break;
                    case "skidmarktexture":
                        currentMat.SkidMarkTexture = val;
                        break;
                    case "generatesmoke":
                        currentMat.GenerateSmoke = val == "1" || val.Equals("true", StringComparison.OrdinalIgnoreCase);
                        break;
                    case "generatedust":
                        currentMat.GenerateDust = val == "1" || val.Equals("true", StringComparison.OrdinalIgnoreCase);
                        break;
                    case "generategrass":
                        currentMat.GenerateGrass = val == "1" || val.Equals("true", StringComparison.OrdinalIgnoreCase);
                        break;
                    case "generatetyrescrap":
                        currentMat.GenerateTyreScrap = val == "1" || val.Equals("true", StringComparison.OrdinalIgnoreCase);
                        break;
                    case "generateskidmark":
                        currentMat.GenerateSkidMark = val == "1" || val.Equals("true", StringComparison.OrdinalIgnoreCase);
                        break;
                }
            }

            return result;
        }
    }
}
