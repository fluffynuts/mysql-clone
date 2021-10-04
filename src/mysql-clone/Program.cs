using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using PeanutButter.EasyArgs;
using PeanutButter.EasyArgs.Attributes;
using PeanutButter.Utils;

namespace mysql_clone
{
    class Program
    {
        private static string _lastLabel;

        static int Main(string[] args)
        {
            var opts = args.ParseTo<IOptions>();
            var tools = FindTools(opts);
            if (!tools.IsValid)
            {
                Console.WriteLine(
                    "Unable to find mysql and mysqldump. Either ensure they are in your path or set the MYSQL_BIN environment variable to the path in which they can be found");
                return 1;
            }

            if (opts.Verbose || opts.Debug)
            {
                Console.WriteLine($"Using mysqldump at: {tools.MySqlDump}");
                Console.WriteLine($"Using mysql at: {tools.MySqlCli}");
            }

            if (string.IsNullOrWhiteSpace(opts.SourcePassword) || string.IsNullOrWhiteSpace(opts.TargetPassword))
            {
                opts.Interactive = true;
            }

            using var tempFile = new AutoTempFile();
            if (string.IsNullOrWhiteSpace(opts.DumpFile))
            {
                opts.DumpFile = tempFile.Path;
            }

            RunInteractiveIfRequired(opts);

            if (opts.RestoreOnly)
            {
                if (!File.Exists(opts.DumpFile))
                {
                    Console.WriteLine($"dump file not found at {opts.DumpFile}");
                    return 2;
                }
            }
            else
            {
                DumpSource(opts, tools);
            }

            CreateTargetDatabase(opts, tools);
            RestoreTargetDatabase(opts, tools);
            RunAfterCommands(opts, tools);

            return 0;
        }

        private static string ReadPassword(string prompt)
        {
            Console.Out.Write($"{prompt}: ");
            var captured = "";
            while (true)
            {
                var key = Console.ReadKey(true);
                if (key.Key == ConsoleKey.Enter)
                {
                    Console.Out.Write("\n");
                    return captured;
                }

                Console.Out.Write("*");
                captured += key.KeyChar;
            }
        }

        private static void RunInteractiveIfRequired(IOptions opts)
        {
            Console.WriteLine("Source database:");

            opts.SourceHost = Ask("  host", opts.SourceHost);
            opts.SourceUser = Ask("  user", opts.SourceUser);
            opts.SourcePassword = ReadPassword("  password");
            opts.SourceDatabase = Ask("  database", opts.SourceDatabase);
            opts.SourcePort = TryGetInt(() => Ask("  port", opts.SourcePort.ToString()));

            Console.WriteLine("Target database:");
            opts.TargetHost = Ask("  host", opts.TargetHost);
            opts.TargetUser = Ask("  user", opts.TargetUser);
            opts.TargetPassword = ReadPassword("  password");
            opts.TargetDatabase = Ask("  database", opts.TargetDatabase);
            opts.TargetPort = TryGetInt(() => Ask("  port", opts.TargetPort.ToString()));

            Console.WriteLine("Miscellaneous:");
            opts.DumpFile = Ask("  dump file:", opts.DumpFile);
            opts.RestoreOnly = TryGetBoolean(() => Ask("  restore only:", opts.RestoreOnly.ToString()));
            opts.AfterRestoration = new[] { Ask("  after restore, run sql / file", "", emptyOk: true) };
        }

        private static bool TryGetBoolean(Func<string> func)
        {
            var input = func();
            if (bool.TryParse(input, out var result))
            {
                return result;
            }

            return input.AsBoolean();
        }

        private static int TryGetInt(Func<string> func)
        {
            while (true)
            {
                if (int.TryParse(func(), out var result))
                {
                    return result;
                }
            }
        }

        private static string Ask(
            string prompt,
            string current,
            bool emptyOk = false
        )
        {
            do
            {
                var more = string.IsNullOrWhiteSpace(current)
                    ? ""
                    : $" ({current})";
                Console.Out.Write($"{prompt}{more}: ");
                var thisAnswer = Console.ReadLine()?.Trim();
                if (!string.IsNullOrWhiteSpace(thisAnswer))
                {
                    current = thisAnswer;
                }
            } while (string.IsNullOrWhiteSpace(current) || emptyOk);

            return current;
        }

