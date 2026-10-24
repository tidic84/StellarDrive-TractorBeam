using System.Linq;
using UnityEngine;

namespace TractorBeam.Visual
{
    /// <summary>Materials borrowed from the game, so what the mod draws matches it.</summary>
    internal static class Assets
    {
        private static Material _toolAtlas, _spark;
        private static GameObject _root;

        /// <summary>
        /// Holder for the prefabs built at runtime. It must stay active: the game reads part prefabs with
        /// GetComponentsInChildren (sockets, interactions, tool visuals), which skips inactive objects.
        /// It sits far below the world.
        /// </summary>
        public static Transform TemplateRoot
        {
            get
            {
                if (_root == null)
                {
                    _root = new GameObject("TractorBeam.Templates");
                    _root.transform.position = new Vector3(0f, -100000f, 0f);
                    Object.DontDestroyOnLoad(_root);
                }
                return _root.transform;
            }
        }

        public static bool IsTemplate(Component c) => _root != null && c.transform.IsChildOf(_root.transform);

        private static Material Find(string name) =>
            Resources.FindObjectsOfTypeAll<Material>().FirstOrDefault(m => m != null && m.name == name);

        /// <summary>The hand-drawn atlas material of the mining laser (ink lines + hatching + ship palette).</summary>
        public static Material ToolAtlas => _toolAtlas != null ? _toolAtlas : (_toolAtlas = Find("MiningLaser") ?? Find("PartMetal"));

        /// <summary>Additive spark material of the mining laser (a particle shader: it takes vertex colours).</summary>
        public static Material Spark => _spark != null ? _spark : (_spark = Find("LaserSparkMat"));

        public static Material Glow(Color c, float intensity)
        {
            var m = new Material(Spark) { name = "TractorBeam.Glow" };
            m.SetColor("_BaseColor", Color.white);
            m.SetColor("_EmissionColor", c * intensity);
            m.SetColor("_Color", Color.white);
            return m;
        }

        public static GameObject MeshChild(Transform parent, string name, Mesh mesh, Material mat)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;
            return go;
        }

        public static void ClearSceneRefs()
        {
            _toolAtlas = null;
            _spark = null;
        }
    }
}
