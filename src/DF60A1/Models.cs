using System;

namespace DF60A1
{
    internal sealed class CharacterRecord
    {
        public int Id;
        public int AccountId;
        public byte Slot;
        public string Name;
        public byte Job;
        public byte GrowType;
        public byte Level;
        public byte TownId;
        public byte AreaId;
        public short X;
        public short Y;
        public byte Direction;
    }

    internal sealed class CreateCharacterResult
    {
        public bool Success;
        public string Error;
        public CharacterRecord Character;

        public static CreateCharacterResult Fail(string error)
        {
            return new CreateCharacterResult { Success = false, Error = error };
        }
    }
}
