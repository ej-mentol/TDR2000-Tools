using System;
using System.Numerics;

namespace TDR.Tools.Export
{
    public enum EntityCategory
    {
        MovableProp,
        TrafficDrone,
        PowerupItem,
        Pedestrian
    }

    public sealed class PlacedEntity
    {
        public EntityCategory Category { get; set; }
        public string InstanceId { get; set; } = string.Empty;
        public string ModelHieName { get; set; } = string.Empty;
        public Matrix4x4 WorldTransform { get; set; } = Matrix4x4.Identity;
        public string? Tag { get; set; }
        public int TypeId { get; set; }
    }
}
