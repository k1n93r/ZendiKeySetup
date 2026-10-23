using Microsoft.Win32;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace ZendiKeySetup
{
    internal static class AppPaths
    {
        internal const string BaseUrl = "https://api.zendigikey.shop/v1";
        internal const string ClaudeCodeBaseUrl = "https://api.zendigikey.shop";
        internal const string ProviderId = "zendigikey";

        internal static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        internal static readonly string DataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ZendiKey Setup");
        internal static readonly string InstalledExe = Path.Combine(DataDirectory, "ZendiKeySetup.exe");
        internal static readonly string CredentialFile = Path.Combine(DataDirectory, "credential.dat");
        internal static readonly string BackupDirectory = Path.Combine(DataDirectory, "backups");
        internal static readonly string ClaudeSettings = Path.Combine(Home, ".claude", "settings.json");
        internal static readonly string CodexConfig = Path.Combine(Home, ".codex", "config.toml");
        internal static readonly string CodexModelCatalog = Path.Combine(Home, ".codex", "zendigikey-model-catalog.json");
        internal static readonly string ClaudeDesktopConfigDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Claude-3p",
            "configLibrary");
        internal static readonly string ClaudeDesktopMeta = Path.Combine(ClaudeDesktopConfigDirectory, "_meta.json");
        internal static readonly string ClaudeDesktopLocalConfig = Path.Combine(
            ClaudeDesktopConfigDirectory,
            "d72336c1-5448-4ed0-a2fa-67349f74d11b.json");
    }

    internal static class InstalledApp
    {
        internal static string EnsureCurrentCopy()
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);

            string source = Process.GetCurrentProcess().MainModule.FileName;
            string target = AppPaths.InstalledExe;
            if (string.Equals(
                Path.GetFullPath(source),
                Path.GetFullPath(target),
                StringComparison.OrdinalIgnoreCase))
            {
                return target;
            }

            // Same build already installed (for example the GUI copy is open while the CLI runs):
            // nothing to copy.
            if (File.Exists(target) && SameContent(source, target))
            {
                return target;
            }

            string pending = target + ".new";
            File.Copy(source, pending, true);
            try
            {
                File.Copy(pending, target, true);
            }
            catch (IOException)
            {
                // The installed copy is running (older build). Keep using the current executable
                // for this session; the next launch from the download will replace it.
                return source;
            }
            finally
            {
                TryDelete(pending);
            }

            return target;
        }

        private static bool SameContent(string left, string right)
        {
            try
            {
                FileInfo a = new FileInfo(left);
                FileInfo b = new FileInfo(right);
                if (a.Length != b.Length)
                {
                    return false;
                }
                using (SHA256 sha = SHA256.Create())
                using (FileStream fa = File.OpenRead(left))
                using (FileStream fb = File.OpenRead(right))
                {
                    byte[] ha = sha.ComputeHash(fa);
                    byte[] hb = sha.ComputeHash(fb);
                    return ha.SequenceEqual(hb);
                }
            }
            catch
            {
                return false;
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }
    }

    internal static class CredentialStore
    {
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes(
            "ZendiKey Setup credential storage v1");

        internal static bool Exists
        {
            get { return File.Exists(AppPaths.CredentialFile); }
        }

        internal static bool IsReadable
        {
            get
            {
                string key;
                bool readable = TryLoad(out key);
                key = null;
                return readable;
            }
        }

        internal static void Save(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new ArgumentException("API key is required.");
            }

            Directory.CreateDirectory(AppPaths.DataDirectory);
            byte[] plain = Encoding.UTF8.GetBytes(key.Trim());
            try
            {
                AtomicFile.WriteBytes(AppPaths.CredentialFile, ProtectBytes(plain));
            }
            finally
            {
                Array.Clear(plain, 0, plain.Length);
            }
        }

        internal static bool TryLoad(out string key)
        {
            key = null;
            try
            {
                if (!File.Exists(AppPaths.CredentialFile))
                {
                    return false;
                }

                byte[] protectedBytes = File.ReadAllBytes(AppPaths.CredentialFile);
                byte[] plain = UnprotectBytes(protectedBytes);
                try
                {
                    key = Encoding.UTF8.GetString(plain);
                    return !string.IsNullOrWhiteSpace(key);
                }
                finally
                {
                    Array.Clear(plain, 0, plain.Length);
                }
            }
            catch
            {
                key = null;
                return false;
            }
        }

        internal static byte[] ProtectBytes(byte[] value)
        {
            return ProtectedData.Protect(value, Entropy, DataProtectionScope.CurrentUser);
        }

        internal static byte[] UnprotectBytes(byte[] value)
        {
            return ProtectedData.Unprotect(value, Entropy, DataProtectionScope.CurrentUser);
        }
    }

    internal static class AtomicFile
    {
        internal static void WriteText(string path, string text)
        {
            WriteBytes(path, new UTF8Encoding(false).GetBytes(text));
        }

        internal static void WriteBytes(string path, byte[] bytes)
        {
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllBytes(temporary, bytes);
            try
            {
                File.Copy(temporary, path, true);
            }
            finally
            {
                try
                {
                    File.Delete(temporary);
                }
                catch
                {
                }
            }
        }
    }

    internal sealed class GatewayCheckResult
    {
        internal GatewayCheckResult()
        {
            Models = new List<string>();
        }

        internal List<string> Models { get; set; }
        internal bool ModelsOk { get; set; }
        internal bool ClaudeOk { get; set; }
        internal bool CodexOk { get; set; }
        internal string ModelsMessage { get; set; }
        internal string ClaudeMessage { get; set; }
        internal string CodexMessage { get; set; }
        internal string ClaudeModel { get; set; }
        internal string CodexModel { get; set; }

        internal bool AllOk
        {
            get { return ModelsOk && ClaudeOk && CodexOk; }
        }

        internal string Summary
        {
            get
            {
                List<string> parts = new List<string>();
                if (!ModelsOk)
                {
                    parts.Add(ModelsMessage);
                }
                if (!ClaudeOk && !string.IsNullOrEmpty(ClaudeMessage))
                {
                    parts.Add("Claude: " + ClaudeMessage);
                }
                if (!CodexOk && !string.IsNullOrEmpty(CodexMessage))
                {
                    parts.Add("Codex: " + CodexMessage);
                }
                return string.Join(" | ", parts.ToArray());
            }
        }
    }

    internal static class GatewayClient
    {
        internal static async Task<GatewayCheckResult> FindCompatibleModelsAsync(
            string key,
            IEnumerable<string> models,
            string preferredClaude,
            string preferredCodex)
        {
            return await FindCompatibleModelsAsync(
                key,
                models,
                preferredClaude,
                preferredCodex,
                true,
                true).ConfigureAwait(false);
        }

        internal static async Task<GatewayCheckResult> FindCompatibleModelsAsync(
            string key,
            IEnumerable<string> models,
            string preferredClaude,
            string preferredCodex,
            bool checkClaude,
            bool checkCodex)
        {
            GatewayCheckResult result = new GatewayCheckResult();
            result.Models = models == null ? new List<string>() : models.ToList();
            result.ModelsOk = result.Models.Count > 0;
            result.ModelsMessage = result.ModelsOk
                ? result.Models.Count.ToString(CultureInfo.InvariantCulture) + " models available"
                : "The gateway returned no models.";
            if (!result.ModelsOk)
            {
                return result;
            }

            using (HttpClient claudeClient = CreateClient(key))
            using (HttpClient codexClient = CreateClient(key))
            {
                Task<ModelProbe> claudeTask = checkClaude
                    ? ProbeClaudeAsync(
                        claudeClient,
                        ModelSelector.ClaudeCandidates(result.Models, preferredClaude))
                    : Task.FromResult(new ModelProbe(true, null, "Not selected"));
                Task<ModelProbe> codexTask = checkCodex
                    ? ProbeCodexAsync(
                        codexClient,
                        ModelSelector.CodexCandidates(result.Models, preferredCodex))
                    : Task.FromResult(new ModelProbe(true, null, "Not selected"));
                await Task.WhenAll(claudeTask, codexTask).ConfigureAwait(false);

                ModelProbe claude = claudeTask.Result;
                ModelProbe codex = codexTask.Result;
                result.ClaudeOk = claude.Ok;
                result.ClaudeModel = claude.Model;
                result.ClaudeMessage = claude.Message;
                result.CodexOk = codex.Ok;
                result.CodexModel = codex.Model;
                result.CodexMessage = codex.Message;
                return result;
            }
        }

        internal static async Task<GatewayCheckResult> CheckAsync(
            string key,
            string claudeModel,
            string codexModel,
            bool runProtocolChecks)
        {
            GatewayCheckResult result = new GatewayCheckResult();
            if (string.IsNullOrWhiteSpace(key))
            {
                result.ModelsMessage = "API key is empty.";
                return result;
            }

            using (HttpClient client = CreateClient(key))
            {
                try
                {
                    HttpResponseMessage response = await client.GetAsync(AppPaths.BaseUrl + "/models").ConfigureAwait(false);
                    string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        result.ModelsMessage = HttpFailure(response.StatusCode, body);
                        return result;
                    }

                    result.Models = ParseModels(body);
                    if (result.Models.Count == 0)
                    {
                        result.ModelsMessage = "The gateway returned no models.";
                        return result;
                    }

                    result.ModelsOk = true;
                    result.ModelsMessage = result.Models.Count.ToString(CultureInfo.InvariantCulture) + " models available";
                }
                catch (Exception ex)
                {
                    result.ModelsMessage = CleanError(ex.Message);
                    return result;
                }

                if (!runProtocolChecks)
                {
                    result.ClaudeOk = true;
                    result.CodexOk = true;
                    return result;
                }

                if (string.IsNullOrWhiteSpace(claudeModel))
                {
                    claudeModel = ModelSelector.PickClaude(result.Models);
                }
                if (string.IsNullOrWhiteSpace(codexModel))
                {
                    codexModel = ModelSelector.PickCodex(result.Models);
                }

                Task<ProtocolCheck> claudeTask = CheckClaudeAsync(client, claudeModel);
                Task<ProtocolCheck> codexTask = CheckCodexAsync(client, codexModel);
                await Task.WhenAll(claudeTask, codexTask).ConfigureAwait(false);

                ProtocolCheck claude = claudeTask.Result;
                ProtocolCheck codex = codexTask.Result;
                result.ClaudeOk = claude.Ok;
                result.ClaudeModel = claude.Ok ? claudeModel : null;
                result.ClaudeMessage = claude.Message;
                result.CodexOk = codex.Ok;
                result.CodexModel = codex.Ok ? codexModel : null;
                result.CodexMessage = codex.Message;
                return result;
            }
        }

        private static async Task<ModelProbe> ProbeClaudeAsync(HttpClient client, IEnumerable<string> candidates)
        {
            string last = "No Claude-compatible model was found.";
            foreach (string model in candidates)
            {
                ProtocolCheck check = await CheckClaudeAsync(client, model).ConfigureAwait(false);
                if (check.Ok)
                {
                    return new ModelProbe(true, model, check.Message);
                }
                last = model + ": " + check.Message;
            }
            return new ModelProbe(false, null, last);
        }

        private static async Task<ModelProbe> ProbeCodexAsync(HttpClient client, IEnumerable<string> candidates)
        {
            string last = "No Responses-compatible model was found.";
            foreach (string model in candidates)
            {
                ProtocolCheck check = await CheckCodexAsync(client, model).ConfigureAwait(false);
                if (check.Ok)
                {
                    return new ModelProbe(true, model, check.Message);
                }
                last = model + ": " + check.Message;
            }
            return new ModelProbe(false, null, last);
        }

        private static HttpClient CreateClient(string key)
        {
            HttpClient client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(45);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key.Trim());
            client.DefaultRequestHeaders.Add("User-Agent", "ZendiKeySetup/1.2");
            return client;
        }

        private static async Task<ProtocolCheck> CheckClaudeAsync(HttpClient client, string model)
        {
            if (string.IsNullOrWhiteSpace(model))
            {
                return new ProtocolCheck(false, "No Claude-compatible model was found.");
            }

            Dictionary<string, object> payload = new Dictionary<string, object>();
            payload["model"] = model;
            payload["max_tokens"] = 12;
            payload["messages"] = new object[]
            {
                new Dictionary<string, object>
                {
                    { "role", "user" },
                    { "content", "Reply exactly: OK" }
                }
            };

            HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, AppPaths.BaseUrl + "/messages");
            request.Headers.Add("x-api-key", client.DefaultRequestHeaders.Authorization.Parameter);
            request.Headers.Add("anthropic-version", "2023-06-01");
            request.Content = new StringContent(Json.Serialize(payload), Encoding.UTF8, "application/json");

            try
            {
                using (HttpResponseMessage response = await client.SendAsync(request).ConfigureAwait(false))
                {
                    string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    return response.IsSuccessStatusCode
                        ? new ProtocolCheck(true, model + " responded")
                        : new ProtocolCheck(false, HttpFailure(response.StatusCode, body));
                }
            }
            catch (Exception ex)
            {
                return new ProtocolCheck(false, CleanError(ex.Message));
            }
            finally
            {
                request.Dispose();
            }
        }

        private static async Task<ProtocolCheck> CheckCodexAsync(HttpClient client, string model)
        {
            if (string.IsNullOrWhiteSpace(model))
            {
                return new ProtocolCheck(false, "No Responses-compatible model was found.");
            }

            Dictionary<string, object> payload = new Dictionary<string, object>();
            payload["model"] = model;
            payload["input"] = "Reply exactly: OK";
            payload["max_output_tokens"] = 12;
            payload["store"] = false;

            HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, AppPaths.BaseUrl + "/responses");
            request.Content = new StringContent(Json.Serialize(payload), Encoding.UTF8, "application/json");

            try
            {
                using (HttpResponseMessage response = await client.SendAsync(request).ConfigureAwait(false))
                {
                    string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    return response.IsSuccessStatusCode
                        ? new ProtocolCheck(true, model + " responded")
                        : new ProtocolCheck(false, HttpFailure(response.StatusCode, body));
                }
            }
            catch (Exception ex)
            {
                return new ProtocolCheck(false, CleanError(ex.Message));
            }
            finally
            {
                request.Dispose();
            }
        }

        private static List<string> ParseModels(string json)
        {
            Dictionary<string, object> root = Json.DeserializeDictionary(json);
            List<string> models = new List<string>();
            object data;
            if (!root.TryGetValue("data", out data))
            {
                return models;
            }

            IEnumerable values = data as IEnumerable;
            if (values == null)
            {
                return models;
            }

            foreach (object value in values)
            {
                Dictionary<string, object> item = Json.AsDictionary(value);
                object id;
                if (item != null && item.TryGetValue("id", out id) && id != null)
                {
                    string model = Convert.ToString(id, CultureInfo.InvariantCulture);
                    if (!string.IsNullOrWhiteSpace(model) && !models.Contains(model, StringComparer.OrdinalIgnoreCase))
                    {
                        models.Add(model);
                    }
                }
            }

            models.Sort(StringComparer.OrdinalIgnoreCase);
            return models;
        }

        private static string HttpFailure(HttpStatusCode statusCode, string body)
        {
            string message = null;
            try
            {
                Dictionary<string, object> root = Json.DeserializeDictionary(body);
                object error;
                if (root.TryGetValue("error", out error))
                {
                    Dictionary<string, object> detail = Json.AsDictionary(error);
                    object value;
                    if (detail != null && detail.TryGetValue("message", out value))
                    {
                        message = Convert.ToString(value, CultureInfo.InvariantCulture);
                    }
                    else
                    {
                        message = Convert.ToString(error, CultureInfo.InvariantCulture);
                    }
                }
                else
                {
                    object value;
                    if (root.TryGetValue("message", out value))
                    {
                        message = Convert.ToString(value, CultureInfo.InvariantCulture);
                    }
                }
            }
            catch
            {
            }

            if (string.IsNullOrWhiteSpace(message))
            {
                message = "HTTP " + ((int)statusCode).ToString(CultureInfo.InvariantCulture);
            }
            return CleanError(message);
        }

        private static string CleanError(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "Unknown gateway error.";
            }

            value = Regex.Replace(value, @"(?i)(bearer\s+|sk-[a-z0-9_-]*)([a-z0-9_-]{8,})", "$1[redacted]");
            value = value.Replace("\r", " ").Replace("\n", " ").Trim();
            return value.Length > 300 ? value.Substring(0, 300) + "..." : value;
        }

        private sealed class ProtocolCheck
        {
            internal ProtocolCheck(bool ok, string message)
            {
                Ok = ok;
                Message = message;
            }

            internal bool Ok { get; private set; }
            internal string Message { get; private set; }
        }

        private sealed class ModelProbe
        {
            internal ModelProbe(bool ok, string model, string message)
            {
                Ok = ok;
                Model = model;
                Message = message;
            }

            internal bool Ok { get; private set; }
            internal string Model { get; private set; }
            internal string Message { get; private set; }
        }
    }

    internal static class ModelSelector
    {
        private static readonly string[] ClaudePreferences =
        {
            "claude-opus-5.5",
            "claude-sonnet-5.5",
            "claude-opus-5",
            "claude-sonnet-5",
            "claude-fable-5",
            "claude-opus-4.8",
            "claude-opus-4.7",
            "claude-opus-4.6",
            "claude-sonnet-4.6",
            "claude-haiku-4.5",
            "claude-sonnet-4.5",
            "claude-sonnet-4-5",
            "claude-opus-4.1",
            "claude-opus-4-1"
        };

        private static readonly string[] CodexPreferences =
        {
            "gpt-6.1-sol",
            "gpt-6-sol",
            "gpt-5.6-sol",
            "gpt-6-luna",
            "gpt-5.6-luna",
            "gpt-6-astra",
            "gpt-5.6-terra",
            "gpt-5.5",
            "gpt-5.4",
            "gpt-5.4-mini",
            // Gemini (Antigravity) keys: Codex talks to them through the same Responses endpoint.
            "gemini-4-argon",
            "gemini-3.8-flash",
            "gemini-3-flash",
            "gemini-2.5-flash",
            // Claude (Kiro channel) in the gateway's price order: Opus 5.5 > Sonnet 5.5 > Opus 5 > Sonnet 5 > 4.x
            "claude-opus-5.5",
            "claude-sonnet-5.5",
            "claude-opus-5",
            "claude-sonnet-5",
            "claude-opus-4.8",
            "claude-sonnet-4.6"
        };

        /// <summary>First gateway model of a Claude family ("opus", "sonnet", "haiku", "fable")
        /// in preference order, or null when the key exposes none.</summary>
        internal static string PickClaudeFamily(IEnumerable<string> models, string family)
        {
            List<string> list = models == null ? new List<string>() : models.ToList();
            string marker = "-" + family + "-";
            foreach (string preference in ClaudePreferences)
            {
                if (preference.IndexOf(marker, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }
                foreach (string candidate in list)
                {
                    if (string.Equals(candidate, preference, StringComparison.OrdinalIgnoreCase))
                    {
                        return candidate;
                    }
                }
            }
            foreach (string candidate in list)
            {
                if (candidate != null && candidate.StartsWith("claude-", StringComparison.OrdinalIgnoreCase) &&
                    candidate.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return candidate;
                }
            }
            return null;
        }

        internal static string PickClaude(IEnumerable<string> models)
        {
            List<string> list = models == null ? new List<string>() : models.ToList();
            string preferred = PickExact(list, ClaudePreferences);
            if (preferred != null)
            {
                return preferred;
            }

            return list.FirstOrDefault(delegate(string value)
            {
                return value.IndexOf("claude", StringComparison.OrdinalIgnoreCase) >= 0 &&
                       value.IndexOf("sonnet", StringComparison.OrdinalIgnoreCase) >= 0;
            }) ?? list.FirstOrDefault(delegate(string value)
            {
                return value.IndexOf("claude", StringComparison.OrdinalIgnoreCase) >= 0;
            });
        }

        internal static string PickCodex(IEnumerable<string> models)
        {
            List<string> list = models == null ? new List<string>() : models.ToList();
            string preferred = PickExact(list, CodexPreferences);
            if (preferred != null)
            {
                return preferred;
            }

            return list.FirstOrDefault(delegate(string value)
            {
                return value.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase);
            }) ?? list.FirstOrDefault();
        }

        internal static List<string> ClaudeModels(IEnumerable<string> models)
        {
            return (models ?? Enumerable.Empty<string>())
                .Where(delegate(string value)
                {
                    return value.IndexOf("claude", StringComparison.OrdinalIgnoreCase) >= 0;
                })
                .OrderBy(delegate(string value) { return value; }, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        internal static List<string> CodexModels(IEnumerable<string> models)
        {
            return (models ?? Enumerable.Empty<string>())
                .OrderBy(delegate(string value) { return value; }, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        internal static List<string> ClaudeCandidates(IEnumerable<string> models, string preferred)
        {
            List<string> available = ClaudeModels(models);
            List<string> ordered = new List<string>();
            AddIfAvailable(ordered, available, preferred);
            foreach (string preference in ClaudePreferences)
            {
                AddIfAvailable(ordered, available, preference);
            }
            foreach (string model in available)
            {
                AddIfAvailable(ordered, available, model);
            }
            return ordered;
        }

        internal static List<string> CodexCandidates(IEnumerable<string> models, string preferred)
        {
            List<string> available = CodexModels(models);
            List<string> ordered = new List<string>();
            AddIfAvailable(ordered, available, preferred);
            foreach (string preference in CodexPreferences)
            {
                AddIfAvailable(ordered, available, preference);
            }
            foreach (string model in available.Where(delegate(string value)
            {
                return value.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase);
            }))
            {
                AddIfAvailable(ordered, available, model);
            }
            foreach (string model in available.Where(delegate(string value)
            {
                return value.StartsWith("gemini-", StringComparison.OrdinalIgnoreCase);
            }))
            {
                AddIfAvailable(ordered, available, model);
            }
            foreach (string model in available)
            {
                AddIfAvailable(ordered, available, model);
            }
            return ordered;
        }

        internal static string PickFamily(IEnumerable<string> models, string family, string fallback)
        {
            string match = (models ?? Enumerable.Empty<string>()).FirstOrDefault(delegate(string value)
            {
                return value.IndexOf("claude", StringComparison.OrdinalIgnoreCase) >= 0 &&
                       value.IndexOf(family, StringComparison.OrdinalIgnoreCase) >= 0;
            });
            return string.IsNullOrWhiteSpace(match) ? fallback : match;
        }

        private static string PickExact(List<string> models, string[] preferences)
        {
            foreach (string preference in preferences)
            {
                string match = models.FirstOrDefault(delegate(string model)
                {
                    return string.Equals(model, preference, StringComparison.OrdinalIgnoreCase);
                });
                if (match != null)
                {
                    return match;
                }
            }
            return null;
        }

        private static void AddIfAvailable(List<string> output, List<string> available, string candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                return;
            }
            string match = available.FirstOrDefault(delegate(string value)
            {
                return string.Equals(value, candidate, StringComparison.OrdinalIgnoreCase);
            });
            if (match != null && !output.Contains(match, StringComparer.OrdinalIgnoreCase))
            {
                output.Add(match);
            }
        }
    }

    internal sealed class ApplyResult
    {
        internal string BackupDirectory { get; set; }
        internal string ClaudeDesktopMessage { get; set; }
        internal string InstalledExecutable { get; set; }
    }

    internal static class ConfigManager
    {
        private const string ClaudePolicyPath = @"SOFTWARE\Policies\Claude";
        private static readonly string[] ClaudePolicyValues =
        {
            "inferenceProvider",
            "inferenceGatewayBaseUrl",
            "inferenceCredentialKind",
            "inferenceCredentialHelper",
            "inferenceGatewayAuthScheme",
            "inferenceModels",
            "modelDiscoveryEnabled"
        };

        internal static ApplyResult Apply(
            string key,
            string claudeModel,
            string codexModel,
            IEnumerable<string> availableModels)
        {
            return Apply(key, claudeModel, codexModel, availableModels, true, true);
        }

        internal static ApplyResult Apply(
            string key,
            string claudeModel,
            string codexModel,
            IEnumerable<string> availableModels,
            bool configureClaude,
            bool configureCodex)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new ArgumentException("API key is required.");
            }
            if (!configureClaude && !configureCodex)
            {
                throw new ArgumentException("Select Claude, Codex, or both.");
            }
            if (configureClaude && string.IsNullOrWhiteSpace(claudeModel))
            {
                throw new ArgumentException("A Claude model is required.");
            }
            if (configureCodex && string.IsNullOrWhiteSpace(codexModel))
            {
                throw new ArgumentException("A Codex model is required.");
            }

            string installedExe = InstalledApp.EnsureCurrentCopy();
            string backup = BackupManager.Create();

            try
            {
                CredentialStore.Save(key);
                if (configureClaude)
                {
                    WriteClaudeSettings(installedExe, claudeModel, availableModels);
                    WriteClaudeDesktopLocalConfig(installedExe, claudeModel, availableModels);
                }
                if (configureCodex)
                {
                    string catalog = WriteCodexModelCatalog(codexModel, availableModels);
                    WriteCodexConfig(installedExe, codexModel, catalog);
                }
            }
            catch (Exception applyError)
            {
                try
                {
                    BackupManager.Restore(backup);
                }
                catch (Exception restoreError)
                {
                    throw new InvalidOperationException(
                        "Configuration failed and the automatic restore also failed. Backup: " + backup +
                        ". Apply error: " + applyError.Message + ". Restore error: " + restoreError.Message,
                        applyError);
                }

                throw new InvalidOperationException(
                    "Configuration failed; previous settings were restored. " + applyError.Message,
                    applyError);
            }

            bool managedOverride = configureClaude && HasManagedClaudeConnection();
            return new ApplyResult
            {
                BackupDirectory = backup,
                InstalledExecutable = installedExe,
                ClaudeDesktopMessage = !configureClaude
                    ? "not selected"
                    : managedOverride
                        ? "Local configuration written, but an existing managed policy overrides it."
                        : "configured locally (restart Claude Desktop)"
            };
        }

        internal static string FindExistingKey()
        {
            string key;
            if (CredentialStore.TryLoad(out key))
            {
                return key;
            }

            try
            {
                if (File.Exists(AppPaths.ClaudeSettings))
                {
                    Dictionary<string, object> settings = Json.DeserializeDictionary(
                        File.ReadAllText(AppPaths.ClaudeSettings));
                    Dictionary<string, object> env = Json.AsDictionary(GetValue(settings, "env"));
                    if (env != null)
                    {
                        string baseUrl = Convert.ToString(GetValue(env, "ANTHROPIC_BASE_URL"), CultureInfo.InvariantCulture);
                        if (!string.IsNullOrWhiteSpace(baseUrl) &&
                            baseUrl.IndexOf("zendigikey.shop", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            key = Convert.ToString(GetValue(env, "ANTHROPIC_AUTH_TOKEN"), CultureInfo.InvariantCulture);
                            if (string.IsNullOrWhiteSpace(key))
                            {
                                key = Convert.ToString(GetValue(env, "ANTHROPIC_API_KEY"), CultureInfo.InvariantCulture);
                            }
                            if (!string.IsNullOrWhiteSpace(key))
                            {
                                return key.Trim();
                            }
                        }
                    }
                }
            }
            catch
            {
            }

            string environmentBase = Environment.GetEnvironmentVariable("ANTHROPIC_BASE_URL");
            if (!string.IsNullOrWhiteSpace(environmentBase) &&
                environmentBase.IndexOf("zendigikey.shop", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                key = Environment.GetEnvironmentVariable("ANTHROPIC_AUTH_TOKEN");
                if (string.IsNullOrWhiteSpace(key))
                {
                    key = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
                }
                if (!string.IsNullOrWhiteSpace(key))
                {
                    return key.Trim();
                }
            }

            return null;
        }

        internal static bool IsClaudeConfigured()
        {
            try
            {
                Dictionary<string, object> settings = Json.DeserializeDictionary(
                    File.ReadAllText(AppPaths.ClaudeSettings));
                Dictionary<string, object> env = Json.AsDictionary(GetValue(settings, "env"));
                string baseUrl = env == null ? null : Convert.ToString(GetValue(env, "ANTHROPIC_BASE_URL"));
                string helper = Convert.ToString(GetValue(settings, "apiKeyHelper"));
                return string.Equals(NormalizeUrl(baseUrl), AppPaths.ClaudeCodeBaseUrl, StringComparison.OrdinalIgnoreCase) &&
                       !string.IsNullOrWhiteSpace(helper) &&
                       helper.IndexOf("ZendiKeySetup.exe", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch
            {
                return false;
            }
        }

        internal static string GetConfiguredClaudeModel()
        {
            try
            {
                Dictionary<string, object> settings = Json.DeserializeDictionary(
                    File.ReadAllText(AppPaths.ClaudeSettings));
                return Convert.ToString(GetValue(settings, "model"));
            }
            catch
            {
                return null;
            }
        }

        internal static string GetConfiguredCodexModel()
        {
            try
            {
                string top = Toml.TopLevel(File.ReadAllText(AppPaths.CodexConfig));
                Match match = Regex.Match(
                    top,
                    @"(?im)^\s*model\s*=\s*[""']([^""']+)[""']\s*(?:#.*)?$");
                return match.Success ? match.Groups[1].Value : null;
            }
            catch
            {
                return null;
            }
        }

        internal static bool IsCodexConfigured()
        {
            try
            {
                string value = File.ReadAllText(AppPaths.CodexConfig);
                string top = Toml.TopLevel(value);
                return Regex.IsMatch(top, @"(?im)^\s*model_provider\s*=\s*[""']zendigikey[""']\s*(?:#.*)?$") &&
                       Regex.IsMatch(value, @"(?im)^\s*\[model_providers\.zendigikey\]\s*$") &&
                       value.IndexOf(AppPaths.BaseUrl, StringComparison.OrdinalIgnoreCase) >= 0 &&
                       Regex.IsMatch(value, @"(?im)^\s*\[model_providers\.zendigikey\.auth\]\s*$");
            }
            catch
            {
                return false;
            }
        }

        internal static bool IsClaudeDesktopConfigured()
        {
            try
            {
                RegistryKey managed = OpenManagedClaudeConnection();
                if (managed != null)
                {
                    using (managed)
                    {
                        return IsGatewayConfiguration(
                            Convert.ToString(managed.GetValue("inferenceProvider")),
                            Convert.ToString(managed.GetValue("inferenceGatewayBaseUrl")),
                            Convert.ToString(managed.GetValue("inferenceCredentialKind")),
                            Convert.ToString(managed.GetValue("inferenceCredentialHelper")));
                    }
                }

                if (!File.Exists(AppPaths.ClaudeDesktopMeta) || !File.Exists(AppPaths.ClaudeDesktopLocalConfig))
                {
                    return false;
                }

                Dictionary<string, object> meta = Json.DeserializeDictionary(File.ReadAllText(AppPaths.ClaudeDesktopMeta));
                if (!string.Equals(
                    Convert.ToString(GetValue(meta, "appliedId")),
                    LocalClaudeConfigId,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                Dictionary<string, object> config = Json.DeserializeDictionary(File.ReadAllText(AppPaths.ClaudeDesktopLocalConfig));
                return IsGatewayConfiguration(
                    Convert.ToString(GetValue(config, "inferenceProvider")),
                    Convert.ToString(GetValue(config, "inferenceGatewayBaseUrl")),
                    Convert.ToString(GetValue(config, "inferenceCredentialKind")),
                    Convert.ToString(GetValue(config, "inferenceCredentialHelper")));
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Removes everything ZendiKey Setup wrote so Codex CLI, the ChatGPT app and (if it was
        /// configured) Claude go back to their stock settings and their own login. A backup is
        /// created first so "Restore latest" can bring the ZendiKey configuration back.
        /// </summary>
        internal static string ResetToDefault()
        {
            string backup = BackupManager.Create();

            if (File.Exists(AppPaths.CodexConfig))
            {
                string cleaned = Toml.RemoveZendi(File.ReadAllText(AppPaths.CodexConfig));
                if (string.IsNullOrWhiteSpace(cleaned))
                {
                    File.Delete(AppPaths.CodexConfig);
                }
                else
                {
                    AtomicFile.WriteText(AppPaths.CodexConfig, cleaned);
                }
            }
            DeleteIfExists(AppPaths.CodexModelCatalog);

            if (IsClaudeConfigured())
            {
                Dictionary<string, object> settings = Json.DeserializeDictionary(File.ReadAllText(AppPaths.ClaudeSettings));
                Dictionary<string, object> env = Json.AsDictionary(GetValue(settings, "env"));
                if (env != null)
                {
                    env.Remove("ANTHROPIC_BASE_URL");
                    env.Remove("ANTHROPIC_DEFAULT_OPUS_MODEL");
                    env.Remove("ANTHROPIC_DEFAULT_SONNET_MODEL");
                    env.Remove("ANTHROPIC_DEFAULT_HAIKU_MODEL");
                    env.Remove("ANTHROPIC_DEFAULT_FABLE_MODEL");
                    if (env.Count == 0)
                    {
                        settings.Remove("env");
                    }
                }
                settings.Remove("model");
                settings.Remove("apiKeyHelper");
                AtomicFile.WriteText(AppPaths.ClaudeSettings, Json.PrettySerialize(settings) + Environment.NewLine);
            }

            if (File.Exists(AppPaths.ClaudeDesktopMeta))
            {
                try
                {
                    Dictionary<string, object> meta = Json.DeserializeDictionary(File.ReadAllText(AppPaths.ClaudeDesktopMeta));
                    List<object> entries = new List<object>();
                    IEnumerable existing = GetValue(meta, "entries") as IEnumerable;
                    if (existing != null)
                    {
                        foreach (object raw in existing)
                        {
                            Dictionary<string, object> entry = Json.AsDictionary(raw);
                            if (entry == null || !string.Equals(Convert.ToString(GetValue(entry, "id")), LocalClaudeConfigId, StringComparison.OrdinalIgnoreCase))
                            {
                                entries.Add(raw);
                            }
                        }
                    }
                    meta["entries"] = entries.ToArray();
                    if (string.Equals(Convert.ToString(GetValue(meta, "appliedId")), LocalClaudeConfigId, StringComparison.OrdinalIgnoreCase))
                    {
                        meta.Remove("appliedId");
                    }
                    AtomicFile.WriteText(AppPaths.ClaudeDesktopMeta, Json.PrettySerialize(meta) + Environment.NewLine);
                }
                catch
                {
                }
                DeleteIfExists(AppPaths.ClaudeDesktopLocalConfig);
            }

            DeleteIfExists(AppPaths.CredentialFile);
            return backup;
        }

        private static void DeleteIfExists(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }

        internal static string RestoreLatestBackup()
        {
            if (!Directory.Exists(AppPaths.BackupDirectory))
            {
                throw new InvalidOperationException("No backup is available.");
            }

            string latest = Directory.GetDirectories(AppPaths.BackupDirectory)
                .Where(delegate(string path) { return File.Exists(Path.Combine(path, "manifest.txt")); })
                .OrderByDescending(delegate(string path) { return path; }, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

            if (latest == null)
            {
                throw new InvalidOperationException("No valid backup is available.");
            }

            BackupManager.Restore(latest);
            return latest;
        }

        internal static string MergeClaudeJson(string original, string helperExe, string model)
        {
            return MergeClaudeJson(original, helperExe, model, new[] { model });
        }

        internal static string MergeClaudeJson(
            string original,
            string helperExe,
            string model,
            IEnumerable<string> availableModels)
        {
            Dictionary<string, object> settings = string.IsNullOrWhiteSpace(original)
                ? new Dictionary<string, object>(StringComparer.Ordinal)
                : Json.DeserializeDictionary(original);

            Dictionary<string, object> env = Json.AsDictionary(GetValue(settings, "env"));
            if (env == null)
            {
                env = new Dictionary<string, object>(StringComparer.Ordinal);
            }

            env["ANTHROPIC_BASE_URL"] = AppPaths.ClaudeCodeBaseUrl;
            env.Remove("ANTHROPIC_AUTH_TOKEN");
            env.Remove("ANTHROPIC_API_KEY");
            // Map Claude Code's tier aliases (opus/sonnet/haiku/fable) onto the gateway's
            // models so "/model opus" or "/model haiku" pick a real gateway model instead of
            // all tiers collapsing onto the selected one.
            string opus = ModelSelector.PickClaudeFamily(availableModels, "opus") ?? model;
            string sonnet = ModelSelector.PickClaudeFamily(availableModels, "sonnet") ?? model;
            string haiku = ModelSelector.PickClaudeFamily(availableModels, "haiku") ?? sonnet;
            string fable = ModelSelector.PickClaudeFamily(availableModels, "fable") ?? opus;
            env["ANTHROPIC_DEFAULT_OPUS_MODEL"] = opus;
            env["ANTHROPIC_DEFAULT_SONNET_MODEL"] = sonnet;
            env["ANTHROPIC_DEFAULT_HAIKU_MODEL"] = haiku;
            env["ANTHROPIC_DEFAULT_FABLE_MODEL"] = fable;

            settings["env"] = env;
            settings["model"] = model;
            settings["apiKeyHelper"] = QuoteCommand(helperExe) + " --credential";
            return Json.PrettySerialize(settings) + Environment.NewLine;
        }

        internal static string MergeCodexToml(string original, string helperExe, string model)
        {
            return Toml.Merge(original, helperExe, model, null);
        }

        private static void WriteClaudeSettings(
            string helperExe,
            string model,
            IEnumerable<string> availableModels)
        {
            string original = File.Exists(AppPaths.ClaudeSettings)
                ? File.ReadAllText(AppPaths.ClaudeSettings)
                : string.Empty;
            AtomicFile.WriteText(
                AppPaths.ClaudeSettings,
                MergeClaudeJson(original, helperExe, model, availableModels));
        }

        private static void WriteCodexConfig(string helperExe, string model, string catalogPath)
        {
            string original = File.Exists(AppPaths.CodexConfig)
                ? File.ReadAllText(AppPaths.CodexConfig)
                : string.Empty;
            AtomicFile.WriteText(AppPaths.CodexConfig, Toml.Merge(original, helperExe, model, catalogPath));
        }

        private static string WriteCodexModelCatalog(string selectedModel, IEnumerable<string> availableModels)
        {
            CommandResult result = CommandRunner.Run(
                "cmd.exe",
                "/d /s /c \"codex debug models --bundled\"",
                30000);
            if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.Output))
            {
                return File.Exists(AppPaths.CodexModelCatalog) ? AppPaths.CodexModelCatalog : null;
            }

            try
            {
                Dictionary<string, object> source = Json.DeserializeDictionary(result.Output);
                IEnumerable rawModels = GetValue(source, "models") as IEnumerable;
                if (rawModels == null)
                {
                    return null;
                }

                string catalogJson = BuildCodexModelCatalog(rawModels, selectedModel, availableModels);
                if (catalogJson == null)
                {
                    return null;
                }
                AtomicFile.WriteText(AppPaths.CodexModelCatalog, catalogJson + Environment.NewLine);
                return AppPaths.CodexModelCatalog;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Builds the Codex model catalog from the bundled catalog of the installed Codex and the
        /// models the gateway exposes for this key. Every gateway model is included: bundled
        /// entries are reused, and models the installed Codex does not know yet (for example a
        /// newer GPT generation on an older Codex) are synthesised from a bundled template so
        /// they show up in the model picker instead of being silently dropped.
        /// </summary>
        internal static string BuildCodexModelCatalog(
            IEnumerable rawModels,
            string selectedModel,
            IEnumerable<string> availableModels)
        {
            Dictionary<string, Dictionary<string, object>> bundled =
                new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, object> template = null;
            foreach (object raw in rawModels)
            {
                Dictionary<string, object> model = Json.AsDictionary(raw);
                string slug = model == null ? null : Convert.ToString(GetValue(model, "slug"));
                if (string.IsNullOrWhiteSpace(slug))
                {
                    continue;
                }
                bundled[slug] = model;
                bool listed = string.Equals(Convert.ToString(GetValue(model, "visibility")), "list", StringComparison.OrdinalIgnoreCase);
                if (slug.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase) && listed &&
                    (template == null || string.Equals(slug, "gpt-6-sol", StringComparison.OrdinalIgnoreCase)))
                {
                    template = model;
                }
            }

            List<string> ordered = ModelSelector.CodexCandidates(availableModels, selectedModel);
            if (ordered.Count == 0)
            {
                return null;
            }

            List<object> entries = new List<object>();
            int priority = 1;
            foreach (string slug in ordered)
            {
                Dictionary<string, object> existing;
                Dictionary<string, object> entry;
                if (bundled.TryGetValue(slug, out existing))
                {
                    entry = new Dictionary<string, object>(existing);
                }
                else if (template != null)
                {
                    entry = new Dictionary<string, object>(template);
                    entry["slug"] = slug;
                    entry["display_name"] = PrettyModelName(slug);
                    entry["description"] = "Available through ZendiKey";
                }
                else
                {
                    continue;
                }

                // Keep gateway models visible and selectable, and drop upgrade/migration nags that
                // point at models this key may not have.
                entry["visibility"] = "list";
                entry["supported_in_api"] = true;
                entry["priority"] = priority++;
                entry["upgrade"] = null;
                entry["availability_nux"] = null;
                entries.Add(entry);
            }

            if (entries.Count == 0)
            {
                return null;
            }

            Dictionary<string, object> catalog = new Dictionary<string, object>();
            catalog["models"] = entries.ToArray();
            return Json.PrettySerialize(catalog);
        }

        internal static string PrettyModelName(string slug)
        {
            string[] parts = (slug ?? string.Empty).Split('-');
            List<string> output = new List<string>();
            foreach (string part in parts)
            {
                if (part.Length == 0)
                {
                    continue;
                }
                if (part.Equals("gpt", StringComparison.OrdinalIgnoreCase))
                {
                    output.Add("GPT");
                }
                else if (char.IsDigit(part[0]))
                {
                    output.Add(part);
                }
                else
                {
                    output.Add(char.ToUpperInvariant(part[0]) + part.Substring(1));
                }
            }
            return string.Join("-", output.ToArray());
        }

        private static void WriteClaudeDesktopLocalConfig(
            string helperExe,
            string selectedModel,
            IEnumerable<string> availableModels)
        {
            List<string> models = ModelSelector.ClaudeModels(availableModels);
            models.RemoveAll(delegate(string model)
            {
                return string.Equals(model, selectedModel, StringComparison.OrdinalIgnoreCase);
            });
            models.Insert(0, selectedModel);

            Dictionary<string, object> config = new Dictionary<string, object>();
            config["inferenceProvider"] = "gateway";
            config["inferenceGatewayBaseUrl"] = AppPaths.BaseUrl;
            config["inferenceCredentialKind"] = "helper-script";
            config["inferenceCredentialHelper"] = helperExe;
            config["inferenceGatewayAuthScheme"] = "bearer";
            config["inferenceModels"] = models.ToArray();
            config["modelDiscoveryEnabled"] = true;

            Dictionary<string, object> meta = File.Exists(AppPaths.ClaudeDesktopMeta)
                ? Json.DeserializeDictionary(File.ReadAllText(AppPaths.ClaudeDesktopMeta))
                : new Dictionary<string, object>();
            List<object> entries = new List<object>();
            IEnumerable existingEntries = GetValue(meta, "entries") as IEnumerable;
            if (existingEntries != null)
            {
                foreach (object raw in existingEntries)
                {
                    Dictionary<string, object> entry = Json.AsDictionary(raw);
                    if (entry == null || !string.Equals(
                        Convert.ToString(GetValue(entry, "id")),
                        LocalClaudeConfigId,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        entries.Add(raw);
                    }
                }
            }

            entries.Add(new Dictionary<string, object>
            {
                { "id", LocalClaudeConfigId },
                { "name", "ZendiKey" }
            });
            meta["appliedId"] = LocalClaudeConfigId;
            meta["entries"] = entries.ToArray();

            Directory.CreateDirectory(AppPaths.ClaudeDesktopConfigDirectory);
            AtomicFile.WriteText(AppPaths.ClaudeDesktopLocalConfig, Json.PrettySerialize(config) + Environment.NewLine);
            AtomicFile.WriteText(AppPaths.ClaudeDesktopMeta, Json.PrettySerialize(meta) + Environment.NewLine);
        }

        private static bool HasManagedClaudeConnection()
        {
            using (RegistryKey key = OpenManagedClaudeConnection())
            {
                return key != null;
            }
        }

        private static RegistryKey OpenManagedClaudeConnection()
        {
            RegistryKey machine = TryOpenPolicy(Registry.LocalMachine);
            if (HasConnectionValues(machine))
            {
                return machine;
            }
            if (machine != null)
            {
                machine.Dispose();
            }

            RegistryKey user = TryOpenPolicy(Registry.CurrentUser);
            if (HasConnectionValues(user))
            {
                return user;
            }
            if (user != null)
            {
                user.Dispose();
            }
            return null;
        }

        private static RegistryKey TryOpenPolicy(RegistryKey hive)
        {
            try
            {
                return hive.OpenSubKey(ClaudePolicyPath);
            }
            catch
            {
                return null;
            }
        }

        private static bool HasConnectionValues(RegistryKey key)
        {
            return key != null &&
                   (key.GetValue("inferenceProvider") != null ||
                    key.GetValue("inferenceGatewayBaseUrl") != null ||
                    key.GetValue("inferenceCredentialKind") != null ||
                    key.GetValue("inferenceCredentialHelper") != null);
        }

        private static bool IsGatewayConfiguration(
            string provider,
            string baseUrl,
            string credentialKind,
            string credentialHelper)
        {
            return string.Equals(provider, "gateway", StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(NormalizeUrl(baseUrl), AppPaths.BaseUrl, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(credentialKind, "helper-script", StringComparison.OrdinalIgnoreCase) &&
                   !string.IsNullOrWhiteSpace(credentialHelper) &&
                   credentialHelper.IndexOf("ZendiKeySetup.exe", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string QuoteCommand(string path)
        {
            return "\"" + path.Replace("\"", "\\\"") + "\"";
        }

        private static object GetValue(Dictionary<string, object> values, string name)
        {
            if (values == null)
            {
                return null;
            }
            object value;
            return values.TryGetValue(name, out value) ? value : null;
        }

        private static string NormalizeUrl(string url)
        {
            return string.IsNullOrWhiteSpace(url) ? string.Empty : url.Trim().TrimEnd('/');
        }

        internal static string[] ManagedPolicyValueNames
        {
            get { return (string[])ClaudePolicyValues.Clone(); }
        }

        internal static string PolicyPath
        {
            get { return ClaudePolicyPath; }
        }

        internal const string LocalClaudeConfigId = "d72336c1-5448-4ed0-a2fa-67349f74d11b";
    }

    internal static class Toml
    {
        private static readonly Regex SectionPattern = new Regex(@"^\s*\[[^\]]+\]\s*(?:#.*)?$");
        private static readonly Regex ZendiSectionPattern = new Regex(
            @"^\s*\[\s*model_providers\.(?:""zendigikey""|zendigikey)(?:\.[^\]]+)?\s*\]\s*(?:#.*)?$",
            RegexOptions.IgnoreCase);

        internal static string TopLevel(string content)
        {
            string normalized = (content ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
            string[] lines = normalized.Split('\n');
            List<string> result = new List<string>();
            foreach (string line in lines)
            {
                if (SectionPattern.IsMatch(line))
                {
                    break;
                }
                result.Add(line);
            }
            return string.Join(Environment.NewLine, result.ToArray());
        }

        internal static string Merge(string content, string helperExe, string model, string catalogPath)
        {
            string normalized = (content ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
            List<string> source = normalized.Split('\n').ToList();
            List<string> kept = new List<string>();
            bool skip = false;

            foreach (string line in source)
            {
                if (SectionPattern.IsMatch(line))
                {
                    skip = ZendiSectionPattern.IsMatch(line);
                }
                if (!skip)
                {
                    kept.Add(line);
                }
            }

            int firstSection = kept.FindIndex(delegate(string line) { return SectionPattern.IsMatch(line); });
            if (firstSection < 0)
            {
                firstSection = kept.Count;
            }

            SetTopLevel(kept, firstSection, "model_provider", StringValue(AppPaths.ProviderId));
            firstSection = kept.FindIndex(delegate(string line) { return SectionPattern.IsMatch(line); });
            if (firstSection < 0)
            {
                firstSection = kept.Count;
            }
            SetTopLevel(kept, firstSection, "model", StringValue(model));
            firstSection = kept.FindIndex(delegate(string line) { return SectionPattern.IsMatch(line); });
            if (firstSection < 0)
            {
                firstSection = kept.Count;
            }
            SetTopLevel(kept, firstSection, "review_model", StringValue(model));
            if (!string.IsNullOrWhiteSpace(catalogPath))
            {
                firstSection = kept.FindIndex(delegate(string line) { return SectionPattern.IsMatch(line); });
                if (firstSection < 0)
                {
                    firstSection = kept.Count;
                }
                SetTopLevel(kept, firstSection, "model_catalog_json", StringValue(catalogPath));
            }

            while (kept.Count > 0 && string.IsNullOrWhiteSpace(kept[kept.Count - 1]))
            {
                kept.RemoveAt(kept.Count - 1);
            }
            if (kept.Count > 0)
            {
                kept.Add(string.Empty);
            }

            kept.Add("[model_providers.zendigikey]");
            kept.Add("name = \"ZendiKey\"");
            kept.Add("base_url = " + StringValue(AppPaths.BaseUrl));
            kept.Add("wire_api = \"responses\"");
            kept.Add(string.Empty);
            kept.Add("[model_providers.zendigikey.auth]");
            kept.Add("command = " + StringValue(helperExe));
            kept.Add("args = [\"--credential\"]");

            return string.Join(Environment.NewLine, kept.ToArray()) + Environment.NewLine;
        }

        /// <summary>
        /// Drops the ZendiKey provider sections and, when ZendiKey was the active provider, the
        /// top-level model / provider / catalog keys it wrote. Everything else is kept verbatim.
        /// </summary>
        internal static string RemoveZendi(string content)
        {
            string normalized = (content ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
            List<string> kept = new List<string>();
            bool skip = false;
            foreach (string line in normalized.Split('\n'))
            {
                if (SectionPattern.IsMatch(line))
                {
                    skip = ZendiSectionPattern.IsMatch(line);
                }
                if (!skip)
                {
                    kept.Add(line);
                }
            }

            int firstSection = kept.FindIndex(delegate(string line) { return SectionPattern.IsMatch(line); });
            if (firstSection < 0)
            {
                firstSection = kept.Count;
            }
            Regex provider = new Regex(@"^\s*model_provider\s*=\s*[""']" + Regex.Escape(AppPaths.ProviderId) + @"[""']", RegexOptions.IgnoreCase);
            bool ours = kept.Take(firstSection).Any(delegate(string line) { return provider.IsMatch(line); });
            if (ours)
            {
                Regex managed = new Regex(@"^\s*(model_provider|model|review_model|model_catalog_json)\s*=", RegexOptions.IgnoreCase);
                for (int i = firstSection - 1; i >= 0; i--)
                {
                    if (managed.IsMatch(kept[i]))
                    {
                        kept.RemoveAt(i);
                    }
                }
            }

            while (kept.Count > 0 && string.IsNullOrWhiteSpace(kept[kept.Count - 1]))
            {
                kept.RemoveAt(kept.Count - 1);
            }
            return kept.Count == 0 ? string.Empty : string.Join(Environment.NewLine, kept.ToArray()) + Environment.NewLine;
        }

        private static void SetTopLevel(List<string> lines, int firstSection, string name, string value)
        {
            Regex pattern = new Regex(@"^\s*" + Regex.Escape(name) + @"\s*=", RegexOptions.IgnoreCase);
            for (int i = 0; i < firstSection; i++)
            {
                if (pattern.IsMatch(lines[i]))
                {
                    lines[i] = name + " = " + value;
                    return;
                }
            }

            lines.Insert(firstSection, name + " = " + value);
        }

        private static string StringValue(string value)
        {
            return "\"" + (value ?? string.Empty)
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n") + "\"";
        }
    }

    internal static class BackupManager
    {
        internal static string Create()
        {
            string directory = Path.Combine(
                AppPaths.BackupDirectory,
                DateTime.Now.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(directory);

            BackupFile(AppPaths.ClaudeSettings, directory, "claude-settings");
            BackupFile(AppPaths.CodexConfig, directory, "codex-config");
            BackupFile(AppPaths.CodexModelCatalog, directory, "codex-model-catalog");
            BackupFile(AppPaths.CredentialFile, directory, "credential");
            BackupFile(AppPaths.ClaudeDesktopMeta, directory, "claude-desktop-meta");
            BackupFile(AppPaths.ClaudeDesktopLocalConfig, directory, "claude-desktop-local");
            BackupRegistry(directory);
            File.WriteAllText(
                Path.Combine(directory, "manifest.txt"),
                "ZendiKey Setup backup v1" + Environment.NewLine +
                DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture) + Environment.NewLine,
                new UTF8Encoding(false));
            return directory;
        }

        internal static void Restore(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                throw new DirectoryNotFoundException("Backup directory not found: " + directory);
            }

            RestoreFile(AppPaths.ClaudeSettings, directory, "claude-settings");
            RestoreFile(AppPaths.CodexConfig, directory, "codex-config");
            RestoreFile(AppPaths.CodexModelCatalog, directory, "codex-model-catalog");
            RestoreFile(AppPaths.CredentialFile, directory, "credential");
            RestoreFile(AppPaths.ClaudeDesktopMeta, directory, "claude-desktop-meta");
            RestoreFile(AppPaths.ClaudeDesktopLocalConfig, directory, "claude-desktop-local");
            RestoreRegistry(directory);
        }

        private static void BackupFile(string source, string directory, string name)
        {
            if (!File.Exists(source))
            {
                File.WriteAllText(Path.Combine(directory, name + ".missing"), string.Empty);
                return;
            }

            byte[] plain = File.ReadAllBytes(source);
            try
            {
                File.WriteAllBytes(Path.Combine(directory, name + ".protected"), CredentialStore.ProtectBytes(plain));
            }
            finally
            {
                Array.Clear(plain, 0, plain.Length);
            }
        }

        private static void RestoreFile(string target, string directory, string name)
        {
            string protectedPath = Path.Combine(directory, name + ".protected");
            string missingPath = Path.Combine(directory, name + ".missing");
            if (File.Exists(protectedPath))
            {
                byte[] plain = CredentialStore.UnprotectBytes(File.ReadAllBytes(protectedPath));
                try
                {
                    AtomicFile.WriteBytes(target, plain);
                }
                finally
                {
                    Array.Clear(plain, 0, plain.Length);
                }
            }
            else if (File.Exists(missingPath) && File.Exists(target))
            {
                File.Delete(target);
            }
        }

        private static void BackupRegistry(string directory)
        {
            Dictionary<string, object> snapshot = new Dictionary<string, object>();
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(ConfigManager.PolicyPath))
            {
                foreach (string name in ConfigManager.ManagedPolicyValueNames)
                {
                    Dictionary<string, object> item = new Dictionary<string, object>();
                    object value = key == null ? null : key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                    if (value == null)
                    {
                        item["exists"] = false;
                    }
                    else
                    {
                        item["exists"] = true;
                        item["kind"] = key.GetValueKind(name).ToString();
                        item["value"] = RegistryValueToObject(value);
                    }
                    snapshot[name] = item;
                }
            }

            byte[] plain = Encoding.UTF8.GetBytes(Json.Serialize(snapshot));
            try
            {
                File.WriteAllBytes(
                    Path.Combine(directory, "claude-desktop-registry.protected"),
                    CredentialStore.ProtectBytes(plain));
            }
            finally
            {
                Array.Clear(plain, 0, plain.Length);
            }
        }

        private static void RestoreRegistry(string directory)
        {
            string path = Path.Combine(directory, "claude-desktop-registry.protected");
            if (!File.Exists(path))
            {
                return;
            }

            byte[] plain = CredentialStore.UnprotectBytes(File.ReadAllBytes(path));
            Dictionary<string, object> snapshot;
            try
            {
                snapshot = Json.DeserializeDictionary(Encoding.UTF8.GetString(plain));
            }
            finally
            {
                Array.Clear(plain, 0, plain.Length);
            }

            bool hasSavedValue = false;
            foreach (string name in ConfigManager.ManagedPolicyValueNames)
            {
                object raw;
                Dictionary<string, object> item = snapshot.TryGetValue(name, out raw)
                    ? Json.AsDictionary(raw)
                    : null;
                if (item != null && Convert.ToBoolean(Get(item, "exists"), CultureInfo.InvariantCulture))
                {
                    hasSavedValue = true;
                    break;
                }
            }

            RegistryKey writable = null;
            try
            {
                writable = hasSavedValue
                    ? Registry.CurrentUser.CreateSubKey(ConfigManager.PolicyPath)
                    : Registry.CurrentUser.OpenSubKey(ConfigManager.PolicyPath, true);

                if (writable == null)
                {
                    if (hasSavedValue)
                    {
                        throw new UnauthorizedAccessException("Cannot restore the Claude Desktop policy registry key.");
                    }
                    return;
                }

                foreach (string name in ConfigManager.ManagedPolicyValueNames)
                {
                    object raw;
                    Dictionary<string, object> item = snapshot.TryGetValue(name, out raw)
                        ? Json.AsDictionary(raw)
                        : null;
                    bool exists = item != null && Convert.ToBoolean(Get(item, "exists"), CultureInfo.InvariantCulture);
                    if (!exists)
                    {
                        writable.DeleteValue(name, false);
                        continue;
                    }

                    RegistryValueKind kind;
                    if (!Enum.TryParse(Convert.ToString(Get(item, "kind")), out kind))
                    {
                        kind = RegistryValueKind.String;
                    }
                    writable.SetValue(name, ObjectToRegistryValue(Get(item, "value"), kind), kind);
                }
            }
            finally
            {
                if (writable != null)
                {
                    writable.Dispose();
                }
            }
        }

        private static object RegistryValueToObject(object value)
        {
            byte[] bytes = value as byte[];
            if (bytes != null)
            {
                return Convert.ToBase64String(bytes);
            }
            return value;
        }

        private static object ObjectToRegistryValue(object value, RegistryValueKind kind)
        {
            if (kind == RegistryValueKind.Binary)
            {
                return Convert.FromBase64String(Convert.ToString(value));
            }
            if (kind == RegistryValueKind.DWord)
            {
                return Convert.ToInt32(value, CultureInfo.InvariantCulture);
            }
            if (kind == RegistryValueKind.QWord)
            {
                return Convert.ToInt64(value, CultureInfo.InvariantCulture);
            }
            if (kind == RegistryValueKind.MultiString)
            {
                IEnumerable list = value as IEnumerable;
                if (list != null && !(value is string))
                {
                    List<string> items = new List<string>();
                    foreach (object item in list)
                    {
                        items.Add(Convert.ToString(item, CultureInfo.InvariantCulture));
                    }
                    return items.ToArray();
                }
                return new[] { Convert.ToString(value, CultureInfo.InvariantCulture) };
            }
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static object Get(Dictionary<string, object> values, string name)
        {
            object value;
            return values != null && values.TryGetValue(name, out value) ? value : null;
        }
    }

    internal static class Json
    {
        internal static string Serialize(object value)
        {
            JavaScriptSerializer serializer = CreateSerializer();
            return serializer.Serialize(value);
        }

        internal static string PrettySerialize(object value)
        {
            return Pretty(Serialize(value));
        }

        internal static Dictionary<string, object> DeserializeDictionary(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return new Dictionary<string, object>();
            }

            object parsed = CreateSerializer().DeserializeObject(json);
            Dictionary<string, object> dictionary = AsDictionary(parsed);
            if (dictionary == null)
            {
                throw new FormatException("Expected a JSON object.");
            }
            return dictionary;
        }

        internal static Dictionary<string, object> AsDictionary(object value)
        {
            Dictionary<string, object> direct = value as Dictionary<string, object>;
            if (direct != null)
            {
                return direct;
            }

            IDictionary dictionary = value as IDictionary;
            if (dictionary == null)
            {
                return null;
            }

            Dictionary<string, object> result = new Dictionary<string, object>();
            foreach (DictionaryEntry item in dictionary)
            {
                result[Convert.ToString(item.Key, CultureInfo.InvariantCulture)] = item.Value;
            }
            return result;
        }

        private static JavaScriptSerializer CreateSerializer()
        {
            return new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 100 };
        }

        private static string Pretty(string json)
        {
            StringBuilder output = new StringBuilder();
            bool inString = false;
            bool escaped = false;
            int indent = 0;

            for (int i = 0; i < json.Length; i++)
            {
                char current = json[i];
                if (inString)
                {
                    output.Append(current);
                    if (escaped)
                    {
                        escaped = false;
                    }
                    else if (current == '\\')
                    {
                        escaped = true;
                    }
                    else if (current == '"')
                    {
                        inString = false;
                    }
                    continue;
                }

                if (current == '"')
                {
                    inString = true;
                    output.Append(current);
                }
                else if (current == '{' || current == '[')
                {
                    output.Append(current);
                    output.AppendLine();
                    indent++;
                    AppendIndent(output, indent);
                }
                else if (current == '}' || current == ']')
                {
                    output.AppendLine();
                    indent--;
                    AppendIndent(output, indent);
                    output.Append(current);
                }
                else if (current == ',')
                {
                    output.Append(current);
                    output.AppendLine();
                    AppendIndent(output, indent);
                }
                else if (current == ':')
                {
                    output.Append(": ");
                }
                else if (!char.IsWhiteSpace(current))
                {
                    output.Append(current);
                }
            }

            return output.ToString();
        }

        private static void AppendIndent(StringBuilder builder, int indent)
        {
            builder.Append(' ', Math.Max(0, indent) * 2);
        }
    }

    internal sealed class DetectionItem
    {
        internal bool Installed { get; set; }
        internal string DisplayValue { get; set; }
    }

    internal sealed class ClientStatus
    {
        internal DetectionItem ClaudeCode { get; set; }
        internal DetectionItem CodexCli { get; set; }
        internal DetectionItem ClaudeDesktop { get; set; }
        internal DetectionItem ChatGptDesktop { get; set; }
    }

    internal static class SystemDetector
    {
        internal static ClientStatus Detect()
        {
            return new ClientStatus
            {
                ClaudeCode = DetectCommand("claude", "--version"),
                CodexCli = DetectCommand("codex", "--version"),
                ClaudeDesktop = DetectAppx("Claude", "Claude Desktop"),
                ChatGptDesktop = DetectAppx("ChatGPT|OpenAI\\.(?:Codex|Chat)", "ChatGPT Desktop")
            };
        }

        private static DetectionItem DetectCommand(string command, string arguments)
        {
            CommandResult result = CommandRunner.Run(
                "cmd.exe",
                "/d /s /c \"" + command + " " + arguments + "\"",
                15000);
            if (result.ExitCode != 0)
            {
                return new DetectionItem { Installed = false, DisplayValue = "Not installed" };
            }

            string line = FirstLine(result.Output);
            return new DetectionItem
            {
                Installed = true,
                DisplayValue = string.IsNullOrWhiteSpace(line) ? "Installed" : line
            };
        }

        private static DetectionItem DetectAppx(string pattern, string displayName)
        {
            string script =
                "$p = Get-AppxPackage | Where-Object { $_.Name -match '" + pattern.Replace("'", "''") +
                "' -or $_.PackageFullName -match '" + pattern.Replace("'", "''") +
                "' } | Select-Object -First 1; if ($p) { [Console]::Write($p.Version.ToString()) } else { exit 3 }";
            CommandResult result = CommandRunner.RunPowerShell(script, 30000);
            if (result.ExitCode != 0)
            {
                return new DetectionItem { Installed = false, DisplayValue = "Not installed" };
            }

            string version = FirstLine(result.Output);
            return new DetectionItem
            {
                Installed = true,
                DisplayValue = string.IsNullOrWhiteSpace(version) ? displayName : "v" + version
            };
        }

        private static string FirstLine(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }
            return value.Replace("\r", string.Empty).Split('\n')[0].Trim();
        }
    }

    internal sealed class InstallResult
    {
        internal InstallResult()
        {
            Messages = new List<string>();
        }

        internal List<string> Messages { get; private set; }
        internal bool Success { get; set; }
    }

    internal static class InstallManager
    {
        // Microsoft Store listing of the ChatGPT app (package OpenAI.Codex) that hosts Codex mode.
        internal const string ChatGptStoreId = "9PLM9XGG6VKS";
        private const string CodexReleaseDownload = "https://github.com/openai/codex/releases/latest/download/";
        private const string CodexReleaseApi = "https://api.github.com/repos/openai/codex/releases/latest";
        private const int WingetAlreadyInstalled = unchecked((int)0x8A15002B);

        internal static readonly string CodexPortableDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            "Codex");
        internal static readonly string CodexPortableExe = Path.Combine(CodexPortableDirectory, "codex.exe");

        internal static async Task<InstallResult> InstallMissingAsync(
            Action<string> report,
            bool installClaude,
            bool installCodex)
        {
            return await InstallMissingAsync(report, installClaude, installCodex, installCodex).ConfigureAwait(false);
        }

        internal static async Task<InstallResult> InstallMissingAsync(
            Action<string> report,
            bool installClaude,
            bool installCodexCli,
            bool installChatGptApp)
        {
            InstallResult summary = new InstallResult { Success = true };
            ClientStatus before = await Task.Run(delegate { return SystemDetector.Detect(); });

            if (installClaude && !before.ClaudeCode.Installed)
            {
                await InstallWinget(
                    "Claude Code",
                    "--id Anthropic.ClaudeCode -e --silent --accept-source-agreements --accept-package-agreements",
                    report,
                    summary);
            }
            else if (installClaude)
            {
                AddMessage(summary, report, "Claude Code is already installed.");
            }

            if (installCodexCli)
            {
                await EnsureCodexCliAsync(before.CodexCli, report, summary);
            }

            if (installClaude && !before.ClaudeDesktop.Installed)
            {
                await InstallWinget(
                    "Claude Desktop",
                    "--id Anthropic.Claude -e --silent --accept-source-agreements --accept-package-agreements",
                    report,
                    summary);
            }
            else if (installClaude)
            {
                AddMessage(summary, report, "Claude Desktop is already installed.");
            }

            if (installChatGptApp)
            {
                await EnsureChatGptAppAsync(before.ChatGptDesktop, report, summary);
            }

            return summary;
        }

        /// <summary>
        /// Codex CLI without any prerequisite: npm when the PC already has Node.js, otherwise the
        /// standalone Windows binary from the official GitHub release (no Node.js, no admin rights).
        /// </summary>
        private static async Task EnsureCodexCliAsync(DetectionItem current, Action<string> report, InstallResult summary)
        {
            bool npm = await HasCommandAsync("npm --version");

            if (!current.Installed)
            {
                if (npm)
                {
                    AddMessage(summary, report, "Installing Codex CLI with npm...");
                    CommandResult npmInstall = await Task.Run(delegate
                    {
                        return CommandRunner.Run("cmd.exe", "/d /s /c \"npm install -g @openai/codex@latest\"", 1200000);
                    });
                    RefreshProcessPath();
                    if (npmInstall.ExitCode == 0 && await HasCommandAsync("codex --version"))
                    {
                        AddMessage(summary, report, "Codex CLI installed (" + await CodexVersionAsync() + ").");
                        return;
                    }
                    AddMessage(summary, report, "npm install did not work (" + LastUsefulLine(npmInstall.Output) + "); using the standalone build instead.");
                }

                await InstallCodexPortableAsync(report, summary);
                return;
            }

            if (await IsPortableCodexAsync())
            {
                await UpdateCodexPortableAsync(report, summary);
                return;
            }

            if (npm)
            {
                // Older Codex builds do not know the newest models; refresh to latest so the
                // bundled catalog and the Responses client match what the gateway offers.
                AddMessage(summary, report, "Updating Codex CLI to the latest version...");
                CommandResult codexUpdate = await Task.Run(delegate
                {
                    return CommandRunner.Run("cmd.exe", "/d /s /c \"npm install -g @openai/codex@latest\"", 1200000);
                });
                AddMessage(
                    summary,
                    report,
                    codexUpdate.ExitCode == 0
                        ? "Codex CLI is up to date (" + await CodexVersionAsync() + ")."
                        : "Codex CLI update skipped: " + LastUsefulLine(codexUpdate.Output));
                return;
            }

            AddMessage(summary, report, "Codex CLI is already installed (" + current.DisplayValue + ").");
        }

        /// <summary>
        /// Downloads the standalone Codex CLI for this CPU into %LocalAppData%\Programs\Codex and
        /// puts that folder on the user PATH. Returns true when `codex --version` works afterwards.
        /// </summary>
        internal static async Task<bool> InstallCodexPortableAsync(Action<string> report, InstallResult summary)
        {
            string asset = IsArm64() ? "codex-aarch64-pc-windows-msvc.exe.zip" : "codex-x86_64-pc-windows-msvc.exe.zip";
            string archive = Path.Combine(Path.GetTempPath(), "zendikey-" + asset);
            string pending = CodexPortableExe + ".new";

            try
            {
                AddMessage(summary, report, "Downloading Codex CLI (standalone, no Node.js needed)...");
                await DownloadFileAsync(CodexReleaseDownload + asset, archive, report);

                Directory.CreateDirectory(CodexPortableDirectory);
                AddMessage(summary, report, "Unpacking Codex CLI...");
                string mainName = asset.Substring(0, asset.Length - ".zip".Length);
                await Task.Run(delegate
                {
                    // The archive holds the CLI itself (codex-<arch>-pc-windows-msvc.exe) plus the
                    // helper binaries it spawns (command runner, sandbox setup, ripgrep, voice).
                    // Everything is unpacked next to codex.exe, mirroring the npm package layout.
                    bool found = false;
                    using (ZipArchive zip = ZipFile.OpenRead(archive))
                    {
                        foreach (ZipArchiveEntry entry in zip.Entries)
                        {
                            if (string.IsNullOrEmpty(entry.Name))
                            {
                                continue;
                            }
                            string relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                            if (relative.Contains(".." + Path.DirectorySeparatorChar) || Path.IsPathRooted(relative))
                            {
                                continue;
                            }
                            string target;
                            if (string.Equals(entry.FullName, mainName, StringComparison.OrdinalIgnoreCase))
                            {
                                target = pending;
                                found = true;
                            }
                            else
                            {
                                target = Path.Combine(CodexPortableDirectory, relative);
                                Directory.CreateDirectory(Path.GetDirectoryName(target));
                            }
                            entry.ExtractToFile(target, true);
                        }
                    }
                    if (!found)
                    {
                        throw new InvalidOperationException("The Codex download did not contain " + mainName + ".");
                    }
                });

                try
                {
                    File.Copy(pending, CodexPortableExe, true);
                }
                catch (IOException)
                {
                    throw new InvalidOperationException("codex.exe is in use. Close Codex / ChatGPT and run the setup again.");
                }

                AddUserPath(CodexPortableDirectory);
                RefreshProcessPath();

                CommandResult probe = await Task.Run(delegate
                {
                    return CommandRunner.Run(CodexPortableExe, "--version", 30000);
                });
                if (probe.ExitCode != 0)
                {
                    throw new InvalidOperationException("codex.exe was unpacked but does not start: " + LastUsefulLine(probe.Output));
                }

                AddMessage(summary, report, "Codex CLI installed (" + FirstLine(probe.Output) + ") in " + CodexPortableDirectory + ".");
                return true;
            }
            catch (Exception ex)
            {
                summary.Success = false;
                AddMessage(summary, report, "Codex CLI failed: " + ex.Message);
                return false;
            }
            finally
            {
                TryDelete(pending);
                TryDelete(archive);
            }
        }

        private static async Task UpdateCodexPortableAsync(Action<string> report, InstallResult summary)
        {
            string installed = await CodexVersionAsync();
            string latest = await LatestCodexVersionAsync();
            if (string.IsNullOrWhiteSpace(latest))
            {
                AddMessage(summary, report, "Codex CLI is installed (" + installed + "); update check skipped.");
                return;
            }
            if (installed.IndexOf(latest, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                AddMessage(summary, report, "Codex CLI is up to date (" + installed + ").");
                return;
            }

            AddMessage(summary, report, "Updating Codex CLI " + installed + " -> " + latest + "...");
            await InstallCodexPortableAsync(report, summary);
        }

        private static async Task<bool> IsPortableCodexAsync()
        {
            if (!File.Exists(CodexPortableExe))
            {
                return false;
            }
            CommandResult where = await Task.Run(delegate
            {
                return CommandRunner.Run("cmd.exe", "/d /s /c \"where codex\"", 15000);
            });
            string first = (where.Output ?? string.Empty).Replace("\r", string.Empty).Split('\n')[0].Trim();
            return string.Equals(first, CodexPortableExe, StringComparison.OrdinalIgnoreCase);
        }

        private static async Task<string> LatestCodexVersionAsync()
        {
            try
            {
                using (HttpClient client = CreateHttpClient(TimeSpan.FromSeconds(30)))
                {
                    string body = await client.GetStringAsync(CodexReleaseApi).ConfigureAwait(false);
                    Dictionary<string, object> root = Json.DeserializeDictionary(body);
                    object tag;
                    if (!root.TryGetValue("tag_name", out tag) || tag == null)
                    {
                        return null;
                    }
                    Match match = Regex.Match(Convert.ToString(tag, CultureInfo.InvariantCulture), @"\d+(\.\d+)+");
                    return match.Success ? match.Value : null;
                }
            }
            catch
            {
                return null;
            }
        }

        private static async Task<string> CodexVersionAsync()
        {
            CommandResult result = await Task.Run(delegate
            {
                return File.Exists(CodexPortableExe)
                    ? CommandRunner.Run(CodexPortableExe, "--version", 15000)
                    : CommandRunner.Run("cmd.exe", "/d /s /c \"codex --version\"", 15000);
            });
            string line = FirstLine(result.Output);
            return string.IsNullOrWhiteSpace(line) ? "unknown version" : line;
        }

        private static string FirstLine(string value)
        {
            return (value ?? string.Empty).Replace("\r", string.Empty).Split('\n')[0].Trim();
        }

        /// <summary>
        /// The ChatGPT app (Codex mode) only ships through the Microsoft Store. winget can install
        /// it silently when the Store source works; otherwise the Store page is opened so the
        /// customer only has to press Install.
        /// </summary>
        private static async Task EnsureChatGptAppAsync(DetectionItem current, Action<string> report, InstallResult summary)
        {
            if (current.Installed)
            {
                AddMessage(summary, report, "ChatGPT app is already installed (" + current.DisplayValue + ").");
                return;
            }

            string winget = FindWinget();
            if (winget != null)
            {
                AddMessage(summary, report, "Installing the ChatGPT app from the Microsoft Store...");
                CommandResult result = await Task.Run(delegate
                {
                    return CommandRunner.Run(
                        winget,
                        "install --id " + ChatGptStoreId + " -e -s msstore --silent --accept-source-agreements --accept-package-agreements --disable-interactivity",
                        1200000);
                });
                if (result.ExitCode == 0 || result.ExitCode == WingetAlreadyInstalled)
                {
                    DetectionItem after = await Task.Run(delegate { return SystemDetector.Detect().ChatGptDesktop; });
                    if (after.Installed)
                    {
                        AddMessage(summary, report, "ChatGPT app installed (" + after.DisplayValue + ").");
                        return;
                    }
                }
                AddMessage(summary, report, "winget could not install the ChatGPT app: " + LastUsefulLine(result.Output));
            }
            else
            {
                AddMessage(summary, report, "winget is not available on this PC.");
            }

            if (OpenStorePage(ChatGptStoreId))
            {
                summary.Success = false;
                AddMessage(summary, report, "Microsoft Store opened on the ChatGPT app: press Install there, then click Refresh status.");
            }
            else
            {
                summary.Success = false;
                AddMessage(summary, report, "Install the ChatGPT app from the Microsoft Store (search \"ChatGPT\" by OpenAI), then click Refresh status.");
            }
        }

        private static string FindWinget()
        {
            CommandResult probe = CommandRunner.Run("cmd.exe", "/d /s /c \"winget --version\"", 20000);
            if (probe.ExitCode == 0)
            {
                return "winget.exe";
            }
            string alias = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft",
                "WindowsApps",
                "winget.exe");
            return File.Exists(alias) ? alias : null;
        }

        private static bool OpenStorePage(string productId)
        {
            try
            {
                Process.Start(new ProcessStartInfo("ms-windows-store://pdp/?ProductId=" + productId) { UseShellExecute = true });
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static async Task<CommandResult> InstallWinget(
            string name,
            string installArguments,
            Action<string> report,
            InstallResult summary)
        {
            string winget = FindWinget() ?? "winget.exe";
            AddMessage(summary, report, "Installing " + name + "...");
            CommandResult result = await Task.Run(delegate
            {
                return CommandRunner.Run(
                    winget,
                    "install " + installArguments + " --disable-interactivity",
                    1200000);
            });
            if (result.ExitCode == WingetAlreadyInstalled)
            {
                result.ExitCode = 0;
            }
            RecordResult(name, result, report, summary);
            return result;
        }

        private static async Task DownloadFileAsync(string url, string target, Action<string> report)
        {
            using (HttpClient client = CreateHttpClient(TimeSpan.FromMinutes(30)))
            using (HttpResponseMessage response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException("Download failed: HTTP " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture));
                }

                long total = response.Content.Headers.ContentLength ?? -1;
                long done = 0;
                long nextReport = 20L * 1024 * 1024;
                byte[] buffer = new byte[1024 * 1024];
                using (Stream input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (FileStream output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, buffer.Length, true))
                {
                    int read;
                    while ((read = await input.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
                    {
                        await output.WriteAsync(buffer, 0, read).ConfigureAwait(false);
                        done += read;
                        if (report != null && done >= nextReport)
                        {
                            nextReport += 20L * 1024 * 1024;
                            report("Downloaded " + (done / 1048576).ToString(CultureInfo.InvariantCulture) + " MB" +
                                   (total > 0 ? " of " + (total / 1048576).ToString(CultureInfo.InvariantCulture) + " MB" : string.Empty) + "...");
                        }
                    }
                }
            }
        }

        private static HttpClient CreateHttpClient(TimeSpan timeout)
        {
            HttpClient client = new HttpClient();
            client.Timeout = timeout;
            client.DefaultRequestHeaders.Add("User-Agent", "ZendiKeySetup/1.2");
            return client;
        }

        private static bool IsArm64()
        {
            string arch = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITEW6432") ??
                          Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE") ?? string.Empty;
            return arch.IndexOf("ARM64", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static async Task<bool> HasCommandAsync(string commandLine)
        {
            CommandResult result = await Task.Run(delegate
            {
                return CommandRunner.Run("cmd.exe", "/d /s /c \"" + commandLine + "\"", 15000);
            });
            return result.ExitCode == 0;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SendMessageTimeout(
            IntPtr hWnd,
            uint msg,
            UIntPtr wParam,
            string lParam,
            uint flags,
            uint timeout,
            out UIntPtr result);

        /// <summary>Appends a folder to the current user's PATH (HKCU\Environment) if it is missing.</summary>
        private static void AddUserPath(string directory)
        {
            using (RegistryKey env = Registry.CurrentUser.OpenSubKey("Environment", true))
            {
                if (env == null)
                {
                    return;
                }
                string current = Convert.ToString(env.GetValue("Path", string.Empty, RegistryValueOptions.DoNotExpandEnvironmentNames));
                string[] parts = current.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Any(delegate(string part)
                {
                    return string.Equals(part.Trim().TrimEnd('\\'), directory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
                }))
                {
                    return;
                }
                string updated = string.IsNullOrWhiteSpace(current) ? directory : current.TrimEnd(';') + ";" + directory;
                env.SetValue("Path", updated, RegistryValueKind.ExpandString);
            }

            try
            {
                UIntPtr ignored;
                SendMessageTimeout(new IntPtr(0xFFFF), 0x001A, UIntPtr.Zero, "Environment", 0x0002, 5000, out ignored);
            }
            catch
            {
            }
        }

        private static void RefreshProcessPath()
        {
            string current = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.Process);
            string machine = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.Machine);
            string user = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User);
            string combined = string.Join(
                ";",
                new[] { current, machine, user, CodexPortableDirectory }
                    .Where(delegate(string value) { return !string.IsNullOrWhiteSpace(value); })
                    .ToArray());
            Environment.SetEnvironmentVariable("Path", combined, EnvironmentVariableTarget.Process);
        }

        private static void RecordResult(
            string name,
            CommandResult result,
            Action<string> report,
            InstallResult summary)
        {
            if (result.ExitCode == 0)
            {
                AddMessage(summary, report, name + " installed.");
            }
            else
            {
                summary.Success = false;
                string detail = string.IsNullOrWhiteSpace(result.Output) ? "exit " + result.ExitCode : LastUsefulLine(result.Output);
                AddMessage(summary, report, name + " failed: " + detail);
            }
        }

        private static void AddMessage(InstallResult result, Action<string> report, string message)
        {
            result.Messages.Add(message);
            if (report != null)
            {
                report(message);
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }

        private static string LastUsefulLine(string value)
        {
            string[] lines = (value ?? string.Empty).Replace("\r", string.Empty).Split('\n');
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                if (!string.IsNullOrWhiteSpace(lines[i]))
                {
                    string line = lines[i].Trim();
                    return line.Length > 180 ? line.Substring(0, 180) + "..." : line;
                }
            }
            return "unknown error";
        }
    }

    internal sealed class CommandResult
    {
        internal int ExitCode { get; set; }
        internal string Output { get; set; }
    }

    internal static class CommandRunner
    {
        internal static CommandResult RunPowerShell(string script, int timeoutMilliseconds)
        {
            // Plain -Command instead of -EncodedCommand: base64-encoded PowerShell is a
            // common malware pattern and trips antivirus heuristics (Avast CyberCapture).
            string command = script.Replace("\"", "\\\"");
            return Run(
                "powershell.exe",
                "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"" + command + "\"",
                timeoutMilliseconds);
        }

        internal static CommandResult Run(string fileName, string arguments, int timeoutMilliseconds)
        {
            ProcessStartInfo start = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            try
            {
                using (Process process = Process.Start(start))
                {
                    Task<string> output = process.StandardOutput.ReadToEndAsync();
                    Task<string> error = process.StandardError.ReadToEndAsync();
                    if (!process.WaitForExit(timeoutMilliseconds))
                    {
                        try
                        {
                            process.Kill();
                        }
                        catch
                        {
                        }
                        return new CommandResult { ExitCode = -2, Output = "Command timed out." };
                    }

                    Task.WaitAll(new Task[] { output, error }, 5000);
                    string standardOutput = output.IsCompleted ? output.Result : string.Empty;
                    string standardError = error.IsCompleted ? error.Result : string.Empty;
                    string combined = standardOutput;
                    if (!string.IsNullOrWhiteSpace(standardError))
                    {
                        if (!string.IsNullOrWhiteSpace(combined))
                        {
                            combined += Environment.NewLine;
                        }
                        combined += standardError;
                    }
                    return new CommandResult { ExitCode = process.ExitCode, Output = combined.Trim() };
                }
            }
            catch (Exception ex)
            {
                return new CommandResult { ExitCode = -1, Output = ex.Message };
            }
        }
    }

    internal static class SelfTests
    {
        private static int _passed;

        internal static int Run()
        {
            try
            {
                TestModelSelection();
                TestClaudeMerge();
                TestCodexMerge();
                TestDpapi();
                TestKeyCodes();
                Stdout.WriteLine("Self-test passed: " + _passed.ToString(CultureInfo.InvariantCulture) + " checks");
                return 0;
            }
            catch (Exception ex)
            {
                Stdout.WriteLine("Self-test failed: " + ex.Message);
                return 1;
            }
        }

        private static void TestModelSelection()
        {
            string[] models = { "other", "claude-sonnet-4.5", "gpt-5.6-sol" };
            Assert(ModelSelector.PickClaude(models) == "claude-sonnet-4.5", "Claude model selection");
            Assert(ModelSelector.PickCodex(models) == "gpt-5.6-sol", "Codex model selection");

            // Gemini (Antigravity) key: only gemini models are exposed, argon must win.
            string[] gemini = { "gemini-2.5-flash-lite", "gemini-3-flash", "gemini-4-argon", "gemini-3-pro-image" };
            Assert(ModelSelector.PickCodex(gemini) == "gemini-4-argon", "Gemini key picks argon for Codex");
            List<string> ordered = ModelSelector.CodexCandidates(new[] { "other", "gemini-3-flash", "gpt-5.6-sol", "gemini-4-argon" }, null);
            Assert(ordered[0] == "gpt-5.6-sol" && ordered[1] == "gemini-4-argon" && ordered[2] == "gemini-3-flash" && ordered[3] == "other", "Codex candidates: gpt, gemini, rest");
        }

        private static void TestClaudeMerge()
        {
            string source = "{\"theme\":\"dark\",\"env\":{\"KEEP\":\"yes\",\"ANTHROPIC_AUTH_TOKEN\":\"secret\"}}";
            string merged = ConfigManager.MergeClaudeJson(source, @"C:\Test App\helper.exe", "claude-test");
            Dictionary<string, object> root = Json.DeserializeDictionary(merged);
            Dictionary<string, object> env = Json.AsDictionary(root["env"]);
            Assert(Convert.ToString(root["theme"]) == "dark", "Claude unrelated setting preserved");
            Assert(Convert.ToString(env["KEEP"]) == "yes", "Claude environment preserved");
            Assert(!env.ContainsKey("ANTHROPIC_AUTH_TOKEN"), "Claude plaintext token removed");
            Assert(Convert.ToString(env["ANTHROPIC_BASE_URL"]) == AppPaths.ClaudeCodeBaseUrl, "Claude base URL");
            Assert(Convert.ToString(root["apiKeyHelper"]).Contains("--credential"), "Claude credential helper");
            Assert(Convert.ToString(env["ANTHROPIC_DEFAULT_OPUS_MODEL"]) == "claude-test", "Claude tiers fall back to selected model");

            string[] gateway = { "gpt-6.1-sol", "claude-haiku-4.5", "claude-sonnet-4.6", "claude-sonnet-5.5", "claude-opus-4.8", "claude-opus-5.5" };
            string tiered = ConfigManager.MergeClaudeJson(source, @"C:\Test App\helper.exe", "claude-opus-5.5", gateway);
            Dictionary<string, object> tieredEnv = Json.AsDictionary(Json.DeserializeDictionary(tiered)["env"]);
            Assert(Convert.ToString(tieredEnv["ANTHROPIC_DEFAULT_OPUS_MODEL"]) == "claude-opus-5.5", "Claude opus tier");
            Assert(Convert.ToString(tieredEnv["ANTHROPIC_DEFAULT_SONNET_MODEL"]) == "claude-sonnet-5.5", "Claude sonnet tier");
            Assert(Convert.ToString(tieredEnv["ANTHROPIC_DEFAULT_HAIKU_MODEL"]) == "claude-haiku-4.5", "Claude haiku tier");
            Assert(Convert.ToString(tieredEnv["ANTHROPIC_DEFAULT_FABLE_MODEL"]) == "claude-opus-5.5", "Claude fable tier falls back to opus");
            Assert(ModelSelector.PickClaude(gateway) == "claude-opus-5.5", "Claude default pick is Opus 5.5");
        }

        private static void TestCodexMerge()
        {
            string source =
                "model_provider = \"old\"\nmodel = \"old-model\"\n\n" +
                "[features]\ngoals = true\n\n" +
                "[model_providers.zendigikey]\nbase_url = \"https://old.invalid\"\n";
            string merged = ConfigManager.MergeCodexToml(source, @"C:\Test App\helper.exe", "gpt-test");
            Assert(merged.Contains("model_provider = \"zendigikey\""), "Codex provider selected");
            Assert(merged.Contains("model = \"gpt-test\""), "Codex model selected");
            Assert(merged.Contains("[features]"), "Codex unrelated section preserved");
            Assert(Count(merged, "[model_providers.zendigikey]") == 1, "Codex provider replaced once");
            Assert(merged.Contains("[model_providers.zendigikey.auth]"), "Codex credential helper");
            Assert(merged.Contains("C:\\\\Test App\\\\helper.exe"), "Codex Windows path escaped");

            string reset = Toml.RemoveZendi(merged);
            Assert(!reset.Contains("zendigikey"), "Reset removes ZendiKey provider");
            Assert(!reset.Contains("model = "), "Reset removes model written by ZendiKey");
            Assert(reset.Contains("[features]") && reset.Contains("goals = true"), "Reset keeps unrelated section");
            string foreign = "model_provider = \"other\"\nmodel = \"x\"\n\n[model_providers.zendigikey]\nbase_url = \"https://old.invalid\"\n";
            string resetForeign = Toml.RemoveZendi(foreign);
            Assert(resetForeign.Contains("model_provider = \"other\"") && resetForeign.Contains("model = \"x\"") && !resetForeign.Contains("zendigikey"), "Reset keeps a foreign provider's model");
        }

        private static void TestKeyCodes()
        {
            Assert(KeyCodeClient.IsZenCode("ZEN-ABC-123-XYZ"), "ZEN code recognised");
            Assert(KeyCodeClient.IsZenCode("  zen-abc-123-xyz "), "ZEN code case/space tolerant");
            Assert(!KeyCodeClient.IsZenCode("sk-abc"), "sk key is not a code");
            Assert(KeyCodeClient.FromProtocolLink("zendikey://ZEN-ABC-123-XYZ") == "ZEN-ABC-123-XYZ", "Link with code");
            Assert(KeyCodeClient.FromProtocolLink("zendikey://open/ZEN-ABC-123-XYZ/") == "ZEN-ABC-123-XYZ", "Link with path");
            Assert(KeyCodeClient.FromProtocolLink("zendikey:sk-test123") == "sk-test123", "Link with key");
            Assert(KeyCodeClient.FromProtocolLink("https://example.com") == null, "Non zendikey link ignored");
            Assert(KeyStatus.QuotaLabel(100000000) == "100M", "Quota label M");
            Assert(KeyStatus.QuotaLabel(61200000) == "61.2M", "Quota label fraction");
            Assert(KeyStatus.QuotaLabel(2500000000L) == "2.5B", "Quota label B");
            Assert(KeyStatus.PackageLabel("gpt", 50000000, false) == "GPT 50M", "Package label");
            Assert(KeyStatus.PackageLabel("claude", 0, true) == "CLAUDE UNLIMITED", "Unlimited label");
            Assert(ConfigManager.PrettyModelName("gpt-6.1-sol") == "GPT-6.1-Sol", "Pretty model name");

            // Catalog: bundled knows gpt-5.6-sol only; the gateway also offers gpt-6.1-sol, which
            // must be synthesised from the bundled template instead of being dropped.
            string bundledJson = "{\"models\":[{\"slug\":\"gpt-5.6-sol\",\"display_name\":\"GPT-5.6-Sol\",\"visibility\":\"list\",\"priority\":5,\"upgrade\":{\"model\":\"x\"},\"context_window\":272000}]}";
            IEnumerable bundledModels = Json.DeserializeDictionary(bundledJson)["models"] as IEnumerable;
            string catalog = ConfigManager.BuildCodexModelCatalog(bundledModels, "gpt-6.1-sol", new[] { "gpt-5.6-sol", "gpt-6.1-sol", "claude-opus-5" });
            Dictionary<string, object> parsed = Json.DeserializeDictionary(catalog);
            List<Dictionary<string, object>> entries = new List<Dictionary<string, object>>();
            foreach (object raw in (IEnumerable)parsed["models"])
            {
                entries.Add(Json.AsDictionary(raw));
            }
            Assert(entries.Count == 3, "Catalog keeps every gateway model");
            Assert(Convert.ToString(entries[0]["slug"]) == "gpt-6.1-sol", "Selected model first");
            Assert(Convert.ToString(entries[0]["display_name"]) == "GPT-6.1-Sol", "Synthesised display name");
            Assert(Convert.ToString(entries[0]["context_window"]) == "272000", "Template fields copied");
            Assert(entries[1]["upgrade"] == null, "Upgrade nag removed");
            Assert(Convert.ToString(entries[2]["slug"]) == "claude-opus-5", "Non-gpt gateway model included");
        }

        private static void TestDpapi()
        {
            byte[] plain = Encoding.UTF8.GetBytes("local-test-key");
            byte[] encrypted = CredentialStore.ProtectBytes(plain);
            byte[] decrypted = CredentialStore.UnprotectBytes(encrypted);
            Assert(!plain.SequenceEqual(encrypted), "DPAPI encrypts data");
            Assert(plain.SequenceEqual(decrypted), "DPAPI round trip");
        }

        private static int Count(string value, string expected)
        {
            int count = 0;
            int index = 0;
            while ((index = value.IndexOf(expected, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += expected.Length;
            }
            return count;
        }

        private static void Assert(bool condition, string name)
        {
            if (!condition)
            {
                throw new InvalidOperationException(name);
            }
            _passed++;
        }
    }
}
