using System.Collections.Generic;
using Ships.Interface.Model.Parts;
using Ships.Interface.Model.State;
using UnityEngine;

namespace TractorBeam.Docking
{
    /// <summary>One size of tractor pad: the landing zone is a box standing on the pad, Width x Width x Height.</summary>
    internal sealed class PadSpec
    {
        public ushort Id;
        public float Width, Height;
        public string Fr, En;
    }

    /// <summary>
    /// The tractor pads and their landing zones. A pad is placed on a floor; its frame has its origin at the centre of
    /// the pad's top face, y along the part's up (the landing direction) and z along its forward. The zone is
    /// |x| ≤ W/2, |z| ≤ W/2, 0 ≤ y ≤ H in that frame.
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

        public static bool InZone(PadSpec spec, Vector3 padLocal, float margin = 0f)
        {
            float h = spec.Width * 0.5f + margin;
            return Mathf.Abs(padLocal.x) <= h && Mathf.Abs(padLocal.z) <= h && padLocal.y >= -margin && padLocal.y <= spec.Height + margin;
        }
    }
}
