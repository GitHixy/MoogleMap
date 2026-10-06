using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Fates;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using MoogleMap.Models;

namespace MoogleMap.Map;

public enum MarkerKind
{
    Party,
    Player,
    Enemy,
    /// <summary>Non-hostile battle NPCs: pets, chocobos, trust and duty support allies.</summary>
    Ally,
    Npc,
    Treasure,
    Aetheryte,
    Gathering,
    /// <summary>Anything else you can click: doors, exits, quest objects, cairns.</summary>
    Interactable,
    Quest,
    Flag,
    Fate,
    /// <summary>A waymark on the ground: A, B, C, D, 1 to 4.</summary>
    Waymark,
    /// <summary>A sign over a character: attack, bind, ignore, shapes.</summary>
    Sign,
    /// <summary>An NPC or object with a quest icon over it: a quest to take, an objective, a turn-in.</summary>
    QuestNpc,
}

public enum FateOutcome : byte
{
    /// <summary>Gone without an ending: out of range, or the zone changed.</summary>
    Vanished,
    Completed,
    Failed,
}

/// <summary>Something that moves or comes and goes, in world space.</summary>
public struct LiveMarker
{
    public MarkerKind Kind;
    public Vector3 Position;
    public float Rotation;
    /// <summary>Hitbox for creatures, area radius for FATEs.</summary>
    public float Radius;
    public string? Name;
    /// <summary>Game icon to draw instead of a shape, or 0.</summary>
    public uint Icon;
    /// <summary>Enemies in combat, FATEs running.</summary>
    public bool Active;
    /// <summary>An enemy whose target is the player.</summary>
    public bool TargetsYou;
    /// <summary>An enemy big enough to be the duty's boss.</summary>
    public bool Boss;
    /// <summary>Party members: 1 tank, 2 melee, 3 ranged, 4 healer, 0 unknown.</summary>
    public byte Role;
    /// <summary>Party members: job abbreviation, like WAR or WHM.</summary>
    public string? Job;
    /// <summary>Party members: health from 0 to 1.</summary>
    public float Health;
    /// <summary>Party members: knocked out.</summary>
    public bool Dead;
    /// <summary>Party members: knocked out, with a raise waiting for them to accept.</summary>
    public bool Raised;
    /// <summary>FATEs: completion from 0 to 1.</summary>
    public float Progress;
    /// <summary>FATEs: seconds left, or a negative number when there's no clock.</summary>
    public long Remaining;
    /// <summary>FATEs: seconds since it first appeared, for its entrance.</summary>
    public float Age;
    /// <summary>FATEs: how far through its exit it is, 0 to 1, or a negative number while it's still on.</summary>
    public float Leaving;
    /// <summary>FATEs leaving: how it ended.</summary>
    public FateOutcome Outcome;
}

/// <summary>
/// Collects what to show on the map once per framework tick. Only things the game itself shows
/// are collected: untargetable objects (hidden traps, invisible triggers) are skipped.
/// </summary>
public sealed class LiveMarkers
{
    private List<LiveMarker> building = new(256);
    private List<LiveMarker> ready = new(256);

    public IReadOnlyList<LiveMarker> All => ready;

    /// <summary>
    /// Where characters are standing this tick, whether or not they're shown: anywhere a person,
    /// NPC or minion stands is walkable, which is what the explorer grows from.
    /// </summary>
    public List<Vector3> Seeds { get; } = new(256);

    public Vector3? PlayerPosition { get; private set; }
    public float PlayerRotation { get; private set; }

    /// <summary>Where the player's current target is, and how big, whatever kind of thing it is.</summary>
    public (Vector3 Position, float Radius)? Target { get; private set; }

    /// <summary>Positions of every object by id this tick, to put signs over the right characters.</summary>
    private readonly Dictionary<ulong, Vector3> positions = new(512);
    private uint[]? waymarkIcons;
    private uint[]? signIcons;

    /// <summary>Seconds a FATE's exit takes on the map.</summary>
    public const float FateExit = 1.1f;

    /// <summary>FATEs seen on the last tick, with when each first showed up.</summary>
    private readonly Dictionary<ushort, (LiveMarker Marker, double Since)> fates = new();
    /// <summary>FATEs on their way out, kept a moment longer so they can leave gracefully.</summary>
    private readonly List<(LiveMarker Marker, double Since)> leaving = [];
    private readonly HashSet<ushort> seenThisTick = [];

