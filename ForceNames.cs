using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using MySqlConnector;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ForceNames;

public class ForceNamesPlugin : BasePlugin
{
    public override string ModuleName => "ForceNames";
    public override string ModuleVersion => "1.1.0";
    public override string ModuleAuthor => "Midori";

    // Config
    private ForceNamesConfig _cfg = new();
    private readonly JsonSerializerOptions _jsonOpt = new(JsonSerializerDefaults.Web)
    { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    // Paths
    private static string Abs(string p) => Path.GetFullPath(p);
    private string DesiredDir  => Abs(Path.Combine(ModuleDirectory, "..", "..", "configs", "plugins", "ForceNames"));
    private string DesiredPath => Abs(Path.Combine(DesiredDir, "ForceNames.json"));

    // MySQL Schema
    private const string TBL = "forcenames_players";
    private const string COL_SID  = "steamid64";
    private const string COL_NICK = "nickname";

    // Lifecycle, Timer
    private bool _unloading;
    private int _timerGen = 0;

    public override void Load(bool hotReload)
    {
        _unloading = false;
        EnsureConfig();

        if (_cfg.UseMySql)
        {
            if (TryLoadMappingsFromMySql(out var map))
            {
                _cfg.Mappings = map;
                Console.WriteLine($"[ForceNames] MySQL loaded mappings: {_cfg.Mappings.Count}");
            }
            else
            {
                Console.WriteLine("[ForceNames] MySQL load failed. Using last in-memory mappings.");
            }
        }

        StartPeriodicApply();

        AddCommand("css_fn", "Force name by SteamID64", CmdForceName);
        AddCommand("css_unfn", "Remove forced name", CmdUnforceName);
        AddCommand("css_fn_reload", "Reload config (prefers subfolder)", CmdReload);
        AddCommand("css_fn_list", "List mappings", CmdList);
    }

    public override void Unload(bool hotReload) => _unloading = true;

    // Periodic Apply
    private void StartPeriodicApply()
    {
        _timerGen++;
        var myGen = _timerGen;
        var interval = Math.Clamp(_cfg.ApplyIntervalSec, 1.0f, 60.0f);

        AddTimer(interval, () => TimerTick(myGen));
    }

    private void TimerTick(int myGen)
    {
        if (_unloading || myGen != _timerGen) return;
        try { ApplyAllOnlinePlayers(); } catch (Exception ex) { Console.WriteLine($"[ForceNames] periodic: {ex}"); }

        var interval = Math.Clamp(_cfg.ApplyIntervalSec, 1.0f, 60.0f);
        if (myGen == _timerGen) AddTimer(interval, () => TimerTick(myGen));
    }

    private void ApplyAllOnlinePlayers()
    {
        foreach (var p in Utilities.GetPlayers())
            if (p?.IsValid == true) TryApplyForcedName(p);
    }

    [GameEventHandler]
    public HookResult OnPlayerFullConnect(EventPlayerConnectFull e, GameEventInfo info)
    {
        var p = e.Userid;
        if (p?.IsValid == true) TryApplyForcedName(p);
        return HookResult.Continue;
    }

    private void TryApplyForcedName(CCSPlayerController player)
    {
        try
        {
            if (player == null || !player.IsValid || player.IsBot) return;

            var sid = player.SteamID.ToString();
            if (_cfg.Mappings != null && _cfg.Mappings.TryGetValue(sid, out var forced))
            {
                var current = player.PlayerName ?? string.Empty;
                if (string.Equals(current, forced, StringComparison.Ordinal)) return;

                player.PlayerName = forced;
                try { Utilities.SetStateChanged(player, "CCSPlayerController", "m_iszPlayerName"); } catch { }

                Console.WriteLine($"[ForceNames] Applied '{forced}' to {sid} (was '{current}')");
            }
        }
        catch (Exception ex) { Console.WriteLine($"[ForceNames] TryApplyForcedName error: {ex}"); }
    }

    // Commands
    [ConsoleCommand("css_fn")]
    private void CmdForceName(CCSPlayerController? caller, CommandInfo cmd)
    {
        if (caller != null && !AdminManager.PlayerHasPermissions(caller, "@css/root"))
        { caller?.PrintToChat($" {ChatColors.Red}You do not have permission."); return; }

        if (cmd.ArgCount < 3) { caller?.PrintToChat("Usage: !fn <steamid64> <nickname>"); return; }

        var sid = cmd.GetArg(1);
        var parts = new List<string>(); for (int i=2;i<cmd.ArgCount;i++) parts.Add(cmd.GetArg(i));
        var nickname = CleanNickname(string.Join(' ', parts));

        if (_cfg.UseMySql)
        {
            if (!UpsertMySqlMapping(sid, nickname)) { caller?.PrintToChat($"[{ChatColors.Green}ForceNames{ChatColors.White}] DB write failed."); return; }
            _cfg.Mappings[sid] = nickname;
            caller?.PrintToChat($"[{ChatColors.Green}ForceNames{ChatColors.White}] DB(MySql) set {sid} ⇒ '{nickname}'");
        }

        else
        {
            _cfg.Mappings[sid] = nickname; SaveConfig();
            caller?.PrintToChat($"[{ChatColors.Green}ForceNames{ChatColors.White}] Config(.json) set {sid} ⇒ '{nickname}'");
        }

        if (ulong.TryParse(sid, out var sid64))
        {
            var target = Utilities.GetPlayerFromSteamId(sid64);
            if (target?.IsValid == true) TryApplyForcedName(target);
        }
    }

    [ConsoleCommand("css_unfn")]
    private void CmdUnforceName(CCSPlayerController? caller, CommandInfo cmd)
    {
        if (caller != null && !AdminManager.PlayerHasPermissions(caller, "@css/root"))
        { caller?.PrintToChat($" {ChatColors.Red}You do not have permission."); return; }

        if (cmd.ArgCount < 2) { caller?.PrintToChat("Usage: !unfn <steamid64>"); return; }
        var sid = cmd.GetArg(1);

        if (_cfg.UseMySql)
        {
            if (!DeleteMySqlMapping(sid)) { caller?.PrintToChat($"[{ChatColors.Green}ForceNames{ChatColors.White}] DB(MySql) delete failed."); return; }
            _cfg.Mappings.Remove(sid);
            caller?.PrintToChat($"[{ChatColors.Green}ForceNames{ChatColors.White}] DB(MySql) unset {sid}");
        }
        else
        {
            if (_cfg.Mappings.Remove(sid)) { SaveConfig(); caller?.PrintToChat($"[{ChatColors.Green}ForceNames{ChatColors.White}] Config(.json) unset {sid}"); }
            else caller?.PrintToChat($"[{ChatColors.Green}ForceNames{ChatColors.White}] No mapping for {sid} in Config(.json)");
        }
    }

    [ConsoleCommand("css_fn_reload")]
    private void CmdReload(CCSPlayerController? caller, CommandInfo cmd)
    {
        if (caller != null && !AdminManager.PlayerHasPermissions(caller, "@css/root"))
        { caller?.PrintToChat($" {ChatColors.Red}You do not have permission."); return; }

        ReloadConfigAndApply(cmd);
    }

    [ConsoleCommand("css_fn_list")]
    private void CmdList(CCSPlayerController? caller, CommandInfo cmd)
    {
        if (caller != null && !AdminManager.PlayerHasPermissions(caller, "@css/root"))
        { caller?.PrintToChat($" {ChatColors.Red}You do not have permission."); return; }
        if (_cfg.Mappings.Count == 0) { caller?.PrintToChat($"[{ChatColors.Green}ForceNames{ChatColors.White}] empty"); return; }
        foreach (var kv in _cfg.Mappings) caller?.PrintToChat($"{kv.Key} => {ChatColors.Purple}{kv.Value}");
    }

    // Reload Core (JSON, MySQL)
    private void ReloadConfigAndApply(CommandInfo cmd)
    {
        if (!_cfg.UseMySql)
        {
            if (!File.Exists(DesiredPath)) { SaveConfig(); cmd.ReplyToCommand($"[{ChatColors.Green}ForceNames{ChatColors.White}] Created new config."); return; }
            try
            {
                var json = File.ReadAllText(DesiredPath);
                _cfg = JsonSerializer.Deserialize<ForceNamesConfig>(json, _jsonOpt) ?? _cfg;
                SaveConfig();
                ApplyAllOnlinePlayers();
                StartPeriodicApply();
                cmd.CallingPlayer?.PrintToChat($"[{ChatColors.Green}ForceNames{ChatColors.White}] Reloaded JSON & applied.");
            }
            catch (Exception ex) { cmd.ReplyToCommand($"[{ChatColors.Green}ForceNames{ChatColors.White}] Reload failed: {ex.Message}"); }
            return;
        }

        // MySQL Mode
        if (TryLoadMappingsFromMySql(out var map))
        {
            _cfg.Mappings = map;
            ApplyAllOnlinePlayers();
            StartPeriodicApply();
            cmd.ReplyToCommand($"[{ChatColors.Green}ForceNames{ChatColors.White}] Reloaded MySQL & applied. ({map.Count} rows)");
        }
        else cmd.ReplyToCommand($"[{ChatColors.Green}ForceNames{ChatColors.White}] MySQL reload failed. See console.");
    }

    // Config
    private void EnsureConfig()
    {
        try
        {
            if (File.Exists(DesiredPath)) { LoadConfig(); return; }
            Directory.CreateDirectory(DesiredDir);
            _cfg = new ForceNamesConfig();
            SaveConfig();
            Console.WriteLine($"[ForceNames] Created default: {DesiredPath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ForceNames] EnsureConfig error: {ex}");
            _cfg = new ForceNamesConfig();
        }
    }

    private void LoadConfig()
    {
        var json = File.ReadAllText(DesiredPath);
        _cfg = JsonSerializer.Deserialize<ForceNamesConfig>(json, _jsonOpt) ?? new ForceNamesConfig();
    }

    private void SaveConfig()
    {
        Directory.CreateDirectory(DesiredDir);
        var json = JsonSerializer.Serialize(_cfg, _jsonOpt);
        var tmp = DesiredPath + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, DesiredPath, true);
    }

