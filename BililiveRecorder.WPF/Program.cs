using System;
using System.CommandLine;
using System.CommandLine.NamingConventionBinder;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using BililiveRecorder.Core.Api;
using BililiveRecorder.Flv.Pipeline;
using BililiveRecorder.ToolBox;
using Esprima;
using Jint.Runtime;
using Serilog;
using Serilog.Core;
using Serilog.Exceptions;
using Serilog.Formatting.Compact;
using Serilog.Formatting.Display;

#nullable enable
namespace BililiveRecorder.WPF
{
    internal static class Program
    {
        private const int CODE__WPF = 0x5F_57_50_46;

        internal static readonly LoggingLevelSwitch levelSwitchGlobal;
        internal static readonly LoggingLevelSwitch levelSwitchConsole;
        internal static readonly Logger logger;

#if DEBUG
        internal static readonly bool DebugMode = System.Diagnostics.Debugger.IsAttached;
#else
        internal static readonly bool DebugMode = false;
#endif

        static Program()
        {
            AttachConsole(-1);
            levelSwitchGlobal = new LoggingLevelSwitch(Serilog.Events.LogEventLevel.Debug);
            if (DebugMode)
                levelSwitchGlobal.MinimumLevel = Serilog.Events.LogEventLevel.Verbose;
            levelSwitchConsole = new LoggingLevelSwitch(Serilog.Events.LogEventLevel.Error);
            logger = BuildLogger();
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
            Log.Logger = logger;
            ServicePointManager.Expect100Continue = false;
        }

        [STAThread]
        public static int Main(string[] args)
        {
            try
            {
                logger.Debug("Starting, Version: {Version}, CurrentDirectory: {CurrentDirectory}, CommandLine: {CommandLine}",
                             GitVersionInformation.InformationalVersion,
                             Environment.CurrentDirectory,
                             Environment.CommandLine);
                var code = BuildCommand().Invoke(args);
                logger.Debug("Exit code: {ExitCode}, RunWpf: {RunWpf}", code, code == CODE__WPF);
                return code == CODE__WPF ? Commands.RunWpfReal() : code;
            }
            finally
            {
                logger.Dispose();
            }
        }

        private static RootCommand BuildCommand()
        {
            var run = new Command("run", "Run BililiveRecorder at path")
            {
                new Argument<string?>("path", () => null, "Work directory"),
                new Option<bool>("--ask-path", "Ask path in GUI even when \"don't ask again\" is selected before."),
                new Option<bool>("--hide", "Minimize to tray")
            };
            run.Handler = CommandHandler.Create((string? path, bool askPath, bool hide) => Commands.RunWpfHandler(path: path, askPath: askPath, hide: hide));
            var root = new RootCommand("")
            {
                run,
                new ToolCommand(),
            };
            root.Handler = CommandHandler.Create(Commands.RunRootCommandHandler);
            return root;
        }

        private static class Commands
        {
            internal static int RunRootCommandHandler() {
                return RunWpfHandler(path: null, askPath: false, hide: false);
            }
            internal static int RunWpfHandler(string? path, bool askPath, bool hide)
            {
                Pages.RootPage.CommandArgumentRecorderPath = path;
                Pages.RootPage.CommandArgumentAskPath = askPath;
                Pages.RootPage.CommandArgumentHide = hide;
                return CODE__WPF;
            }

            internal static int RunWpfReal()
            {
                var cancel = new CancellationTokenSource();
                var token = cancel.Token;
                try
                {
                    SleepBlocker.Start();

                    var app = new App();
                    app.InitializeComponent();
                    app.DispatcherUnhandledException += App_DispatcherUnhandledException;

                    return app.Run();
                }
                finally
                {
                    cancel.Cancel();
                    StreamStartedNotification.Cleanup();
                }
            }
        }

        private static class SleepBlocker
        {
            internal static void Start()
            {
                var t = new Thread(EntryPoint)
                {
                    Name = "SystemSleepBlocker",
                    IsBackground = true,
                    Priority = ThreadPriority.BelowNormal
                };
                t.Start();
            }

            [DllImport("kernel32", CharSet = CharSet.Auto, SetLastError = true)]
            private static extern EXECUTION_STATE SetThreadExecutionState(EXECUTION_STATE esFlags);

            [Flags]
            private enum EXECUTION_STATE : uint
            {
                ES_AWAYMODE_REQUIRED = 0x00000040,
                ES_CONTINUOUS = 0x80000000,
                ES_DISPLAY_REQUIRED = 0x00000002,
                ES_SYSTEM_REQUIRED = 0x00000001
            }

            private static void EntryPoint()
            {
                try
                {
                    while (true)
                    {
                        try
                        {
                            _ = SetThreadExecutionState(EXECUTION_STATE.ES_SYSTEM_REQUIRED | EXECUTION_STATE.ES_CONTINUOUS);
                        }
                        catch (Exception) { }
                        Thread.Sleep(millisecondsTimeout: 30 * 1000);
                    }
                }
                catch (Exception) { }
            }
        }

        private static Logger BuildLogger()
        {
            var logFilePath = Environment.GetEnvironmentVariable("BILILIVERECORDER_LOG_FILE_PATH");
            if (string.IsNullOrWhiteSpace(logFilePath))
                logFilePath = Path.Combine(AppContext.BaseDirectory, "logs", "bilirec.txt");

            return new LoggerConfiguration()
                .MinimumLevel.ControlledBy(levelSwitchGlobal)
                .Enrich.WithProcessId()
                .Enrich.WithThreadId()
                .Enrich.WithThreadName()
                .Enrich.WithExceptionDetails()
                .Destructure.AsScalar<IPAddress>()
                .Destructure.AsScalar<ProcessingComment>()
                .Destructure.AsScalar<StreamCodecQn>()
                .Destructure.ByTransforming<Flv.Xml.XmlFlvFile.XmlFlvFileMeta>(x => new
                {
                    x.Version,
                    x.ExportTime,
                    x.FileSize,
                    x.FileCreationTime,
                    x.FileModificationTime,
                })
                .WriteTo.Console(levelSwitch: levelSwitchConsole)
#if DEBUG
                .WriteTo.Debug()
                .WriteTo.Async(l => l.Sink<WpfLogEventSink>(Serilog.Events.LogEventLevel.Debug))
#else
                .WriteTo.Async(l => l.Sink<WpfLogEventSink>(Serilog.Events.LogEventLevel.Information))
#endif
                .WriteTo.Async(l => l.File(new CompactJsonFormatter(), logFilePath, shared: true, rollingInterval: RollingInterval.Day, rollOnFileSizeLimit: true))
                .CreateLogger();

        }

        [DllImport("kernel32")]
        private static extern bool AttachConsole(int pid);

        [HandleProcessCorruptedStateExceptions, SecurityCritical]
        private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is Exception ex)
                logger.Fatal(ex, "Unhandled exception from AppDomain.UnhandledException");
        }

        [HandleProcessCorruptedStateExceptions, SecurityCritical]
        private static void TaskScheduler_UnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs e) =>
            logger.Error(e.Exception, "Unobserved exception from TaskScheduler.UnobservedTaskException");

        [HandleProcessCorruptedStateExceptions, SecurityCritical]
        private static void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e) =>
            logger.Fatal(e.Exception, "Unhandled exception from Application.DispatcherUnhandledException");

    }
}
