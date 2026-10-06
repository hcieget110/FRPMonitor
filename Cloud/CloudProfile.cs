using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FRPMonitor.Cloud;

// Only connection metadata is JSON; passwords are stored by Windows Credential Manager.
public sealed class CloudProfile
{
    public bool Enabled { get; set; }
    public string Host { get; set; } = "";
    public int Port { get; set; } = 22;
    public string User { get; set; } = "";
    public string Fingerprints { get; set; } = "";
    [System.Text.Json.Serialization.JsonIgnore]
    public string CredentialTarget => "FRPMonitor-Cloud-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Host.Trim().ToLowerInvariant() + ":" + Port + ":" + User)))[..24];
    public static string PathName => Path.Combine(Settings.DataFolder, "cloud-settings.json");
    public static CloudProfile Load()
    {
        if (!File.Exists(PathName)) return new();
        try { return JsonSerializer.Deserialize<CloudProfile>(File.ReadAllText(PathName)) ?? new(); }
        catch { return new(); }
    }
    public void Save() => Disk.AtomicJson(PathName, this);
    public void Validate()
    {
        if (!Enabled) return;
        if (string.IsNullOrWhiteSpace(Host) || Host.Any(char.IsWhiteSpace) || Port is < 1 or > 65535 || string.IsNullOrWhiteSpace(User))
            throw new FormatException("请填写云主机地址、SSH 端口和用户名。");
        if (string.IsNullOrWhiteSpace(Fingerprints)) throw new FormatException("未找到已信任的主机密钥。请先用 SSH 客户端确认主机身份，或填写 SHA256 指纹。");
    }
    internal static string NormalizeFingerprint(string value) => value.Trim().Replace("SHA256:", "", StringComparison.OrdinalIgnoreCase).TrimEnd('=');
    public bool Trusts(string fingerprint) => Fingerprints.Split(',', StringSplitOptions.RemoveEmptyEntries).Any(f => NormalizeFingerprint(f) == NormalizeFingerprint(fingerprint));
    public static string KnownFingerprints(string host, int port)
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "known_hosts");
        if (!File.Exists(path)) return "";
        var match = port == 22 ? host : "[" + host + "]:" + port;
        var hashes = new List<string>();
        foreach (var line in File.ReadLines(path))
        {
            var fields = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 3 || !fields[0].Split(',').Contains(match, StringComparer.OrdinalIgnoreCase)) continue;
            try { hashes.Add(Convert.ToBase64String(SHA256.HashData(Convert.FromBase64String(fields[2]))).TrimEnd('=')); } catch (FormatException) { }
        }
        return string.Join(",", hashes.Distinct());
    }
}

