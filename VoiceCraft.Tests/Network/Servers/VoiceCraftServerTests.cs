using System;
using System.Net;
using VoiceCraft.Core.World;
using VoiceCraft.Network;
using VoiceCraft.Network.NetPeers;
using VoiceCraft.Network.Packets.VcPackets;
using VoiceCraft.Network.Packets.VcPackets.Request;
using VoiceCraft.Network.Packets.VcPackets.Response;
using VoiceCraft.Network.Servers;
using VoiceCraft.Network.World;

namespace VoiceCraft.Tests.Network.Servers;

public class VoiceCraftServerTests
{
    [Fact]
    public void InfoRequest_SendsInfoResponse()
    {
        using var world = new VoiceCraftWorld();
        var server = new TestVoiceCraftServer(world);
        var endpoint = new IPEndPoint(IPAddress.Loopback, 9050);

        var request = PacketPool<VcInfoRequestPacket>.GetPacket(() => new VcInfoRequestPacket());
        request.Set(123);
        server.Dispatch(request, endpoint);

        var response = Assert.IsType<VcInfoResponsePacket>(server.LastUnconnectedPacket);
        Assert.Equal(server.Motd, response.Motd);
        Assert.Equal(server.PositioningType, response.PositioningType);
        Assert.Equal(123, response.Tick);
    }

    [Fact]
    public void LoginRequest_WithIncompatibleVersion_IsRejected()
    {
        using var world = new VoiceCraftWorld();
        var server = new TestVoiceCraftServer(world);
        var packet = new VcLoginRequestPacket();
        packet.Set(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "en-US",
            new Version(999, 0, 0),
            PositioningType.Client);

        server.Dispatch(packet, new object());

        Assert.False(server.Accepted);
        Assert.Equal("VoiceCraft.DisconnectReason.IncompatibleVersion", server.LastRejectedReason);
    }

    [Fact]
    public void LoginRequest_WhenServerFull_IsRejected()
    {
        using var world = new VoiceCraftWorld();
        var server = new TestVoiceCraftServer(world)
        {
            SimulatedConnectedPeers = 1,
            SimulatedMaxClients = 1
        };
        var packet = CreateValidLoginPacket(PositioningType.Client);

        server.Dispatch(packet, new object());

        Assert.False(server.Accepted);
        Assert.Equal("VoiceCraft.DisconnectReason.ServerFull", server.LastRejectedReason);
    }

    [Fact]
    public void LoginRequest_WhenPositioningTypeMismatches_IsRejected()
    {
        using var world = new VoiceCraftWorld();
        var server = new TestVoiceCraftServer(world)
        {
            SimulatedPositioningType = PositioningType.Server
        };
        var packet = CreateValidLoginPacket(PositioningType.Client);

        server.Dispatch(packet, new object());

        Assert.False(server.Accepted);
        Assert.Equal("VoiceCraft.DisconnectReason.ServerSidedOnly", server.LastRejectedReason);
    }

    [Fact]
    public void LoginRequest_WhenValid_IsAccepted()
    {
        using var world = new VoiceCraftWorld();
        var server = new TestVoiceCraftServer(world);
        var marker = new object();
        var packet = CreateValidLoginPacket(PositioningType.Client);

        server.Dispatch(packet, marker);

        Assert.True(server.Accepted);
        Assert.Same(marker, server.AcceptedData);
        Assert.Null(server.LastRejectedReason);
    }

    [Fact]
    public void SetPropertyRequest_FromServerPositionedClient_OnlySetsClientPropertyKeys()
    {
        using var world = new VoiceCraftWorld();
        var server = new TestVoiceCraftServer(world);
        server.ClientPropertyKeys.Add("Allowed");
        var peer = new ConnectedNetPeer(PositioningType.Server);
        var entity = new VoiceCraftNetworkEntity(peer, 1);
        peer.Tag = entity;

        server.Dispatch(CreatePropertyPacket("Allowed", true), peer);
        server.Dispatch(CreatePropertyPacket("ProximityEffect:MaxRange", 100000f), peer);

        Assert.True(entity.TryGetProperty<bool>("Allowed", out var allowed) && allowed);
        Assert.False(entity.TryGetProperty<float>("ProximityEffect:MaxRange", out _));
    }

    private static VcSetPropertyRequestPacket CreatePropertyPacket(string key, object value)
    {
        var packet = new VcSetPropertyRequestPacket();
        packet.Set(key, value);
        return packet;
    }

    private sealed class ConnectedNetPeer(PositioningType positioningType)
        : VoiceCraftNetPeer(null, Guid.NewGuid(), Guid.NewGuid(), "en-US", positioningType)
    {
        public override VcConnectionState ConnectionState => VcConnectionState.Connected;
    }

    private static VcLoginRequestPacket CreateValidLoginPacket(PositioningType positioningType)
    {
        var packet = new VcLoginRequestPacket();
        packet.Set(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "en-US",
            VoiceCraftServer.Version,
            positioningType);
        return packet;
    }

    private sealed class TestVoiceCraftServer(VoiceCraftWorld world) : VoiceCraftServer(world)
    {
        public bool Accepted { get; private set; }
        public object? AcceptedData { get; private set; }
        public string? LastRejectedReason { get; private set; }
        public IVoiceCraftPacket? LastUnconnectedPacket { get; private set; }
        public int SimulatedConnectedPeers { get; set; }
        public uint SimulatedMaxClients { get; set; } = 10;
        public PositioningType SimulatedPositioningType { get; set; } = PositioningType.Client;

        public override string Motd => "Test Motd";
        public override PositioningType PositioningType => SimulatedPositioningType;
        public override uint MaxClients => SimulatedMaxClients;
        public override int ConnectedPeers => SimulatedConnectedPeers;

        public void Dispatch(IVoiceCraftPacket packet, object? data)
        {
            ExecutePacket(packet, data);
        }

        public override void Start()
        {
        }

        public override void Update()
        {
        }

        public override void Stop()
        {
        }

        public override void SendUnconnectedPacket<T>(IPEndPoint endPoint, T packet)
        {
            LastUnconnectedPacket = packet;
        }

        public override void SendPacket<T>(VoiceCraftNetPeer vcNetPeer, T packet, VcDeliveryMethod deliveryMethod = VcDeliveryMethod.Reliable)
        {
        }

        public override void Broadcast<T>(T packet, VcDeliveryMethod deliveryMethod = VcDeliveryMethod.Reliable, params VoiceCraftNetPeer?[] excludes)
        {
        }

        public override void Disconnect(VoiceCraftNetPeer vcNetPeer, string reason, bool force = false)
        {
        }

        public override void DisconnectAll(string? reason = null)
        {
        }

        protected override void AcceptRequest(VcLoginRequestPacket packet, object? data)
        {
            Accepted = true;
            AcceptedData = data;
        }

        protected override void RejectRequest(VcLoginRequestPacket packet, string reason, object? data)
        {
            LastRejectedReason = reason;
        }
    }
}
