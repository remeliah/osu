// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace osu.Game.Online.Bancho
{
    internal enum ClientPacket : ushort
    {
        ChangeAction = 0,
        SendPublicMessage = 1,
        Logout = 2,
        RequestStatusUpdate = 3,
        Pong = 4,
        StartSpectating = 16,
        StopSpectating = 17,
        SpectateFrames = 18,
        CantSpectate = 21,
        SendPrivateMessage = 25,
        PartLobby = 29,
        JoinLobby = 30,
        CreateMatch = 31,
        JoinMatch = 32,
        PartMatch = 33,
        MatchChangeSlot = 38,
        MatchReady = 39,
        MatchLock = 40,
        MatchChangeSettings = 41,
        MatchStart = 44,
        MatchScoreUpdate = 47,
        MatchComplete = 49,
        MatchChangeMods = 51,
        MatchLoadComplete = 52,
        MatchNoBeatmap = 54,
        MatchNotReady = 55,
        MatchFailed = 56,
        MatchHasBeatmap = 59,
        MatchSkipRequest = 60,
        ChannelJoin = 63,
        MatchTransferHost = 70,
        MatchChangeTeam = 77,
        ChannelLeave = 78,
        RequestUserStats = 85,
        MatchInvite = 87,
        MatchChangePassword = 90,
        UserPresenceRequest = 97,
    }

    internal enum ServerPacket : ushort
    {
        UserId = 5,
        SendMessage = 7,
        Ping = 8,
        UserStats = 11,
        UserLogout = 12,
        SpectatorJoined = 13,
        SpectatorLeft = 14,
        SpectateFrames = 15,
        SpectatorCantSpectate = 22,
        Notification = 24,
        UpdateMatch = 26,
        NewMatch = 27,
        DisposeMatch = 28,
        MatchJoinSuccess = 36,
        MatchJoinFail = 37,
        FellowSpectatorJoined = 42,
        FellowSpectatorLeft = 43,
        AllPlayersLoaded = 45,
        MatchStart = 46,
        MatchScoreUpdate = 48,
        MatchTransferHost = 50,
        MatchAllPlayersLoaded = 53,
        MatchPlayerFailed = 57,
        MatchComplete = 58,
        MatchSkip = 61,
        Unauthorized = 62,
        ChannelJoinSuccess = 64,
        ChannelInfo = 65,
        ChannelKicked = 66,
        ChannelAutoJoin = 67,
        Privileges = 71,
        FriendsList = 72,
        ProtocolVersion = 75,
        MatchPlayerSkipped = 81,
        UserPresence = 83,
        MatchInvite = 88,
        ChannelInfoEnd = 89,
        MatchChangePassword = 91,
        SilenceEnd = 92,
        UserSilenced = 94,
        UserPresenceSingle = 95,
        UserPresenceBundle = 96,
        UserDmBlocked = 100,
        TargetIsSilenced = 101,
        VersionUpdateForced = 102,
        AccountRestricted = 104,
        MatchAbort = 106,
    }

    internal readonly record struct BanchoPacket(ServerPacket Type, byte[] Payload);

    internal static class BanchoPacketCodec
    {
        private const byte string_present = 0x0b;

        public static byte[] Encode(ClientPacket type, Action<BinaryWriter>? writePayload = null)
        {
            using var payloadStream = new MemoryStream();

            if (writePayload != null)
            {
                using var payloadWriter = new BinaryWriter(payloadStream, Encoding.UTF8, true);
                writePayload(payloadWriter);
            }

            byte[] payload = payloadStream.ToArray();
            byte[] packet = new byte[7 + payload.Length];
            BinaryPrimitives.WriteUInt16LittleEndian(packet, (ushort)type);
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(3), payload.Length);
            payload.CopyTo(packet, 7);
            return packet;
        }

        public static IReadOnlyList<BanchoPacket> Decode(ReadOnlySpan<byte> data)
        {
            var packets = new List<BanchoPacket>();
            int offset = 0;

            while (offset < data.Length)
            {
                if (data.Length - offset < 7)
                    throw new InvalidDataException("Incomplete Bancho packet header.");

                ushort type = BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
                int length = BinaryPrimitives.ReadInt32LittleEndian(data[(offset + 3)..]);
                offset += 7;

                if (length < 0 || length > data.Length - offset)
                    throw new InvalidDataException("Invalid Bancho packet payload length.");

                packets.Add(new BanchoPacket((ServerPacket)type, data.Slice(offset, length).ToArray()));
                offset += length;
            }

            return packets;
        }

        public static void WriteString(BinaryWriter writer, string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                writer.Write((byte)0);
                return;
            }

            byte[] bytes = Encoding.UTF8.GetBytes(value);
            writer.Write(string_present);
            writeUleb128(writer, (uint)bytes.Length);
            writer.Write(bytes);
        }

        public static string ReadString(BinaryReader reader)
        {
            byte marker = reader.ReadByte();
            if (marker == 0)
                return string.Empty;
            if (marker != string_present)
                throw new InvalidDataException($"Invalid str marker 0x{marker:x2}.");

            uint length = readUleb128(reader);
            if (length > int.MaxValue)
                throw new InvalidDataException("str is too long.");

            byte[] bytes = reader.ReadBytes((int)length);
            if (bytes.Length != length)
                throw new EndOfStreamException();

            return Encoding.UTF8.GetString(bytes);
        }

        public static void WriteMessage(BinaryWriter writer, string sender, string message, string target, int senderId)
        {
            WriteString(writer, sender);
            WriteString(writer, message);
            WriteString(writer, target);
            writer.Write(senderId);
        }

        public static void WriteIntList(BinaryWriter writer, IReadOnlyCollection<int> values)
        {
            if (values.Count > short.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(values), "bListInt cannot contain more than 32767 entries.");

            writer.Write((short)values.Count);
            foreach (int value in values)
                writer.Write(value);
        }

        private static void writeUleb128(BinaryWriter writer, uint value)
        {
            do
            {
                byte current = (byte)(value & 0x7f);
                value >>= 7;
                if (value != 0)
                    current |= 0x80;
                writer.Write(current);
            } while (value != 0);
        }

        private static uint readUleb128(BinaryReader reader)
        {
            uint value = 0;
            int shift = 0;

            while (shift < 35)
            {
                byte current = reader.ReadByte();
                value |= (uint)(current & 0x7f) << shift;
                if ((current & 0x80) == 0)
                    return value;
                shift += 7;
            }

            throw new InvalidDataException("Invalid ULEB128 value.");
        }
    }
}