    public void Update(Configuration config, MapInfo? map, double now)
    {
        building.Clear();
        Seeds.Clear();
        positions.Clear();

        var me = Plugin.ObjectTable.LocalPlayer;
        PlayerPosition = me?.Position;
        PlayerRotation = me?.Rotation ?? 0f;

        var target = Plugin.TargetManager.Target;
        Target = target is null ? null : (target.Position, target.HitboxRadius);

        // In duties, an enemy with many times the player's health is a boss. Trash sits at a few
        // times a tank's health and bosses at tens of times, so the line falls cleanly between them,
        // whatever the level, without relying on undocumented sheet fields.
        var bossHp = me is not null && map?.IsDuty == true ? (ulong)me.MaxHp * BossHealthRatio : ulong.MaxValue;

        if (me is not null)
        {
            positions[me.GameObjectId] = me.Position;
            foreach (var obj in Plugin.ObjectTable)
            {
                if (obj is null || obj.Address == me.Address) continue;
                positions[obj.GameObjectId] = obj.Position;
                if (obj.ObjectKind is ObjectKind.Pc or ObjectKind.BattleNpc or ObjectKind.EventNpc or ObjectKind.Retainer or ObjectKind.Companion)
                    Seeds.Add(obj.Position);
                if (Classify(config, obj, me.GameObjectId, bossHp) is { } marker)
                    building.Add(marker);
            }
        }

        if ((config.ShowWaymarks || config.ShowSigns) && !Loading())
            AddMarkings(config);

        if (config.ShowFates)
            AddFates(now);
        else
        {
            fates.Clear();
            leaving.Clear();
        }

        // The map agent rebuilds its markers while a zone loads; reading them then reads freed memory.
        if (map is not null && (config.ShowQuestMarkers || config.ShowFlag) && !Loading())
            AddAgentMarkers(config, map);

        (building, ready) = (ready, building);
    }

    public void Clear()
    {
        building.Clear();
        ready.Clear();
        Seeds.Clear();
        positions.Clear();
        fates.Clear();
        leaving.Clear();
        PlayerPosition = null;
        Target = null;
    }

    /// <summary>How many times the player's health an enemy needs to count as a boss.</summary>
    private const uint BossHealthRatio = 15;

    private static LiveMarker? Classify(Configuration config, IGameObject obj, ulong me, ulong bossHp)
    {
        var kind = obj.ObjectKind;

        if (kind == ObjectKind.Pc)
        {
            if (obj is not ICharacter pc) return null;
            var party = (pc.StatusFlags & (StatusFlags.PartyMember | StatusFlags.AllianceMember)) != 0;
            if (party ? !config.ShowParty : !config.ShowPlayers) return null;
            return party ? WithJob(Make(MarkerKind.Party, obj, obj.Name.TextValue), pc) : Make(MarkerKind.Player, obj, obj.Name.TextValue);
        }

        // Anything below must be something you can actually see and click.
        if (!obj.IsTargetable) return null;

        // NPCs and objects with a quest icon over their heads, shown with that icon. Players are
        // left out (theirs is an online status) and so are enemies.
        if (config.ShowQuestNpcs && kind is ObjectKind.EventNpc or ObjectKind.EventObj or ObjectKind.BattleNpc
            && (kind != ObjectKind.BattleNpc || obj is IBattleNpc { StatusFlags: var flags } && (flags & StatusFlags.Hostile) == 0)
            && NamePlateIcon(obj) is var questIcon and not 0)
        {
            var q = Make(MarkerKind.QuestNpc, obj, obj.Name.TextValue);
            q.Icon = questIcon;
            return q;
        }

        switch (kind)
        {
            case ObjectKind.BattleNpc when obj is IBattleNpc npc:
                if (npc.IsDead || npc.MaxHp == 0) return null;

                var hostile = (npc.StatusFlags & StatusFlags.Hostile) != 0;
                if (hostile && npc.BattleNpcKind == BattleNpcSubKind.Combatant)
                {
                    if (!config.ShowEnemies) return null;
                    var m = Make(MarkerKind.Enemy, obj, obj.Name.TextValue);
                    m.Active = (npc.StatusFlags & StatusFlags.InCombat) != 0;
                    m.TargetsYou = npc.TargetObjectId == me;
                    m.Health = Math.Clamp(npc.CurrentHp / (float)npc.MaxHp, 0f, 1f);
                    m.Boss = config.BossSpotlight && npc.MaxHp >= bossHp;
                    return m;
                }

                if (npc.BattleNpcKind == BattleNpcSubKind.NpcPartyMember)
                    return config.ShowParty ? WithJob(Make(MarkerKind.Party, obj, obj.Name.TextValue), npc) : null;

                // Pets and chocobos crowd the map for nothing; other friendly fighters stay.
                if (npc.BattleNpcKind is BattleNpcSubKind.Pet or BattleNpcSubKind.Buddy) return null;
                return config.ShowNpcs ? Make(MarkerKind.Ally, obj, obj.Name.TextValue) : null;

            case ObjectKind.EventNpc:
                return config.ShowNpcs ? Make(MarkerKind.Npc, obj, obj.Name.TextValue) : null;

            case ObjectKind.Treasure:
                return config.ShowObjects ? Make(MarkerKind.Treasure, obj, obj.Name.TextValue) : null;

            case ObjectKind.Aetheryte:
                return config.ShowObjects ? Make(MarkerKind.Aetheryte, obj, obj.Name.TextValue) : null;

            case ObjectKind.GatheringPoint:
                return config.ShowGathering ? Make(MarkerKind.Gathering, obj, obj.Name.TextValue) : null;

            case ObjectKind.EventObj:
                // Nameless event objects are triggers and scenery, not things worth a marker.
                var name = obj.Name.TextValue;
                return config.ShowObjects && name.Length > 0 ? Make(MarkerKind.Interactable, obj, name) : null;
        }

        return null;
    }

