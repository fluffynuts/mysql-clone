using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PeanutButter.EasyArgs;
using PeanutButter.Utils;

namespace mysql_clone
{
    class Program
    {
        private static string _lastLabel;
        private static DateTime _started;

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


            using var tempFile = new AutoTempFile();
            if (string.IsNullOrWhiteSpace(opts.DumpFile))
            {
                opts.DumpFile = tempFile.Path;
            }
            else if (opts.RestoreOnly && !File.Exists(opts.DumpFile))
            {
                Console.WriteLine($"dump file not found at {opts.DumpFile}");
                return 2;
            }

            ForceInteractiveIfRequired(opts);
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

        private static void ForceInteractiveIfRequired(IOptions opts)
        {
            var sourcePasswordRequired = !opts.RestoreOnly && string.IsNullOrWhiteSpace(opts.SourcePassword);
            var targetPasswordRequired = string.IsNullOrWhiteSpace(opts.TargetPassword);
            var sourceDbRequired = !opts.RestoreOnly && string.IsNullOrWhiteSpace(opts.SourceDatabase);
            var targetDbRequired = string.IsNullOrWhiteSpace(opts.TargetDatabase);
            // bare minimum required inputs as server, user & port have defaults
            if (sourcePasswordRequired ||
                targetPasswordRequired ||
                sourceDbRequired ||
                targetDbRequired)
            {
                opts.Interactive = true;
            }
        }

        private static string ReadPassword(string prompt, string existing)
        {
            var add = string.IsNullOrWhiteSpace(existing)
                ? ""
                : $" ({new String('*', existing.Length)})";
            Console.Out.Write($"{prompt}{add}: ");
            var captured = "";
            while (true)
            {
                var key = Console.ReadKey(true);
                if (key.Key == ConsoleKey.Enter)
                {
                    Console.Out.Write("\n");
                    return string.IsNullOrWhiteSpace(captured)
                        ? existing
                        : captured;
                }

                Console.Out.Write("*");
                captured += key.KeyChar;
            }
        }

        private static void RunInteractiveIfRequired(IOptions opts)
        {
            if (!opts.Interactive)
            {
                return;
            }

            Console.WriteLine("Please specify a location for the dump file, if you'd like to:");
            Console.WriteLine("- if the file exists, export can be skipped (import-only)");
            Console.WriteLine("- if the file does not exist, it will be created, and left on disk");
            Console.WriteLine("- if you accept the default, a temp file will be created and destroyed at exit");
            opts.DumpFile = Ask("    dump file location:", opts.DumpFile);
            if (File.Exists(opts.DumpFile))
            {
                opts.RestoreOnly = TryGetBoolean(() =>
                    Ask(
                        "  restore from existing dump file? ",
                        "Y"
                    )
                );
            }

            if (!opts.RestoreOnly)
            {
                Console.WriteLine("Source database:");

                opts.SourceHost = Ask("  host", opts.SourceHost);
                opts.SourceUser = Ask("  user", opts.SourceUser);
                opts.SourcePassword = ReadPassword("  password", "");
                opts.SourceDatabase = Ask("  database", opts.SourceDatabase);
                opts.SourcePort = TryGetInt(() => Ask("  port", opts.SourcePort.ToString()));
            }

            Console.WriteLine("Target database:");
            opts.TargetHost = Ask("  host", opts.TargetHost);
            var (targetUser, targetPassword, targetDatabase, targetPort) =
                (opts.TargetUser, opts.TargetPassword, opts.TargetDatabase, opts.TargetPort);
            if (opts.SourceHost == opts.TargetHost)
            {
                targetUser = opts.SourceUser;
                targetPassword = opts.SourcePassword;
                targetPort = opts.SourcePort;
            }
            else
            {
                targetDatabase = opts.SourceDatabase; // assume a copy
            }

            opts.TargetUser = Ask("  user", FirstNonEmpty(opts.TargetUser, targetUser));
            opts.TargetPassword = ReadPassword("  password", FirstNonEmpty(opts.TargetPassword, targetPassword));
            opts.TargetDatabase = Ask("  database", FirstNonEmpty(opts.TargetDatabase, targetDatabase));
            opts.TargetPort = TryGetInt(() => Ask("  port", opts.TargetPort.ToString()));


            opts.AfterRestoration = new[] {Ask("  after restore, run sql / file", "", emptyOk: true)};

            string FirstNonEmpty(params string[] values)
            {
                return values.Aggregate(
                    "",
                    (acc, cur) => string.IsNullOrWhiteSpace(acc)
                        ? cur
                        : acc
                );
            }
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
            } while (ShouldAskAgain());

            return current;

