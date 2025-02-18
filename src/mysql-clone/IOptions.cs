using PeanutButter.EasyArgs.Attributes;

namespace mysql_clone;

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

    [Description("Do not run in the restore with charset utf8mb4 overridden to utf8")]
    public bool RetainOriginalEncodings { get; set; }

    [Description("Host to use for target AND source")]
    [Default("localhost")]
    public string Host { get; set; }

    [Description("User to use for target AND source")]
    [Default("root")]
    public string User { get; set; }

    [Description("Password to use for target AND host")]
    public string Password { get; set; }
}

public static class OptionsExtensions
{
    public static IOptions FixUpForSourceAndTargetOnSameMachine(
        this IOptions opts
    )
    {
        if (opts.SourceHost != opts.TargetHost ||
            opts.SourceUser != opts.TargetUser)

        {
            return opts;
        }

        var password = opts.Password ?? opts.SourcePassword ?? opts.TargetPassword;
        opts.Password = opts.SourcePassword = opts.TargetPassword = password;
        return opts;
    }
}