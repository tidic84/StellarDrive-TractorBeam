using System;
using System.Collections.Generic;
using Core.Services;
using Players.Services;
using Ships.Joints;
using Ships.Services;
using UnityEngine;
using WorldTracking.Interface.Services;

namespace ParkingTractorBeam.Core
{
    /// <summary>Per-scene cache over the game's ServiceLocator (which scans tagged objects on every call).</summary>
    internal static class Svc
    {
        private static readonly Dictionary<Type, object> Cache = new Dictionary<Type, object>();

        public static void Clear() => Cache.Clear();

        public static T Get<T>() where T : class
        {
            if (Cache.TryGetValue(typeof(T), out object o) && o is T t && !(o is UnityEngine.Object uo && uo == null))
                return t;
            T found = ServiceLocator.GetService<T>();
            if (found != null) Cache[typeof(T)] = found;
            return found;
        }

        public static ShipsClientTracker ShipsClient => Get<ShipsClientTracker>();
        public static ShipsServerTracker ShipsServer => Get<ShipsServerTracker>();
        public static PlayersClientTracker PlayersClient => Get<PlayersClientTracker>();
        public static PlayersServerTracker PlayersServer => Get<PlayersServerTracker>();
        public static IWorldTransposeClient TransposeClient => Get<IWorldTransposeClient>();
        public static ISpacesServerProvider SpacesServer => Get<ISpacesServerProvider>();
        public static ISpacesClientProvider SpacesClient => Get<ISpacesClientProvider>();
        public static ShipsJointsTreeTrackerServer JointsServer => Get<ShipsJointsTreeTrackerServer>();
        public static ShipsJointsTreeTrackerClient JointsClient => Get<ShipsJointsTreeTrackerClient>();
        public static ShipsJointsChangeProviderServer JointChangesServer => Get<ShipsJointsChangeProviderServer>();
        public static ShipsJointsChangeProviderClient JointChangesClient => Get<ShipsJointsChangeProviderClient>();
    }
}
