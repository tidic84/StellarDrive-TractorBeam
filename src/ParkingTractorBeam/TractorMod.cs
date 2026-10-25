using MelonLoader;
using ParkingTractorBeam.Client;
using ParkingTractorBeam.Content;
using ParkingTractorBeam.Core;
using ParkingTractorBeam.Docking;
using ParkingTractorBeam.Net;
using ParkingTractorBeam.Visual;
using UnityEngine.SceneManagement;

[assembly: MelonInfo(typeof(ParkingTractorBeam.TractorMod), "ParkingTractorBeam", "0.3.0", "tidic84")]
[assembly: MelonGame("CuriousOwlGames", "StellarDrive")]

namespace ParkingTractorBeam
{
    public partial class TractorMod : MelonMod
    {
        internal const string MainGameScene = "MainGame";
        internal const int ProtocolVersion = 2;

        internal static TractorMod Instance { get; private set; }
        internal static MelonLogger.Instance Log => Instance.LoggerInstance;

        internal static bool InGame => SceneManager.GetActiveScene().name == MainGameScene;

        public override void OnInitializeMelon()
        {
            Instance = this;
            MelonPreferences_Category prefs = MelonPreferences.CreateCategory("ParkingTractorBeam", "Parking Tractor Beam");
            PadParts.Patch(HarmonyInstance);
            Docks.Patch(HarmonyInstance);
            PadConfigs.Patch(HarmonyInstance);
            PadMenu.Patch(HarmonyInstance);
            Tractor.Patch(HarmonyInstance);
            TractorClient.Init(prefs);
#if DEV
            DevInit();
#endif
            // its former name: both at once register the same parts twice and the world never loads
            foreach (MelonBase m in MelonBase.RegisteredMelons)
                if (m.Info.Name == "TractorBeam")
                    Log.Error("Tractor Beam (the former name of this mod) is installed too: remove Mods/TractorBeam.dll.");
            Log.Msg("ParkingTractorBeam ready.");
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            Svc.Clear();
            Assets.ClearSceneRefs();
            UiKit.ClearCache();
            Docks.Reset();
            TractorClient.Reset();
            PadMenu.Reset();
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
                PadConfigs.Reset();
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
                PadMenu.Update();
            }
#if DEV
            DevUpdate();
#endif
        }
    }
}
