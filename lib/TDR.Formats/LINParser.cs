using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;

namespace TDR.PakLib.Formats
{
    public sealed class TDRSpline
    {
        public string Name { get; set; } = string.Empty;
        public List<Vector3> Points { get; } = new();

        /// <summary>
        /// Calculates a placement Matrix4x4 positioned at Points[pointIndex] oriented towards the next point.
        /// </summary>
        public Matrix4x4 GetSpawnMatrix(int pointIndex = 0, float yOffset = 0.35f)
        {
            if (Points.Count == 0) return Matrix4x4.Identity;

            int idx0 = Math.Clamp(pointIndex, 0, Points.Count - 1);
            Vector3 pos = Points[idx0];
            pos.Y += yOffset;

            Vector3 forward;
            if (idx0 < Points.Count - 1)
            {
                forward = Points[idx0 + 1] - Points[idx0];
            }
            else if (idx0 > 0)
            {
                forward = Points[idx0] - Points[idx0 - 1];
            }
            else
            {
                forward = Vector3.UnitZ;
            }

            // Keep vehicle and train upright by projecting forward tangent onto horizontal XZ plane
            forward.Y = 0f;

            if (forward.LengthSquared() < 0.0001f)
            {
                forward = Vector3.UnitZ;
            }
            else
            {
                forward = Vector3.Normalize(forward);
            }

            Vector3 realUp = Vector3.UnitY;
            Vector3 right = Vector3.Normalize(Vector3.Cross(forward, realUp));

            // TDR2000 vehicle models have Front = -Z, Right = +X, Up = +Y.
            // Basis mapping:
            // Local +X -> right
            // Local +Y -> realUp
            // Local -Z -> forward (Local +Z -> -forward)
            return new Matrix4x4(
                right.X,    right.Y,    right.Z,    0f,
                realUp.X,   realUp.Y,   realUp.Z,   0f,
                -forward.X, -forward.Y, -forward.Z, 0f,
                pos.X,      pos.Y,      pos.Z,      1f
            );
        }

        public void ApplyTransform(Matrix4x4 transform)
        {
            for (int i = 0; i < Points.Count; i++)
            {
                Points[i] = Vector3.Transform(Points[i], transform);
            }
        }
    }

    public sealed class TDRSplineContainer
    {
        public string Name { get; set; } = string.Empty;
        public List<TDRSpline> Splines { get; } = new();

        /// <summary>
        /// Loads either an ASCII .lin file or a binary .lins spline package, optionally applying an alignment matrix.
        /// </summary>
        public static TDRSplineContainer Load(byte[] data, string fileName, byte[]? optionsData = null)
        {
            var container = (fileName.EndsWith(".lins", StringComparison.OrdinalIgnoreCase) || IsBinaryLins(data))
                ? LoadLins(data, fileName)
                : LoadLin(data, fileName);

            if (optionsData != null && optionsData.Length > 0)
            {
                var alignment = ParseAlignmentOptions(optionsData);
                if (alignment.HasValue)
                {
                    foreach (var spline in container.Splines)
                    {
                        spline.ApplyTransform(alignment.Value);
                    }
                }
            }

            return container;
        }

        public static bool IsBinaryLins(byte[]? data)
        {
            if (data == null || data.Length < 16) return false;
            int nameLen = BitConverter.ToInt32(data, 0);
            if (nameLen <= 0 || nameLen > 256 || data.Length < 4 + nameLen + 4) return false;

            for (int i = 4; i < 4 + nameLen; i++)
            {
                byte b = data[i];
                if (b == 0) break;
                if (b < 0x20 || b > 0x7E) return false;
            }

            int numPoints = BitConverter.ToInt32(data, 4 + nameLen);
            if (numPoints <= 0 || numPoints > 100000) return false;

            return data.Length >= 4 + nameLen + 4 + (numPoints * 12);
        }

