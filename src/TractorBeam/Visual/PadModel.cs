using UnityEngine;

namespace TractorBeam.Visual
{
    /// <summary>
    /// The tractor pad: a low bolted plate (its top is the landing surface, <see cref="Docking.Pads.TopHeight"/>) with
    /// the beam emitter ring in the middle and hazard marks, one chevron per size.
    /// </summary>
    internal static class PadModel
    {
        public static GameObject Build(int size)
        {
            var k = new MeshKit();
            // base plate and deck
            k.ChamferBox(new Vector3(0f, 0.03f, 0f), new Vector3(0.98f, 0.06f, 0.98f), 0.015f, Shade.HatchDark, Shade.Dark);
            k.ChamferBox(new Vector3(0f, 0.08f, 0f), new Vector3(0.9f, 0.04f, 0.9f), 0.01f, Shade.Grey, Shade.Dark);
            // emitter: stepped ring around a lens
            k.With(new Vector3(0f, 0.1f, 0f), new Vector3(-90f, 0f, 0f), () =>
            {
                k.Tube(new Vector3(0f, 0f, 0.0075f), 0.36f, 0.3f, 0.015f, 32, Shade.Light, Shade.Dark);
                k.Tube(new Vector3(0f, 0f, 0.01f), 0.28f, 0.2f, 0.02f, 32, Shade.Blue, Shade.Dark);
                k.Cylinder(new Vector3(0f, 0f, 0.005f), 0.19f, 0.01f, 24, Shade.Black, Shade.Dark);
                for (int i = 0; i < 8; i++)
                {
                    float a = i * Mathf.PI / 4f;
                    k.Cylinder(new Vector3(Mathf.Cos(a) * 0.33f, Mathf.Sin(a) * 0.33f, 0.017f), 0.008f, 0.004f, 6, Shade.Black, Shade.Black);
                }
            });
            // hazard corners
            for (int x = -1; x <= 1; x += 2)
                for (int z = -1; z <= 1; z += 2)
                {
                    k.Box(new Vector3(x * 0.39f, 0.101f, z * 0.3f), new Vector3(0.06f, 0.002f, 0.16f), Shade.Yellow);
                    k.Box(new Vector3(x * 0.3f, 0.101f, z * 0.39f), new Vector3(0.16f, 0.002f, 0.06f), Shade.Yellow);
                }
            // size chevrons on the front edge (the side the landed ship faces)
            for (int i = 0; i < size; i++)
            {
                float z = 0.405f - i * 0.03f;
                k.Box(new Vector3(-0.025f, 0.101f, z), new Vector3(0.05f, 0.002f, 0.014f), Shade.Orange);
                k.Box(new Vector3(0.025f, 0.101f, z), new Vector3(0.05f, 0.002f, 0.014f), Shade.Orange);
            }
            var root = new GameObject("TractorPadModel");
            Assets.MeshChild(root.transform, "Body", k.Build("TractorBeam.Pad" + size), Assets.ToolAtlas);
            return root;
        }
    }
}
