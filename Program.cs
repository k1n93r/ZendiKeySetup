using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

[assembly: AssemblyTitle("ZendiKey Setup")]
[assembly: AssemblyDescription("Configure Codex (GPT and Gemini keys) for zendigikey.shop")]
[assembly: AssemblyCompany("ZendiKey")]
[assembly: AssemblyProduct("ZendiKey Setup")]
[assembly: AssemblyCopyright("Copyright 2026")]
[assembly: AssemblyVersion("1.2.0.0")]
[assembly: AssemblyFileVersion("1.2.0.0")]

namespace ZendiKeySetup
{
    internal static class Program
    {
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [STAThread]
        private static int Main(string[] args)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;

            try
            {
                if (ShouldPrintCredential(args))
                {
                    return PrintCredential();
                }

                if (HasArgument(args, "--self-test"))
                {
                    return SelfTests.Run();
                }

                if (HasArgument(args, "--status"))
                {
                    return CommandLineStatus();
                }

                // Claude setup is paused: the CLI configures Codex only unless --claude-only is
                // passed explicitly (--codex-only is kept for older scripts).
                bool claudeOnly = HasArgument(args, "--claude-only");
                bool configureClaude = claudeOnly;
                bool configureCodex = !claudeOnly;

                if (HasArgument(args, "--install-codex-portable"))
                {
                    InstallResult portable = new InstallResult { Success = true };
                    bool ok = InstallManager.InstallCodexPortableAsync(Stdout.WriteLine, portable).GetAwaiter().GetResult();
                    return ok ? 0 : 1;
                }

                if (HasArgument(args, "--apply-existing"))
                {
                    return ApplyExisting(configureClaude, configureCodex).GetAwaiter().GetResult();
                }

                if (HasArgument(args, "--apply-stdin"))
                {
                    return ApplyFromStdin(configureClaude, configureCodex).GetAwaiter().GetResult();
                }

                if (HasArgument(args, "--install-missing"))
                {
                    return InstallMissing().GetAwaiter().GetResult();
                }

                if (HasArgument(args, "--restore-latest"))
                {
                    string restored = ConfigManager.RestoreLatestBackup();
                    Stdout.WriteLine("Restored: " + restored);
                    return 0;
                }

                try
                {
                    SetProcessDPIAware();
                }
                catch
                {
                }

                // Keep a stable copy in %LocalAppData% and point zendikey:// links at it so
                // Telegram/web "open in app" links keep working after the download is deleted.
                string initialInput = null;
                try
                {
                    string installedExe = InstalledApp.EnsureCurrentCopy();
                    ProtocolRegistrar.Register(installedExe);
                }
                catch
                {
                }
                int i;
                for (i = 0; i < args.Length; i++)
                {
                    string fromLink = KeyCodeClient.FromProtocolLink(args[i]);
                    if (!string.IsNullOrWhiteSpace(fromLink))
                    {
                        initialInput = fromLink;
                        break;
                    }
                    if (KeyCodeClient.IsZenCode(args[i]) || args[i].StartsWith("sk-", StringComparison.OrdinalIgnoreCase))
                    {
                        initialInput = args[i].Trim();
                        break;
                    }
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm(initialInput));
                return 0;
            }
            catch (Exception ex)
            {
                if (Environment.UserInteractive && !Console.IsOutputRedirected)
                {
                    MessageBox.Show(
                        ex.Message,
                        "ZendiKey Setup",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }
                else
                {
                    Stdout.WriteLine("ERROR: " + ex.Message);
                }

                return 1;
            }
        }

        private static bool ShouldPrintCredential(string[] args)
        {
            if (HasArgument(args, "--credential"))
            {
                return true;
            }

            return !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CLAUDE_HELPER_CONTEXT"));
        }

        private static int PrintCredential()
        {
            string key;
            if (!CredentialStore.TryLoad(out key) || string.IsNullOrWhiteSpace(key))
            {
                return 2;
            }

            Stdout.WriteLine(key.Trim());
            return 0;
        }

