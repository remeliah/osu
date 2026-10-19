// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.IO;
using System.Text;

namespace osu.Game.Online.Bancho
{
    [Flags]
    internal enum LegacyMatchSlotStatus : byte
    {
        Open = 1,
        Locked = 2,
        NotReady = 4,
        Ready = 8,
        NoMap = 16,
        Playing = 32,
        Complete = 64,
        Quit = 128,
        HasPlayer = NotReady | Ready | NoMap | Playing | Complete,
    }

    internal enum LegacyMatchTeam : byte
    {
        Neutral,
        Blue,
        Red,
    }

    internal sealed class LegacyBanchoMatch
    {
        public const int SLOT_COUNT = 16;

        public ushort ID;
        public bool InProgress;
        public byte Powerplay;
        public uint Mods;
        public string Name = string.Empty;
        public string Password = string.Empty;
        public bool HasPassword;
        public string BeatmapName = string.Empty;
        public int BeatmapID;
        public string BeatmapChecksum = string.Empty;
        public LegacyMatchSlotStatus[] SlotStatuses = new LegacyMatchSlotStatus[SLOT_COUNT];
        public LegacyMatchTeam[] SlotTeams = new LegacyMatchTeam[SLOT_COUNT];
        public int[] SlotUserIDs = new int[SLOT_COUNT];
        public int HostID;
        public byte RulesetID;
        public byte WinCondition;
        public byte TeamType;
        public bool FreeMods;
        public uint[] SlotMods = new uint[SLOT_COUNT];
        public int Seed;

        public LegacyBanchoMatch Clone() => new LegacyBanchoMatch
        {
            ID = ID,
            InProgress = InProgress,
            Powerplay = Powerplay,
            Mods = Mods,
            Name = Name,
            Password = Password,
            HasPassword = HasPassword,
            BeatmapName = BeatmapName,
            BeatmapID = BeatmapID,
            BeatmapChecksum = BeatmapChecksum,
            SlotStatuses = (LegacyMatchSlotStatus[])SlotStatuses.Clone(),
            SlotTeams = (LegacyMatchTeam[])SlotTeams.Clone(),
            SlotUserIDs = (int[])SlotUserIDs.Clone(),
            HostID = HostID,
            RulesetID = RulesetID,
            WinCondition = WinCondition,
            TeamType = TeamType,
            FreeMods = FreeMods,
            SlotMods = (uint[])SlotMods.Clone(),
            Seed = Seed,
        };
    }

    internal static class LegacyBanchoMatchCodec
    {
        public static LegacyBanchoMatch Read(BinaryReader reader)
        {
            var match = new LegacyBanchoMatch
            {
                ID = reader.ReadUInt16(),
                InProgress = reader.ReadByte() == 1,
                Powerplay = reader.ReadByte(),
                Mods = reader.ReadUInt32(),
                Name = BanchoPacketCodec.ReadString(reader),
            };

            readPassword(reader, match);
            match.BeatmapName = BanchoPacketCodec.ReadString(reader);
            match.BeatmapID = reader.ReadInt32();
            match.BeatmapChecksum = BanchoPacketCodec.ReadString(reader);

            for (int i = 0; i < LegacyBanchoMatch.SLOT_COUNT; i++)
                match.SlotStatuses[i] = (LegacyMatchSlotStatus)reader.ReadByte();
            for (int i = 0; i < LegacyBanchoMatch.SLOT_COUNT; i++)
                match.SlotTeams[i] = (LegacyMatchTeam)reader.ReadByte();
            for (int i = 0; i < LegacyBanchoMatch.SLOT_COUNT; i++)
            {
                if ((match.SlotStatuses[i] & LegacyMatchSlotStatus.HasPlayer) != 0)
                    match.SlotUserIDs[i] = reader.ReadInt32();
            }

            match.HostID = reader.ReadInt32();
            match.RulesetID = reader.ReadByte();
            match.WinCondition = reader.ReadByte();
            match.TeamType = reader.ReadByte();
            match.FreeMods = reader.ReadByte() == 1;
            if (match.FreeMods)
            {
                for (int i = 0; i < LegacyBanchoMatch.SLOT_COUNT; i++)
                    match.SlotMods[i] = reader.ReadUInt32();
            }

            match.Seed = reader.ReadInt32();
            return match;
        }

        public static void Write(BinaryWriter writer, LegacyBanchoMatch match, bool hidePassword = false)
        {
            writer.Write(match.ID);
            writer.Write((byte)(match.InProgress ? 1 : 0));
            writer.Write(match.Powerplay);
            writer.Write(match.Mods);
            BanchoPacketCodec.WriteString(writer, match.Name);

            if (hidePassword && match.HasPassword)
            {
                writer.Write((byte)0x0b);
                writer.Write((byte)0);
            }
            else
                BanchoPacketCodec.WriteString(writer, match.Password);

            BanchoPacketCodec.WriteString(writer, match.BeatmapName);
            writer.Write(match.BeatmapID);
            BanchoPacketCodec.WriteString(writer, match.BeatmapChecksum);

            foreach (LegacyMatchSlotStatus status in match.SlotStatuses)
                writer.Write((byte)status);
            foreach (LegacyMatchTeam team in match.SlotTeams)
                writer.Write((byte)team);
            for (int i = 0; i < LegacyBanchoMatch.SLOT_COUNT; i++)
            {
                if ((match.SlotStatuses[i] & LegacyMatchSlotStatus.HasPlayer) != 0)
                    writer.Write(match.SlotUserIDs[i]);
            }

            writer.Write(match.HostID);
            writer.Write(match.RulesetID);
            writer.Write(match.WinCondition);
            writer.Write(match.TeamType);
            writer.Write((byte)(match.FreeMods ? 1 : 0));
            if (match.FreeMods)
            {
                foreach (uint mods in match.SlotMods)
                    writer.Write(mods);
            }

            writer.Write(match.Seed);
        }

        private static void readPassword(BinaryReader reader, LegacyBanchoMatch match)
        {
            byte marker = reader.ReadByte();
            if (marker == 0)
                return;
            if (marker != 0x0b)
                throw new InvalidDataException($"Invalid Bancho match password marker 0x{marker:x2}.");

            int length = readUleb128(reader);
            match.HasPassword = true;
            match.Password = length == 0 ? string.Empty : Encoding.UTF8.GetString(reader.ReadBytes(length));
        }

        private static int readUleb128(BinaryReader reader)
        {
            int value = 0;
            int shift = 0;
            while (shift < 35)
            {
                byte current = reader.ReadByte();
                value |= (current & 0x7f) << shift;
                if ((current & 0x80) == 0)
                    return value;
                shift += 7;
            }

            throw new InvalidDataException("Invalid ULEB128 match string length.");
        }
    }
}
