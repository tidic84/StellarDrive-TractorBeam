using MelonLoader;
using TractorBeam.Client;
using TractorBeam.Content;
using TractorBeam.Core;
using TractorBeam.Docking;
using TractorBeam.Net;
using TractorBeam.Visual;
using UnityEngine.SceneManagement;

[assembly: MelonInfo(typeof(TractorBeam.TractorMod), "TractorBeam", "0.1.0", "tidic84")]
[assembly: MelonGame("CuriousOwlGames", "StellarDrive")]

namespace TractorBeam
{
    public partial class TractorMod : MelonMod
    {
        internal const string MainGameScene = "MainGame";
        internal const int ProtocolVersion = 1;

        internal static TractorMod Instance { get; private set; }
        internal static MelonLogger.Instance Log => Instance.LoggerInstance;

        internal static bool InGame => SceneManager.GetActiveScene().name == MainGameScene;

        public override void OnInitializeMelon()
        {
            Instance = this;
            MelonPreferences_Category prefs = MelonPreferences.CreateCategory("TractorBeam", "Tractor Beam");
            PadParts.Patch(HarmonyInstance);
            Docks.Patch(HarmonyInstance);
            Tractor.Patch(HarmonyInstance);
            TractorClient.Init(prefs);
#if DEV
            DevInit();
#endif
            Log.Msg("TractorBeam ready.");
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            Svc.Clear();
            Assets.ClearSceneRefs();
            UiKit.ClearCache();
            Docks.Reset();
            TractorClient.Reset();
            if (sceneName == MainGameScene)
            {
                PadParts.EnsureParts();
                PadParts.RegisterResearch();
            }
#if DEV
            DevSceneLoaded(buildIndex, sceneName);
#endif
        }

        public override void OnSceneWasUnloaded(int buildIndex, string sceneName)
        {
            // the host's records live as long as the world (reloaded from the save with the next one)
            if (sceneName == MainGameScene)
            {
                Docks.ResetServer();
                Tractor.ResetServer();
            }
        }

        public override void OnUpdate()
        {
            TractorNet.Tick();
            if (InGame)
            {
                Docks.ClientTick();
                TractorClient.Update();
            }
#if DEV
            DevUpdate();
#endif
        }
    }
}
