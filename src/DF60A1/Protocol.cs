using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace DF60A1
{
    internal sealed class PacketFrame
    {
        public const int HeaderLength = 10;
        public byte Type;
        public byte ProtocolId;
        public uint DeclaredCrc32;
        public byte[] Body;

        public bool HasValidCrc32 { get { return DeclaredCrc32 == 0 || DeclaredCrc32 == Crc32.Compute(Body); } }

        public static async Task<PacketFrame> ReadAsync(Stream stream)
        {
            var header = new byte[HeaderLength];
            if (!await ReadExactAsync(stream, header, true).ConfigureAwait(false)) return null;
            var length = LittleEndian.ReadUInt32(header, 2);
            if (length < HeaderLength || length > 4 * 1024 * 1024) throw new InvalidDataException("无效 DNF 包长度: " + length);
            var body = new byte[(int)length - HeaderLength];
            if (body.Length > 0 && !await ReadExactAsync(stream, body, false).ConfigureAwait(false)) throw new EndOfStreamException();
            return new PacketFrame { Type = header[0], ProtocolId = header[1], DeclaredCrc32 = LittleEndian.ReadUInt32(header, 6), Body = body };
        }

        private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, bool eofAllowed)
        {
            var offset = 0;
            while (offset < buffer.Length)
            {
                var count = await stream.ReadAsync(buffer, offset, buffer.Length - offset).ConfigureAwait(false);
                if (count == 0) return eofAllowed && offset == 0 ? false : false;
                offset += count;
            }
            return true;
        }
    }

    internal sealed class GameServerPacket
    {
        public readonly byte Type;
        public readonly byte ProtocolId;
        public readonly byte[] Payload;

        public GameServerPacket(byte type, byte protocolId, byte[] payload)
        {
            Type = type; ProtocolId = protocolId; Payload = payload ?? new byte[0];
        }

        public byte[] Encode()
        {
            var output = new byte[6 + Payload.Length];
            output[0] = Type; output[1] = ProtocolId;
            LittleEndian.WriteUInt32(output, 2, (uint)output.Length);
            Buffer.BlockCopy(Payload, 0, output, 6, Payload.Length);
            GamePayloadCipher.Encrypt(output, 6, Payload.Length);
            return output;
        }

        public async Task WriteAsync(Stream stream)
        {
            var packet = Encode();
            await stream.WriteAsync(packet, 0, packet.Length).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
        }
    }

    internal sealed class EntrancePacket
    {
        public readonly byte ProtocolId;
        public readonly byte[] Payload;
        public EntrancePacket(byte protocolId, byte[] payload) { ProtocolId = protocolId; Payload = payload ?? new byte[0]; }
        public byte[] Encode()
        {
            var output = new byte[PacketFrame.HeaderLength + 1 + Payload.Length];
            output[0] = 0; output[1] = ProtocolId; LittleEndian.WriteUInt32(output, 2, (uint)output.Length);
            output[10] = 1; Buffer.BlockCopy(Payload, 0, output, 11, Payload.Length); return output;
        }
        public async Task WriteAsync(Stream stream)
        {
            var output = Encode(); await stream.WriteAsync(output, 0, output.Length).ConfigureAwait(false); await stream.FlushAsync().ConfigureAwait(false);
        }
    }

    internal static class GamePayloadCipher
    {
        // DNF.exe 1.0.1.9, sub_40D6D0, copies the six-byte-header packet's
        // wire payload and decodes it through sub_776F80(payload, size, 0):
        //     plain = ROL6(cipher) XOR 0xB5
        // Server encoding must therefore apply the inverse operation below.
        private const byte XorKey = 0xB5;
        private const int Rotation = 6;

        public static void Encrypt(byte[] bytes, int offset, int count)
        {
            for (var index = offset; index < offset + count; index++)
                bytes[index] = RotateRight((byte)(bytes[index] ^ XorKey), Rotation);
        }

        public static void Decrypt(byte[] bytes, int offset, int count)
        {
            for (var index = offset; index < offset + count; index++)
                bytes[index] = (byte)(RotateLeft(bytes[index], Rotation) ^ XorKey);
        }

        private static byte RotateLeft(byte value, int count) { return (byte)((value << count) | (value >> (8 - count))); }
        private static byte RotateRight(byte value, int count) { return (byte)((value >> count) | (value << (8 - count))); }
    }

    internal sealed class GameClientCipherState
    {
        private List<uint> _seeds = CreateSeeds();
        public int CandidateCount { get { return _seeds.Count; } }

        public bool TryDecode(PacketFrame frame, out PacketFrame decoded)
        {
            decoded = frame;
            if (frame.Body.Length < 2 || _seeds.Count == 0) return false;
            if (frame.Body.Length == 2)
            {
                AdvanceAll();
                return frame.HasValidCrc32;
            }
            if (frame.DeclaredCrc32 == 0) return false;

            var nextSeeds = new List<uint>();
            byte[] chosen = null;
            var signatures = new Dictionary<int, byte[]>();
            var failures = new HashSet<int>();
            foreach (var originalSeed in _seeds)
            {
                var seed = originalSeed;
                var random = GameClientPayloadCipher.Advance(ref seed);
                var key = (byte)random;
                var rotation = (int)((random >> 8) & 7);
                var signature = key | (rotation << 8);
                byte[] plain;
                if (failures.Contains(signature)) continue;
                if (!signatures.TryGetValue(signature, out plain))
                {
                    plain = (byte[])frame.Body.Clone();
                    GameClientPayloadCipher.Decrypt(frame.Body, 2, plain, 2, frame.Body.Length - 2, key, rotation);
                    if (Crc32.Compute(plain) != frame.DeclaredCrc32)
                    {
                        failures.Add(signature);
                        continue;
                    }
                    signatures[signature] = plain;
                }
                nextSeeds.Add(seed);
                if (chosen == null) chosen = plain;
            }
            if (chosen == null)
            {
                // DNF.exe 1.0.1.9 can choose a fresh 15-bit transform seed for
                // each command instead of continuing from the previous packet.
                // Keep rolling-seed compatibility, but retry this packet from
                // the complete seed space when the carried state cannot match.
                if (_seeds.Count != 0x8000)
                {
                    _seeds = CreateSeeds();
                    return TryDecode(frame, out decoded);
                }
                return false;
            }
            _seeds = nextSeeds;
            decoded = new PacketFrame { Type = frame.Type, ProtocolId = frame.ProtocolId, DeclaredCrc32 = frame.DeclaredCrc32, Body = chosen };
            return true;
        }

        private void AdvanceAll()
        {
            for (var index = 0; index < _seeds.Count; index++) { var seed = _seeds[index]; GameClientPayloadCipher.Advance(ref seed); _seeds[index] = seed; }
        }

        private static List<uint> CreateSeeds()
        {
            var result = new List<uint>(0x8000);
            for (uint seed = 0; seed < 0x8000; seed++) result.Add(seed);
            return result;
        }
    }

    internal static class GameClientPayloadCipher
    {
        public static uint Advance(ref uint seed)
        {
            var first = Next(seed); var second = Next(first); var third = Next(second); seed = third;
            var high11 = (first >> 16) & 0x7ff; var high10 = (second >> 16) & 0x3ff; var final10 = (third >> 16) & 0x3ff;
            return final10 ^ ((high10 ^ (high11 << 10)) << 10);
        }

        public static void Decrypt(byte[] source, int sourceOffset, byte[] target, int targetOffset, int count, byte key, int rotation)
        {
            for (var index = 0; index < count; index++)
            {
                var value = (byte)(source[sourceOffset + index] ^ key);
                target[targetOffset + index] = rotation == 0 ? value : (byte)((value >> rotation) | (value << (8 - rotation)));
            }
        }

        private static uint Next(uint value) { return unchecked(value * 0x41C64E6D + 0x3039); }
    }

    internal static class Crc32
    {
        private static readonly uint[] Table = CreateTable();
        public static uint Compute(byte[] bytes)
        {
            return Compute(bytes, 0);
        }
        public static uint Compute(byte[] bytes, uint seed)
        {
            var crc = ~seed;
            for (var index = 0; index < bytes.Length; index++) crc = Table[(crc ^ bytes[index]) & 0xff] ^ (crc >> 8);
            return ~crc;
        }
        private static uint[] CreateTable()
        {
            var table = new uint[256];
            for (uint index = 0; index < 256; index++)
            {
                var value = index;
                for (var bit = 0; bit < 8; bit++) value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
                table[index] = value;
            }
            return table;
        }
    }

    internal static class LittleEndian
    {
        public static ushort ReadUInt16(byte[] value, int offset) { return (ushort)(value[offset] | value[offset + 1] << 8); }
        public static short ReadInt16(byte[] value, int offset) { return unchecked((short)ReadUInt16(value, offset)); }
        public static uint ReadUInt32(byte[] value, int offset) { return (uint)(value[offset] | value[offset + 1] << 8 | value[offset + 2] << 16 | value[offset + 3] << 24); }
        public static void WriteUInt16(byte[] value, int offset, ushort number) { value[offset] = (byte)number; value[offset + 1] = (byte)(number >> 8); }
        public static void WriteUInt32(byte[] value, int offset, uint number) { value[offset] = (byte)number; value[offset + 1] = (byte)(number >> 8); value[offset + 2] = (byte)(number >> 16); value[offset + 3] = (byte)(number >> 24); }
    }
}
