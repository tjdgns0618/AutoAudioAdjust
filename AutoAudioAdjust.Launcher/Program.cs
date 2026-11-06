using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AutoAudioAdjust.Launcher;

internal static class Program
{
    private const uint MbOk = 0x00000000;
    private const uint MbIconError = 0x00000010;
    private const uint MbIconInformation = 0x00000040;
    private const int SwRestore = 9;

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Contains("--check", StringComparer.OrdinalIgnoreCase))
            {
                return FindApplication() is null ? 1 : 0;
            }

            var runningApp = Process.GetProcessesByName("AutoAudioAdjust.App").FirstOrDefault();
            if (runningApp is not null)
            {
                BringToFront(runningApp);
                MessageBoxW(IntPtr.Zero, "Auto Audio Adjust가 이미 실행 중입니다.", "Auto Audio Adjust", MbOk | MbIconInformation);
                runningApp.Dispose();
                return 0;
            }

            var appPath = FindApplication();
            if (appPath is null)
            {
                MessageBoxW(
                    IntPtr.Zero,
                    "실행 파일을 찾을 수 없습니다.\n\n실행기를 배포 폴더의 원래 위치에서 실행해 주세요.",
                    "Auto Audio Adjust · 실행 오류",
                    MbOk | MbIconError);
                return 1;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = appPath,
                WorkingDirectory = Path.GetDirectoryName(appPath)!,
                UseShellExecute = true
            });
            return 0;
        }
        catch (Exception exception)
        {
            MessageBoxW(
                IntPtr.Zero,
                $"앱을 시작하지 못했습니다.\n\n{exception.Message}",
                "Auto Audio Adjust · 실행 오류",
                MbOk | MbIconError);
            return 2;
        }
    }

    private static string? FindApplication()
    {
        var baseDirectory = AppContext.BaseDirectory;
        string[] candidates =
        [
            Path.Combine(baseDirectory, "App", "AutoAudioAdjust.App.exe"),
            Path.Combine(baseDirectory, "AutoAudioAdjust.App.exe"),
            Path.GetFullPath(Path.Combine(baseDirectory, "..", "..", "..", "..", "AutoAudioAdjust.App", "bin", "Release", "net9.0-windows", "win-x64", "publish", "AutoAudioAdjust.App.exe"))
        ];

        return candidates.FirstOrDefault(File.Exists);
    }

    private static void BringToFront(Process process)
    {
        if (process.MainWindowHandle == IntPtr.Zero)
        {
            return;
        }

        ShowWindow(process.MainWindowHandle, SwRestore);
        SetForegroundWindow(process.MainWindowHandle);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int MessageBoxW(IntPtr windowHandle, string text, string caption, uint type);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr windowHandle, int command);
}
