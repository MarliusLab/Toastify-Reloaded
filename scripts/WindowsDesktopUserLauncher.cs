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
            string diagnostic;
            return TryStart(executable, minimized, out diagnostic);
        }

        public static bool TryStart(string executable, bool minimized, out string diagnostic)
        {
            diagnostic = "";
            string stage = "ValidateExecutable";
            IntPtr process = IntPtr.Zero, token = IntPtr.Zero, duplicate = IntPtr.Zero, environment = IntPtr.Zero;
            PROCESS_INFORMATION started = new PROCESS_INFORMATION();
            try
            {
                if (String.IsNullOrWhiteSpace(executable) || !Path.IsPathRooted(executable) || !File.Exists(executable) ||
                    !String.Equals(Path.GetFileName(executable), "ToastifyReloaded.exe", StringComparison.OrdinalIgnoreCase))
                    return Failure(out diagnostic, stage, 0);
                stage = "GetShellWindow";
                IntPtr shell = GetShellWindow();
                if (shell == IntPtr.Zero) return Failure(out diagnostic, stage, 0);
                uint shellPid;
                stage = "GetWindowThreadProcessId";
                GetWindowThreadProcessId(shell, out shellPid);
                if (shellPid == 0) return Failure(out diagnostic, stage, Marshal.GetLastWin32Error());
                uint shellSession, thisSession;
                stage = "ProcessIdToSessionId";
                if (!ProcessIdToSessionId(shellPid, out shellSession) ||
                    !ProcessIdToSessionId((uint)Process.GetCurrentProcess().Id, out thisSession))
                    return Failure(out diagnostic, stage, Marshal.GetLastWin32Error());
                if (shellSession != thisSession) return Failure(out diagnostic, "DesktopSessionMismatch", 0);
                stage = "OpenShellProcess";
                process = OpenProcess(0x1000, false, shellPid);
                if (process == IntPtr.Zero) return Failure(out diagnostic, stage, Marshal.GetLastWin32Error());
                StringBuilder path = new StringBuilder(32768);
                uint size = (uint)path.Capacity;
                stage = "QueryShellProcessImage";
                if (!QueryFullProcessImageName(process, 0, path, ref size))
                    return Failure(out diagnostic, stage, Marshal.GetLastWin32Error());
                if (!String.Equals(path.ToString(), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), StringComparison.OrdinalIgnoreCase))
                    return Failure(out diagnostic, "UnexpectedDesktopShell", 0);
                stage = "OpenShellProcessToken";
                if (!OpenProcessToken(process, 0x0002 | 0x0008, out token)) return Failure(out diagnostic, stage, Marshal.GetLastWin32Error());
                int elevated, returned;
                stage = "GetShellTokenElevation";
                if (!GetTokenInformation(token, 20, out elevated, sizeof(int), out returned))
                    return Failure(out diagnostic, stage, Marshal.GetLastWin32Error());
                if (elevated != 0) return Failure(out diagnostic, "ElevatedDesktopShell", 0);
                stage = "DuplicateShellToken";
                if (!DuplicateTokenEx(token, 0x02000000, IntPtr.Zero, 2, 1, out duplicate)) return Failure(out diagnostic, stage, Marshal.GetLastWin32Error());
                stage = "CreateDesktopUserEnvironment";
                if (!CreateEnvironmentBlock(out environment, duplicate, false)) return Failure(out diagnostic, stage, Marshal.GetLastWin32Error());
                STARTUPINFO info = new STARTUPINFO();
                info.cb = Marshal.SizeOf(typeof(STARTUPINFO));
                info.lpDesktop = "winsta0\\default";
                string command = "\"" + executable + "\"" + (minimized ? " --minimized" : "");
                stage = "CreateDesktopUserProcess";
                if (!CreateProcessWithTokenW(duplicate, 1, executable, new StringBuilder(command), 0x00000400,
                    environment, Path.GetDirectoryName(executable), ref info, out started))
                    return Failure(out diagnostic, stage, Marshal.GetLastWin32Error());
                diagnostic = "Desktop user process created (PID " + started.dwProcessId + ").";
                return true;
            }
            catch (Exception ex) { diagnostic = stage + ": " + ex.GetType().Name + ": " + ex.Message; return false; }
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
        static bool Failure(out string diagnostic, string stage, int error)
        {
            diagnostic = stage + (error == 0 ? " failed." : " failed (Win32 " + error + ": " + new System.ComponentModel.Win32Exception(error).Message + ").");
            return false;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct STARTUPINFO
        {
            public int cb; public string lpReserved; public string lpDesktop; public string lpTitle;
            public uint dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }
        [StructLayout(LayoutKind.Sequential)] struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public uint dwProcessId, dwThreadId; }
        [DllImport("user32.dll")] static extern IntPtr GetShellWindow();
        [DllImport("user32.dll", SetLastError = true)] static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref uint size);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool GetTokenInformation(IntPtr token, int kind, out int value, int length, out int returned);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool DuplicateTokenEx(IntPtr token, uint access, IntPtr attributes, int level, int type, out IntPtr duplicate);
        [DllImport("userenv.dll", SetLastError = true)] static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);
        [DllImport("userenv.dll")] static extern bool DestroyEnvironmentBlock(IntPtr environment);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool CreateProcessWithTokenW(IntPtr token, uint logonFlags, string application, StringBuilder command, uint flags, IntPtr environment, string directory, ref STARTUPINFO startup, out PROCESS_INFORMATION process);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    }
}