        /// <summary>
        /// Parses OPT_ALIGNMENT_MATRIX44 from a companion .txt file.
        /// </summary>
        public static Matrix4x4? ParseAlignmentOptions(byte[] optData)
        {
            if (optData == null || optData.Length == 0) return null;
            string text = Encoding.ASCII.GetString(optData);
            var lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.StartsWith("OPT_ALIGNMENT_MATRIX44", StringComparison.OrdinalIgnoreCase))
                {
                    var values = new List<float>();
                    for (int j = i + 1; j < lines.Length && values.Count < 16; j++)
                    {
                        string subLine = lines[j].Contains("//") ? lines[j][..lines[j].IndexOf("//")].Trim() : lines[j].Trim();
                        if (string.IsNullOrWhiteSpace(subLine)) continue;
                        if (subLine.StartsWith("OPT_", StringComparison.OrdinalIgnoreCase)) break;

                        var parts = subLine.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        foreach (var part in parts)
                        {
                            if (float.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out float val))
                            {
                                values.Add(val);
                            }
                        }
                    }

                    if (values.Count >= 16)
                    {
                        return new Matrix4x4(
                            values[0], values[1], values[2], values[3],
                            values[4], values[5], values[6], values[7],
                            values[8], values[9], values[10], values[11],
                            values[12], values[13], values[14], Math.Abs(values[15]) < 1e-5f ? 1f : values[15]
                        );
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// Parses a single ASCII .lin spline file (whitespace-delimited X Y Z coordinates).
        /// </summary>
        public static TDRSplineContainer LoadLin(byte[] data, string fileName)
        {
            var container = new TDRSplineContainer { Name = fileName };
            var spline = new TDRSpline { Name = Path.GetFileNameWithoutExtension(fileName) };

            string text = Encoding.ASCII.GetString(data);
            string[] lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);

            foreach (string rawLine in lines)
            {
                string clean = rawLine.Trim();
                if (clean.StartsWith("//") || clean.StartsWith("#") || string.IsNullOrWhiteSpace(clean))
                    continue;

                string[] parts = clean.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3 &&
                    float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) &&
                    float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y) &&
                    float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
                {
                    var pt = new Vector3(x, y, z);
                    if (spline.Points.Count == 0 || Vector3.DistanceSquared(spline.Points[^1], pt) > 1e-6f)
                    {
                        spline.Points.Add(pt);
                    }
                }
            }

            if (spline.Points.Count > 0)
            {
                container.Splines.Add(spline);
            }

            return container;
        }

        /// <summary>
        /// Parses a binary .lins spline collection: [int32 nameLen, char[] name, int32 numPoints, float32[3] points...].
        /// </summary>
        public static TDRSplineContainer LoadLins(byte[] data, string fileName)
        {
            var container = new TDRSplineContainer { Name = fileName };
            if (data == null || data.Length == 0) return container;

            using var ms = new MemoryStream(data);
            using var br = new BinaryReader(ms);

            while (ms.Position < ms.Length)
            {
                if (ms.Length - ms.Position < 4) break;
                int nameLen = br.ReadInt32();
                if (nameLen <= 0 || nameLen > 1024 || ms.Position + nameLen > ms.Length) break;

                byte[] nameBytes = br.ReadBytes(nameLen);
                string splineName = Encoding.ASCII.GetString(nameBytes).TrimEnd('\0');

                if (ms.Position + 4 > ms.Length) break;
                int numPoints = br.ReadInt32();
                if (numPoints < 0 || numPoints > 100000) break;

                var spline = new TDRSpline { Name = splineName };
                for (int i = 0; i < numPoints; i++)
                {
                    if (ms.Position + 12 > ms.Length) break;
                    float x = br.ReadSingle();
                    float y = br.ReadSingle();
                    float z = br.ReadSingle();
                    var pt = new Vector3(x, y, z);
                    if (spline.Points.Count == 0 || Vector3.DistanceSquared(spline.Points[^1], pt) > 1e-6f)
                    {
                        spline.Points.Add(pt);
                    }
                }

                if (spline.Points.Count > 0)
                {
                    container.Splines.Add(spline);
                }
            }

            return container;
        }
    }
}
