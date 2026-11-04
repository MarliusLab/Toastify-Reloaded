using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace ToastifyPackaging
{
    // The installer is elevated. Launch through the actual desktop shell's
    // non-elevated token, never by inheriting the installer's administrator token.
    public static class DesktopUserLauncher
    {
        public static bool TryStart(string executable, bool minimized)
        {
            IntPtr process = IntPtr.Zero, token = IntPtr.Zero, duplicate = IntPtr.Zero, environment = IntPtr.Zero;
            PROCESS_INFORMATION started = new PROCESS_INFORMATION();
            try
            {
                IntPtr shell = GetShellWindow();
                if (shell == IntPtr.Zero) return false;
                uint shellPid;
                GetWindowThreadProcessId(shell, out shellPid);
                uint shellSession, thisSession;
                if (!ProcessIdToSessionId(shellPid, out shellSession) ||
                    !ProcessIdToSessionId((uint)Process.GetCurrentProcess().Id, out thisSession) || shellSession != thisSession) return false;
                process = OpenProcess(0x1000, false, shellPid);
                if (process == IntPtr.Zero) return false;
                StringBuilder path = new StringBuilder(32768);
                uint size = (uint)path.Capacity;
                if (!QueryFullProcessImageName(process, 0, path, ref size) ||
                    !String.Equals(path.ToString(), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), StringComparison.OrdinalIgnoreCase)) return false;
                if (!OpenProcessToken(process, 0x0002 | 0x0008, out token)) return false;
                int elevated, returned;
                if (!GetTokenInformation(token, 20, out elevated, sizeof(int), out returned) || elevated != 0) return false;
                if (!DuplicateTokenEx(token, 0x02000000, IntPtr.Zero, 2, 1, out duplicate)) return false;
                if (!CreateEnvironmentBlock(out environment, duplicate, false)) return false;
                STARTUPINFO info = new STARTUPINFO();
                info.cb = Marshal.SizeOf(typeof(STARTUPINFO));
                info.lpDesktop = "winsta0\\default";
                string command = "\"" + executable + "\"" + (minimized ? " --minimized" : "");
                return CreateProcessWithTokenW(duplicate, 1, executable, new StringBuilder(command), 0x00000400,
                    environment, Path.GetDirectoryName(executable), ref info, out started);
            }
            catch { return false; }
            finally
            {
                if (started.hThread != IntPtr.Zero) CloseHandle(started.hThread);
                if (started.hProcess != IntPtr.Zero) CloseHandle(started.hProcess);
                if (environment != IntPtr.Zero) DestroyEnvironmentBlock(environment);
                if (duplicate != IntPtr.Zero) CloseHandle(duplicate);
                if (token != IntPtr.Zero) CloseHandle(token);
                if (process != IntPtr.Zero) CloseHandle(process);
            }
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct STARTUPINFO
        {
            public int cb; public string lpReserved; public string lpDesktop; public string lpTitle;
            public uint dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }
        [StructLayout(LayoutKind.Sequential)] struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public uint dwProcessId, dwThreadId; }
        [DllImport("user32.dll")] static extern IntPtr GetShellWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("kernel32.dll")] static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);
        [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref uint size);
        [DllImport("advapi32.dll")] static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("advapi32.dll")] static extern bool GetTokenInformation(IntPtr token, int kind, out int value, int length, out int returned);
        [DllImport("advapi32.dll")] static extern bool DuplicateTokenEx(IntPtr token, uint access, IntPtr attributes, int level, int type, out IntPtr duplicate);
        [DllImport("userenv.dll")] static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);
        [DllImport("userenv.dll")] static extern bool DestroyEnvironmentBlock(IntPtr environment);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] static extern bool CreateProcessWithTokenW(IntPtr token, uint logonFlags, string application, StringBuilder command, uint flags, IntPtr environment, string directory, ref STARTUPINFO startup, out PROCESS_INFORMATION process);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    }
}
