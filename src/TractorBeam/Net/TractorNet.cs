using System;
using System.Collections.Generic;
using System.IO;
using FishNet;
using FishNet.Broadcast;
using FishNet.Connection;
using FishNet.Managing;
using FishNet.Serializing;
using FishNet.Transporting;
using UnityEngine;

namespace TractorBeam.Net
{
    /// <summary>The single FishNet broadcast the mod uses; the payload is our own binary format (see <see cref="Op"/>).</summary>
    public struct TractorPacket : IBroadcast
    {
        public byte[] Data;
    }

    internal enum Op : byte
    {
        // client -> server
        Hello = 1,
        Request = 2,

        // server -> client
        Result = 64,
        Beam = 65,
        Docks = 66,
    }

    /// <summary>Registers the broadcast with whichever NetworkManager is running and routes packets by opcode.</summary>
    internal static class TractorNet
    {
        public delegate void ServerHandler(NetworkConnection sender, BinaryReader r);
        public delegate void ClientHandler(BinaryReader r);

        private static readonly Dictionary<Op, ServerHandler> ServerHandlers = new Dictionary<Op, ServerHandler>();
        private static readonly Dictionary<Op, ClientHandler> ClientHandlers = new Dictionary<Op, ClientHandler>();

        private static NetworkManager _registeredOn;
        private static bool _serializersSet;
        private static bool _helloSent;

        public static event Action<NetworkConnection> OnClientHello;

        public static bool IsServer => InstanceFinder.IsServerStarted;
        public static bool IsClient => InstanceFinder.IsClientStarted;

        public static void OnServer(Op op, ServerHandler h) => ServerHandlers[op] = h;
        public static void OnClient(Op op, ClientHandler h) => ClientHandlers[op] = h;

        /// <summary>Called every frame: (re)binds to the current NetworkManager and says hello once the client is up.</summary>
        public static void Tick()
        {
            EnsureSerializers();
            NetworkManager nm = InstanceFinder.NetworkManager;
            if (nm == null)
            {
                _registeredOn = null;
                _helloSent = false;
                return;
            }
            if (_registeredOn != nm)
            {
                _registeredOn = nm;
                _helloSent = false;
                nm.ServerManager.RegisterBroadcast<TractorPacket>(OnServerPacket);
                nm.ClientManager.RegisterBroadcast<TractorPacket>(OnClientPacket);
                TractorMod.Log.Msg("Network channel registered.");
            }
            if (!_helloSent && nm.ClientManager.Started && nm.ClientManager.Connection != null && nm.ClientManager.Connection.IsAuthenticated)
            {
                _helloSent = true;
                SendToServer(Op.Hello, w => w.Write(TractorMod.ProtocolVersion));
            }
        }

        private static void EnsureSerializers()
        {
            if (_serializersSet) return;
            _serializersSet = true;
            GenericWriter<TractorPacket>.SetWrite(WritePacket);
            GenericReader<TractorPacket>.SetRead(ReadPacket);
        }

        private static void WritePacket(Writer w, TractorPacket p) => w.WriteBytesAndSize(p.Data ?? Array.Empty<byte>());

        private static TractorPacket ReadPacket(Reader r) => new TractorPacket { Data = r.ReadBytesAndSizeAllocated() };

        private static byte[] Build(Op op, Action<BinaryWriter> body)
        {
            using (var ms = new MemoryStream(64))
            using (var w = new BinaryWriter(ms))
            {
                w.Write((byte)op);
                body?.Invoke(w);
                w.Flush();
                return ms.ToArray();
            }
        }

        public static void SendToServer(Op op, Action<BinaryWriter> body, Channel channel = Channel.Reliable)
        {
            if (!IsClient) return;
            InstanceFinder.ClientManager.Broadcast(new TractorPacket { Data = Build(op, body) }, channel);
        }

        public static void SendToAll(Op op, Action<BinaryWriter> body, Channel channel = Channel.Reliable)
        {
            if (!IsServer) return;
            InstanceFinder.ServerManager.Broadcast(new TractorPacket { Data = Build(op, body) }, true, channel);
        }

        public static void SendToAllExcept(NetworkConnection except, Op op, Action<BinaryWriter> body, Channel channel = Channel.Reliable)
        {
            if (!IsServer) return;
            InstanceFinder.ServerManager.BroadcastExcept(except, new TractorPacket { Data = Build(op, body) }, true, channel);
        }

        public static void SendTo(NetworkConnection conn, Op op, Action<BinaryWriter> body, Channel channel = Channel.Reliable)
        {
            if (!IsServer || conn == null) return;
            InstanceFinder.ServerManager.Broadcast(conn, new TractorPacket { Data = Build(op, body) }, true, channel);
        }

        private static void OnServerPacket(NetworkConnection conn, TractorPacket p, Channel channel)
        {
            if (p.Data == null || p.Data.Length == 0) return;
            var op = (Op)p.Data[0];
            try
            {
                using (var r = new BinaryReader(new MemoryStream(p.Data, 1, p.Data.Length - 1)))
                {
                    if (op == Op.Hello)
                    {
                        int version = r.ReadInt32();
                        if (version != TractorMod.ProtocolVersion)
                            TractorMod.Log.Warning($"Client {conn.ClientId} runs Tractor Beam protocol {version}, host runs {TractorMod.ProtocolVersion}.");
                        OnClientHello?.Invoke(conn);
                        return;
                    }
                    if (ServerHandlers.TryGetValue(op, out var h)) h(conn, r);
                }
            }
            catch (Exception ex)
            {
                TractorMod.Log.Error($"Server packet {op} failed: {ex}");
            }
        }

        private static void OnClientPacket(TractorPacket p, Channel channel)
        {
            if (p.Data == null || p.Data.Length == 0) return;
            var op = (Op)p.Data[0];
            try
            {
                using (var r = new BinaryReader(new MemoryStream(p.Data, 1, p.Data.Length - 1)))
                {
                    if (ClientHandlers.TryGetValue(op, out var h)) h(r);
                }
            }
            catch (Exception ex)
            {
                TractorMod.Log.Error($"Client packet {op} failed: {ex}");
            }
        }

        // ---- small helpers for our binary format

        public static void Write(this BinaryWriter w, Vector3 v)
        {
            w.Write(v.x); w.Write(v.y); w.Write(v.z);
        }

        public static Vector3 ReadVector3(this BinaryReader r) => new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

        public static void Write(this BinaryWriter w, Vector3Int v)
        {
            w.Write(v.x); w.Write(v.y); w.Write(v.z);
        }

        public static Vector3Int ReadVector3Int(this BinaryReader r) => new Vector3Int(r.ReadInt32(), r.ReadInt32(), r.ReadInt32());

        public static void Write(this BinaryWriter w, Quaternion q)
        {
            w.Write(q.x); w.Write(q.y); w.Write(q.z); w.Write(q.w);
        }

        public static Quaternion ReadQuaternion(this BinaryReader r) => new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
    }
}