            bool ShouldAskAgain()
            {
                if (emptyOk)
                {
                    return false;
                }

                return string.IsNullOrWhiteSpace(current);
            }
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
                    StreamFileToStdIn(after, io, Fail, s => FixEncodingsIfRequired(s, opts));
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
            StreamFileToStdIn(inputFile, io, Fail, s => FixEncodingsIfRequired(s, opts));
            io.Process.StandardInput.Close();
            io.Process.WaitForExit();
            if (io.Process.ExitCode == 0)
            {
                Ok();
            }
            else
            {
                Fail();
                foreach (var line in io.StandardOutput)
                {
                    Console.WriteLine($"ERR: {line}");
                }
            }
        }

        private static void FixEncodingsIfRequired(
            char[] buffer,
            IOptions opts
        )
        {
            if (opts.RetainOriginalEncodings)
            {
                return;
            }
            ReplaceInBuffer(buffer, FindCi, ReplCi);
            ReplaceInBuffer(buffer, FindCharset, ReplCharset);
        }

        private static readonly char[] FindCi = "utf8mb4_0900_ai_ci".ToCharArray();
        private static readonly char[] ReplCi = "utf8_general_ci   ".ToCharArray().PadToLength(FindCi.Length, ' ');
        private static readonly char[] FindCharset = "CHARSET=utf8mb4".ToCharArray();
        private static readonly char[] ReplCharset = "CHARSET=utf8".ToCharArray().PadToLength(FindCharset.Length, ' ');

        private static void ReplaceInBuffer(
            char[] buffer,
            char[] find,
            char[] replace
        )
        {
            // hack - only works if the buffers are the same length
            // otherwise there is bound to be some strangeness
            var matched = 0;
            for (var i = 0; i < buffer.Length; i++)
            {
                if (buffer[i] != find[matched])
                {
                    matched = 0;
                    continue;
                }

                matched++;
                if (matched != find.Length)
                {
                    continue;
                }

                var replIdx = 0;
                for (var j = i - find.Length + 1; j <= i; j++)
                {
                    buffer[j] = replace[replIdx++];
                }
                matched = 0;
            }
        }

        private static void StreamFileToStdIn(
            string fileName,
            IProcessIO io,
            Action onFail,
            Action<char[]> mutator
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
                var perc = (int) (fractionDone * 100);
                var runTime = (decimal) (DateTime.Now - started).TotalSeconds;
                var secondsLeft = fractionDone > 0
                    ? runTime / fractionDone
                    : -1;

                ReportProgress(perc, (int) secondsLeft);
                var remaining = fileStream.Length - totalRead;
                var toRead = remaining > chunk
                    ? chunk
                    : remaining;
                lastRead = reader.Read(buffer, 0, (int) toRead);
                totalRead += lastRead;
                mutator(buffer);
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

            io.Process.StandardInput.WriteLine("exit");
            io.Process.WaitForExit();
            if (io.Process.ExitCode != 0)
            {
                Fail();
            }

            Ok();
        }

        public static void StartStatus(string label)
        {
            _lastLabel = label;
            _started = DateTime.Now;
            Console.Out.Write($"[BUSY]     {label}");
        }

        public static void Ok()
        {
            var runTime = DateTime.Now - _started;
            Console.Out.Write($"\r[ OK ] {_lastLabel} ({runTime})\n");
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

            if (io.ExitCode == 0)
            {
                return;
            }

            onFail();
            Console.WriteLine("");
            foreach (var line in io.StandardError)
            {
                Console.WriteLine(line);
            }

            Console.WriteLine($"Command was: {io.Process.StartInfo.FileName} {io.Process.StartInfo.Arguments}");

            Environment.Exit(io.ExitCode);
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
                "--routines",
                "--hex-blob",
                opts.SourceDatabase
            );
            io.MaxBufferLines = 1024;
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

            var mysqldump = FindInDirs("mysqldump", pathParts, opts);
            var mysqlcli = FindInDirs("mysql", pathParts, opts);
            Console.WriteLine($"using mysqldump at {mysqldump}");
            Console.WriteLine($"using mysql at {mysqlcli}");
            return new Tools()
            {
                MySqlDump = mysqldump,
                MySqlCli = mysqlcli
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

    internal static class CharArrayExtensions
    {
        internal static char[] PadToLength(
            this char[] data,
            int requiredLength,
            char padWith
        )
        {
            if (data.Length > requiredLength)
            {
                throw new Exception($"Can't pad '{data}' to {requiredLength}: already exceeds this length");
            }

            if (data.Length == requiredLength)
            {
                return data;
            }
            var result = new char[requiredLength];
            data.CopyTo(result, 0);
            for (var i = data.Length; i < result.Length; i++)
            {
                result[i] = padWith;
            }
            return result;
        }
    }
}