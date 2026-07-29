using System;
using System.IO;
using System.Linq;

namespace DF60A1
{
    internal static class SelfTest
    {
        public static int Run()
        {
            var root = Path.Combine(Path.GetTempPath(), "dnf2008-emulator-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var schema = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "schema.sql");
                using (var store = new SqliteGameStore(Path.Combine(root, "test.sqlite"), schema))
                {
                    var account = store.EnsureAccount("test", "test");
                    var created = store.CreateCharacter(account, "赛丽亚测试", 0);
                    if (!created.Success) throw new InvalidOperationException(created.Error);
                    var characters = store.ListCharacters(account);
                    if (characters.Count != 1 || characters[0].Name != "赛丽亚测试") throw new InvalidOperationException("SQLite 角色往返失败");
                    if (characters[0].TownId != 1 || characters[0].AreaId != 1 ||
                        characters[0].X != 474 || characters[0].Y != 336 || characters[0].Direction != 5)
                        throw new InvalidOperationException("国服新角色赛丽亚房间初始位置失败");

                    var packet = GamePackets.CharacterList(characters).Encode();
                    if (packet.Length < 7 || packet[0] != 0 || packet[1] != GamePackets.UserInfo) throw new InvalidOperationException("USERINFO 编码失败");

                    var loginPacket = GamePackets.LoginOk();
                    if (loginPacket.Payload == null || loginPacket.Payload.Length != 19 || loginPacket.Payload[0] != 1)
                        throw new InvalidOperationException("2008 LOGIN 成功响应布局失败");

                    var payload = packet.Skip(6).ToArray(); GamePayloadCipher.Decrypt(payload, 0, payload.Length);
                    if (payload[0] != 2 || LittleEndian.ReadUInt16(payload, 1) != 1) throw new InvalidOperationException("服务端负载加密往返失败");
                    var characterNameBytes = System.Text.Encoding.GetEncoding(936).GetBytes("赛丽亚测试");
                    var characterJobOffset = 9 + characterNameBytes.Length;
                    if (payload.Length != 24 + characterNameBytes.Length || payload[payload.Length - 1] != 1)
                        throw new InvalidOperationException("2008 mode 2 角色预览记录布局失败");
                    if (LittleEndian.ReadUInt16(payload, 3) != 0 || LittleEndian.ReadUInt32(payload, 5) != (uint)characterNameBytes.Length ||
                        !payload.Skip(9).Take(characterNameBytes.Length).SequenceEqual(characterNameBytes) ||
                        payload[characterJobOffset] != 0 || payload[characterJobOffset + 1] != 0 || payload[characterJobOffset + 2] != 1)
                        throw new InvalidOperationException("2008 角色卡 slot/job/grow/level 字段布局失败");
                    if (payload.Skip(characterJobOffset + 3).Take(11).Any(value => value != 0))
                        throw new InvalidOperationException("2008 角色卡状态与公会字段默认值失败");

                    var emptyPacket = GamePackets.CharacterList(new CharacterRecord[0]).Encode();
                    if (emptyPacket.Length != 9 || emptyPacket[6] != 0xDE || emptyPacket[7] != 0xD6 || emptyPacket[8] != 0xD6)
                        throw new InvalidOperationException("2008 服务端下行固定向量失败");
                    var emptyPayload = emptyPacket.Skip(6).ToArray();
                    GamePayloadCipher.Decrypt(emptyPayload, 0, emptyPayload.Length);
                    if (emptyPayload.Length != 3 || emptyPayload[0] != 2 || LittleEndian.ReadUInt16(emptyPayload, 1) != 0)
                        throw new InvalidOperationException("2008 空角色列表布局失败");

                    var channelPacket = GamePackets.Channel(new byte[16]).Encode();
                    if (channelPacket.Length != 32 || channelPacket.Skip(6).Any(value => value != 0xD6))
                        throw new InvalidOperationException("2008 CHANNEL_INFO 固定向量失败");
                    var channelPayload = channelPacket.Skip(6).ToArray();
                    GamePayloadCipher.Decrypt(channelPayload, 0, channelPayload.Length);
                    if (channelPayload.Length != 26 || channelPayload.Any(value => value != 0))
                        throw new InvalidOperationException("2008 CHANNEL_INFO 最小配置布局失败");

                    byte parsedJob; string parsedName;
                    var createBody = ParseHex("03000204000000B2E2CAD4");
                    if (!GamePackets.TryReadCreateCharacter(createBody, out parsedJob, out parsedName) ||
                        parsedJob != 2 || parsedName != "测试")
                        throw new InvalidOperationException("2008 CREATE_CHARACTER 请求解析失败");
                    createBody[2] = 5;
                    if (GamePackets.TryReadCreateCharacter(createBody, out parsedJob, out parsedName))
                        throw new InvalidOperationException("2008 CREATE_CHARACTER 接受了无效职业");

                    var selectPacket = GamePackets.SelectOk(characters[0], 1).Encode();
                    var selectPayload = selectPacket.Skip(6).ToArray();
                    GamePayloadCipher.Decrypt(selectPayload, 0, selectPayload.Length);
                    if (selectPayload.Length != 42 || selectPayload[0] != 1)
                        throw new InvalidOperationException("2008 SELECT_CHARACTER 成功包布局失败");
                    if (LittleEndian.ReadUInt32(selectPayload, 1) != (uint)characters[0].Id ||
                        LittleEndian.ReadUInt16(selectPayload, 5) != 1 ||
                        selectPayload[37] != characters[0].TownId)
                        throw new InvalidOperationException("2008 SELECT_CHARACTER 角色ID/服务器ID/城镇字段失败");

                    var townCharacter = GamePackets.TownCharacter(characters[0], 1).Payload;
                    var townNameOffset = 9 + characterNameBytes.Length;
                    if (townCharacter.Length != 53 + characterNameBytes.Length ||
                        LittleEndian.ReadUInt32(townCharacter, 5) != (uint)characterNameBytes.Length ||
                        !townCharacter.Skip(9).Take(characterNameBytes.Length).SequenceEqual(characterNameBytes) ||
                        townCharacter[townNameOffset] != characters[0].Job ||
                        townCharacter[townNameOffset + 1] != characters[0].GrowType ||
                        townCharacter[townNameOffset + 2] != characters[0].Level ||
                        townCharacter[townCharacter.Length - 13] != 1 ||
                        townCharacter.Skip(townCharacter.Length - 12).Any(value => value != 0))
                        throw new InvalidOperationException("2008 USERINFO mode 0 完整实体尾布局失败");

                    var userArea = GamePackets.Area(characters[0], 1).Payload;
                    var areaUsers = GamePackets.AreaRoster(characters[0], 1).Payload;
                    if (userArea.Length != 10 || userArea[9] != 3 ||
                        areaUsers.Length != 12 || areaUsers[11] != 3)
                        throw new InvalidOperationException("2008 USER_AREA/AREA_USERS 城镇状态布局失败");

                    var skillInfo = GamePackets.TownInitialization()
                        .Single(value => value.ProtocolId == GamePackets.SkillInfo).Payload;
                    if (skillInfo.Length != 4 || skillInfo.Any(value => value != 0))
                        throw new InvalidOperationException("2008 SKILL_INFO 空技能与辅助列表布局失败");

                    var townInit82 = GamePackets.TownInit82Empty();
                    if (townInit82.ProtocolId != GamePackets.TownInit82 ||
                        townInit82.Payload.Length != 18 || townInit82.Payload[0] != 1 ||
                        townInit82.Payload.Skip(1).Any(value => value != 0))
                        throw new InvalidOperationException("2008 CMD 82 空响应布局失败");

                    var finishLoaded = GamePackets.FinishLoaded();
                    if (finishLoaded.Type != GamePackets.Notification ||
                        finishLoaded.ProtocolId != GamePackets.FinishLoadingNotification ||
                        finishLoaded.Payload.Length != 0)
                        throw new InvalidOperationException("2008 FINISH_LOADING 通知布局失败");

                    var tutorialBootstrap = GamePackets.TutorialBootstrapPacket();
                    if (tutorialBootstrap.Type != GamePackets.Notification ||
                        tutorialBootstrap.ProtocolId != GamePackets.TutorialBootstrap ||
                        tutorialBootstrap.Payload.Length != 0)
                        throw new InvalidOperationException("2008 教程启动通知布局失败");

                    var userState = GamePackets.UserStateChanged(1, 1);
                    if (userState.ProtocolId != GamePackets.UserState || userState.Payload.Length != 3 ||
                        LittleEndian.ReadUInt16(userState.Payload, 0) != 1 || userState.Payload[2] != 1)
                        throw new InvalidOperationException("2008 地下城用户状态通知布局失败");
                    var enterTutorial = GamePackets.EnterTutorialSelection();
                    if (enterTutorial.ProtocolId != GamePackets.EnterSelectDungeonNotification ||
                        enterTutorial.Payload.Length != 2 || enterTutorial.Payload[0] != 1 || enterTutorial.Payload[1] != 0)
                        throw new InvalidOperationException("2008 进入地下城选择通知布局失败");

                    ushort selectedDungeon; byte selectedDifficulty; byte selectedOption;
                    if (!GamePackets.TryReadSelectDungeon(ParseHex("080010270000"), out selectedDungeon, out selectedDifficulty, out selectedOption) ||
                        selectedDungeon != GamePackets.TutorialDungeonId || selectedDifficulty != 0 || selectedOption != 0)
                        throw new InvalidOperationException("2008 SELECT_DUNGEON 请求解析失败");
                    var tutorialInfo = GamePackets.TutorialDungeonInfo(0);
                    if (tutorialInfo.ProtocolId != GamePackets.DungeonInfo || tutorialInfo.Payload.Length != 8 ||
                        LittleEndian.ReadUInt16(tutorialInfo.Payload, 0) != GamePackets.TutorialDungeonId ||
                        tutorialInfo.Payload[4] != byte.MaxValue || tutorialInfo.Payload[5] != byte.MaxValue)
                        throw new InvalidOperationException("2008 教程地下城信息通知布局失败");
                    var tutorialMap = GamePackets.TutorialStartMap();
                    if (tutorialMap.ProtocolId != GamePackets.StartMap || tutorialMap.Payload.Length != 48 ||
                        tutorialMap.Payload[6] != 1 || LittleEndian.ReadUInt16(tutorialMap.Payload, 7) != GamePackets.TutorialMapId ||
                        tutorialMap.Payload[9] != 4 || LittleEndian.ReadUInt16(tutorialMap.Payload, 13) != 1)
                        throw new InvalidOperationException("2008 教程 START_MAP 通知布局失败");

                    var cipher = new GameClientCipherState();
                    PacketFrame decoded;
                    var check = new PacketFrame
                    {
                        Type = 1, ProtocolId = 0, DeclaredCrc32 = 0xE38A6876,
                        Body = ParseHex("00007575757575757575")
                    };
                    if (!cipher.TryDecode(check, out decoded) || decoded.Body.Skip(2).Any(value => value != 0))
                        throw new InvalidOperationException("频道首包解密失败");
                    var login = new PacketFrame
                    {
                        Type = 1, ProtocolId = 1, DeclaredCrc32 = 0xFD14BAC5,
                        Body = ParseHex("0100838282829FDB5E9F838282829FDB5E9F82808282824E5E61C37807B61B0A5B0EC2")
                    };
                    if (!cipher.TryDecode(login, out decoded) || LittleEndian.ReadUInt32(decoded.Body, 2) != 4 ||
                        System.Text.Encoding.ASCII.GetString(decoded.Body, 6, 4) != "test")
                        throw new InvalidOperationException("2008 独立种子登录包解密失败");
                }
                Console.WriteLine("SELF-TEST OK: SQLite、角色列表/实体、城镇区域、创建请求、选角响应与 2008 客户端包解密均通过。");
                return 0;
            }
            catch (Exception exception) { Console.Error.WriteLine("SELF-TEST FAILED: " + exception); return 1; }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        private static byte[] ParseHex(string value)
        {
            var result = new byte[value.Length / 2];
            for (var index = 0; index < result.Length; index++)
                result[index] = Convert.ToByte(value.Substring(index * 2, 2), 16);
            return result;
        }
    }
}
