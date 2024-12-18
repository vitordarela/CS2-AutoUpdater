﻿namespace AutoUpdater
{
    using CounterStrikeSharp.API;
    using CounterStrikeSharp.API.Core;
    using CounterStrikeSharp.API.Core.Attributes;
    using CounterStrikeSharp.API.Modules.Timers;
    using CounterStrikeSharp.API.Modules.Cvars;
    using CounterStrikeSharp.API.Modules.Utils;
    using System.Text.RegularExpressions;
    using Microsoft.Extensions.Logging;
    using System.Net.Http.Json;
    using Docker.DotNet;
    using Docker.DotNet.Models;
    using MySqlConnector;

    [MinimumApiVersion(178)]
    public partial class AutoUpdater : BasePlugin, IPluginConfig<PluginConfig>
    {
        public override string ModuleName => "AutoUpdater";
        public override string ModuleAuthor => "dranix";
        public override string ModuleDescription => "Auto Updater for Counter-Strike 2.";
        public override string ModuleVersion => "1.0.4";

        private const string SteamApiEndpoint =
            "https://api.steampowered.com/ISteamApps/UpToDateCheck/v0001/?appid=730&version={0}";

        public required PluginConfig Config { get; set; } = new();
        private static Dictionary<int, bool> PlayersNotified = new();
        private static ConVar? sv_visiblemaxplayers;
        private static double UpdateFoundTime;
        private static bool IsServerLoading;
        private static bool RestartRequired;
        private static bool UpdateAvailable;
        private static int RequiredVersion;
        public Database Database = null!;
        public string DbConnectionString = string.Empty;
        public CounterStrikeSharp.API.Modules.Timers.Timer? unreadyToRestartTimer = null;

        public override void Load(bool hotReload)
        {
            sv_visiblemaxplayers = ConVar.Find("sv_visiblemaxplayers");

            RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn);

            RegisterListener<Listeners.OnGameServerSteamAPIActivated>(OnGameServerSteamAPIActivated);
            RegisterListener<Listeners.OnServerHibernationUpdate>(OnServerHibernationUpdate);
            RegisterListener<Listeners.OnClientConnected>(OnClientConnected);
            RegisterListener<Listeners.OnClientDisconnect>(OnClientDisconnect);
            RegisterListener<Listeners.OnMapStart>(OnMapStart);
            RegisterListener<Listeners.OnMapEnd>(OnMapEnd);

            DbConnectionString = BuildConnectionString();
            Database = new Database(this, DbConnectionString);

            RegisterOrUpdateContainerSteamVersion();

            AddTimer(Config.UpdateCheckInterval, CheckServerVersion, TimerFlags.REPEAT);
            ScheduleDailyJobAtSevenAM();

        }

        private async void RegisterOrUpdateContainerSteamVersion()
        {
            await Database.CreateTable();

            string steamInfPatchVersion = await GetSteamInfPatchVersion();
            Console.WriteLine("steamInfPatchVersion: "+ steamInfPatchVersion);

            var containerName = Environment.GetEnvironmentVariable("CONTAINER_NAME");

            var container = await Database.GetContainer(containerName);

            Console.WriteLine("Container: " + container);

            if (container != null)
            {
                Console.WriteLine("Container Version: " + container.app_version);
                if (container.app_version != steamInfPatchVersion)
                {
                    await Database.UpdateVersionContainer(containerName, steamInfPatchVersion, true);
                }

                return;

            }
            else
            {
                await Database.AddContainerToDb(containerName, steamInfPatchVersion);
            }

        }

        private string BuildConnectionString()
        {   
            Console.WriteLine("Building connection string");
            var builder = new MySqlConnectionStringBuilder
            {
                Database = Config.MySQLDatabase,
                UserID = Config.MySQLUsername,
                Password = Config.MySQLPassword,
                Server = Config.MySQLHostname,
                Port = (uint)Config.MySQLPort
            };

            Console.WriteLine("OK!");
            return builder.ConnectionString;
        }

        private void ScheduleDailyJobAtSevenAM()
        {
            Logger.LogInformation("Auto restart daily.. 7:00 AM");

            DateTime now = DateTime.Now;
            DateTime nextRun = now.Date.AddHours(7);

            if (now > nextRun)
            {
                // if passed from 7:00 AM, Schedule to next day
                nextRun = nextRun.AddDays(1);
            }

            TimeSpan initialDelay = nextRun - now;

            float initialDelayInSeconds = (float)initialDelay.TotalSeconds;

            // only for test purpose (trigger 5 in 5 min)
            //float repeatIntervalInSeconds = 5 * 60;

            var timer = new Timer(
           interval: initialDelayInSeconds,
           callback: ExecuteJob);

            Logger.LogInformation("Auto restart daily.. CONFIGURED");
        }

        private void ExecuteJob()
        {
            Logger.LogInformation("Auto restart daily.. STARTING");
            Server.NextFrame(ManageServerUpdate);
        }

        public override void Unload(bool hotReload) => Dispose();

        public void OnConfigParsed(PluginConfig config)
        {
            if (config.Version < Config.Version) Logger.LogWarning(Localizer["AutoUpdater.Console.ConfigVersionMismatch", Config.Version, config.Version]);

            Config = config;
        }

        private void OnGameServerSteamAPIActivated() => Logger.LogInformation(Localizer["AutoUpdater.Console.UpdateCheckInitiated"]);

        private void OnServerHibernationUpdate(bool isHibernating)
        {
            if (isHibernating) Logger.LogInformation(Localizer["AutoUpdater.Console.HibernateWarning"]);
        }

        private static void OnMapStart(string mapName)
        {
            PlayersNotified.Clear();
            IsServerLoading = false;
        }

        private void OnMapEnd()
        {
            if (RestartRequired && Config.ShutdownOnMapChangeIfPendingUpdate) ShutdownServer();
            IsServerLoading = true;
        }

        private static void OnClientConnected(int playerSlot)
        {
            CCSPlayerController player = Utilities.GetPlayerFromSlot(playerSlot);
            if (!player.IsValid || player.IsBot || player.IsHLTV) return;

            PlayersNotified.Add(playerSlot, false);
        }

        private static void OnClientDisconnect(int playerSlot)
        {
            PlayersNotified.Remove(playerSlot);
        }

        private async void CheckServerVersion()
        {
            try
            {
                var serverName = Environment.GetEnvironmentVariable("CS2_SERVERNAME");
                Server.ExecuteCommand($"hostname {serverName}");
                Logger.LogInformation($"Update server name: {serverName}");

                if (RestartRequired || !await IsUpdateAvailable()) return;

                Logger.LogInformation($"Update Available calling ManageServerUpdate..");

                Server.NextFrame(PrepareToRestartContainer);

            }
            catch (Exception ex)
            {
                Logger.LogError(Localizer["AutoUpdater.Console.ErrorUpdateCheck", ex.Message]);
            }
        }

        private void PrepareToRestartContainer()
        {
            //aqui logica antes de reiniciar
            // Verificar se existe algum container com o updated = 0
            // se tiver atualizando, ele deve esperar a atualização terminar para reiniciar.
            // se não tiver atualizando, ele pode reiniciar o container, atribuindo o valor do updated para 0 deste container bloqueando a fila de restart.

            AddTimer(2f, async () =>
            {
                Logger.LogInformation($"Update Available checking if have some container restarting..");
                bool havePendingServer = await Database.CheckContainersUpdateing();

                if (havePendingServer)
                {
                    return;
                }

                Logger.LogInformation("Update Available > No Pending Server > Calling ManageServerUpdate");
                var containerName = Environment.GetEnvironmentVariable("CONTAINER_NAME");
                await Database.UpdateUpdatedFlag(containerName, false);

                Server.NextFrame(ManageServerUpdate);

            }, TimerFlags.REPEAT);          
        }

        private void ManageServerUpdate()
        {
            if (!UpdateAvailable)
            {
                UpdateFoundTime = Server.CurrentTime;
                UpdateAvailable = true;
                
                Logger.LogInformation(Localizer["AutoUpdater.Console.NewUpdateReleased", RequiredVersion]);
            }

            List<CCSPlayerController> players = GetCurrentPlayers();

            if (IsServerLoading || !CheckPlayers(players.Count)) return;

            players.ForEach(NotifyPlayerAboutUpdate);
            players.ForEach(controller => PlayersNotified[controller.Slot] = true);

            AddTimer(players.Count <= Config.MinPlayersInstantShutdown ? 1 : Config.ShutdownDelay,
                PrepareServerShutdown,
                Config.ShutdownOnMapChangeIfPendingUpdate ? TimerFlags.STOP_ON_MAPCHANGE : 0);

            RestartRequired = true;
        }

        private bool CheckPlayers(int players)
        {
            var slots = sv_visiblemaxplayers?.GetPrimitiveValue<int>() ?? -1;

            if (slots == -1) slots = Server.MaxPlayers;

            return (float)players / slots < Config.MinPlayerPercentageShutdownAllowed ||
                   Config.MinPlayersInstantShutdown >= players;
        }

        private HookResult OnPlayerSpawn(EventPlayerSpawn @event, GameEventInfo info)
        {
            if (!UpdateAvailable) return HookResult.Continue;

            CCSPlayerController player = @event.Userid;

            if (!player.IsValid || player.IsBot || player.TeamNum <= (byte)CsTeam.Spectator) return HookResult.Continue;
            if (PlayersNotified.TryGetValue(player.Slot, out bool notified) && notified) return HookResult.Continue;

            PlayersNotified[player.Slot] = true;

            Server.NextFrame(() => NotifyPlayerAboutUpdate(player));

            return HookResult.Continue;
        }

        private void NotifyPlayerAboutUpdate(CCSPlayerController player)
        {
            int remainingTime = Math.Max(1, Config.ShutdownDelay - (int)(Server.CurrentTime - UpdateFoundTime));

            string timeUnitLabel =
                remainingTime >= 60 ? "AutoUpdater.Chat.MinuteLabel" : "AutoUpdater.Chat.SecondLabel";
            
            string pluralSuffix = remainingTime > 120 || (remainingTime < 60 && remainingTime != 1)
                ? $"{Localizer["AutoUpdater.Chat.PluralSuffix"]}"
                : string.Empty;

            string timeToRestart =
                $"{(remainingTime >= 60 ? remainingTime / 60 : remainingTime)} {Localizer[timeUnitLabel]}{pluralSuffix}";

            player.PrintToChat(
                $" {Localizer["AutoUpdater.Chat.Prefix"]} {Localizer["AutoUpdater.Chat.NewUpdateReleased", RequiredVersion, timeToRestart]}");
        }

        private async Task<bool> IsUpdateAvailable()
        {
            //string steamInfPatchVersion = await GetSteamInfPatchVersion();
            var container = await Database.GetContainer(Environment.GetEnvironmentVariable("CONTAINER_NAME"));

            //var container = new
            //{
            //    app_version = steamInfPatchVersion
            //};

            if (string.IsNullOrWhiteSpace(container?.app_version))
            {
                Logger.LogError(Localizer["AutoUpdater.Console.ErrorPatchVersionNull"]);
                return false;
            }

            using HttpClient httpClient = new HttpClient();
            var response = await httpClient.GetAsync(string.Format(SteamApiEndpoint, container?.app_version));

            if (!response.IsSuccessStatusCode)
            {
                Logger.LogWarning(Localizer["AutoUpdater.Console.WarningSteamRequestFailed", response.StatusCode]);
                return false;
            }

            var upToDateCheckResponse = await response.Content.ReadFromJsonAsync<UpToDateCheckResponse>();
            RequiredVersion = (int)upToDateCheckResponse?.Response?.RequiredVersion!;


            Logger.LogInformation($"Response SteamAPI | UpToDate: {upToDateCheckResponse?.Response?.UpToDate} | Required Version {RequiredVersion}");


            return upToDateCheckResponse.Response is { Success: true, UpToDate: false };
        }

        private async Task<string> GetSteamInfPatchVersion()
        {
            string steamInfPath = Path.Combine(Server.GameDirectory, "csgo", "steam.inf");

            if (!File.Exists(steamInfPath))
            {
                Logger.LogError(Localizer["AutoUpdater.Console.ErrorSteamInfNotFound", steamInfPath]);
                return string.Empty;
            }

            try
            {
                string steamInfContents = await File.ReadAllTextAsync(steamInfPath);
                Match match = PatchVersionRegex().Match(steamInfContents);

                if (match.Success) return match.Groups[1].Value;

                Logger.LogError(Localizer["AutoUpdater.Console.ErrorPatchVersionKeyNotFound", steamInfPath]);

                return string.Empty;
            }
            catch (Exception ex)
            {
                Logger.LogError(Localizer["AutoUpdater.Console.ErrorReadingSteamInf", ex.Message]);
            }

            return string.Empty;
        }

        private void PrepareServerShutdown()
        {
            List<CCSPlayerController> players = GetCurrentPlayers();

            foreach (var player in players)
            {
                switch (player.Connected)
                {
                    case PlayerConnectedState.PlayerConnected:
                    case PlayerConnectedState.PlayerConnecting:
                    case PlayerConnectedState.PlayerReconnecting:
                        Server.ExecuteCommand(
                            $"kickid {player.UserId} Due to the game update (Version: {RequiredVersion}), the server is now restarting.");
                        break;
                }
            }

            AddTimer(1, ShutdownServer);
        }
        
        private void ShutdownServer()
        {
            Logger.LogInformation(Localizer["AutoUpdater.Console.ServerShutdownInitiated", RequiredVersion]);
            _ = RestartContainer();
        }

        private static List<CCSPlayerController> GetCurrentPlayers()
        {
            return Utilities.GetPlayers().Where(controller => controller is { IsValid: true, IsBot: false, IsHLTV: false }).ToList();
        }

        private async Task RestartContainer()
        {
            try
            {
                Logger.LogInformation("Restarting Container...");

                var dockerUri = new Uri("http://189.1.169.38:2378");
                using (var dockerClient = new DockerClientConfiguration(dockerUri).CreateClient())
                {
                    var containerId = await GetContainerIdAsync(dockerClient);
                    Logger.LogInformation("Restarting Container... ID: " + containerId);

                    if (!string.IsNullOrEmpty(containerId))
                    {
                        await dockerClient.Containers.RestartContainerAsync(containerId, new ContainerRestartParameters());
                        Logger.LogInformation("Container restarted successfully.");
                    }
                    else
                    {
                        Logger.LogError("Failed to find container ID.");
                    }
                }

            }
            catch (Exception ex)
            {
                Logger.LogError(ex.Message);
            }
            
        }

        public static async Task<string> GetContainerIdAsync(DockerClient dockerClient)
        {
            var containerName = Environment.GetEnvironmentVariable("CONTAINER_NAME");

            if (string.IsNullOrEmpty(containerName))
            {
                throw new InvalidOperationException("Env Variable 'CONTAINER_NAME' is not defined.");
            }

            var containerId = await GetContainerIdByNameAsync(dockerClient, containerName);

            return containerId;
        }

        private static async Task<string> GetContainerIdByNameAsync(DockerClient dockerClient, string containerName)
        {
            var response = await dockerClient.Containers.ListContainersAsync(new ContainersListParameters()
            {
                Filters = new Dictionary<string, IDictionary<string, bool>>
                {
                    ["name"] = new Dictionary<string, bool> { [containerName] = true }
                }
            });

            return response.Count > 0 ? response[0].ID : null;
        }

        [GeneratedRegex(@"PatchVersion=(?<version>[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+)", RegexOptions.ExplicitCapture, 1000)]
        private static partial Regex PatchVersionRegex();
    }
}    