        private static void RunAfterCommands(IOptions opts, Tools tools)
        {
            var items = opts.AfterRestoration
                ?.Where(s => !string.IsNullOrWhiteSpace(s))
                .ToArray() ?? new string[0];
            if (items.Length == 0)
            {
                return;
            }

            StartStatus("Run in extra commands");
            using var io = StartMySqlClientForTargetDatabase(opts, tools, Fail);
            foreach (var after in items)
            {
                if (File.Exists(after))
                {
                    StreamFileToStdIn(after, io, Fail);
                }
                else
                {
                    io.Process.StandardInput.Write(after);
                    io.Process.StandardInput.Flush();
                    ReportError(io, Fail);
                }

                Ok();
            }
        }

        private static void RestoreTargetDatabase(
            IOptions opts,
            Tools tools
        )
        {
            var inputFile = opts.DumpFile;
            StartStatus(
                $"Restore target {opts.TargetDatabase} on {opts.TargetHost} from {inputFile}"
            );
            using var io = StartMySqlClientForTargetDatabase(opts, tools, Fail);
            StreamFileToStdIn(inputFile, io, Fail);
            Ok();
        }

        private static void StreamFileToStdIn(
            string fileName,
            IProcessIO io,
            Action onFail
        )
        {
            using var fileStream = File.Open(fileName, FileMode.Open, FileAccess.Read);
            using var reader = new StreamReader(fileStream);
            var totalRead = 0M;
            var chunk = 8 * 1024 * 1024;
            var buffer = new char[chunk];
            var started = DateTime.Now;
            var lastRead = 0M;
            do
            {
                var fractionDone = totalRead / fileStream.Length;
                var perc = (int)(fractionDone * 100);
                var runTime = (decimal)(DateTime.Now - started).TotalSeconds;
                var secondsLeft = fractionDone > 0
                    ? runTime / fractionDone
                    : -1;

                ReportProgress(perc, (int)secondsLeft);
                var remaining = fileStream.Length - totalRead;
                var toRead = remaining > chunk
                    ? chunk
                    : remaining;
                lastRead = reader.Read(buffer, 0, (int)toRead);
                totalRead += lastRead;
                io.Process.StandardInput.Write(buffer, 0, (int)lastRead);
                ReportError(io, onFail);
            } while (lastRead > 0);
        }

        private static IProcessIO StartMySqlClientForTarget(
            IOptions opts,
            Tools tools,
            Action onFail
        )
        {
            var io = ProcessIO.Start(
                tools.MySqlCli,
                "-h", opts.TargetHost,
                "-P", opts.TargetPort.ToString(),
                "-u", opts.TargetUser,
                $"-p{opts.TargetPassword}"
            );
            ReportError(io, onFail);
            return io;
        }

        private static IProcessIO StartMySqlClientForTargetDatabase(
            IOptions opts,
            Tools tools,
            Action onFail
        )
        {
            var io = ProcessIO.Start(
                tools.MySqlCli,
                "-h", opts.TargetHost,
                "-P", opts.TargetPort.ToString(),
                "-D", opts.TargetDatabase,
                "-u", opts.TargetUser,
                $"-p{opts.TargetPassword}"
            );
            ReportError(io, onFail);
            return io;
        }

        private static void CreateTargetDatabase(
            IOptions opts,
            Tools tools
        )
        {
            StartStatus($"Create target: {opts.TargetDatabase} on {opts.TargetHost}");
            using var io = StartMySqlClientForTarget(opts, tools, Fail);
            foreach (var line in GenerateCreationScriptFor(opts.TargetDatabase))
            {
                io.Process.StandardInput.WriteLine(line);
            }

            Ok();
        }

        public static void StartStatus(string label)
        {
            _lastLabel = label;
            Console.Out.Write($"           {label}");
        }

        public static void Ok()
        {
            Console.Out.Write("\r[ OK ]    \n");
        }

        public static void Fail()
        {
            Console.Out.Write("\r[FAIL]    \r");
        }

        public static void ReportProgress(
            int perc,
            int secondsLeft
        )
        {
            if (secondsLeft < 0)
            {
                Console.Out.Write($"\r{perc,2}%       ");
                return;
            }

            var minutes = secondsLeft / 60;
            var seconds = secondsLeft % 60;
            Console.Out.Write($"\r{perc,2}% {minutes:00}:{seconds:00} \r");
        }

        private static void ReportError(IProcessIO io, Action onFail)
        {
            if (!io.Process.HasExited)
            {
                return;
            }

            if (io.ExitCode != 0)
            {
                onFail();
                foreach (var line in io.StandardError)
                {
                    Console.WriteLine(line);
                }

                Console.WriteLine($"Command was: {io.Process.StartInfo.FileName} {io.Process.StartInfo.Arguments}");

                Environment.Exit(io.ExitCode);
            }
        }

