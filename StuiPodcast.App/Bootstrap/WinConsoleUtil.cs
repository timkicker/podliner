using System.Runtime.InteropServices;
using System.Text;

namespace StuiPodcast.App.Bootstrap;

static class WinConsoleUtil
{
    public static void Enable()
    {
        try
        {
            Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

            const int STD_OUTPUT_HANDLE = -11;
            const uint ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004;
            const uint DISABLE_NEWLINE_AUTO_RETURN = 0x0008;

            var handle = GetStdHandle(STD_OUTPUT_HANDLE);
            if (handle == nint.Zero || handle == new nint(-1)) return;

            if (GetConsoleMode(handle, out uint mode))
            {
                uint newMode = mode | ENABLE_VIRTUAL_TERMINAL_PROCESSING | DISABLE_NEWLINE_AUTO_RETURN;
                SetConsoleMode(handle, newMode);
            }
        }
        catch { }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern nint GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetConsoleMode(nint hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetConsoleMode(nint hConsoleHandle, uint dwMode);
}
