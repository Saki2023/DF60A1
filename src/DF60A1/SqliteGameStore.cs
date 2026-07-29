using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Text;

namespace DF60A1
{
    internal sealed class SqliteGameStore : IDisposable
    {
        private readonly string _connectionString;
        private readonly object _gate = new object();

        public SqliteGameStore(string databasePath, string schemaPath)
        {
            var directory = Path.GetDirectoryName(databasePath);
            if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
            _connectionString = "Data Source=" + databasePath + ";Version=3;Foreign Keys=True;Journal Mode=WAL;";

            using (var connection = Open())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = File.ReadAllText(schemaPath, Encoding.UTF8);
                command.ExecuteNonQuery();
            }
        }

        public int EnsureAccount(string accountName, string password)
        {
            accountName = string.IsNullOrWhiteSpace(accountName) ? "test" : accountName.Trim();
            lock (_gate)
            using (var connection = Open())
            using (var transaction = connection.BeginTransaction())
            {
                using (var insert = connection.CreateCommand())
                {
                    insert.Transaction = transaction;
                    insert.CommandText = "INSERT OR IGNORE INTO accounts(account_name,password) VALUES(@name,@password);";
                    insert.Parameters.AddWithValue("@name", accountName);
                    insert.Parameters.AddWithValue("@password", password ?? string.Empty);
                    insert.ExecuteNonQuery();
                }
                int id;
                using (var select = connection.CreateCommand())
                {
                    select.Transaction = transaction;
                    select.CommandText = "SELECT account_id FROM accounts WHERE account_name=@name;";
                    select.Parameters.AddWithValue("@name", accountName);
                    id = Convert.ToInt32(select.ExecuteScalar());
                }
                using (var update = connection.CreateCommand())
                {
                    update.Transaction = transaction;
                    update.CommandText = "UPDATE accounts SET last_login_at=CURRENT_TIMESTAMP WHERE account_id=@id;";
                    update.Parameters.AddWithValue("@id", id);
                    update.ExecuteNonQuery();
                }
                transaction.Commit();
                return id;
            }
        }

        public List<CharacterRecord> ListCharacters(int accountId)
        {
            var result = new List<CharacterRecord>();
            lock (_gate)
            using (var connection = Open())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT character_id,account_id,slot_index,name,job,grow_type,level,town_id,area_id,pos_x,pos_y,direction FROM characters WHERE account_id=@id ORDER BY slot_index;";
                command.Parameters.AddWithValue("@id", accountId);
                using (var reader = command.ExecuteReader())
                    while (reader.Read()) result.Add(Map(reader));
            }
            return result;
        }

        public CreateCharacterResult CreateCharacter(int accountId, string name, byte job)
        {
            name = (name ?? string.Empty).Trim();
            var encodedLength = Encoding.GetEncoding(936).GetByteCount(name);
            if (encodedLength < 1 || encodedLength > 18) return CreateCharacterResult.Fail("角色名必须为 1-18 个 GBK 字节");
            // DNF.exe 1.0.1.9 create UI exposes five jobs.  Value 5 is the
            // client's internal "no selection" sentinel, not a valid job.
            if (job > 4) return CreateCharacterResult.Fail("职业编号无效（2008 客户端只接受 0-4）");

            lock (_gate)
            using (var connection = Open())
            using (var transaction = connection.BeginTransaction())
            {
                int slot;
                using (var slotCommand = connection.CreateCommand())
                {
                    slotCommand.Transaction = transaction;
                    slotCommand.CommandText = "SELECT COALESCE(MAX(slot_index),-1)+1 FROM characters WHERE account_id=@id;";
                    slotCommand.Parameters.AddWithValue("@id", accountId);
                    slot = Convert.ToInt32(slotCommand.ExecuteScalar());
                }
                if (slot >= 16) return CreateCharacterResult.Fail("角色槽已满");

                try
                {
                    long id;
                    using (var insert = connection.CreateCommand())
                    {
                        insert.Transaction = transaction;
                        // Spell out the initial town position instead of
                        // relying on an already-created database's column
                        // defaults.  In this client's Script.pvf, town 1 area
                        // 1 is Elvengard/Gate.map.  The map's only
                        // [town movable area] is (330,324,289,24), whose centre
                        // is (474,336).  The 474,234 pair in Elvengard.twn's
                        // [gate] block is the return point in area 0, not the
                        // spawn point inside the Seria room.
                        insert.CommandText = "INSERT INTO characters(account_id,slot_index,name,job,town_id,area_id,pos_x,pos_y,direction) VALUES(@account,@slot,@name,@job,1,1,474,336,5); SELECT last_insert_rowid();";
                        insert.Parameters.AddWithValue("@account", accountId);
                        insert.Parameters.AddWithValue("@slot", slot);
                        insert.Parameters.AddWithValue("@name", name);
                        insert.Parameters.AddWithValue("@job", job);
                        id = (long)insert.ExecuteScalar();
                    }
                    transaction.Commit();
                    return new CreateCharacterResult
                    {
                        Success = true,
                        Character = new CharacterRecord { Id = (int)id, AccountId = accountId, Slot = (byte)slot, Name = name, Job = job, GrowType = 0, Level = 1, TownId = 1, AreaId = 1, X = 474, Y = 336, Direction = 5 }
                    };
                }
                catch (SQLiteException exception)
                {
                    transaction.Rollback();
                    return CreateCharacterResult.Fail(exception.ResultCode == SQLiteErrorCode.Constraint ? "角色名已存在" : exception.Message);
                }
            }
        }

        public void UpdatePosition(int characterId, byte town, byte area, short x, short y, byte direction)
        {
            lock (_gate)
            using (var connection = Open())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "UPDATE characters SET town_id=@town,area_id=@area,pos_x=@x,pos_y=@y,direction=@direction,updated_at=CURRENT_TIMESTAMP WHERE character_id=@id;";
                command.Parameters.AddWithValue("@town", town);
                command.Parameters.AddWithValue("@area", area);
                command.Parameters.AddWithValue("@x", x);
                command.Parameters.AddWithValue("@y", y);
                command.Parameters.AddWithValue("@direction", direction);
                command.Parameters.AddWithValue("@id", characterId);
                command.ExecuteNonQuery();
            }
        }

        private SQLiteConnection Open()
        {
            var connection = new SQLiteConnection(_connectionString);
            connection.Open();
            return connection;
        }

        private static CharacterRecord Map(SQLiteDataReader reader)
        {
            return new CharacterRecord
            {
                Id = reader.GetInt32(0), AccountId = reader.GetInt32(1), Slot = (byte)reader.GetInt32(2), Name = reader.GetString(3),
                Job = (byte)reader.GetInt32(4), GrowType = (byte)reader.GetInt32(5), Level = (byte)reader.GetInt32(6),
                TownId = (byte)reader.GetInt32(7), AreaId = (byte)reader.GetInt32(8), X = (short)reader.GetInt32(9),
                Y = (short)reader.GetInt32(10), Direction = (byte)reader.GetInt32(11)
            };
        }

        public void Dispose() { }
    }
}
