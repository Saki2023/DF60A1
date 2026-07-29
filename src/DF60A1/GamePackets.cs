using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DF60A1
{
    internal static class GamePackets
    {
        public const byte Notification = 0;
        public const byte Command = 1;
        public const byte CheckConnection = 0;
        public const byte ChannelInfo = 1;
        public const byte UserInfo = 2;
        public const byte UserState = 3;
        public const byte Stamina = 4;
        public const byte DungeonPermission = 5;
        public const byte ItemList = 13;
        public const byte SkillInfo = 19;
        public const byte AcceptableQuestList = 21;
        public const byte UserPosition = 22;
        public const byte UserArea = 23;
        public const byte AreaUsers = 24;
        public const byte UdpHost = 26;
        public const byte EnterSelectDungeonNotification = 27;
        public const byte DungeonInfo = 28;
        public const byte StartMap = 29;
        public const byte FinishLoadingNotification = 30;
        public const byte TutorialBootstrap = 136;
        public const byte Login = 1;
        public const byte Exit = 3;
        public const byte SelectCharacter = 4;
        public const byte CreateCharacter = 5;
        public const byte DeleteCharacter = 6;
        public const byte ReturnSelectCharacter = 7;
        public const byte GetUserInfo = 8;
        public const byte EnterSelectDungeon = 15;
        public const byte SelectDungeon = 16;
        public const byte SetUserPosition = 37;
        public const byte SetUserArea = 38;
        public const byte FinishLoading = 40;
        public const byte TownInit82 = 82;
        public const byte TownInit130 = 130;
        public const byte ConfirmActiveModule = 153;
        public const byte SecurityCard = 161;
        public const ushort TutorialDungeonId = 10000;
        public const ushort TutorialMapId = 61000;

        public static GameServerPacket Check(byte[] token) { return new GameServerPacket(Notification, CheckConnection, (byte[])token.Clone()); }
        public static GameServerPacket Channel(byte[] token)
        {
            // DNF.exe 1.0.1.9, sub_419420 NOTIPACKET_CHANNEL_INFO case 1,
            // consumes a 26-byte configuration record:
            //   uint32, uint32, byte, byte, uint32, uint32 count,
            //   [count * 0x104-byte address records], uint32, uint32.
            // The 55 client instead places its 16-byte challenge at offset 10;
            // doing that here turns the four trailing fields into arbitrary
            // ASCII integers and prevents the 2008 client from sending LOGIN.
            // A zero-address local configuration is the smallest safe record.
            return new GameServerPacket(Notification, ChannelInfo, new byte[26]);
        }
        public static GameServerPacket LoginOk()
        {
            // DNF.exe 1.0.1.9, sub_40F290 command-response case 1.
            // The success flag is followed by four bytes, three uint32 values
            // and two final bytes. A shorter body makes the client reader run
            // beyond the declared payload and leaves login state uninitialised.
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write((byte)1); // command succeeded
                writer.Write((byte)0);
                writer.Write((byte)0);
                writer.Write((byte)0); // channel/region mode
                writer.Write((byte)0);
                writer.Write(0u);
                writer.Write(0u);
                writer.Write(0u);
                writer.Write((byte)0);
                writer.Write((byte)0);
                return new GameServerPacket(Command, Login, stream.ToArray());
            }
        }
        public static GameServerPacket CommandResult(byte id, bool success) { return new GameServerPacket(Command, id, new byte[] { success ? (byte)1 : (byte)0 }); }
        public static GameServerPacket CommandError(byte id, byte error) { return new GameServerPacket(Command, id, new byte[] { 0, error }); }
        public static GameServerPacket TownInit82Empty()
        {
            // sub_40F290 command case 82:
            // success:u8, field1:u8, field2:u8, name:DSTR,
            // field3:u8, field4:u8, value1:u32, value2:u32, count:u8.
            // Even an empty name consumes its uint32 length, so the smallest
            // valid successful response is 18 bytes rather than 14.
            return new GameServerPacket(Command, TownInit82, new byte[18]
            {
                1, 0, 0,
                0, 0, 0, 0,
                0, 0,
                0, 0, 0, 0,
                0, 0, 0, 0,
                0
            });
        }
        public static GameServerPacket TownInit130Empty()
        {
            // sub_40F290 command case 130: success plus an empty entry count.
            return new GameServerPacket(Command, TownInit130, new byte[] { 1, 0 });
        }

        public static bool TryReadCreateCharacter(byte[] body, out byte job, out string name)
        {
            job = 0;
            name = null;

            // DNF.exe 1.0.1.9, sub_898E90 at 0x8995AC:
            //   BeginCommand(5)
            //   WriteBytes(&selectedJob, 1)
            //   WriteString(name) => uint32 byte length + raw bytes
            // PacketFrame.Body retains the client's leading ushort sequence,
            // so the command payload begins at body[2].
            if (body == null || body.Length < 7) return false;
            job = body[2];
            if (job > 4) return false;

            var length = LittleEndian.ReadUInt32(body, 3);
            if (length == 0 || length > 30 || body.Length - 7 < (int)length) return false;
            name = Encoding.GetEncoding(936).GetString(body, 7, (int)length);
            return true;
        }

        public static bool TryReadSelectDungeon(byte[] body, out ushort dungeonId, out byte difficulty, out byte option)
        {
            dungeonId = 0;
            difficulty = 0;
            option = 0;

            // Command 16 is sequence:u16, dungeonId:u16, difficulty:u8,
            // option:u8 in DNF.exe 1.0.1.9 (sub_8A1DD0).
            if (body == null || body.Length < 6) return false;
            dungeonId = LittleEndian.ReadUInt16(body, 2);
            difficulty = body[4];
            option = body[5];
            return true;
        }

        public static GameServerPacket CharacterList(IList<CharacterRecord> characters)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write((byte)2); writer.Write((ushort)characters.Count);
                foreach (var character in characters)
                {
                    // sub_419420 -> sub_8B3250 first consumes a DSTR here.
                    // dnf.str[0x26F] ("没有名字") is only the fallback used
                    // when the wire length is zero or invalid; it is not a
                    // replacement for the network field.
                    writer.Write((ushort)character.Slot);
                    var nameBytes = Encoding.GetEncoding(936).GetBytes(character.Name ?? string.Empty);
                    writer.Write((uint)nameBytes.Length);
                    writer.Write(nameBytes);
                    writer.Write(character.Job);
                    writer.Write((byte)(character.GrowType & 0x0f)); // high bits are second-grow state
                    writer.Write(character.Level);
                    writer.Write((byte)0); // secondary job state
                    writer.Write((byte)0); // character state
                    writer.Write((byte)0); // equipped preview pair count
                    writer.Write((byte)0); // avatar preview pair count
                    writer.Write(0u);      // guild id
                    writer.Write((byte)0); // guild/member flag
                    writer.Write((byte)0); // restricted state
                    writer.Write((byte)0); // trailing client state
                    // sub_419420 initializes this preview-object state to 1
                    // before reading the wire value (sub_417D60).  Zero keeps
                    // the newly-created preview object out of its normal state.
                    writer.Write((byte)1);
                }
                return new GameServerPacket(Notification, UserInfo, stream.ToArray());
            }
        }

        public static GameServerPacket SelectOk(CharacterRecord character, ushort userId)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write((byte)1); // command succeeded

                // DNF.exe 1.0.1.9, sub_40F290 command-response case 4.
                // Keep this layout byte-exact: the three entries below are
                // ushort + uint, not the ushort + byte used by later guesses.
                writer.Write((uint)character.Id);
                writer.Write(userId);
                writer.Write((ushort)0);
                writer.Write((ushort)188);
                writer.Write((ushort)0);

                writer.Write((byte)0); // variable byte + uint entry count
                writer.Write(0u);

                for (var index = 0; index < 3; index++)
                {
                    writer.Write(ushort.MaxValue);
                    writer.Write(0u);
                }

                writer.Write((byte)0); // variable byte + string entry count
                writer.Write(character.TownId);
                writer.Write(0u);
                return new GameServerPacket(Command, SelectCharacter, stream.ToArray());
            }
        }

        public static GameServerPacket TownCharacter(CharacterRecord character, ushort userId)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write((byte)0); writer.Write((ushort)1); writer.Write(userId);
                var nameBytes = Encoding.GetEncoding(936).GetBytes(character.Name ?? string.Empty);
                writer.Write((uint)nameBytes.Length);
                writer.Write(nameBytes);
                // Mode 0 uses the same leading DSTR as mode 2.  Once it has
                // been consumed, the following byte is passed to
                // sub_654E50(job) to construct the locally controlled actor.
                writer.Write(character.Job); writer.Write((byte)(character.GrowType & 0x0f)); writer.Write(character.Level);
                // DNF.exe 1.0.1.9, sub_419420 USERINFO mode 0.  This
                // record is longer than the otherwise similar DNF55 layout.
                // In particular the 2008 reader consumes both appearance-list
                // counts and a 20-byte entity tail.  Ending at the first uint
                // makes it read bytes beyond the packet and eventually passes
                // that garbage to sub_417D60 as the town actor state.
                writer.Write((byte)0); // secondary job state
                writer.Write((byte)0); // PvP/rating state (mode-0 only)
                writer.Write((byte)0); // user state
                writer.Write((byte)0); // equipped appearance count
                writer.Write((byte)0); // avatar appearance count
                writer.Write(0u);      // guild id
                writer.Write((byte)0); // guild/member flag
                writer.Write((ushort)0); // guild/title index
                writer.Write(0u);        // display name DSTR length
                writer.Write((byte)0);   // display-name flag
                writer.Write((byte)0);   // restricted state
                writer.Write((byte)0);   // trailing client state
                writer.Write(0u);        // local-player state
                writer.Write((byte)0);   // secondary display type
                writer.Write(0u);        // secondary display DSTR length
                writer.Write((byte)1);   // initial entity state
                writer.Write(0u);
                writer.Write(0u);
                writer.Write(0u);
                return new GameServerPacket(Notification, UserInfo, stream.ToArray());
            }
        }

        public static GameServerPacket CharacterDetails(ushort userId)
        {
            using (var statsStream = new MemoryStream())
            using (var stats = new BinaryWriter(statsStream))
            {
                stats.Write(11600u); stats.Write(11900u);
                stats.Write((short)75); stats.Write((short)75); stats.Write((short)45); stats.Write((short)45);
                for (var index = 0; index < 20; index++) stats.Write((short)0);
                stats.Write(480000u); stats.Write((ushort)0); stats.Write((ushort)500); stats.Write((ushort)8500);
                stats.Write((ushort)8500); stats.Write((ushort)7000); stats.Write((ushort)6000); stats.Write((ushort)4300); stats.Write(500000u);
                var statBytes = statsStream.ToArray();
                using (var stream = new MemoryStream())
                using (var writer = new BinaryWriter(stream))
                {
                    writer.Write((byte)1); writer.Write((ushort)1); writer.Write(userId); writer.Write(0u);
                    writer.Write((uint)statBytes.Length); writer.Write(statBytes); writer.Write((byte)0); writer.Write((byte)0); writer.Write((byte)0);
                    return new GameServerPacket(Notification, UserInfo, stream.ToArray());
                }
            }
        }

        public static IEnumerable<GameServerPacket> TownInitialization()
        {
            yield return new GameServerPacket(Notification, DungeonPermission, new byte[] { 0, 0 });
            using (var item = new MemoryStream())
            using (var writer = new BinaryWriter(item))
            { writer.Write((byte)0); writer.Write((ushort)1); writer.Write((ushort)0); writer.Write((ushort)0); writer.Write(0u); writer.Write((byte)0); writer.Write((ushort)0); writer.Write((byte)0); yield return new GameServerPacket(Notification, ItemList, item.ToArray()); }
            // DNF.exe 1.0.1.9, case 19 at 0x41E3C8: after the ushort
            // skill count the reader consumes a second byte count for the
            // auxiliary skill list (0x41E5B5).  Even an empty response is
            // therefore four bytes, not three.
            yield return new GameServerPacket(Notification, SkillInfo, new byte[] { 0, 0, 0, 0 });
            yield return new GameServerPacket(Notification, AcceptableQuestList, new byte[] { 0 });
        }

        public static GameServerPacket Area(CharacterRecord character, ushort userId)
        {
            using (var stream = new MemoryStream()) using (var writer = new BinaryWriter(stream))
            {
                // 2008 case 23 reads uid, town, area, x, y, direction and a
                // final actor-state byte.  China-server town snapshots use 3.
                writer.Write(userId); writer.Write(character.TownId); writer.Write(character.AreaId);
                writer.Write(character.X); writer.Write(character.Y); writer.Write(character.Direction);
                writer.Write((byte)3);
                return new GameServerPacket(Notification, UserArea, stream.ToArray());
            }
        }
        public static GameServerPacket AreaRoster(CharacterRecord character, ushort userId)
        {
            using (var stream = new MemoryStream()) using (var writer = new BinaryWriter(stream))
            {
                writer.Write(character.TownId); writer.Write(character.AreaId); writer.Write((ushort)1);
                writer.Write(userId); writer.Write(character.X); writer.Write(character.Y); writer.Write(character.Direction);
                writer.Write((byte)3); // China-server per-user town actor state
                return new GameServerPacket(Notification, AreaUsers, stream.ToArray());
            }
        }
        public static GameServerPacket StaminaFull() { return new GameServerPacket(Notification, Stamina, new byte[] { 100 }); }
        public static GameServerPacket TutorialBootstrapPacket()
        {
            // DNF.exe 1.0.1.9 notification case 136 starts the first-login
            // tutorial. It sends command 15 and command 16 for dungeon 10000,
            // enables module bit 30, then confirms that module with command 153.
            return new GameServerPacket(Notification, TutorialBootstrap, new byte[0]);
        }
        public static GameServerPacket UserStateChanged(ushort userId, byte state)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                // Notification case 3 at 0x41B0D3: userId:u16, state:u8.
                writer.Write(userId);
                writer.Write(state);
                return new GameServerPacket(Notification, UserState, stream.ToArray());
            }
        }
        public static GameServerPacket UnavailableUdpHost()
        {
            // Notification case 26 consumes one host-state byte.
            return new GameServerPacket(Notification, UdpHost, new byte[] { 0 });
        }
        public static GameServerPacket EnterTutorialSelection()
        {
            // Notification case 27: entry mode followed by a byte-counted list
            // of blocked party member IDs. Mode 1 with an empty list is solo.
            return new GameServerPacket(Notification, EnterSelectDungeonNotification, new byte[] { 1, 0 });
        }
        public static GameServerPacket TutorialDungeonInfo(byte difficulty)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                // Notification case 28 at 0x41F550 consumes exactly:
                // dungeon:u16, difficulty, maze, bossX, bossY, flagA, flagB.
                writer.Write(TutorialDungeonId);
                writer.Write(difficulty);
                writer.Write((byte)0); // first maze
                writer.Write(byte.MaxValue); // tutorial has no boss room
                writer.Write(byte.MaxValue);
                writer.Write((byte)0);
                writer.Write((byte)0);
                return new GameServerPacket(Notification, DungeonInfo, stream.ToArray());
            }
        }
        public static GameServerPacket TutorialStartMap()
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                // Script.pvf dungeon 10000 is Tutorial/Tutorial.dgn. Its first
                // room is (0,0), map 61000 (Tutorial/tutorial.map), containing
                // four level-1 monster.lst entry 1 rows.
                writer.Write((byte)0); // room X
                writer.Write((byte)0); // room Y
                writer.Write(0u);      // random seed
                writer.Write((byte)1); // full first-visit room state follows
                writer.Write(TutorialMapId);
                writer.Write((byte)4);
                for (byte index = 0; index < 4; index++)
                {
                    writer.Write(index);          // map [monster] row
                    writer.Write((ushort)index); // runtime unique ID
                    writer.Write((ushort)1);     // monster.lst ID
                    writer.Write((byte)1);       // level
                    writer.Write((byte)0);       // normal monster
                    writer.Write((byte)0);       // not a box monster
                    writer.Write((byte)0);       // box index
                }
                writer.Write((byte)0); // no runtime passive-object records
                writer.Write((byte)0); // final room-state flag
                return new GameServerPacket(Notification, StartMap, stream.ToArray());
            }
        }
        public static GameServerPacket FinishLoaded()
        {
            // DNF.exe 1.0.1.9 case 30 consumes no body.  Later China clients
            // carry five status bytes here, but that layout is not compatible
            // with this 2008 packet dispatcher.
            return new GameServerPacket(Notification, FinishLoadingNotification, new byte[0]);
        }

        private static void WriteBytes(BinaryWriter writer, byte[] value) { writer.Write((uint)value.Length); writer.Write(value); }
    }
}
