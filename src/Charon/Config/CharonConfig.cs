using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Charon.Config;

public enum WindowPlacementMode
{
    AutoOffScreen,
    CustomCoordinates,
    VisibleDebug
}

public class CharonConfig
{
    public string LanIp { get; set; } = "auto";
    public int Port { get; set; } = 48590;
    public string ChromePath { get; set; } = "auto";    
    public string VideoBitrate { get; set; } = "8M";
    public int MaxConcurrentTuners { get; set; } = 2;
    public bool Enable1080p { get; set; } = false;
    // Saved Channels DVR Server settings for Gracenote StationId resolution (blank on first run)
    public string ChannelsDvrIp { get; set; } = string.Empty;
    public int ChannelsDvrPort { get; set; } = 8089;
    public string LastSelectedProviderId { get; set; } = string.Empty;
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public WindowPlacementMode WindowPlacement { get; set; } = WindowPlacementMode.AutoOffScreen;
    public int CustomX { get; set; } = -3200;
    public int CustomY { get; set; } = 0;

    [JsonIgnore]
    public string ResolvedLanIp { get; private set; } = "127.0.0.1";

    [JsonIgnore]
    public int ResolvedPort { get; private set; } = 48590;

    [JsonIgnore]
    public string ResolvedChromePath { get; private set; } = string.Empty;

    private static readonly string ConfigFilePath = Path.Combine(AppContext.BaseDirectory, "config.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static CharonConfig LoadOrCreate()
    {
        CharonConfig config;
        if (File.Exists(ConfigFilePath))
        {
            try
            {
                string json = File.ReadAllText(ConfigFilePath);
                config = JsonSerializer.Deserialize<CharonConfig>(json, JsonOptions) ?? new CharonConfig();
            }
            catch
            {
                config = new CharonConfig();
            }
        }
        else
        {
            config = new CharonConfig();
            config.Save();
        }

        config.ResolveRuntimeValues();
        return config;
    }

    public void Save()
    {
        string json = JsonSerializer.Serialize(this, JsonOptions);
        File.WriteAllText(ConfigFilePath, json);
    }

    public void ResolveRuntimeValues()
    {
        ResolvedLanIp = (string.IsNullOrWhiteSpace(LanIp) || LanIp.Equals("auto", StringComparison.OrdinalIgnoreCase))
            ? DetectLocalLanIp()
            : LanIp;

        ResolvedPort = FindNextAvailablePort(Port);
        if (ResolvedPort != Port)
        {
            Port = ResolvedPort;
            Save();
        }

        ResolvedChromePath = (string.IsNullOrWhiteSpace(ChromePath) || ChromePath.Equals("auto", StringComparison.OrdinalIgnoreCase))
            ? DetectChromeExecutable()
            : ChromePath;
    }

    private static string DetectLocalLanIp()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0);
            socket.Connect("8.8.8.8", 65530);
            if (socket.LocalEndPoint is IPEndPoint endPoint)
            {
                return endPoint.Address.ToString();
            }
        }
        catch
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up ||
                    ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;

                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ua.Address))
                        return ua.Address.ToString();
                }
            }
        }

        return "127.0.0.1";
    }

    private static int FindNextAvailablePort(int startingPort)
    {
        var activeListeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
        var usedPorts = new HashSet<int>(activeListeners.Select(e => e.Port));

        for (int port = startingPort; port < 49150; port++)
        {
            if (usedPorts.Contains(port))
                continue;

            try
            {
                using var listener = new TcpListener(IPAddress.Any, port);
                listener.Start();
                listener.Stop();
                return port;
            }
            catch
            {
            }
        }

        return startingPort;
    }

    private static string DetectChromeExecutable()
    {
        string[] candidatePaths =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Google\Chrome\Application\chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Google\Chrome\Application\chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Google\Chrome\Application\chrome.exe")
        ];

        foreach (var path in candidatePaths)
        {
            if (File.Exists(path))
                return path;
        }

        return candidatePaths[0];
    }
}