        private static string[] GenerateCreationScriptFor(string dbName)
        {
            return new[]
            {
                $"drop database if exists `{dbName}`;",
                $"create database `{dbName}`;"
            };
        }

        private static void DumpSource(
            IOptions opts,
            Tools tools
        )
        {
            StartStatus($"Dumping source database {opts.SourceDatabase} on {opts.SourceHost} to {opts.DumpFile}");
            using var io = ProcessIO.Start(
                tools.MySqlDump,
                "-h", opts.SourceHost,
                "-P", opts.SourcePort.ToString(),
                "-u", opts.SourceUser,
                $"-p{opts.SourcePassword}",
                "--hex-blob",
                opts.SourceDatabase
            );
            ReportError(io, Fail);
            using var fs = File.Open(opts.DumpFile, FileMode.Create, FileAccess.Write);
            io.StandardOutput.ForEach(line =>
            {
                fs.WriteString($"{line}\n");
            });
            ReportError(io, Fail);
            Ok();
        }

        public static Tools FindTools(IOptions opts)
        {
            var sep = OperatingSystem.IsWindows()
                ? ";"
                : ":";
            var pathParts = Environment.GetEnvironmentVariable("PATH")
                ?.Split(sep, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                ?.ToList() ?? new List<string>();

            pathParts.Insert(0, Environment.GetEnvironmentVariable("MYSQL_BIN"));

            if (OperatingSystem.IsWindows())
            {
                pathParts.Add(TryFindMySqlBinDirInDefaultWindowsLocation());
            }

            return new Tools()
            {
                MySqlDump = FindInDirs("mysqldump", pathParts, opts),
                MySqlCli = FindInDirs("mysql", pathParts, opts)
            };
        }

        private static string TryFindMySqlBinDirInDefaultWindowsLocation()
        {
            var search = new[]
                {
                    Environment.GetEnvironmentVariable("ProgramFiles"),
                    Environment.GetEnvironmentVariable("ProgramFiles(x86)")
                }.Where(o => o is not null)
                .ToArray();
            foreach (var s in search)
            {
                var mysqlBase = Path.Join(s, "MySql");
                if (!Directory.Exists(mysqlBase))
                {
                    continue;
                }

                var serverDir = Directory.EnumerateDirectories(mysqlBase).FirstOrDefault(
                    p => Path.GetFileName(p).StartsWith("mysql server", StringComparison.OrdinalIgnoreCase)
                );
                if (serverDir is not null)
                {
                    return Path.Join(serverDir, "bin");
                }
            }

            return null;
        }

        private static string FindInDirs(
            string exe,
            IEnumerable<string> pathParts,
            IOptions opts
        )
        {
            if (OperatingSystem.IsWindows())
            {
                exe = $"{exe}.exe";
            }

            return pathParts.Where(s => !string.IsNullOrWhiteSpace(s))
                .Aggregate(
                    null as string,
                    (acc, cur) => acc ?? PathIfExists(Path.Join(cur, exe), opts)
                );
        }

        private static string PathIfExists(string path, IOptions options)
        {
            if (options.Debug)
            {
                Console.WriteLine($"search: {path}");
            }

            return File.Exists(path)
                ? path
                : null;
        }

        public class Tools
        {
            public bool IsValid =>
                MySqlCli is not null &&
                MySqlDump is not null;

            public string MySqlDump { get; set; }
            public string MySqlCli { get; set; }
        }
    }

    public interface IOptions
    {
        [Default("localhost")]
        [ShortName('h')]
        public string SourceHost { get; set; }

        [Default("root")]
        [ShortName('u')]
        public string SourceUser { get; set; }

        [ShortName('p')]
        public string SourcePassword { get; set; }

        [Default(3306)]
        public int SourcePort { get; set; }

        [ShortName('d')]
        public string SourceDatabase { get; set; }

        [Default("localhost")]
        [ShortName('H')]
        public string TargetHost { get; set; }

        [Default("root")]
        [ShortName('U')]
        public string TargetUser { get; set; }

        [ShortName('P')]
        public string TargetPassword { get; set; }

        [ShortName('D')]
        public string TargetDatabase { get; set; }

        [Default(3306)]
        public int TargetPort { get; set; }

        public bool RestoreOnly { get; set; }

        public bool Verbose { get; set; }
        public bool Debug { get; set; }

        [Description("path to use for intermediate sql dump file (optional)")]
        public string DumpFile { get; set; }

        [Description("sql to run on the target database after restoration completes")]
        public string[] AfterRestoration { get; set; }

        [Description("Run in interactive mode")]
        public bool Interactive { get; set; }
    }
}