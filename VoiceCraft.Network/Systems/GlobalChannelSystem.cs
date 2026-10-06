using System;
using System.Collections.Generic;
using VoiceCraft.Core.World;
using VoiceCraft.Network.Servers;
using VoiceCraft.Network.World;

namespace VoiceCraft.Network.Systems;

/// <summary>
/// A server-wide voice channel next to proximity chat. Each client chooses, through two properties it may
/// set on its own entity, whether it talks on the global channel (heard by everyone listening to it, at any
/// distance and in any world, without effects) and whether it listens to it.
/// Only clients linked to a world (a non-empty <see cref="VoiceCraftEntity.WorldId"/>, which the Minecraft
/// add-on sets while the player is in the game) get the channel, so a device that hasn't bound can't listen in.
/// </summary>
/// <remarks>
/// The channel is a talk/listen bit that no audio effect uses: effects apply to every bit a talker and a
/// listener share, so audio on this bit alone skips proximity, visibility and the rest. A global talker talks
/// only on this bit; a proximity talker on every other bit. While the channel is on, this system owns the
/// talk and listen bitmasks of network entities.
/// </remarks>
public class GlobalChannelSystem : IDisposable
{
    /// <summary>bool, default false: talk on the global channel instead of proximity.</summary>
    public const string TalkProperty = "GlobalChannel:Talk";

    /// <summary>bool, default true: hear the global channel.</summary>
    public const string ListenProperty = "GlobalChannel:Listen";

    private readonly VoiceCraftWorld _world;
    private readonly IEnumerable<VoiceCraftServer> _servers;
    private ushort _bitmask;

    public GlobalChannelSystem(VoiceCraftWorld world, IEnumerable<VoiceCraftServer> servers)
    {
        _world = world;
        _servers = servers;
        _world.OnEntityCreated += OnEntityCreated;
        _world.OnEntityDestroyed += OnEntityDestroyed;
    }

    /// <summary>
    /// The bit of the channel, 0 (the default) to turn it off. Must not overlap any audio effect's bitmask.
    /// </summary>
    public ushort Bitmask
    {
        get => _bitmask;
        set
        {
            if (_bitmask == value) return;
            _bitmask = value;
            foreach (var server in _servers)
            {
                if (value == 0)
                {
                    server.ClientPropertyKeys.Remove(TalkProperty);
                    server.ClientPropertyKeys.Remove(ListenProperty);
                }
                else
                {
                    server.ClientPropertyKeys.Add(TalkProperty);
                    server.ClientPropertyKeys.Add(ListenProperty);
                }
            }

            foreach (var entity in _world.Entities)
            {
                if (entity is not VoiceCraftNetworkEntity networkEntity) continue;
                if (value == 0)
                {
                    networkEntity.TalkBitmask = ushort.MaxValue;
                    networkEntity.ListenBitmask = ushort.MaxValue;
                }
                else
                {
                    Apply(networkEntity);
                }
            }
        }
    }

    public void Dispose()
    {
        _world.OnEntityCreated -= OnEntityCreated;
        _world.OnEntityDestroyed -= OnEntityDestroyed;
        foreach (var entity in _world.Entities)
            OnEntityDestroyed(entity);
        GC.SuppressFinalize(this);
    }

    private void Apply(VoiceCraftNetworkEntity entity)
    {
        if (_bitmask == 0) return;
        var otherBits = (ushort)~_bitmask;
        var linked = !string.IsNullOrWhiteSpace(entity.WorldId);
        var talk = linked && GetBool(entity, TalkProperty, false);
        var listen = linked && GetBool(entity, ListenProperty, true);
        entity.TalkBitmask = talk ? _bitmask : otherBits;
        entity.ListenBitmask = listen ? ushort.MaxValue : otherBits;
    }

    private static bool GetBool(VoiceCraftEntity entity, string key, bool fallback) =>
        entity.TryGetProperty<bool>(key, out var value) ? value : fallback;

    private void OnEntityCreated(VoiceCraftEntity entity)
    {
        if (entity is not VoiceCraftNetworkEntity networkEntity) return;
        networkEntity.OnWorldIdUpdated += OnEntityWorldIdUpdated;
        networkEntity.OnPropertyUpdated += OnEntityPropertyUpdated;
        Apply(networkEntity);
    }

    private void OnEntityDestroyed(VoiceCraftEntity entity)
    {
        entity.OnWorldIdUpdated -= OnEntityWorldIdUpdated;
        entity.OnPropertyUpdated -= OnEntityPropertyUpdated;
    }

    private void OnEntityWorldIdUpdated(string worldId, VoiceCraftEntity entity)
    {
        if (entity is VoiceCraftNetworkEntity networkEntity) Apply(networkEntity);
    }

    private void OnEntityPropertyUpdated(string key, object? value, VoiceCraftEntity entity)
    {
        if (key is not (TalkProperty or ListenProperty)) return;
        if (entity is VoiceCraftNetworkEntity networkEntity) Apply(networkEntity);
    }
}