    // MySQL
    private MySqlConnection OpenMySqlConnectionEnsuringDatabase()
    {
        var csb = new MySqlConnectionStringBuilder {
            Server = _cfg.DatabaseHost,
            Port = (uint)_cfg.DatabasePort,
            UserID = _cfg.DatabaseUser,
            Password = _cfg.DatabasePassword,
            Database = _cfg.DatabaseName,
            SslMode = MySqlSslMode.None
        };

        try
        {
            var conn = new MySqlConnection(csb.ConnectionString);
            conn.Open();
            return conn;
        }
        catch (MySqlException ex) when (ex.Number == 1049)
        {
            var csbNoDb = new MySqlConnectionStringBuilder(csb.ConnectionString) { Database = "" };
            using var serverConn = new MySqlConnection(csbNoDb.ConnectionString);
            serverConn.Open();
            using (var cmd = new MySqlCommand(
                $"CREATE DATABASE IF NOT EXISTS `{_cfg.DatabaseName}` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci", serverConn))
            {
                cmd.ExecuteNonQuery();
            }
            serverConn.Close();

            var conn2 = new MySqlConnection(csb.ConnectionString);
            conn2.Open();
            return conn2;
        }
    }

    private void EnsureMySqlTable(MySqlConnection conn)
    {
        var sql = $@"
    CREATE TABLE IF NOT EXISTS `{TBL}` (
    `{COL_SID}`  VARCHAR(20)  NOT NULL,
    `{COL_NICK}` VARCHAR(64)  NOT NULL,
    PRIMARY KEY (`{COL_SID}`),
    KEY `idx_{TBL}_{COL_NICK}` (`{COL_NICK}`)
    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;";
        using var cmd = new MySqlCommand(sql, conn);
        cmd.ExecuteNonQuery();
    }

    private bool TryLoadMappingsFromMySql(out SortedDictionary<string, string> map)
    {
        map = new SortedDictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using var conn = OpenMySqlConnectionEnsuringDatabase();
            EnsureMySqlTable(conn);

            var sql = $"SELECT `{COL_SID}` AS sid, `{COL_NICK}` AS nick " +
                    $"FROM `{TBL}` WHERE `{COL_NICK}` IS NOT NULL AND `{COL_NICK}` <> ''";
            using var cmd = new MySqlCommand(sql, conn);
            using var r = cmd.ExecuteReader();

            while (r.Read())
            {
                var sid  = r["sid"]?.ToString();
                var nick = r["nick"]?.ToString() ?? "";
                if (string.IsNullOrWhiteSpace(sid) || string.IsNullOrWhiteSpace(nick)) continue;
                map[sid] = nick;
            }
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ForceNames] MySQL load error: {ex.Message}");
            return false;
        }
    }

    private bool UpsertMySqlMapping(string sid, string nickname)
    {
        try
        {
            using var conn = OpenMySqlConnectionEnsuringDatabase();
            EnsureMySqlTable(conn);
            var sql = $"INSERT INTO `{TBL}` (`{COL_SID}`,`{COL_NICK}`) VALUES (@sid,@nick) " +
                    $"ON DUPLICATE KEY UPDATE `{COL_NICK}`=VALUES(`{COL_NICK}`);";
            using var cmd = new MySqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@sid", sid);
            cmd.Parameters.AddWithValue("@nick", nickname);
            return cmd.ExecuteNonQuery() > 0;
        }
        catch (Exception ex) { Console.WriteLine($"[ForceNames] DB upsert error: {ex.Message}"); return false; }
    }

    private bool DeleteMySqlMapping(string sid)
    {
        try
        {
            using var conn = OpenMySqlConnectionEnsuringDatabase();
            EnsureMySqlTable(conn);
            var sql = $"DELETE FROM `{TBL}` WHERE `{COL_SID}`=@sid;";
            using var cmd = new MySqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@sid", sid);
            return cmd.ExecuteNonQuery() > 0;
        }
        catch (Exception ex) { Console.WriteLine($"[ForceNames] DB delete error: {ex.Message}"); return false; }
    }

    private static string CleanNickname(string s)
    {
        s = (s ?? string.Empty).Trim();
        s = string.Concat(s.Where(c => !char.IsControl(c)));
        if (s.Length > 31) s = s[..31];
        return s;
    }
}

public class ForceNamesConfig
{
    public SortedDictionary<string, string> Mappings { get; set; } = new(StringComparer.Ordinal);
    public float ApplyIntervalSec { get; set; } = 10.0f;

    public bool UseMySql { get; set; } = false;
    public string DatabaseHost { get; set; } = "localhost";
    public int DatabasePort { get; set; } = 3306;
    public string DatabaseUser { get; set; } = "";
    public string DatabasePassword { get; set; } = "";
    public string DatabaseName { get; set; } = "cs2_players";

}
