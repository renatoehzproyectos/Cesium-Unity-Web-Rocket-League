using System;
using System.Collections.Generic;

namespace RocketWorld.Sim.Roads
{
    /// <summary>Overture transportation <c>class</c> (road subtype only), reduced to what physics needs.</summary>
    public enum RoadClass : byte
    {
        Unknown = 0, Motorway, Trunk, Primary, Secondary, Tertiary, Residential, LivingStreet, Service, Unclassified, Track, Pedestrian, Footway, Cycleway, Path, Steps, Other
    }

    public enum RoadSurface : byte { Unknown = 0, Paved, Asphalt, Concrete, Cobblestone, Gravel, Dirt, Sand, Grass, Other }

    [Flags]
    public enum RoadFlags : byte { None = 0, Bridge = 1, Tunnel = 2, Link = 4, Construction = 8, Roundabout = 16 }

    /// <summary>Connector reference of a segment: stable id hash and the linear-reference position along the segment (0..1).</summary>
    public struct RoadConnector { public uint id; public float at; public RoadConnector(uint id, float at) { this.id = id; this.at = at; } }

    /// <summary>One source segment as it comes out of the tile file: a centreline in lon/lat (degrees) plus attributes.</summary>
    public sealed class RoadSegment
    {
        public uint id;                       // hash of the Overture GERS id (stable across releases while the feature lives)
        public RoadClass cls; public RoadSurface surface; public RoadFlags flags;
        public sbyte level;                   // Overture level_rules value (z-order); 0 = ground
        public float widthM;                  // edge to edge, 0 = unknown (use class default)
        public byte lanes;                    // 0 = unknown
        public double[] lon, lat;             // centreline vertices
        public RoadConnector[] connectors = new RoadConnector[0];
        public bool IsBridge => (flags & RoadFlags.Bridge) != 0;
        public bool IsTunnel => (flags & RoadFlags.Tunnel) != 0;
    }

    public sealed class RoadTileData
    {
        public int z, x, y; public string release = ""; public List<RoadSegment> segments = new List<RoadSegment>();
    }

    /// <summary>Per-class defaults (used when the source has no width) and which classes the car can really drive on.</summary>
    public static class RoadClassInfo
    {
        /// <summary>Drivable surfaces only: footways, steps and cycle paths are not built (they would be bumps for a car and the photogrammetry already has them).</summary>
        public static bool Drivable(RoadClass c)
        {
            switch (c)
            {
                case RoadClass.Footway: case RoadClass.Cycleway: case RoadClass.Path: case RoadClass.Steps: case RoadClass.Pedestrian: return false;
                default: return true;
            }
        }

        /// <summary>Total width in metres: the Overture <c>width_rules</c> value when given, else lanes x 3.4 (+ shoulder), else a class default.</summary>
        public static float Width(RoadSegment s)
        {
            if (s.widthM >= 2f) return Math.Min(s.widthM, 60f);
            if (s.lanes > 0) return Math.Min(s.lanes * 3.4f + (s.cls <= RoadClass.Trunk ? 2.5f : 0.6f), 60f);
            return DefaultWidth(s.cls);
        }

        public static float DefaultWidth(RoadClass c)
        {
            switch (c)
            {
                case RoadClass.Motorway: return 14f;
                case RoadClass.Trunk: return 11f;
                case RoadClass.Primary: return 9f;
                case RoadClass.Secondary: return 8f;
                case RoadClass.Tertiary: return 7f;
                case RoadClass.Residential: case RoadClass.Unclassified: return 6f;
                case RoadClass.LivingStreet: return 5f;
                case RoadClass.Service: return 4.2f;
                case RoadClass.Track: return 3.4f;
                default: return 5f;
            }
        }

        /// <summary>Steepest believable grade (rise/run); steeper fitted profiles are photogrammetry noise.</summary>
        public static float MaxGrade(RoadClass c)
        {
            switch (c)
            {
                case RoadClass.Motorway: return 0.10f;
                case RoadClass.Trunk: case RoadClass.Primary: return 0.14f;
                case RoadClass.Secondary: case RoadClass.Tertiary: return 0.20f;
                case RoadClass.Track: return 0.35f;
                default: return 0.30f;
            }
        }

        /// <summary>Higher = wins when several segments share a node (the node takes the height of the most important road).</summary>
        public static int Priority(RoadClass c) => c == RoadClass.Unknown ? 0 : 20 - (int)c;

        /// <summary>0 = paved, 1 = loose. Only used for the HUD / optional friction; physics stays at the RocketSim world friction on paved roads.</summary>
        public static bool IsLoose(RoadSegment s)
        {
            switch (s.surface) { case RoadSurface.Gravel: case RoadSurface.Dirt: case RoadSurface.Sand: case RoadSurface.Grass: return true; }
            return s.surface == RoadSurface.Unknown && s.cls == RoadClass.Track;
        }
    }

    /// <summary>Flat-array mesh in a local east/up/north frame (Unity axes: x east, y up, z north).</summary>
    public sealed class RoadMeshData
    {
        public List<float> v = new List<float>(4096);     // x,y,z triples
        public List<int> t = new List<int>(8192);
        public List<int> c = new List<int>(8192);         // subset of t that may carve the tile colliders (not tunnels)
        public int VertexCount => v.Count / 3;
        public int TriangleCount => t.Count / 3;
        public int Add(float x, float y, float z) { v.Add(x); v.Add(y); v.Add(z); return v.Count / 3 - 1; }
        public void Tri(int a, int b, int c, bool carve = true) { t.Add(a); t.Add(b); t.Add(c); if (carve) { this.c.Add(a); this.c.Add(b); this.c.Add(c); } }
    }
}
