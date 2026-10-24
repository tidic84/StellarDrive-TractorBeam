using System.Collections.Generic;
using Ships.Interface.Model.Parts;
using Ships.Interface.Model.State;
using UnityEngine;

namespace TractorBeam.Docking
{
    /// <summary>One size of tractor pad: the biggest zone and ship it takes (Width x Width x Height), and how far the zone may go.</summary>
    internal sealed class PadSpec
    {
        public ushort Id;
        public float Width, Height;

        /// <summary>How far from the pad the zone may be moved, on each axis.</summary>
        public float Reach => Width * 2.5f;
        public string Fr, En;
    }

    /// <summary>A landing zone in its pad's frame: a box standing on <see cref="Bottom"/> (centre of its floor).</summary>
    internal struct Zone
    {
        public Vector3 Bottom, Size;

        public Vector3 Center => Bottom + new Vector3(0f, Size.y * 0.5f, 0f);
    }

    /// <summary>
    /// The tractor pads and their landing zones. A pad is placed on a floor; its frame has its origin at the centre of
    /// the pad's top face, y along the part's up (the landing direction) and z along its forward. The zone is a box in
    /// that frame, set from the pad's menu: by default it stands on the pad, Width x Width x Height; it can be moved
    /// anywhere around the carrier (outside the hull, in front of a hangar door...) and made smaller.
    /// </summary>
    internal static class Pads
    {
        public const ushort SmallId = 9381, MediumId = 9382, LargeId = 9383;

        /// <summary>Top of the pad's plate above the part's origin (the floor it stands on).</summary>
        public const float TopHeight = 0.12f;

        /// <summary>Gap left between a landed ship and the pad.</summary>
        public const float Gap = 0.03f;

        public static readonly PadSpec[] Specs =
        {
            new PadSpec { Id = SmallId, Width = 12f, Height = 10f, Fr = "petit", En = "small" },
            new PadSpec { Id = MediumId, Width = 24f, Height = 18f, Fr = "moyen", En = "medium" },
            new PadSpec { Id = LargeId, Width = 48f, Height = 30f, Fr = "grand", En = "large" },
        };

        public static PadSpec Spec(ushort settingsId)
        {
            foreach (PadSpec s in Specs)
                if (s.Id == settingsId) return s;
            return null;
        }

        public static bool IsPad(StatefulPart part) => part?.Settings != null && Spec(part.Settings.id) != null;

        public static PadSpec Spec(StatefulPart part) => part?.Settings == null ? null : Spec(part.Settings.id);

        public static IEnumerable<StatefulPart> PadsOf(IShipStateRead ship)
        {
            foreach (StatefulPart p in ship.StatefulParts)
                if (IsPad(p)) yield return p;
        }

        /// <summary>The pad's frame in its ship's frame (origin on top of the plate).</summary>
        public static (Vector3 pos, Quaternion rot) Frame(StatefulPart pad)
        {
            Quaternion r = pad.LocalRotation;
            return (pad.LocalPosition + r * new Vector3(0f, TopHeight, 0f), r);
        }

        /// <summary>The pad's landing zone (its configuration, or the default: centred on the pad, as big as its size allows).</summary>
        public static Zone ZoneOf(uint ship, StatefulPart pad) => PadConfigs.Get(ship, pad).Zone;

        public static bool InZone(Zone zone, Vector3 padLocal, float margin = 0f)
        {
            Vector3 d = padLocal - zone.Bottom;
            return Mathf.Abs(d.x) <= zone.Size.x * 0.5f + margin && Mathf.Abs(d.z) <= zone.Size.z * 0.5f + margin
                && d.y >= -margin && d.y <= zone.Size.y + margin;
        }
    }
}