    /// <summary>The icon the game shows over an object's head, or 0. A plain value read from the object itself.</summary>
    private static unsafe uint NamePlateIcon(IGameObject obj)
    {
        var native = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)obj.Address;
        return native is null ? 0 : native->NamePlateIconId;
    }

    /// <summary>Quest names by objective id, read once from the Quest sheet.</summary>
    private static readonly Dictionary<uint, string?> QuestNames = new();

    /// <summary>
    /// A quest's name from the id the map agent gives its markers. Quest rows start at 65536,
    /// and the id may come with or without that offset. Leves and other objectives have no name here.
    /// </summary>
    private static string? QuestName(uint objective)
    {
        if (objective == 0) return null;
        if (QuestNames.TryGetValue(objective, out var known)) return known;

        var sheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Quest>();
        var row = sheet.GetRowOrDefault(objective) ?? (objective < 0x10000 ? sheet.GetRowOrDefault(objective + 0x10000) : null);
        var name = row?.Name.ToString();
        QuestNames[objective] = string.IsNullOrWhiteSpace(name) ? null : name;
        return QuestNames[objective];
    }

    /// <summary>Adds the role, job abbreviation and health of a party member.</summary>
    private static LiveMarker WithJob(LiveMarker marker, ICharacter character)
    {
        marker.Health = character.MaxHp > 0 ? Math.Clamp(character.CurrentHp / (float)character.MaxHp, 0f, 1f) : 1f;
        marker.Dead = character.IsDead || (character.MaxHp > 0 && character.CurrentHp == 0);

        // Raise (148, and 1140 in some duties): already being brought back, no need to raise again.
        if (marker.Dead && character is IBattleChara fighter)
        {
            foreach (var status in fighter.StatusList)
            {
                if (status.StatusId is not (148 or 1140)) continue;
                marker.Raised = true;
                break;
            }
        }

        if (character.ClassJob.ValueNullable is { } job)
        {
            marker.Role = job.Role;
            marker.Job = job.Abbreviation.ToString();
        }

        return marker;
    }

    private static LiveMarker Make(MarkerKind kind, IGameObject obj, string? name) => new()
    {
        Kind = kind,
        Position = obj.Position,
        Rotation = obj.Rotation,
        Radius = obj.HitboxRadius,
        Name = name,
    };

    /// <summary>
    /// Running FATEs, plus the ones that just ended, kept for a moment so they can leave the map
    /// with a flourish for a win, a wilt for a failure, or a plain fade when they just drop out.
    /// </summary>
    private void AddFates(double now)
    {
        seenThisTick.Clear();

        foreach (var fate in Plugin.FateTable)
        {
            if (fate is null) continue;
            var id = fate.FateId;

            if (fate.State is FateState.Ending or FateState.Ended or FateState.Failed)
            {
                if (fates.Remove(id, out var ended))
                    Leave(ended.Marker, fate.State == FateState.Failed ? FateOutcome.Failed : FateOutcome.Completed, now);
                continue;
            }

            if (fate.State is not (FateState.Running or FateState.Preparing)) continue;

            seenThisTick.Add(id);
            var since = fates.TryGetValue(id, out var known) ? known.Since : now;
            var marker = new LiveMarker
            {
                Kind = MarkerKind.Fate,
                Position = fate.Position,
                Radius = fate.Radius,
                Name = fate.Name.TextValue,
                Icon = fate.MapIconId != 0 ? fate.MapIconId : fate.IconId,
                Active = fate.State == FateState.Running,
                Progress = fate.Progress / 100f,
                Remaining = fate.State == FateState.Running ? fate.TimeRemaining : -1,
                Age = (float)(now - since),
                Leaving = -1f,
            };

            fates[id] = (marker, since);
            building.Add(marker);
        }

        // Gone from the table without an ending we saw: out of range, or finished between ticks.
        if (fates.Count > seenThisTick.Count)
        {
            foreach (var id in new List<ushort>(fates.Keys))
            {
                if (seenThisTick.Contains(id)) continue;
                var last = fates[id].Marker;
                Leave(last, last.Progress >= 0.999f ? FateOutcome.Completed : FateOutcome.Vanished, now);
                fates.Remove(id);
            }
        }

        for (var i = 0; i < leaving.Count; i++)
        {
            var (marker, since) = leaving[i];
            var t = (float)((now - since) / FateExit);
            if (t >= 1f)
            {
                leaving.RemoveAt(i--);
                continue;
            }

            marker.Leaving = t;
            building.Add(marker);
        }
    }

    /// <summary>On a zone change: FATEs left behind don't get an exit on a map they're not on.</summary>
    public void ForgetFates()
    {
        fates.Clear();
        leaving.Clear();
    }

    private void Leave(LiveMarker marker, FateOutcome outcome, double now)
    {
        marker.Outcome = outcome;
        marker.Leaving = 0f;
        if (outcome == FateOutcome.Completed) marker.Progress = 1f;
        leaving.Add((marker, now));
    }

    /// <summary>Waymarks on the ground and signs over characters, from the game's marking controller.</summary>
    private unsafe void AddMarkings(Configuration config)
    {
        var marking = FFXIVClientStructs.FFXIV.Client.Game.UI.MarkingController.Instance();
        if (marking is null) return;

        waymarkIcons ??= LoadWaymarkIcons();
        signIcons ??= LoadSignIcons();

        if (config.ShowWaymarks)
        {
            var field = marking->FieldMarkers;
            for (var i = 0; i < field.Length && i < waymarkIcons.Length; i++)
            {
                if (!field[i].Active) continue;
                building.Add(new LiveMarker { Kind = MarkerKind.Waymark, Position = field[i].Position, Icon = waymarkIcons[i] });
            }
        }

        if (config.ShowSigns)
        {
            var signs = marking->Markers;
            for (var i = 0; i < signs.Length && i < signIcons.Length; i++)
            {
                ulong id = signs[i];
                if (id == 0 || id == 0xE0000000 || !positions.TryGetValue(id, out var at)) continue;
                building.Add(new LiveMarker { Kind = MarkerKind.Sign, Position = at, Icon = signIcons[i] });
            }
        }
    }

    /// <summary>Map icons of waymarks A to 4, in the order the marking controller keeps them.</summary>
    private static uint[] LoadWaymarkIcons()
    {
        var sheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.FieldMarker>();
        var icons = new List<uint>();
        for (var row = 1u; row <= 8; row++)
            icons.Add(sheet.GetRowOrDefault(row) is { } r ? (r.MapIcon != 0 ? r.MapIcon : r.UiIcon) : 0u);
        return [.. icons];
    }

    /// <summary>Icons of the character signs, in the order the marking controller keeps them.</summary>
    private static uint[] LoadSignIcons()
    {
        var sheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Marker>();
        var icons = new List<uint>();
        for (var row = 1u; row <= 17; row++)
            icons.Add(sheet.GetRowOrDefault(row) is { } r ? (uint)Math.Max(0, r.Icon) : 0u);
        return [.. icons];
    }

    private static bool Loading()
        => Plugin.Condition[ConditionFlag.BetweenAreas] || Plugin.Condition[ConditionFlag.BetweenAreas51];

    /// <summary>Quest objectives and the flag come from the game's own map agent.</summary>
    private unsafe void AddAgentMarkers(Configuration config, MapInfo map)
    {
        var agent = AgentMap.Instance();
        if (agent is null || agent->CurrentMapId != map.RowId) return;

        if (config.ShowQuestMarkers)
        {
            // Only plain values are copied out. The tooltip text is a pointer into memory the game
            // frees and reuses as it rebuilds markers, so it's never followed.
            foreach (var data in agent->EventMarkers)
            {
                if (data.MapId != map.RowId || data.IconId == 0) continue;

                building.Add(new LiveMarker
                {
                    Kind = MarkerKind.Quest,
                    Position = data.Position,
                    Radius = data.Radius,
                    Icon = data.IconId,
                    Name = QuestName(data.ObjectiveId),
                });
            }
        }

        if (config.ShowFlag && agent->FlagMarkerCount > 0)
        {
            var flag = agent->FlagMapMarkers[0];
            if (flag.MapId == map.RowId)
            {
                building.Add(new LiveMarker
                {
                    Kind = MarkerKind.Flag,
                    Position = new Vector3(flag.XFloat, 0f, flag.YFloat),
                    Icon = flag.MapMarker.IconId != 0 ? flag.MapMarker.IconId : 60561u,
                });
            }
        }
    }
}
