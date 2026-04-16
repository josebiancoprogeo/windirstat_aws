using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace windirstat_s3;

public partial class App : System.Windows.Application
{
    private static readonly object LogLock = new();
    private static string LogFilePath => Path.Combine(AppContext.BaseDirectory, "erro_log.txt");

    protected override void OnStartup(StartupEventArgs e)
    {
        RegisterGlobalExceptionHandlers();
        base.OnStartup(e);
    }

    private void RegisterGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteErrorLog("DispatcherUnhandledException", e.Exception);
    }

    private void OnUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        WriteErrorLog("AppDomain.UnhandledException", e.ExceptionObject as Exception, e.ExceptionObject);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        WriteErrorLog("TaskScheduler.UnobservedTaskException", e.Exception);
    }

    private static void WriteErrorLog(string source, Exception? exception, object? rawException = null)
    {
        try
        {
            var builder = new StringBuilder();
            builder.AppendLine(new string('=', 80));
            builder.AppendLine($"Timestamp: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            builder.AppendLine($"Source: {source}");
            builder.AppendLine($"BaseDirectory: {AppContext.BaseDirectory}");

            if (exception != null)
            {
                builder.AppendLine($"Type: {exception.GetType().FullName}");
                builder.AppendLine($"Message: {exception.Message}");
                builder.AppendLine("StackTrace:");
                builder.AppendLine(exception.ToString());
            }
            else if (rawException != null)
            {
                builder.AppendLine($"RawException: {rawException}");
            }
            else
            {
                builder.AppendLine("No exception details available.");
            }

            lock (LogLock)
            {
                File.AppendAllText(LogFilePath, builder.ToString(), Encoding.UTF8);
            }
        }
        catch
        {
        }
    }
}