        private static int CommandLineStatus()
        {
            ClientStatus status = SystemDetector.Detect();
            Stdout.WriteLine("Claude Code: " + status.ClaudeCode.DisplayValue);
            Stdout.WriteLine("Codex CLI: " + status.CodexCli.DisplayValue);
            Stdout.WriteLine("Claude Desktop: " + status.ClaudeDesktop.DisplayValue);
            Stdout.WriteLine("ChatGPT Desktop: " + status.ChatGptDesktop.DisplayValue);
            Stdout.WriteLine("Codex portable: " + (File.Exists(InstallManager.CodexPortableExe) ? InstallManager.CodexPortableExe : "not used"));
            Stdout.WriteLine("Encrypted key: " + (CredentialStore.Exists ? "present" : "missing"));
            Stdout.WriteLine("Claude config: " + (ConfigManager.IsClaudeConfigured() ? "zendigikey" : "other"));
            Stdout.WriteLine("Codex config: " + (ConfigManager.IsCodexConfigured() ? "zendigikey" : "other"));
            Stdout.WriteLine("Claude Desktop config: " + (ConfigManager.IsClaudeDesktopConfigured() ? "zendigikey" : "other"));
            return 0;
        }

        private static async Task<int> ApplyExisting(bool configureClaude, bool configureCodex)
        {
            string key = ConfigManager.FindExistingKey();
            if (string.IsNullOrWhiteSpace(key))
            {
                Stdout.WriteLine("ERROR: No existing ZendiKey credential was found.");
                return 2;
            }

            return await ApplyKey(key, configureClaude, configureCodex);
        }

        private static async Task<int> ApplyFromStdin(bool configureClaude, bool configureCodex)
        {
            string key = Console.In.ReadLine();
            if (string.IsNullOrWhiteSpace(key))
            {
                Stdout.WriteLine("ERROR: No API key or ZEN code was provided on stdin.");
                return 2;
            }

            return await ApplyKey(key, configureClaude, configureCodex);
        }

        private static async Task<int> ApplyKey(string key, bool configureClaude, bool configureCodex)
        {
            if (KeyCodeClient.IsZenCode(key))
            {
                Stdout.WriteLine("Exchanging code " + KeyCodeClient.NormalizeCode(key) + " for a key ...");
                KeyCodeResult claimed = await KeyCodeClient.ClaimAsync(key);
                key = claimed.Key;
                Stdout.WriteLine("Package: " + claimed.Summary + (claimed.Recovered ? " (recovered)" : string.Empty));
            }

            Stdout.WriteLine("Checking https://zendigikey.shop/v1 ...");
            GatewayCheckResult check = await GatewayClient.CheckAsync(key, null, null, false);
            if (!check.ModelsOk)
            {
                Stdout.WriteLine("ERROR: " + check.ModelsMessage);
                return 3;
            }

            string claudeModel = configureClaude ? ModelSelector.PickClaude(check.Models) : null;
            string codexModel = configureCodex ? ModelSelector.PickCodex(check.Models) : null;

            check = await GatewayClient.FindCompatibleModelsAsync(
                key,
                check.Models,
                claudeModel,
                codexModel,
                configureClaude,
                configureCodex);
            if (!check.AllOk)
            {
                Stdout.WriteLine("ERROR: " + check.Summary);
                return 4;
            }

            claudeModel = check.ClaudeModel;
            codexModel = check.CodexModel;
            if (configureClaude)
            {
                Stdout.WriteLine("Claude model: " + claudeModel);
            }
            if (configureCodex)
            {
                Stdout.WriteLine("Codex model: " + codexModel);
            }

            ApplyResult result = ConfigManager.Apply(key, claudeModel, codexModel, check.Models, configureClaude, configureCodex);
            Stdout.WriteLine("Backup: " + result.BackupDirectory);
            if (configureClaude)
            {
                Stdout.WriteLine("Claude Code: configured");
                Stdout.WriteLine("Claude Desktop: " + result.ClaudeDesktopMessage);
            }
            if (configureCodex)
            {
                Stdout.WriteLine("Codex CLI / ChatGPT Desktop: configured");
            }
            Stdout.WriteLine("Credential: encrypted with Windows DPAPI");
            try
            {
                KeyStatus status = await KeyCodeClient.CheckAsync(key);
                Stdout.WriteLine("Key status: " + status.ShortSummary);
            }
            catch (Exception ex)
            {
                Stdout.WriteLine("Key status unavailable: " + ex.Message);
            }
            return 0;
        }

        private static async Task<int> InstallMissing()
        {
            InstallResult result = await InstallManager.InstallMissingAsync(Stdout.WriteLine, false, true);
            return result.Success ? 0 : 1;
        }

        internal static bool HasArgument(string[] args, string expected)
        {
            int i;
            for (i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], expected, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }

    internal static class Stdout
    {
        internal static void WriteLine(string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes((value ?? string.Empty) + Environment.NewLine);
            Stream output = Console.OpenStandardOutput();
            output.Write(bytes, 0, bytes.Length);
            output.Flush();
        }
    }
}
