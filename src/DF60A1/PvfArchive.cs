using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DF60A1
{
    internal sealed class PvfArchiveReader
    {
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("DNF_SCRIPT_PACK");
        private readonly byte[] _archive;
        private readonly int _bodyOffset;
        private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

        public PvfArchiveReader(string path)
        {
            _archive = File.ReadAllBytes(path);
            if (_archive.Length < Magic.Length + 12 || !Matches(_archive, Magic)) throw new InvalidDataException("不是 DNF_SCRIPT_PACK: " + path);
            var treeLength = (int)LittleEndian.ReadUInt32(_archive, Magic.Length);
            var treeCrc = LittleEndian.ReadUInt32(_archive, Magic.Length + 4);
            var fileCount = LittleEndian.ReadUInt32(_archive, Magic.Length + 8);
            if (treeLength <= 0 || treeLength % 4 != 0 || treeLength > _archive.Length - Magic.Length - 12) throw new InvalidDataException("PVF 树头无效");
            var tree = new byte[treeLength]; Buffer.BlockCopy(_archive, Magic.Length + 12, tree, 0, treeLength); Transform(tree, treeCrc, false);
            var cursor = 0; var encoding = Encoding.GetEncoding(949);
            for (uint index = 0; index < fileCount; index++)
            {
                if (cursor > tree.Length - 20) throw new InvalidDataException("PVF 树提前结束");
                var number = LittleEndian.ReadUInt32(tree, cursor); var offset = LittleEndian.ReadUInt32(tree, cursor + 4);
                var length = (int)LittleEndian.ReadUInt32(tree, cursor + 8); var crc = LittleEndian.ReadUInt32(tree, cursor + 12);
                var pathLength = (int)LittleEndian.ReadUInt32(tree, cursor + 16); cursor += 20;
                if (length < 0 || pathLength <= 0 || pathLength > tree.Length - cursor) throw new InvalidDataException("PVF 条目无效");
                var virtualPath = Normalize(encoding.GetString(tree, cursor, pathLength).TrimEnd('\0')); cursor += pathLength;
                _entries[virtualPath] = new Entry(number, offset, length, crc);
            }
            _bodyOffset = Magic.Length + 12 + treeLength;
        }

        public byte[] ReadAllBytes(string path, out uint fileNumber)
        {
            Entry entry; if (!_entries.TryGetValue(Normalize(path), out entry)) throw new FileNotFoundException("PVF 内不存在 " + path);
            fileNumber = entry.Number; var padded = (entry.Length + 3) & ~3; var absolute = checked(_bodyOffset + (int)entry.Offset);
            if (absolute < _bodyOffset || absolute > _archive.Length - padded) throw new InvalidDataException("PVF 条目越界");
            var content = new byte[padded]; Buffer.BlockCopy(_archive, absolute, content, 0, padded); Transform(content, entry.Crc, false);
            if (content.Length == entry.Length) return content; var result = new byte[entry.Length]; Buffer.BlockCopy(content, 0, result, 0, result.Length); return result;
        }

        private static bool Matches(byte[] source, byte[] expected) { for (var i = 0; i < expected.Length; i++) if (source[i] != expected[i]) return false; return true; }
        private static string Normalize(string path) { return path.Replace('\\', '/').TrimStart('/').ToLowerInvariant(); }
        private static void Transform(byte[] bytes, uint key, bool encrypt)
        {
            for (var offset = 0; offset < bytes.Length; offset += 4)
            {
                var value = LittleEndian.ReadUInt32(bytes, offset);
                value = encrypt ? RotateLeft(value, 6) ^ key : RotateRight(value ^ key, 6);
                LittleEndian.WriteUInt32(bytes, offset, value);
            }
        }
        private static uint RotateLeft(uint value, int count) { return value << count | value >> (32 - count); }
        private static uint RotateRight(uint value, int count) { return value >> count | value << (32 - count); }
        private sealed class Entry { public readonly uint Number, Offset, Crc; public readonly int Length; public Entry(uint number, uint offset, int length, uint crc) { Number = number; Offset = offset; Length = length; Crc = crc; } }

        public static byte[] CreateSingleFile(string virtualPath, byte[] content, uint fileNumber)
        {
            var path = Encoding.ASCII.GetBytes(Normalize(virtualPath)); var paddedContent = new byte[(content.Length + 3) & ~3]; Buffer.BlockCopy(content, 0, paddedContent, 0, content.Length);
            var fileCrc = Crc32.Compute(paddedContent, 0); var tree = new byte[(20 + path.Length + 3) & ~3];
            LittleEndian.WriteUInt32(tree, 0, fileNumber); LittleEndian.WriteUInt32(tree, 4, 0); LittleEndian.WriteUInt32(tree, 8, (uint)content.Length);
            LittleEndian.WriteUInt32(tree, 12, fileCrc); LittleEndian.WriteUInt32(tree, 16, (uint)path.Length); Buffer.BlockCopy(path, 0, tree, 20, path.Length);
            var treeCrc = Crc32.Compute(tree, 1); Transform(tree, treeCrc, true); Transform(paddedContent, fileCrc, true);
            var output = new byte[Magic.Length + 12 + tree.Length + paddedContent.Length]; Buffer.BlockCopy(Magic, 0, output, 0, Magic.Length);
            LittleEndian.WriteUInt32(output, Magic.Length, (uint)tree.Length); LittleEndian.WriteUInt32(output, Magic.Length + 4, treeCrc); LittleEndian.WriteUInt32(output, Magic.Length + 8, 1);
            Buffer.BlockCopy(tree, 0, output, Magic.Length + 12, tree.Length); Buffer.BlockCopy(paddedContent, 0, output, Magic.Length + 12 + tree.Length, paddedContent.Length); return output;
        }
    }
}
