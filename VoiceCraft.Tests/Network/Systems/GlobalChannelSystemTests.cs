using System;
using System.Numerics;
using VoiceCraft.Core.World;
using VoiceCraft.Network;
using VoiceCraft.Network.Audio.Effects;
using VoiceCraft.Network.NetPeers;
using VoiceCraft.Network.Servers;
using VoiceCraft.Network.Systems;
using VoiceCraft.Network.World;

namespace VoiceCraft.Tests.Network.Systems;

public class GlobalChannelSystemTests
{
    private const ushort Channel = 16;
    private const ushort OtherBits = unchecked((ushort)~Channel);
    private const string World = "minecraft:overworld";

    [Fact]
    public void UnlinkedClient_CanNeitherTalkNorListenOnTheChannel()
    {
        using var world = new VoiceCraftWorld();
        using var system = new GlobalChannelSystem(world, []) { Bitmask = Channel };
        var entity = CreateNetworkEntity(1);
        entity.SetProperty(GlobalChannelSystem.TalkProperty, true);

        world.AddEntity(entity);

        Assert.Equal(OtherBits, entity.TalkBitmask);
        Assert.Equal(OtherBits, entity.ListenBitmask);
    }

    [Fact]
    public void LinkedClient_TalksNearbyAndHearsTheChannel_ByDefault()
    {
        using var world = new VoiceCraftWorld();
        using var system = new GlobalChannelSystem(world, []) { Bitmask = Channel };
        var entity = CreateNetworkEntity(1);
        world.AddEntity(entity);

        entity.WorldId = World;

        Assert.Equal(OtherBits, entity.TalkBitmask);
        Assert.Equal(ushort.MaxValue, entity.ListenBitmask);
    }

    [Fact]
    public void Properties_SwitchTalkAndListen_AndUnlinkingDropsTheChannel()
    {
        using var world = new VoiceCraftWorld();
        using var system = new GlobalChannelSystem(world, []) { Bitmask = Channel };
        var entity = CreateNetworkEntity(1);
        world.AddEntity(entity);
        entity.WorldId = World;

        entity.SetProperty(GlobalChannelSystem.TalkProperty, true);
        entity.SetProperty(GlobalChannelSystem.ListenProperty, false);
        Assert.Equal(Channel, entity.TalkBitmask);
        Assert.Equal(OtherBits, entity.ListenBitmask);

        entity.SetProperty(GlobalChannelSystem.ListenProperty, true);
        Assert.Equal(ushort.MaxValue, entity.ListenBitmask);

        entity.WorldId = string.Empty;
        Assert.Equal(OtherBits, entity.TalkBitmask);
        Assert.Equal(OtherBits, entity.ListenBitmask);
    }

    [Fact]
    public void GlobalTalker_IsHeardFarAway_OnlyByLinkedListenersOfTheChannel()
    {
        using var world = new VoiceCraftWorld();
        using var effects = new AudioEffectSystem();
        effects.SetEffect(1, new VisibilityEffect());
        effects.SetEffect(2, new ProximityEffect { MaxRange = 30 });
        using var system = new GlobalChannelSystem(world, []) { Bitmask = Channel };
        var visibility = new VisibilitySystem(world, effects);

        var talker = AddLinked(world, 1, Vector3.Zero, "minecraft:the_nether");
        var farListener = AddLinked(world, 2, new Vector3(1000, 0, 0));
        var farNotListening = AddLinked(world, 3, new Vector3(1000, 0, 0));
        var unlinkedNearby = CreateNetworkEntity(4);
        world.AddEntity(unlinkedNearby);
        var nearbyPlayer = AddLinked(world, 5, new Vector3(5, 0, 0), "minecraft:the_nether");
        farNotListening.SetProperty(GlobalChannelSystem.ListenProperty, false);

        talker.SetProperty(GlobalChannelSystem.TalkProperty, true);
        visibility.Update();

        Assert.Contains(farListener, talker.VisibleEntities); // other dimension, 1000 blocks away
        Assert.DoesNotContain(farNotListening, talker.VisibleEntities);
        Assert.DoesNotContain(unlinkedNearby, talker.VisibleEntities);
        // The global talker still hears players near them, who talk with proximity.
        Assert.Contains(talker, nearbyPlayer.VisibleEntities);
        Assert.DoesNotContain(farListener, nearbyPlayer.VisibleEntities);

        talker.SetProperty(GlobalChannelSystem.TalkProperty, false);
        visibility.Update();

        Assert.DoesNotContain(farListener, talker.VisibleEntities);
        Assert.Contains(nearbyPlayer, talker.VisibleEntities);
    }

    [Fact]
    public void Bitmask_LetsClientsSetTheChannelProperties_AndZeroTurnsItOff()
    {
        using var world = new VoiceCraftWorld();
        using var server = new LiteNetVoiceCraftServer(world);
        using var system = new GlobalChannelSystem(world, [server]);
        var entity = CreateNetworkEntity(1);
        world.AddEntity(entity);
        entity.WorldId = World;
        Assert.Equal(ushort.MaxValue, entity.TalkBitmask); // off by default: nothing changes
        Assert.Empty(server.ClientPropertyKeys);

        system.Bitmask = Channel;
        Assert.Contains(GlobalChannelSystem.TalkProperty, server.ClientPropertyKeys);
        Assert.Contains(GlobalChannelSystem.ListenProperty, server.ClientPropertyKeys);
        Assert.Equal(OtherBits, entity.TalkBitmask);

        system.Bitmask = 0;
        Assert.Empty(server.ClientPropertyKeys);
        Assert.Equal(ushort.MaxValue, entity.TalkBitmask);
        Assert.Equal(ushort.MaxValue, entity.ListenBitmask);
    }

    private static VoiceCraftNetworkEntity AddLinked(VoiceCraftWorld world, int id, Vector3 position,
        string worldId = World)
    {
        var entity = CreateNetworkEntity(id);
        world.AddEntity(entity);
        entity.Position = position;
        entity.WorldId = worldId;
        return entity;
    }

    private static VoiceCraftNetworkEntity CreateNetworkEntity(int id)
    {
        return new VoiceCraftNetworkEntity(
            new FakeNetPeer(Guid.NewGuid(), Guid.NewGuid(), "en-US", PositioningType.Server),
            id);
    }

    private sealed class FakeNetPeer(Guid userGuid, Guid serverUserGuid, string locale, PositioningType positioningType)
        : VoiceCraftNetPeer(null, userGuid, serverUserGuid, locale, positioningType)
    {
        public override VcConnectionState ConnectionState => VcConnectionState.Connected;
    }
}
