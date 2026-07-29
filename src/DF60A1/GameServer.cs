using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DF60A1
{
    internal sealed class GameServer : IDisposable
    {
        private readonly ServerOptions _options;
        private readonly SqliteGameStore _store;
        private readonly TcpListener _tcp;
        private readonly UdpCharacterService _udp;
        private readonly ManualResetEventSlim _stopped = new ManualResetEventSlim(false);
        private readonly byte[] _channelScript;
        private volatile bool _running;

        public GameServer(ServerOptions options, SqliteGameStore store)
        {
            _options = options; _store = store;
            var address = IPAddress.Parse(options.Host);
            _tcp = new TcpListener(address, options.Port);
            _udp = new UdpCharacterService(new IPEndPoint(address, options.Port), Log);
            try
            {
                uint fileNumber;
                var channelInfo = new PvfArchiveReader(options.ScriptPvfPath).ReadAllBytes("etc/channel_info.etc", out fileNumber);
                // DNF.exe 1.0.1.9 passes the downloaded buffer through strlen
                // and feeds it directly to the channel-info lexer.  Unlike the
                // later 1.0.44.1 client, protocol 9 therefore expects the raw
                // channel_info.etc text, not a binary ChannelScript.pvf archive.
                _channelScript = channelInfo;
            }
            catch (Exception exception)
            {
                _channelScript = null;
                Console.Error.WriteLine("无法从 Script.pvf 构建 ChannelScript.pvf: " + exception.Message);
            }
        }

        public void Run()
        {
            _running = true; _tcp.Start(); _udp.Start();
            Log("INFO", "DNF 2008 模拟端已启动，TCP/UDP {0}:{1}", _options.Host, _options.Port);
            Log("INFO", "数据库: {0}", _options.DatabasePath);
            Log("INFO", "频道脚本: {0}", _channelScript == null ? "不可用" : _channelScript.Length + " bytes");
            Log("INFO", "按 Ctrl+C 停止；另开终端执行程序 --launch 可按 13 段参数启动客户端。");
            Task.Run((Func<Task>)AcceptLoopAsync);
            _stopped.Wait();
        }

        public void Stop()
        {
            if (!_running) return;
            _running = false;
            try { _tcp.Stop(); } catch { }
            _udp.Stop();
            _stopped.Set();
        }

        private async Task AcceptLoopAsync()
        {
            while (_running)
            {
                try
                {
                    var client = await _tcp.AcceptTcpClientAsync().ConfigureAwait(false);
                    _ = Task.Run(() => HandleClientAsync(client));
                }
                catch (ObjectDisposedException) { break; }
                catch (SocketException) { if (!_running) break; throw; }
            }
        }

        private async Task HandleClientAsync(TcpClient client)
        {
            var remote = client.Client.RemoteEndPoint == null ? "unknown" : client.Client.RemoteEndPoint.ToString();
            Log("INFO", "TCP 客户端连接: {0}", remote);
            var token = new byte[16]; Encoding.ASCII.GetBytes("dnf2008-local").CopyTo(token, 0);
            var cipher = new GameClientCipherState();
            var entrance = new EntranceSession();
            var accountName = _options.Account;
            var accountId = _store.EnsureAccount(accountName, _options.Password);
            CharacterRecord active = null;
            const ushort userId = 1;
            var areaRosterInitialized = false;
            var finishLoadingSent = false;

            try
            {
                using (client)
                using (var stream = client.GetStream())
                {
                // The entrance socket is client-first, while the channel
                // socket waits for a server CHECK_CONNECTION notification.
                // A short first-read race distinguishes both connections even
                // though the local emulator intentionally shares one port.
                var firstRead = PacketFrame.ReadAsync(stream);
                var firstCompleted = await Task.WhenAny(firstRead, Task.Delay(500)).ConfigureAwait(false);
                PacketFrame queued = null;
                if (ReferenceEquals(firstCompleted, firstRead))
                {
                    queued = await firstRead.ConfigureAwait(false);
                }
                else
                {
                    await SendAsync(stream, GamePackets.Check(token), remote).ConfigureAwait(false);
                    Log("INFO", "连接 {0} 识别为频道连接，已发送 CHECK_CONNECTION", remote);
                }

                while (_running)
                {
                    var raw = queued;
                    queued = null;
                    raw = raw ?? await firstRead.ConfigureAwait(false);
                    firstRead = PacketFrame.ReadAsync(stream);
                    if (raw == null) break;
                    LogPacket("RX", remote, raw, raw.Body);
                    if (raw.Type == 0 && EntranceProtocol.IsEntrance(raw.ProtocolId))
                    {
                        var entranceReply = EntranceProtocol.Handle(raw, entrance, _options.Host, _options.Port, _channelScript);
                        if (entranceReply != null) await SendAsync(stream, entranceReply, remote).ConfigureAwait(false);
                        continue;
                    }
                    PacketFrame request;
                    if (!cipher.TryDecode(raw, out request))
                    {
                        // Some 2008 builds send early handshake bodies in plaintext.
                        request = raw;
                        Log("WARN", "无法恢复滚动密钥，暂按明文处理 type={0} id={1} crc={2:X8}", raw.Type, raw.ProtocolId, raw.DeclaredCrc32);
                    }
                    else if (!ReferenceEquals(raw, request))
                    {
                        LogPacket("RX-DEC", remote, request, request.Body);
                    }

                    if (request.Type != GamePackets.Command)
                    {
                        Log("WARN", "未处理包 type={0} id={1}", request.Type, request.ProtocolId);
                        continue;
                    }

                    switch (request.ProtocolId)
                    {
                        case GamePackets.CheckConnection:
                            await SendAsync(stream, GamePackets.Channel(token), remote).ConfigureAwait(false);
                            break;
                        case GamePackets.Login:
                            accountName = ReadLoginName(request.Body) ?? _options.Account;
                            accountId = _store.EnsureAccount(accountName, _options.Password);
                            await SendAsync(stream, GamePackets.LoginOk(), remote).ConfigureAwait(false);
                            Log("INFO", "账号登录: {0} (id={1})", accountName, accountId);
                            break;
                        case GamePackets.Exit:
                            await SendAsync(stream, GamePackets.CommandResult(GamePackets.Exit, true), remote).ConfigureAwait(false);
                            break;
                        case GamePackets.SecurityCard:
                            // CNAntiBotSystem::vftable -> sub_8C74F0 uploads a
                            // uint32 length followed by opaque anti-bot data.
                            // Neither sub_40F290 nor the notification dispatcher
                            // has an id-161 consumer, so this is fire-and-forget.
                            break;
                        case GamePackets.GetUserInfo:
                            await SendRosterAsync(stream, remote, accountId).ConfigureAwait(false);
                            if (_options.AutoSelect)
                            {
                                active = _store.ListCharacters(accountId).FirstOrDefault();
                                if (active != null)
                                {
                                    await Task.Delay(500).ConfigureAwait(false);
                                    await SendAsync(stream, GamePackets.SelectOk(active, userId), remote).ConfigureAwait(false);
                                    await SendCharacterInitializationAsync(stream, remote, active, userId).ConfigureAwait(false);
                                    Log("INFO", "联调自动选择角色 {0}：已发送 SELECT_CHARACTER 与城镇初始化流", active.Name);
                                }
                            }
                            break;
                        case GamePackets.EnterSelectDungeon:
                            if (active != null)
                            {
                                await SendAsync(stream, GamePackets.UserStateChanged(userId, 1), remote).ConfigureAwait(false);
                                await SendAsync(stream, GamePackets.UnavailableUdpHost(), remote).ConfigureAwait(false);
                                await SendAsync(stream, GamePackets.EnterTutorialSelection(), remote).ConfigureAwait(false);
                                Log("INFO", "进入地下城选择流程：角色={0}", active.Name);
                            }
                            break;
                        case GamePackets.SelectDungeon:
                            ushort dungeonId; byte difficulty; byte dungeonOption;
                            if (active == null || !GamePackets.TryReadSelectDungeon(request.Body, out dungeonId, out difficulty, out dungeonOption))
                            {
                                Log("WARN", "SELECT_DUNGEON 请求无效: {0}", Hex(request.Body));
                                break;
                            }
                            if (dungeonId != GamePackets.TutorialDungeonId)
                            {
                                Log("WARN", "尚未实现地下城 {0}，已忽略选择请求", dungeonId);
                                break;
                            }
                            await SendAsync(stream, GamePackets.TutorialDungeonInfo(difficulty), remote).ConfigureAwait(false);
                            await SendAsync(stream, GamePackets.TutorialStartMap(), remote).ConfigureAwait(false);
                            finishLoadingSent = false;
                            Log("INFO", "已启动新手教程副本 dungeon={0}, map={1}, difficulty={2}, option={3}", dungeonId, GamePackets.TutorialMapId, difficulty, dungeonOption);
                            break;
                        case GamePackets.CreateCharacter:
                            byte job; string name;
                            if (!GamePackets.TryReadCreateCharacter(request.Body, out job, out name))
                            {
                                await SendAsync(stream, GamePackets.CommandError(GamePackets.CreateCharacter, 2), remote).ConfigureAwait(false);
                                break;
                            }
                            var created = _store.CreateCharacter(accountId, name, job);
                            await SendAsync(stream, created.Success ? GamePackets.CommandResult(GamePackets.CreateCharacter, true) : GamePackets.CommandError(GamePackets.CreateCharacter, 2), remote).ConfigureAwait(false);
                            Log(created.Success ? "INFO" : "WARN", created.Success ? "已创建角色: {0}, job={1}" : "创建角色失败: {0}", created.Success ? name : created.Error, job);
                            if (created.Success)
                            {
                                // Keep the China-server order used by the 86
                                // implementation: ACK first, refreshed roster
                                // second, after the create UI releases its
                                // pending command state.
                                await SendRosterAsync(stream, remote, accountId).ConfigureAwait(false);
                            }
                            break;
                        case GamePackets.DeleteCharacter:
                            await SendAsync(stream, GamePackets.CommandError(GamePackets.DeleteCharacter, 2), remote).ConfigureAwait(false);
                            break;
                        case GamePackets.SelectCharacter:
                            var characters = _store.ListCharacters(accountId);
                            var slot = request.Body.Length > 2 ? request.Body[2] : (byte)0;
                            active = characters.FirstOrDefault(x => x.Slot == slot);
                            if (active == null)
                            {
                                await SendAsync(stream, GamePackets.CommandError(GamePackets.SelectCharacter, 2), remote).ConfigureAwait(false);
                                break;
                            }
                            await SendAsync(stream, GamePackets.SelectOk(active, userId), remote).ConfigureAwait(false);
                            await SendCharacterInitializationAsync(stream, remote, active, userId).ConfigureAwait(false);
                            areaRosterInitialized = false;
                            finishLoadingSent = false;
                            Log("INFO", "选择角色 {0}，已发送 SELECT_CHARACTER 与城镇初始化流", active.Name);
                            break;
                        case GamePackets.ReturnSelectCharacter:
                            active = null; areaRosterInitialized = false; finishLoadingSent = false;
                            await SendAsync(stream, GamePackets.CommandResult(GamePackets.ReturnSelectCharacter, true), remote).ConfigureAwait(false);
                            await SendRosterAsync(stream, remote, accountId).ConfigureAwait(false);
                            break;
                        case GamePackets.SetUserPosition:
                            if (active != null && request.Body.Length >= 9)
                            {
                                var reportedX = LittleEndian.ReadInt16(request.Body, 2);
                                var reportedY = LittleEndian.ReadInt16(request.Body, 4);
                                active.Direction = request.Body[6];
                                if (!areaRosterInitialized && active.TownId == 1 && active.AreaId == 1)
                                {
                                    // On character selection the client reports
                                    // town/elvengard.twn's [gate] return point
                                    // (474,234).  That point is valid in area 0,
                                    // but lies outside area 1 Gate.map's only
                                    // movable rectangle (330,324,289,24).
                                    // Seed AREA_USERS at the room centre instead.
                                    active.X = 474;
                                    active.Y = 336;
                                    Log("INFO", "首次 SET_USER_POSITION 坐标 {0},{1} 是 area 0 返回点；赛丽亚房间改用 {2},{3}", reportedX, reportedY, active.X, active.Y);
                                }
                                else
                                {
                                    active.X = reportedX;
                                    active.Y = reportedY;
                                }
                                _store.UpdatePosition(active.Id, active.TownId, active.AreaId, active.X, active.Y, active.Direction);
                                if (!areaRosterInitialized)
                                {
                                    // The first SET_USER_POSITION is the end of the
                                    // character-selection initialization.  AREA_USERS
                                    // drives the 2008 client's town-map loader
                                    // (sub_89FB20).  USER_AREA belongs to the later
                                    // SET_USER_AREA transition, while START_MAP is a
                                    // dungeon-room packet and would strand the client
                                    // in its dungeon Loading controller here.
                                    await SendAsync(stream, GamePackets.AreaRoster(active, userId), remote).ConfigureAwait(false);
                                    // AREA_USERS rebuilds the scene-local town actor.
                                    // Replay subtype 0/1 only after that actor exists;
                                    // this is also the order used by the China
                                    // town flow in the 55 reference server.
                                    await Task.Delay(100).ConfigureAwait(false);
                                    await SendAsync(stream, GamePackets.TownCharacter(active, userId), remote).ConfigureAwait(false);
                                    await SendAsync(stream, GamePackets.CharacterDetails(userId), remote).ConfigureAwait(false);
                                    await SendAsync(stream, GamePackets.StaminaFull(), remote).ConfigureAwait(false);
                                    await SendAsync(stream, GamePackets.TutorialBootstrapPacket(), remote).ConfigureAwait(false);
                                    areaRosterInitialized = true;
                                    Log("INFO", "首次 SET_USER_POSITION：已发送 AREA_USERS、角色实体与教程启动通知");
                                }
                            }
                            break;
                        case GamePackets.SetUserArea:
                            if (active != null && request.Body.Length >= 8)
                            {
                                active.TownId = request.Body[2]; active.AreaId = request.Body[3]; active.X = LittleEndian.ReadInt16(request.Body, 4); active.Y = LittleEndian.ReadInt16(request.Body, 6);
                                _store.UpdatePosition(active.Id, active.TownId, active.AreaId, active.X, active.Y, active.Direction);
                                await SendAsync(stream, GamePackets.Area(active, userId), remote).ConfigureAwait(false);
                                await SendAsync(stream, GamePackets.AreaRoster(active, userId), remote).ConfigureAwait(false);
                                await Task.Delay(100).ConfigureAwait(false);
                                await SendAsync(stream, GamePackets.TownCharacter(active, userId), remote).ConfigureAwait(false);
                                await SendAsync(stream, GamePackets.CharacterDetails(userId), remote).ConfigureAwait(false);
                                await SendAsync(stream, GamePackets.StaminaFull(), remote).ConfigureAwait(false);
                                areaRosterInitialized = true;
                            }
                            break;
                        case GamePackets.FinishLoading:
                            await SendAsync(stream, GamePackets.CommandResult(GamePackets.FinishLoading, true), remote).ConfigureAwait(false);
                            if (!finishLoadingSent)
                            {
                                await SendAsync(stream, GamePackets.FinishLoaded(), remote).ConfigureAwait(false);
                                finishLoadingSent = true;
                            }
                            break;
                        case GamePackets.TownInit82:
                            await SendAsync(stream, GamePackets.TownInit82Empty(), remote).ConfigureAwait(false);
                            break;
                        case GamePackets.TownInit130:
                            await SendAsync(stream, GamePackets.TownInit130Empty(), remote).ConfigureAwait(false);
                            break;
                        case GamePackets.ConfirmActiveModule:
                            await SendAsync(stream, GamePackets.CommandResult(GamePackets.ConfirmActiveModule, true), remote).ConfigureAwait(false);
                            break;
                        default:
                            Log("WARN", "未知命令 type={0} id={1} body={2}", request.Type, request.ProtocolId, Hex(request.Body));
                            break;
                    }
                }
                }
            }
            catch (Exception exception)
            {
                Log("ERROR", "连接 {0} 结束: {1}", remote, exception.Message);
            }
            finally { Log("INFO", "TCP 客户端断开: {0}", remote); }
        }

        private async Task SendRosterAsync(Stream stream, string remote, int accountId)
        {
            await SendAsync(stream, GamePackets.CharacterList(_store.ListCharacters(accountId)), remote).ConfigureAwait(false);
        }

        private async Task SendCharacterInitializationAsync(Stream stream, string remote, CharacterRecord character, ushort userId)
        {
            // The client does not request these packets after a successful
            // SELECT_CHARACTER.  The China-server flow pushes them immediately.
            await SendAsync(stream, GamePackets.TownCharacter(character, userId), remote).ConfigureAwait(false);
            await SendAsync(stream, GamePackets.CharacterDetails(userId), remote).ConfigureAwait(false);
            foreach (var packet in GamePackets.TownInitialization())
                await SendAsync(stream, packet, remote).ConfigureAwait(false);
        }

        private async Task SendAsync(Stream stream, GameServerPacket packet, string remote)
        {
            await packet.WriteAsync(stream).ConfigureAwait(false);
            Log("TX", "{0} type={1} id={2} payload={3}", remote, packet.Type, packet.ProtocolId, Hex(packet.Payload));
        }

        private async Task SendAsync(Stream stream, EntrancePacket packet, string remote)
        {
            await packet.WriteAsync(stream).ConfigureAwait(false);
            Log("TX-ENTRANCE", "{0} id={1} payload={2}", remote, packet.ProtocolId, Hex(packet.Payload));
        }

        private static string ReadLoginName(byte[] body)
        {
            byte[] bytes; int next;
            return TryReadLengthBytes(body, 2, out bytes, out next) ? Encoding.GetEncoding(936).GetString(bytes) : null;
        }

        private static bool TryReadLengthBytes(byte[] source, int offset, out byte[] result, out int next)
        {
            result = null; next = offset;
            if (source.Length - offset < 4) return false;
            var length = LittleEndian.ReadUInt32(source, offset); offset += 4;
            if (length > int.MaxValue || source.Length - offset < (int)length) return false;
            result = new byte[(int)length]; Buffer.BlockCopy(source, offset, result, 0, result.Length); next = offset + result.Length; return true;
        }

        private void LogPacket(string direction, string remote, PacketFrame frame, byte[] body)
        {
            Log(direction, "{0} type={1} id={2} crc={3:X8} valid={4} body={5}", remote, frame.Type, frame.ProtocolId, frame.DeclaredCrc32, frame.HasValidCrc32, Hex(body));
        }

        private void Log(string level, string format, params object[] args)
        {
            var line = string.Format("{0:O} [{1}] {2}", DateTimeOffset.Now, level, string.Format(format, args));
            Console.WriteLine(line);
            lock (_options.LogDirectory) File.AppendAllText(Path.Combine(_options.LogDirectory, "server.log"), line + Environment.NewLine, Encoding.UTF8);
        }

        private static string Hex(byte[] value)
        {
            if (value == null) return string.Empty;
            var length = Math.Min(value.Length, 128); var builder = new StringBuilder(length * 2);
            for (var index = 0; index < length; index++) builder.Append(value[index].ToString("X2"));
            if (value.Length > length) builder.Append("..."); return builder.ToString();
        }

        public void Dispose() { Stop(); _stopped.Dispose(); _udp.Dispose(); }
    }
}
