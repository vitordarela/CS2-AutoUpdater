namespace AutoUpdater
{
    using System.Text.Json.Serialization;
    using CounterStrikeSharp.API.Core;

    public sealed class PluginConfig : BasePluginConfig
    {
        [JsonPropertyName("ConfigVersion")]
        public override int Version { get; set; } = 2;

        [JsonPropertyName("UpdateCheckInterval")]
        public int UpdateCheckInterval { get; set; } = 180;

        [JsonPropertyName("ShutdownDelay")]
        public int ShutdownDelay { get; set; } = 120;

        [JsonPropertyName("MinPlayersInstantShutdown")]
        public int MinPlayersInstantShutdown { get; set; } = 1;

        [JsonPropertyName("MinPlayerPercentageShutdownAllowed")]
        public float MinPlayerPercentageShutdownAllowed { get; set; } = 0.6f;

        [JsonPropertyName("ShutdownOnMapChangeIfPendingUpdate")]
        public bool ShutdownOnMapChangeIfPendingUpdate { get; set; } = true;

        [JsonPropertyName("MySQLDatabase")]
        public string MySQLDatabase { get; set; } = "";

        [JsonPropertyName("MySQLHostname")]
        public string MySQLHostname { get; set; } = "";

        [JsonPropertyName("MySQLPort")]
        public int MySQLPort { get; set; } = 3306;

        [JsonPropertyName("MySQLUsername")]
        public string MySQLUsername { get; set; } = "";

        [JsonPropertyName("MySQLPassword")]
        public string MySQLPassword { get; set; } = "";

        [JsonPropertyName("MySQLTableName")]
        public string MySQLTableName { get; set; } = "";

    }
    
}