using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using ScreenRecorder.Native;

namespace ScreenRecorder.Services;

/// <summary>Finds or unpacks the bundled ffmpeg.exe and runs it.</summary>
public static class Ffmpeg
{
    private static string? _path;
    private static readonly IntPtr Job = CreateKillOnCloseJob();

    public static string AppDataDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PortableScreenRecorder");

    public static string ExePath => _path ?? throw new InvalidOperationException("FFmpeg is not ready yet.");

    /// <summary>
    /// Uses ffmpeg.exe next to the app if present (handy for upgrades), otherwise unpacks the
    /// embedded copy once into %LOCALAPPDATA%\PortableScreenRecorder\ffmpeg\&lt;version&gt;.
    /// </summary>
    public static Task<string> EnsureAsync() => Task.Run(() =>
    {
        if (_path != null) return _path;

        string beside = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        if (File.Exists(beside)) return _path = beside;

        string version = typeof(Ffmpeg).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "1";
        version = new string(version.Select(c => char.IsLetterOrDigit(c) || c == '.' || c == '-' ? c : '_').ToArray());
        string root = Path.Combine(AppDataDir, "ffmpeg");
        string dir = Path.Combine(root, version);
        string exe = Path.Combine(dir, "ffmpeg.exe");
        string ok = exe + ".ok";
        if (File.Exists(exe) && File.Exists(ok)) return _path = exe;

        Directory.CreateDirectory(dir);
        var payload = Assembly.Load(new AssemblyName("FfmpegPayload"));
        using (var src = payload.GetManifestResourceStream("ffmpeg.exe")
            ?? throw new FileNotFoundException("This build does not contain ffmpeg.exe. Put ffmpeg.exe next to ScreenGank.exe."))
        {
            string tmp = exe + ".tmp";
            using (var dst = File.Create(tmp)) src.CopyTo(dst);
            File.Move(tmp, exe, true);
        }
        File.WriteAllText(ok, "ok");

        // Remove copies left behind by older versions of the app.
        foreach (var old in Directory.GetDirectories(root))
        {
            if (!string.Equals(old, dir, StringComparison.OrdinalIgnoreCase))
                try { Directory.Delete(old, true); } catch { }
        }
        return _path = exe;
    });

    public static Process Start(IEnumerable<string> args, string? workingDir = null)
    {
        var psi = new ProcessStartInfo(ExePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workingDir ?? AppDataDir,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        Directory.CreateDirectory(psi.WorkingDirectory);
        var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start ffmpeg.");
        if (Job != IntPtr.Zero)
            try { NativeMethods.AssignProcessToJobObject(Job, p.Handle); } catch { }
        return p;
    }

    /// <summary>
    /// Runs ffmpeg to completion. Expects "-progress pipe:1" in args when progress is wanted.
    /// Throws with FFmpeg's last error lines on failure.
    /// </summary>
    public static async Task RunAsync(IEnumerable<string> args, string? workingDir, double totalSeconds,
        IProgress<double>? progress, CancellationToken ct)
    {
        using var p = Start(args, workingDir);
        var errTail = new Queue<string>();
        var errTask = Task.Run(async () =>
        {
            string? line;
            while ((line = await p.StandardError.ReadLineAsync()) != null)
            {
                lock (errTail) { errTail.Enqueue(line); if (errTail.Count > 30) errTail.Dequeue(); }
            }
        });
        var outTask = Task.Run(async () =>
        {
            string? line;
            while ((line = await p.StandardOutput.ReadLineAsync()) != null)
            {
                if (progress != null && totalSeconds > 0 && line.StartsWith("out_time_us=", StringComparison.Ordinal)
                    && long.TryParse(line.AsSpan(12), NumberStyles.Integer, CultureInfo.InvariantCulture, out long us))
                {
                    progress.Report(Math.Clamp(us / 1e6 / totalSeconds, 0, 1));
                }
            }
        });

        using (ct.Register(() => { try { p.Kill(true); } catch { } }))
        {
            await p.WaitForExitAsync(CancellationToken.None);
        }
        await Task.WhenAll(errTask, outTask);
        ct.ThrowIfCancellationRequested();
        if (p.ExitCode != 0)
        {
            string tail; lock (errTail) tail = string.Join(Environment.NewLine, errTail);
            throw new FfmpegException($"FFmpeg failed (exit code {p.ExitCode}).", tail);
        }
        progress?.Report(1);
    }

    public static string Num(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);

    private static IntPtr CreateKillOnCloseJob()
    {
        try
        {
            IntPtr job = NativeMethods.CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero) return IntPtr.Zero;
            var info = new NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.BasicLimitInformation.LimitFlags = NativeMethods.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
            NativeMethods.SetInformationJobObject(job, NativeMethods.JobObjectExtendedLimitInformation, ref info,
                Marshal.SizeOf<NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>());
            return job;
        }
        catch { return IntPtr.Zero; }
    }
}

public sealed class FfmpegException : Exception
{
    public string Details { get; }
    public FfmpegException(string message, string details) : base(message) => Details = details;
    public override string ToString() => Message + Environment.NewLine + Details;
}
