using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace BluetoothSpeaker
{
    public static class AutostartManager
    {
        // Returns true if a service was installed and started and the current process should exit
        public static async Task<bool> EnsureAutostartAsync(string[] originalArgs)
        {
            try
            {
                if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return false;

                // Quick systemd presence check
                if (!Directory.Exists("/run/systemd/system")) return false;

                // If already running under systemd or service exists, skip
                var serviceName = "meanspeaker"; // unified service name
                var exists = await RunSilentWithSuccess("systemctl", $"cat {serviceName}");
                if (exists) return false;

                // Determine an executable path to run under systemd
                var execPath = Environment.ProcessPath ?? string.Empty;
                var isManagedHost = execPath.EndsWith("/dotnet", StringComparison.Ordinal) || execPath.EndsWith("dotnet", StringComparison.Ordinal);
                var isDll = execPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);

                if (string.IsNullOrWhiteSpace(execPath) || isManagedHost || isDll)
                {
                    // Try to publish a self-contained build and use that
                    var published = await TryPublishSelfContainedAsync();
                    if (!string.IsNullOrEmpty(published))
                    {
                        execPath = published;
                    }
                    else
                    {
                        // As a fallback, use the current working directory binary if available
                        var fallback = Path.Combine(AppContext.BaseDirectory, "BluetoothSpeaker");
                        if (File.Exists(fallback)) execPath = fallback;
                    }
                }

                if (string.IsNullOrWhiteSpace(execPath) || !File.Exists(execPath))
                {
                    return false; // Can't determine an executable to register
                }

                // Build arguments string
                var argsStr = BuildArgs(originalArgs);
                var workDir = Path.GetDirectoryName(execPath) ?? "/";

                // Attempt system-level service first (requires sudo)
                var unit = BuildSystemUnit(execPath, workDir, argsStr);
                var wrote = await WriteWithSudo("/etc/systemd/system/meanspeaker.service", unit);
                if (wrote)
                {
                    await RunSilent("systemctl", "daemon-reload");
                    await RunSilent("systemctl", "enable meanspeaker");
                    await RunSilent("systemctl", "restart meanspeaker");
                    Console.WriteLine("✅ Installed system service 'meanspeaker' and started it.");
                    return true; // let systemd-owned instance take over
                }

                // Fallback: user-level service (auto-starts on login; linger may be needed for boot)
                var userDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), ".config/systemd/user");
                Directory.CreateDirectory(userDir);
                var userUnitPath = Path.Combine(userDir, "meanspeaker.service");
                var userUnit = BuildUserUnit(execPath, workDir, argsStr);
                File.WriteAllText(userUnitPath, userUnit);

                await RunSilent("systemctl", "--user daemon-reload");
                await RunSilent("systemctl", "--user enable --now meanspeaker");

                // Try to enable linger so it starts without an active login session
                var user = Environment.UserName;
                await RunSilent("loginctl", $"enable-linger {user}");

                Console.WriteLine("✅ Installed user service 'meanspeaker' and started it.");
                Console.WriteLine("ℹ️ Service will start at boot if user lingering is enabled.");
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static async Task<string> TryPublishSelfContainedAsync()
        {
            try
            {
                // Detect RID from architecture
                var rid = RuntimeInformation.ProcessArchitecture switch
                {
                    Architecture.Arm64 => "linux-arm64",
                    Architecture.Arm => "linux-arm",
                    _ => string.Empty
                };
                if (string.IsNullOrEmpty(rid)) return string.Empty;

                // Locate csproj by walking up from current directory
                var csproj = FindCsprojUpwards("BluetoothSpeaker.csproj");
                if (string.IsNullOrEmpty(csproj)) return string.Empty;

                // Publish to /opt/meanspeaker when possible; else to ~/.local/share/meanspeaker
                var targetDir = "/opt/meanspeaker";
                var canUseOpt = await RunSilentWithSuccess("sudo", "test -d /opt || mkdir -p /opt");
                if (!canUseOpt) targetDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "meanspeaker");
                Directory.CreateDirectory(targetDir);

                var publishArgs = $"publish \"{csproj}\" -c Release -r {rid} --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:InvariantGlobalization=true -o \"{targetDir}\"";
                var ok = await RunSilentWithSuccess("dotnet", publishArgs);
                if (!ok) return string.Empty;

                var exePath = Path.Combine(targetDir, "BluetoothSpeaker");
                if (File.Exists(exePath))
                {
                    // Try make executable
                    await RunSilent("chmod", $"755 \"{exePath}\"");
                    return exePath;
                }
                return string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string? FindCsprojUpwards(string name)
        {
            try
            {
                var dir = Directory.GetCurrentDirectory();
                for (int i = 0; i < 5; i++)
                {
                    var candidate = Path.Combine(dir, name);
                    if (File.Exists(candidate)) return candidate;
                    dir = Directory.GetParent(dir)?.FullName ?? dir;
                }
            }
            catch { }
            return null;
        }

        private static string BuildArgs(string[] args)
        {
            if (args == null || args.Length == 0) return string.Empty;
            var sb = new StringBuilder();
            foreach (var a in args)
            {
                if (string.IsNullOrEmpty(a)) continue;
                if (a.Contains(' ') || a.Contains('"')) sb.Append('"').Append(a.Replace("\"", "\\\"")).Append('"');
                else sb.Append(a);
                sb.Append(' ');
            }
            return sb.ToString().TrimEnd();
        }

        private static string BuildSystemUnit(string execPath, string workDir, string args)
        {
            var cmd = string.IsNullOrEmpty(args) ? execPath : $"{execPath} {args}";
            return $@"[Unit]
Description=MeanSpeaker - Snarky Bluetooth Speaker
After=bluetooth.service bluealsa.service network.target
Wants=bluetooth.service bluealsa.service

[Service]
Type=simple
WorkingDirectory={workDir}
ExecStart={cmd}
Restart=always
RestartSec=5
Environment=DOTNET_EnableWriteXorExecute=0
Environment=DOTNET_CLI_TELEMETRY_OPTOUT=1

[Install]
WantedBy=multi-user.target
";
        }

        private static string BuildUserUnit(string execPath, string workDir, string args)
        {
            var cmd = string.IsNullOrEmpty(args) ? execPath : $"{execPath} {args}";
            return $@"[Unit]
Description=MeanSpeaker (User) - Snarky Bluetooth Speaker

[Service]
Type=simple
WorkingDirectory={workDir}
ExecStart={cmd}
Restart=always
RestartSec=5

[Install]
WantedBy=default.target
";
        }

        private static async Task<bool> WriteWithSudo(string path, string content)
        {
            try
            {
                var tmp = Path.GetTempFileName();
                await File.WriteAllTextAsync(tmp, content);
                var ok = await RunSilentWithSuccess("sudo", $"cp \"{tmp}\" \"{path}\" && sudo chmod 644 \"{path}\" ");
                try { File.Delete(tmp); } catch { }
                return ok;
            }
            catch
            {
                return false;
            }
        }

        private static async Task RunSilent(string file, string args)
        {
            using var p = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = file,
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            try { p.Start(); } catch { return; }
            await p.WaitForExitAsync();
        }

        private static async Task<bool> RunSilentWithSuccess(string file, string args)
        {
            using var p = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = file,
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            try { p.Start(); } catch { return false; }
            await p.WaitForExitAsync();
            return p.ExitCode == 0;
        }
    }
}
