using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace DF60A1
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            var options = ServerOptions.Parse(args);

            if (args.Any(x => string.Equals(x, "--inspect-pvf", StringComparison.OrdinalIgnoreCase)))
            {
                uint fileNumber;
                var bytes = new PvfArchiveReader(options.ScriptPvfPath).ReadAllBytes("etc/channel_info.etc", out fileNumber);
                Console.WriteLine("fileNumber={0} length={1}", fileNumber, bytes.Length);
                Console.WriteLine(Encoding.GetEncoding(949).GetString(bytes).Replace("\0", string.Empty));
                return 0;
            }

            if (args.Any(x => string.Equals(x, "--self-test", StringComparison.OrdinalIgnoreCase)))
                return SelfTest.Run();

            if (args.Any(x => string.Equals(x, "--seed-character", StringComparison.OrdinalIgnoreCase)))
                return SeedCharacter(options);

            if (args.Any(x => string.Equals(x, "--launch", StringComparison.OrdinalIgnoreCase)))
                return LaunchClient(options);

            if (!File.Exists(options.ScriptPvfPath))
            {
                Console.Error.WriteLine("找不到 Script.pvf。");
                Console.Error.WriteLine("请将与 2008 国服客户端匹配的 Script.pvf 放到: " + options.ScriptPvfPath);
                return 5;
            }

            Directory.CreateDirectory(options.DataDirectory);
            Directory.CreateDirectory(options.LogDirectory);
            using (var store = new SqliteGameStore(options.DatabasePath, options.SchemaPath))
            using (var server = new GameServer(options, store))
            {
                Console.CancelKeyPress += delegate(object sender, ConsoleCancelEventArgs eventArgs)
                {
                    eventArgs.Cancel = true;
                    server.Stop();
                };

                server.Run();
            }

            return 0;
        }

        private static int SeedCharacter(ServerOptions options)
        {
            Directory.CreateDirectory(options.DataDirectory);
            using (var store = new SqliteGameStore(options.DatabasePath, options.SchemaPath))
            {
                var accountId = store.EnsureAccount(options.Account, options.Password);
                var created = store.CreateCharacter(accountId, options.CharacterName, options.CharacterJob);
                if (!created.Success)
                {
                    Console.Error.WriteLine("预置角色失败: " + created.Error);
                    return 4;
                }
                Console.WriteLine("已预置角色: {0}, job={1}, slot={2}", created.Character.Name, created.Character.Job, created.Character.Slot);
                return 0;
            }
        }

        private static int LaunchClient(ServerOptions options)
        {
            if (!File.Exists(options.ClientPath))
            {
                Console.Error.WriteLine("找不到客户端: " + options.ClientPath);
                return 2;
            }

            var launcherData = string.Join("?", new[]
            {
                "99", options.Host, options.Port.ToString(), options.Account, options.Password,
                "0", "0", "0", "0", "0", "0", "0", "0"
            });
            var info = new ProcessStartInfo(options.ClientPath, launcherData)
            {
                WorkingDirectory = Path.GetDirectoryName(options.ClientPath),
                UseShellExecute = false
            };
            var process = Process.Start(info);
            Console.WriteLine("已启动 DNF.exe，PID={0}", process == null ? 0 : process.Id);
            Console.WriteLine("启动参数: {0}", launcherData);
            return process == null ? 3 : 0;
        }
    }

    internal sealed class ServerOptions
    {
        public string Host = "127.0.0.1";
        public int Port = 7001;
        public string Account = "test";
        public string Password = "test";
        public string ClientPath;
        public string DataDirectory;
        public string LogDirectory;
        public string DatabasePath;
        public string SchemaPath;
        public string ScriptPvfPath;
        public string CharacterName = "赛丽亚测试";
        public byte CharacterJob;
        public bool AutoSelect;

        public static ServerOptions Parse(string[] args)
        {
            var baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            var projectRoot = Path.GetFullPath(Path.Combine(baseDirectory, "..", "..", "..", "..", "..", ".."));
            var clientDirectory = Path.GetFullPath(Path.Combine(projectRoot, ".."));
            var options = new ServerOptions
            {
                ClientPath = Path.Combine(clientDirectory, "DNF.exe"),
                DataDirectory = Path.Combine(baseDirectory, "data"),
                LogDirectory = Path.Combine(baseDirectory, "logs"),
                SchemaPath = Path.Combine(baseDirectory, "schema.sql")
            };
            options.ScriptPvfPath = Path.Combine(projectRoot, "data", "Script.pvf");

            for (var index = 0; index < args.Length; index++)
            {
                var value = index + 1 < args.Length ? args[index + 1] : null;
                switch (args[index].ToLowerInvariant())
                {
                    case "--host": if (value != null) options.Host = value; index++; break;
                    case "--port": if (value != null) options.Port = int.Parse(value); index++; break;
                    case "--account": if (value != null) options.Account = value; index++; break;
                    case "--password": if (value != null) options.Password = value; index++; break;
                    case "--client": if (value != null) options.ClientPath = Path.GetFullPath(value); index++; break;
                    case "--data": if (value != null) options.DataDirectory = Path.GetFullPath(value); index++; break;
                    case "--character": if (value != null) options.CharacterName = value; index++; break;
                    case "--job": if (value != null) options.CharacterJob = byte.Parse(value); index++; break;
                    case "--auto-select": options.AutoSelect = true; break;
                }
            }

            options.DatabasePath = Path.Combine(options.DataDirectory, "dnf2008.sqlite");
            return options;
        }
    }
}
